using Discord;
using Discord.WebSocket;
using DiscordBot.Exceptions;
using DiscordBot.Games.Managers;
using DiscordBot.Games.Models.Dungeon;
using DiscordBot.Models;
using Microsoft.Extensions.Logging;
using DiscordBot.Games.Models.BattleArena;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using static DiscordBot.Models.CoinAccounts;

namespace DiscordBot.Managers
{
    /// <summary>
    /// Buttons, select menus and modals. Custom ids are built with <see cref="ComponentId"/> ("game:action:args"), and the
    /// game prefix picks the handler. Discord needs every interaction answered within 3 seconds, so handlers acknowledge
    /// first: DeferAsync for actions that change a dashboard, DeferAsync(ephemeral) for private views, or a modal as the
    /// very first response. Refusals and errors go back privately to the person who clicked.
    /// </summary>
    public partial class CommandManager
    {
        public async Task RunComponent(SocketMessageComponent component)
        {
            if (!await PassesGates(component))
                return;

            if (!ComponentId.TryParse(component.Data.CustomId, out string game, out string action, out string[] args))
            {
                await ReplyPrivately(component, "That button doesn't do anything any more.");
                return;
            }

            try
            {
                switch (game)
                {
                    case DungeonManager.ComponentGame:
                        await DungeonComponent(component, action);
                        break;
                    case BattleArenaManager.ComponentGame:
                        await FaceOffComponent(component, action, args);
                        break;
                    default:
                        await ReplyPrivately(component, "That button doesn't do anything any more.");
                        break;
                }
            }
            catch (Exception ex)
            {
                await ReportInteractionError(component, ex);
            }
        }

        public async Task RunModal(SocketModal modal)
        {
            if (!await PassesGates(modal))
                return;

            if (!ComponentId.TryParse(modal.Data.CustomId, out string game, out string action, out string[] args))
            {
                await ReplyPrivately(modal, "That form doesn't do anything any more.");
                return;
            }

            try
            {
                switch (game)
                {
                    case BattleArenaManager.ComponentGame:
                        await FaceOffModal(modal, action, args);
                        break;
                    default:
                        await ReplyPrivately(modal, "That form doesn't do anything any more.");
                        break;
                }
            }
            catch (Exception ex)
            {
                await ReportInteractionError(modal, ex);
            }
        }

        private async Task DungeonComponent(SocketMessageComponent component, string action)
        {
            ulong userId = component.User.Id;
            string name = component.User.Username;

            if (!_dungeonManager.IsDashboard(component.Message.Id))
            {
                await ReplyPrivately(component, "This dungeon has ended. Open a new one with `.dng 1000`.");
                return;
            }

            if (action == "me")
            {
                await component.DeferAsync(ephemeral: true);
                await component.FollowupAsync(await _dungeonManager.DescribeRun(userId), ephemeral: true);
                return;
            }

            //the dashboard edit is the answer, so acknowledge without posting anything
            await component.DeferAsync();

            string refusal;
            switch (action)
            {
                case "join": refusal = await _dungeonManager.Join(await _coinService.Get(userId, name)); break;
                case "leave": refusal = await _dungeonManager.Leave(userId); break;
                case "start": refusal = await _dungeonManager.Start(userId); break;
                case "go": refusal = await _dungeonManager.Go(userId); break;
                case "cash": refusal = await _dungeonManager.Cash(userId); break;
                case "pool": refusal = await _dungeonManager.EnterPool(userId); break;
                case "boss": refusal = await _dungeonManager.Boss(userId, BossChoice.Fight); break;
                case "sneak": refusal = await _dungeonManager.Boss(userId, BossChoice.Sneak); break;
                case "potion": refusal = await _dungeonManager.Potion(userId); break;
                default: refusal = "That button doesn't do anything any more."; break;
            }

            if (refusal != null)
                await component.FollowupAsync(refusal, ephemeral: true);
            else
                MarkActiveFromInteraction(component);
        }

        private const string StaleFight = "That Face Off has ended. Start a new one with `.fo 1000`.";

