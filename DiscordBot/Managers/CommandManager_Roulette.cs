using Discord;
using Discord.WebSocket;
using DiscordBot.Exceptions;
using DiscordBot.Games;
using DiscordBot.Games.Managers;
using DiscordBot.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static DiscordBot.Models.CoinAccounts;

namespace DiscordBot.Managers
{
    /// <summary>
    /// `.roulette` and its buttons. The result is the plain message roulette has always posted, with buttons under it that anyone can
    /// press: each press plays for whoever pressed it, with their own last bets, and posts its own result. The money rules are in
    /// <see cref="RouletteManager"/>.
    /// </summary>
    public partial class CommandManager
    {
        private async Task GameRoulette(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            CoinAccount account = await _coinService.Get(message.Author.Id, message.Author.Username);

            if (args.Count == 0)
            {
                await message.Channel.SendMessageAsync(
                    RouletteView.HowToBet + " Pressing **Spin again** plays your own last bets.",
                    components: RouletteView.Buttons());
                return;
            }

            var bets = RouletteManager.ParseBets(args, account.NetWorth);
            var result = await _rouletteManager.Spin(account.UserId, message.Author.Username, bets);
            await message.Channel.SendMessageAsync(
                RouletteView.Text(result, message.Author.Mention, _rouletteManager.PrestigeDisabled),
                components: RouletteView.Buttons());
        }

        private async Task RouletteComponent(SocketMessageComponent component, string action, string[] args)
        {
            if (action == "bet")
            {
                await component.RespondWithModalAsync(BetsModal());
                return;
            }
            if (action != "spin" && action != "half" && action != "double" && action != "max")
            {
                await ReplyPrivately(component, "That button doesn't do anything any more.");
                return;
            }
            //nothing to repeat yet: ask for bets instead
            if (!_rouletteManager.TryLastBets(component.User.Id, out var last))
            {
                await component.RespondWithModalAsync(BetsModal());
                return;
            }

            //the result is a new message, so acknowledge without posting anything yet
            await component.DeferAsync();
            CoinAccount account = await _coinService.Get(component.User.Id, component.User.Username);
            var bets = action == "spin" ? last : RouletteView.Scale(last, action, account.NetWorth);
            await PlayRoulette(component, account, bets);
        }

        private async Task RouletteModal(SocketModal modal, string action, string[] args)
        {
            if (action != "betsmodal")
            {
                await ReplyPrivately(modal, "That form doesn't do anything any more.");
                return;
            }

            string text = modal.Data.Components.FirstOrDefault(c => c.CustomId == "bets")?.Value?.Trim().ToLowerInvariant() ?? "";
            CoinAccount account = await _coinService.Get(modal.User.Id, modal.User.Username);
            List<RouletteChoice> bets;
            try
            {
                bets = RouletteManager.ParseBets(text.Split(' ', StringSplitOptions.RemoveEmptyEntries), account.NetWorth);
            }
            catch (BadSyntaxException)
            {
                await ReplyPrivately(modal, $"Those bets don't look right. {RouletteView.HowToBet}");
                return;
            }
            catch (BadInputException ex)
            {
                await ReplyPrivately(modal, ex.Message);
                return;
            }

            await modal.DeferAsync();
            await PlayRoulette(modal, account, bets);
        }

        /// <summary>Plays the bets for whoever pressed the button and posts the result publicly, with the buttons.</summary>
        private async Task PlayRoulette(SocketInteraction interaction, CoinAccount account, List<RouletteChoice> bets)
        {
            var result = await _rouletteManager.Spin(account.UserId, interaction.User.Username, bets);
            MarkActiveFromInteraction(interaction);
            await interaction.FollowupAsync(
                RouletteView.Text(result, interaction.User.Mention, _rouletteManager.PrestigeDisabled),
                components: RouletteView.Buttons(),
                allowedMentions: AllowedMentions.None);
        }

        private static Modal BetsModal() => new ModalBuilder()
            .WithTitle("Place your bets")
            .WithCustomId(ComponentId.Build(MoneyComponents.Roulette, "betsmodal"))
            .AddTextInput("Spot then amount, as many as you like", "bets", TextInputStyle.Short,
                placeholder: "red 1000 17 50 odd 25%", minLength: 3, maxLength: 100, required: true)
            .Build();
    }
}
