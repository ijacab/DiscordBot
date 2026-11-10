using Discord;
using Discord.WebSocket;
using DiscordBot.Exceptions;
using DiscordBot.Games.Managers;
using DiscordBot.Games.Models.Dungeon;
using DiscordBot.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

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