        /// <summary>
        /// Face Off buttons carry the game id: fo:action:gameId[:arg]. Dashboard buttons answer with DeferAsync and a private
        /// follow-up. Attack and Gamble with more than one opponent, and Back a fighter, answer with a private select menu first;
        /// choosing a fighter to back opens the amount form.
        /// </summary>
        private async Task FaceOffComponent(SocketMessageComponent component, string action, string[] args)
        {
            ulong userId = component.User.Id;
            if (args.Length == 0 || !Guid.TryParseExact(args[0], "N", out Guid gameId) || !_battleArenaManager.IsLive(gameId))
            {
                await ReplyPrivately(component, StaleFight);
                return;
            }

            bool onDashboard = action != "target" && action != "backpick";
            if (onDashboard && !_battleArenaManager.IsDashboard(gameId, component.Message.Id))
            {
                await ReplyPrivately(component, StaleFight);
                return;
            }

            switch (action)
            {
                case "join":
                {
                    await component.DeferAsync();
                    CoinAccount account = await _coinService.Get(userId, component.User.Username);
                    double percent = args.Length > 1 && double.TryParse(args[1], out double p) ? p : 10;
                    double bet = percent >= 100 ? account.NetWorth : Math.Round(account.NetWorth * percent / 100, 2);
                    await FollowUp(component, await _battleArenaManager.Join(account, bet, component.Channel, component.GuildId ?? 0));
                    return;
                }
                case "joincustom":
                    await component.RespondWithModalAsync(AmountModal(ComponentId.Build(BattleArenaManager.ComponentGame, "joinamt", args[0]), "Join the Face Off", "Your bet"));
                    return;
                case "leave":
                    await component.DeferAsync();
                    await FollowUp(component, await _battleArenaManager.Leave(userId));
                    return;
                case "start":
                    await component.DeferAsync();
                    await FollowUp(component, await _battleArenaManager.Start(userId), quietOnSuccess: true);
                    return;
                case "pick":
                {
                    if (args.Length < 2 || !BattleActionInfo.TryParse(args[1], out BattleAction pick))
                    {
                        await ReplyPrivately(component, "That button doesn't do anything any more.");
                        return;
                    }
                    var opponents = _battleArenaManager.OpponentsOf(userId);
                    if ((pick == BattleAction.Attack || pick == BattleAction.Gamble) && opponents.Count > 1)
                    {
                        var menu = new SelectMenuBuilder()
                            .WithCustomId(ComponentId.Build(BattleArenaManager.ComponentGame, "target", args[0], args[1]))
                            .WithPlaceholder($"Who do you {pick.ToString().ToLowerInvariant()}?");
                        foreach (var opponent in opponents.Take(25))
                            menu.AddOption(opponent.Name, opponent.Id.ToString(), $"{opponent.Hp} HP");
                        await component.RespondAsync($"Pick a target for **{pick}**.", components: new ComponentBuilder().WithSelectMenu(menu).Build(), ephemeral: true);
                        return;
                    }
                    await component.DeferAsync();
                    await FollowUp(component, await _battleArenaManager.Pick(userId, pick, null));
                    return;
                }
                case "target":
                {
                    //this select lives on the private picker message, so the answer replaces that message
                    await component.DeferAsync();
                    string message;
                    if (args.Length < 2 || !BattleActionInfo.TryParse(args[1], out BattleAction pick)
                        || component.Data.Values.Count == 0 || !ulong.TryParse(component.Data.Values.First(), out ulong targetId))
                        message = "That pick didn't come through. Press the button again.";
                    else
                        message = (await _battleArenaManager.Pick(userId, pick, targetId)).Message;
                    await component.ModifyOriginalResponseAsync(m =>
                    {
                        m.Content = message;
                        m.Components = new ComponentBuilder().Build();
                    });
                    return;
                }
                case "ability":
                    await component.DeferAsync();
                    await FollowUp(component, await _battleArenaManager.UseAbility(userId));
                    return;
                case "card":
                    await component.DeferAsync(ephemeral: true);
                    await ShowCardPrivately(component, userId);
                    return;
                case "back":
                {
                    var fighters = _battleArenaManager.FightersOf(gameId);
                    if (fighters.Any(f => f.Id == userId))
                    {
                        await ReplyPrivately(component, "You're fighting in this one, so you can't back anyone.");
                        return;
                    }
                    var menu = new SelectMenuBuilder()
                        .WithCustomId(ComponentId.Build(BattleArenaManager.ComponentGame, "backpick", args[0]))
                        .WithPlaceholder("Who do you back?");
                    foreach (var fighter in fighters.Take(25))
                        menu.AddOption(fighter.Name, fighter.Id.ToString(), fighter.Multiple.HasValue ? $"Pays about x{fighter.Multiple:0.##} right now" : "Nobody has backed them yet");
                    await component.RespondAsync("Pick a fighter to back. Backers of the winner share everything backed on this fight.",
                        components: new ComponentBuilder().WithSelectMenu(menu).Build(), ephemeral: true);
                    return;
                }
                case "backpick":
                {
                    if (component.Data.Values.Count == 0 || !ulong.TryParse(component.Data.Values.First(), out ulong fighterId))
                    {
                        await ReplyPrivately(component, "That pick didn't come through. Press Back a fighter again.");
                        return;
                    }
                    string fighterName = _battleArenaManager.FightersOf(gameId).FirstOrDefault(f => f.Id == fighterId).Name ?? "this fighter";
                    await component.RespondWithModalAsync(AmountModal(ComponentId.Build(BattleArenaManager.ComponentGame, "backamt", args[0], fighterId.ToString()), $"Back {fighterName}", "Your stake"));
                    return;
                }
                default:
                    await ReplyPrivately(component, "That button doesn't do anything any more.");
                    return;
            }
        }

