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
    /// `.slots` and the slot machine's buttons. The machine is one message edited in place (Games/SlotView.cs); its buttons carry
    /// the owner's id and only work for them. The money rules are in <see cref="SlotMachineManager"/>.
    /// </summary>
    public partial class CommandManager
    {
        private const string MachineClosed = "That machine has closed. Type `.slots` to open a new one.";
        private const string NotYourMachine = "That's someone else's machine. Type `.slots` to get your own.";

        private async Task GameSlots(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            CoinAccount account = await _coinService.Get(message.Author.Id, message.Author.Username);
            if (Math.Floor(account.NetWorth) < 1)
                throw new BadInputException("You're out of cash to bet. Wait for the hourly $1,000.");

            double bet;
            if (args.Count == 0)
                bet = SlotMachineManager.DefaultBet(account.NetWorth);
            else if (TryExtractBetAmount(args, account, out double asked) && asked >= 1)
                bet = SlotMachineManager.ClampBet(asked, account.NetWorth);
            else
                throw new BadSyntaxException();

            SlotSession session = _slotMachineManager.Open(account.UserId, message.Author.Username, bet);
            var posted = await message.Channel.SendMessageAsync(
                embed: SlotView.Build(_slotMachineManager.Idle(session, account.NetWorth)),
                components: SlotView.Buttons(account.UserId, bet, account.NetWorth));
            _slotMachineManager.Attach(posted.Id, session);
            await TryDeleteMessageAsync(message); //the machine is the answer, so the typed command is only clutter
        }

        private async Task SlotComponent(SocketMessageComponent component, string action, string[] args)
        {
            if (!ButtonOwner.TryRead(args, 0, out ulong ownerId))
            {
                await ReplyPrivately(component, MachineClosed);
                return;
            }
            if (!ButtonOwner.Allows(ownerId, component.User.Id))
            {
                await ReplyPrivately(component, NotYourMachine);
                return;
            }
            if (!_slotMachineManager.TryGet(component.Message.Id, out SlotSession session))
            {
                await ReplyPrivately(component, MachineClosed);
                return;
            }

            if (action == "bet")
            {
                await component.RespondWithModalAsync(AmountModal(ComponentId.Build(MoneyComponents.Slots, "betamt", ownerId.ToString()), "Set your bet", "Bet per spin"));
                return;
            }

            //the machine message is the answer, so acknowledge without posting anything
            await component.DeferAsync();
            IUserMessage message = component.Message;

            if (action == "spin")
            {
                if (!await _slotMachineManager.Play(message, session))
                    await component.FollowupAsync("Hold on, it's still spinning.", ephemeral: true);
                else
                    MarkActiveFromInteraction(component);
                return;
            }

            CoinAccount account = await _coinService.Get(session.UserId, session.Name);
            if (action == "cash")
            {
                _slotMachineManager.Close(message.Id);
                string total = session.SessionNet >= 0 ? $"+${Common.Helpers.FormatHelper.GetCommaNumber(session.SessionNet)}" : $"-${Common.Helpers.FormatHelper.GetCommaNumber(-session.SessionNet)}";
                var closing = _slotMachineManager.Idle(session, account.NetWorth,
                    $"💰 Cashed out after {session.Spins} spin{(session.Spins == 1 ? "" : "s")}: **{total}**. Type `.slots` to play again.");
                await message.ModifyAsync(m =>
                {
                    m.Embed = SlotView.Build(closing);
                    m.Components = new ComponentBuilder().Build();
                });
                return;
            }

            if (action != "half" && action != "double" && action != "max")
            {
                await component.FollowupAsync("That button doesn't do anything any more.", ephemeral: true);
                return;
            }
            session.Bet = SlotView.NextBet(action, session.Bet, account.NetWorth);
            session.LastUsed = DateTime.UtcNow;
            await ShowMachine(message, session, account);
        }

        private async Task SlotModal(SocketModal modal, string action, string[] args)
        {
            if (action != "betamt" || !ButtonOwner.TryRead(args, 0, out ulong ownerId) || modal.Message == null
                || !_slotMachineManager.TryGet(modal.Message.Id, out SlotSession session))
            {
                await ReplyPrivately(modal, MachineClosed);
                return;
            }
            if (!ButtonOwner.Allows(ownerId, modal.User.Id))
            {
                await ReplyPrivately(modal, NotYourMachine);
                return;
            }

            string text = modal.Data.Components.FirstOrDefault(c => c.CustomId == "amount")?.Value?.Trim().ToLowerInvariant() ?? "";
            CoinAccount account = await _coinService.Get(session.UserId, session.Name);
            if (!TryExtractBetAmount(new List<string> { text }, account, out double amount) || amount < 1)
            {
                await ReplyPrivately(modal, "That amount doesn't look right. Use a number like 1000, a percentage like 10%, or max.");
                return;
            }

            await modal.DeferAsync();
            session.Bet = SlotMachineManager.ClampBet(amount, account.NetWorth);
            session.LastUsed = DateTime.UtcNow;
            await ShowMachine(modal.Message, session, account);
        }

        /// <summary>Redraws the idle machine with the current bet.</summary>
        private async Task ShowMachine(IUserMessage message, SlotSession session, CoinAccount account)
        {
            var screen = _slotMachineManager.Idle(session, account.NetWorth);
            await message.ModifyAsync(m =>
            {
                m.Embed = SlotView.Build(screen);
                m.Components = SlotView.Buttons(session.UserId, session.Bet, account.NetWorth);
            });
        }
    }
}
