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
    /// `.roulette` and the table's buttons. The table is one message edited in place (Games/RouletteView.cs); its buttons carry the
    /// owner's id and only work for them. The money rules are in <see cref="RouletteManager"/>.
    /// </summary>
    public partial class CommandManager
    {
        private const string TableClosed = "That table has closed. Type `.roulette` to open a new one.";
        private const string NotYourTable = "That's someone else's table. Type `.roulette` to get your own.";

        private async Task GameRoulette(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            CoinAccount account = await _coinService.Get(message.Author.Id, message.Author.Username);
            RouletteSession session = _rouletteManager.Open(account.UserId, message.Author.Username);

            if (args.Count == 0)
            {
                //an empty table: the buttons place the first bets
                var posted = await message.Channel.SendMessageAsync(
                    embed: RouletteView.Build(_rouletteManager.Idle(session, account.NetWorth)),
                    components: RouletteView.Buttons(account.UserId, 0, account.NetWorth));
                _rouletteManager.Attach(posted.Id, session);
                await TryDeleteMessageAsync(message);
                return;
            }

            session.Bets = RouletteManager.ParseBets(args, account.NetWorth);

            //the first spin happens at once, on the table message the buttons then keep editing
            var table = await message.Channel.SendMessageAsync(
                embed: RouletteView.Build(_rouletteManager.Idle(session, account.NetWorth, "🎡 No more bets... spinning!")),
                components: new ComponentBuilder().Build());
            try
            {
                await session.Gate.WaitAsync();
                try
                {
                    var screen = await _rouletteManager.Spin(session);
                    _rouletteManager.Attach(table.Id, session);
                    await _rouletteManager.Show(table, session, screen);
                }
                finally
                {
                    session.Gate.Release();
                }
            }
            catch (Exception)
            {
                try { await table.DeleteAsync(); } catch (Exception) { /* nothing to clean up if it is already gone */ }
                throw;
            }
            await TryDeleteMessageAsync(message); //the table shows what happened, so the typed command is only clutter
        }

        private async Task RouletteComponent(SocketMessageComponent component, string action, string[] args)
        {
            if (!ButtonOwner.TryRead(args, 0, out ulong ownerId))
            {
                await ReplyPrivately(component, TableClosed);
                return;
            }
            if (!ButtonOwner.Allows(ownerId, component.User.Id))
            {
                await ReplyPrivately(component, NotYourTable);
                return;
            }
            if (!_rouletteManager.TryGet(component.Message.Id, out RouletteSession session))
            {
                await ReplyPrivately(component, TableClosed);
                return;
            }

            if (action == "bet")
            {
                await component.RespondWithModalAsync(new ModalBuilder()
                    .WithTitle("Place your bets")
                    .WithCustomId(ComponentId.Build(MoneyComponents.Roulette, "betsmodal", ownerId.ToString()))
                    .AddTextInput("Spot then amount, as many as you like", "bets", TextInputStyle.Short,
                        placeholder: "red 1000 17 50 odd 25%", minLength: 3, maxLength: 100, required: true)
                    .Build());
                return;
            }

            //the table message is the answer, so acknowledge without posting anything
            await component.DeferAsync();
            IUserMessage message = component.Message;

            if (action == "spin")
            {
                if (!await _rouletteManager.Play(message, session))
                    await component.FollowupAsync("Hold on, it's still spinning.", ephemeral: true);
                else
                    MarkActiveFromInteraction(component);
                return;
            }

            CoinAccount account = await _coinService.Get(session.UserId, session.Name);
            if (action == "cash")
            {
                _rouletteManager.Close(message.Id);
                string total = session.SessionNet >= 0 ? $"+${Common.Helpers.FormatHelper.GetCommaNumber(session.SessionNet)}" : $"-${Common.Helpers.FormatHelper.GetCommaNumber(-session.SessionNet)}";
                var closing = _rouletteManager.Idle(session, account.NetWorth,
                    $"💰 Left the table after {session.Spins} spin{(session.Spins == 1 ? "" : "s")}: **{total}**. Type `.roulette` to play again.");
                await message.ModifyAsync(m =>
                {
                    m.Embed = RouletteView.Build(closing);
                    m.Components = new ComponentBuilder().Build();
                });
                return;
            }

            if (action != "half" && action != "double" && action != "max")
            {
                await component.FollowupAsync("That button doesn't do anything any more.", ephemeral: true);
                return;
            }
            session.Bets = RouletteView.Scale(session.Bets, action, account.NetWorth);
            session.LastUsed = DateTime.UtcNow;
            await _rouletteManager.Show(message, session, _rouletteManager.Idle(session, account.NetWorth));
        }

        private async Task RouletteModal(SocketModal modal, string action, string[] args)
        {
            if (action != "betsmodal" || !ButtonOwner.TryRead(args, 0, out ulong ownerId) || modal.Message == null
                || !_rouletteManager.TryGet(modal.Message.Id, out RouletteSession session))
            {
                await ReplyPrivately(modal, TableClosed);
                return;
            }
            if (!ButtonOwner.Allows(ownerId, modal.User.Id))
            {
                await ReplyPrivately(modal, NotYourTable);
                return;
            }

            string text = modal.Data.Components.FirstOrDefault(c => c.CustomId == "bets")?.Value?.Trim().ToLowerInvariant() ?? "";
            CoinAccount account = await _coinService.Get(session.UserId, session.Name);
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

            await modal.DeferAsync();
            session.Bets = RouletteView.Scale(bets, "keep", account.NetWorth);
            session.LastUsed = DateTime.UtcNow;
            await _rouletteManager.Show(modal.Message, session, _rouletteManager.Idle(session, account.NetWorth));
        }
    }
}