        private async Task FaceOffModal(SocketModal modal, string action, string[] args)
        {
            await modal.DeferAsync(ephemeral: true);
            if (args.Length == 0 || !Guid.TryParseExact(args[0], "N", out Guid gameId) || !_battleArenaManager.IsLive(gameId))
            {
                await modal.FollowupAsync(StaleFight, ephemeral: true);
                return;
            }

            string text = modal.Data.Components.FirstOrDefault(c => c.CustomId == "amount")?.Value?.Trim().ToLowerInvariant() ?? "";
            CoinAccount account = await _coinService.Get(modal.User.Id, modal.User.Username);
            if (!TryExtractBetAmount(new List<string> { text }, account, out double amount) || amount <= 0)
            {
                await modal.FollowupAsync("That amount doesn't look right. Use a number like 1000, a percentage like 10%, or max.", ephemeral: true);
                return;
            }

            (bool Ok, string Message) result;
            if (action == "joinamt")
                result = await _battleArenaManager.Join(account, amount, modal.Channel, modal.GuildId ?? 0);
            else if (action == "backamt" && args.Length > 1 && ulong.TryParse(args[1], out ulong fighterId))
                result = await _battleArenaManager.Back(account, gameId, fighterId, amount);
            else
                result = (false, "That form doesn't do anything any more.");

            await modal.FollowupAsync(result.Message, ephemeral: true);
            if (result.Ok)
                MarkActiveFromInteraction(modal);
        }

        private static Modal AmountModal(string customId, string title, string label)
        {
            return new ModalBuilder()
                .WithTitle(title)
                .WithCustomId(customId)
                .AddTextInput(label, "amount", TextInputStyle.Short, placeholder: "1000, 10% or max", minLength: 1, maxLength: 20, required: true)
                .Build();
        }

        private async Task ShowCardPrivately(SocketMessageComponent component, ulong userId)
        {
            CoinAccount account = await _coinService.Get(userId, component.User.Username);
            BattlePerson card = account.BattlePerson;
            if (card == null)
            {
                await component.FollowupAsync("You don't have a card yet. Type `.cardpull` to get one.", ephemeral: true);
                return;
            }
            Embed embed = new EmbedBuilder().WithTitle(card.Name).WithDescription(card.GetSummary()).WithColor(Color.DarkRed).Build();
            if (!string.IsNullOrEmpty(card.FilePath) && File.Exists(card.FilePath))
                await component.FollowupWithFileAsync(card.FilePath, embed: embed, ephemeral: true);
            else
                await component.FollowupAsync(embed: embed, ephemeral: true);
        }

        /// <summary>Sends a Face Off action's message privately. Successful clicks also count as money activity.</summary>
        private async Task FollowUp(SocketMessageComponent component, (bool Ok, string Message) result, bool quietOnSuccess = false)
        {
            if (result.Ok)
                MarkActiveFromInteraction(component);
            if (!string.IsNullOrEmpty(result.Message) && !(result.Ok && quietOnSuccess))
                await component.FollowupAsync(result.Message, ephemeral: true);
        }

        /// <summary>The same checks typed commands get: the bot is running, the user isn't banned, and isn't a bot.</summary>
        private async Task<bool> PassesGates(SocketInteraction interaction)
        {
            if (interaction.User.IsBot)
                return false;
            if (_stopped)
            {
                await ReplyPrivately(interaction, "The bot is stopped at the moment.");
                return false;
            }
            if (_bannedUserIds.Contains(interaction.User.Id))
            {
                await ReplyPrivately(interaction, "you are BANNED YOU CANNOT INTERACT WITH BOT");
                return false;
            }
            return true;
        }

        /// <summary>Game clicks count as money activity for the raffle, like typed game commands in a server channel.</summary>
        private void MarkActiveFromInteraction(SocketInteraction interaction)
        {
            if (interaction.GuildId.HasValue && interaction.ChannelId.HasValue)
                _coinService.MarkActive(interaction.User.Id, interaction.ChannelId.Value);
        }

        private async Task ReportInteractionError(SocketInteraction interaction, Exception ex)
        {
            if (ex is BadInputException)
            {
                await ReplyPrivately(interaction, ex.Message);
                return;
            }
            _logger.LogError(ex, "Interaction {Id} failed: {Message}", (interaction as SocketMessageComponent)?.Data.CustomId ?? (interaction as SocketModal)?.Data.CustomId, ex.Message);
            await ReplyPrivately(interaction, "Something went wrong.");
        }

        /// <summary>Answers only the person who clicked. Works whether or not the interaction was acknowledged yet.</summary>
        private static async Task ReplyPrivately(SocketInteraction interaction, string text)
        {
            try
            {
                if (interaction.HasResponded)
                    await interaction.FollowupAsync(text, ephemeral: true);
                else
                    await interaction.RespondAsync(text, ephemeral: true);
            }
            catch (Exception)
            {
                //the interaction expired, so there's no one left to tell
            }
        }
    }
}
