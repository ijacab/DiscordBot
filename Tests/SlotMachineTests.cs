using Common.Services;
using Discord;
using DiscordBot;
using DiscordBot.Exceptions;
using DiscordBot.Games;
using DiscordBot.Games.Managers;
using DiscordBot.Managers;
using DiscordBot.Models;
using DiscordBot.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using static DiscordBot.Models.CoinAccounts;

namespace Tests
{
    public class SlotMachineTests
    {
        private static SlotSymbol[] Line(params SlotSymbol[] symbols) => symbols;

        [Theory]
        [InlineData(SlotSymbol.Seven, 250)]
        [InlineData(SlotSymbol.Star, 120)]
        [InlineData(SlotSymbol.Bell, 60)]
        [InlineData(SlotSymbol.Grape, 30)]
        [InlineData(SlotSymbol.Orange, 20)]
        [InlineData(SlotSymbol.Lemon, 12)]
        [InlineData(SlotSymbol.Cherry, 8)]
        public void ThreeOfAKind_PaysByTheTable(SlotSymbol symbol, double multiple)
        {
            var (paid, jackpot, _) = SlotMachine.Evaluate(Line(symbol, symbol, symbol));
            Assert.Equal(multiple, paid);
            Assert.False(jackpot);
        }

        [Fact]
        public void ThreeDiamonds_AreTheJackpot()
        {
            var (paid, jackpot, label) = SlotMachine.Evaluate(Line(SlotSymbol.Diamond, SlotSymbol.Diamond, SlotSymbol.Diamond));
            Assert.True(jackpot);
            Assert.Equal(SlotMachine.JackpotFloorMultiple, paid);
            Assert.Contains("JACKPOT", label);
        }

        [Fact]
        public void Cherries_PayASmallAmount()
        {
            Assert.Equal(2, SlotMachine.Evaluate(Line(SlotSymbol.Cherry, SlotSymbol.Lemon, SlotSymbol.Cherry)).Multiple);
            Assert.Equal(1, SlotMachine.Evaluate(Line(SlotSymbol.Cherry, SlotSymbol.Lemon, SlotSymbol.Bell)).Multiple);
            Assert.Equal(0, SlotMachine.Evaluate(Line(SlotSymbol.Lemon, SlotSymbol.Cherry, SlotSymbol.Bell)).Multiple); //only the left reel counts alone
            Assert.Equal(0, SlotMachine.Evaluate(Line(SlotSymbol.Lemon, SlotSymbol.Lemon, SlotSymbol.Bell)).Multiple);
        }

        [Fact]
        public void TheHouseWins_ButNotByMuch()
        {
            double rtp = SlotMachine.ReturnToPlayer();
            Assert.InRange(rtp, 0.90, 0.96);
            //even if every jackpot paid the most it can, the machine still doesn't give money away
            Assert.True(rtp + SlotMachine.ExtraReturnAtJackpotCap() < 1);
        }

        [Fact]
        public void EveryReel_HasTheSameSymbolsInTheSameAmounts_ButDifferentOrders()
        {
            var counts = Enumerable.Range(0, 3).Select(reel =>
                Enumerable.Range(0, SlotMachine.StripLength).GroupBy(i => SlotMachine.SymbolAt(reel, i)).OrderBy(g => g.Key).Select(g => g.Count()).ToArray()).ToList();
            Assert.Equal(counts[0], counts[1]);
            Assert.Equal(counts[0], counts[2]);
            Assert.Equal(SlotMachine.StripLength, counts[0].Sum());
            Assert.Equal(8, counts[0].Length);
            Assert.NotEqual(Enumerable.Range(0, 30).Select(i => SlotMachine.SymbolAt(0, i)), Enumerable.Range(0, 30).Select(i => SlotMachine.SymbolAt(1, i)));
        }

        [Fact]
        public void Window_ShowsTheNeighboursAndWrapsRound()
        {
            SlotResult result = SlotMachine.At(new[] { 0, 5, 29 });
            Assert.Equal(SlotMachine.SymbolAt(0, 29), result.Window[0][0]);
            Assert.Equal(SlotMachine.SymbolAt(0, 0), result.Window[0][1]);
            Assert.Equal(SlotMachine.SymbolAt(0, 1), result.Window[0][2]);
            Assert.Equal(SlotMachine.SymbolAt(2, 0), result.Window[2][2]); //past the end of the strip
            Assert.Equal(new[] { SlotMachine.SymbolAt(0, 0), SlotMachine.SymbolAt(1, 5), SlotMachine.SymbolAt(2, 29) }, result.Line);
        }

        [Theory]
        [InlineData(10, 0, 1_000)] //an empty pool still pays the floor
        [InlineData(10, 5_000, 5_000)]
        [InlineData(10, 1e12, 20_000)] //and a small bet can't take a huge pool
        public void JackpotPayout_IsThePoolBetweenTheFloorAndTheCap(double bet, double pool, double expected)
        {
            Assert.Equal(expected, SlotMachine.JackpotPayout(bet, pool));
        }

        //---- the manager, with a real bet system and no Discord ----

        /// <summary>A Random whose next stops are chosen by the test.</summary>
        private sealed class FixedRandom : Random
        {
            private readonly Queue<int> _values;
            public FixedRandom(params int[] values) => _values = new Queue<int>(values);
            public override int Next(int maxValue) => _values.Dequeue();
        }

        private static int[] StopsFor(Func<SlotResult, bool> wanted)
        {
            for (int a = 0; a < SlotMachine.StripLength; a++)
                for (int b = 0; b < SlotMachine.StripLength; b++)
                    for (int c = 0; c < SlotMachine.StripLength; c++)
                        if (wanted(SlotMachine.At(new[] { a, b, c })))
                            return new[] { a, b, c };
            throw new InvalidOperationException("no stops found");
        }

        private static (SlotMachineManager Slots, CoinService Coins, BetManager Bets, CoinAccount Account, SlotSession Session) Setup(double cash = 10_000, double pool = 0, double bet = 1_000)
        {
            var settings = new AppSettings { PrestigeDisabled = true };
            var coins = new CoinService(new LocalFileService(), settings);
            coins.GetAll().Accounts.Clear();
            coins.GetAll().JackpotPool = pool;
            var account = new CoinAccount { UserId = 5, Name = "spinner", NetWorth = cash };
            coins.GetAll().Accounts.Add(account);
            var bets = new BetManager(coins, new StockService(new LocalFileService(), coins), settings);
            var slots = new SlotMachineManager(coins, bets, NullLogger<SlotMachineManager>.Instance) { FrameDelay = TimeSpan.Zero };
            return (slots, coins, bets, account, slots.Open(5, "spinner", bet));
        }

        [Fact]
        public async Task ALosingSpin_TakesTheBetAndFeedsTheJackpot()
        {
            var (slots, coins, bets, account, session) = Setup();
            SlotSpin spin = await slots.Spin(session, new FixedRandom(StopsFor(r => r.Multiple == 0)));

            Assert.Equal(9_000, account.NetWorth, 2);
            Assert.Equal(750, coins.JackpotPool, 2);
            Assert.Equal(-1_000, session.SessionNet, 2);
            Assert.Equal(1, session.Spins);
            Assert.Equal(0, spin.TotalPaid);
            Assert.DoesNotContain(5UL, bets.InitiatedBetUserIds); //no bet left open
        }

        [Fact]
        public async Task AWinningSpin_PaysTheMultiple()
        {
            var (slots, coins, _, account, session) = Setup(bet: 100);
            await slots.Spin(session, new FixedRandom(StopsFor(r => r.Multiple == 8)));
            Assert.Equal(10_700, account.NetWorth, 2);
            Assert.Equal(0, coins.JackpotPool, 2);
        }

        [Fact]
        public async Task ThreeDiamonds_TakeThePool()
        {
            var (slots, coins, _, account, session) = Setup(bet: 100, pool: 50_000);
            SlotSpin spin = await slots.Spin(session, new FixedRandom(StopsFor(r => r.Jackpot)));

            Assert.Equal(0, coins.JackpotPool, 2);
            Assert.Equal(50_000, spin.TotalPaid, 2);
            Assert.Equal(10_000 - 100 + 50_000, account.NetWorth, 2);
        }

        [Fact]
        public async Task ThreeDiamonds_WithAnEmptyPool_StillPayTheFloor()
        {
            var (slots, coins, _, account, session) = Setup(bet: 100, pool: 10);
            await slots.Spin(session, new FixedRandom(StopsFor(r => r.Jackpot)));
            Assert.Equal(0, coins.JackpotPool, 2);
            Assert.Equal(10_000 - 100 + 100 * SlotMachine.JackpotFloorMultiple, account.NetWorth, 2);
        }

        [Fact]
        public async Task ABetCantBeBiggerThanYourCash_AndNoCashIsRefused()
        {
            var (slots, _, _, account, session) = Setup(cash: 50, bet: 1_000);
            await slots.Spin(session, new FixedRandom(StopsFor(r => r.Multiple == 0)));
            Assert.Equal(50, session.Bet);
            Assert.Equal(0, account.NetWorth, 2);

            await Assert.ThrowsAsync<BadInputException>(() => slots.Spin(session, new FixedRandom(0, 0, 0)));
        }

        [Fact]
        public async Task SpinningWhileABetIsOpen_IsRefusedWithoutTakingMoney()
        {
            var (slots, _, bets, account, session) = Setup();
            await bets.InitiateBet(account, 500); //e.g. sitting in a blackjack game
            await Assert.ThrowsAsync<BadInputException>(() => slots.Spin(session, new FixedRandom(0, 0, 0)));
            Assert.Equal(9_500, account.NetWorth, 2);
        }

        [Fact]
        public async Task Play_EditsTheMessageForEveryFrame_AndRefusesASecondSpinAtOnce()
        {
            var (slots, _, _, _, session) = Setup(bet: 10);
            var message = new Mock<IUserMessage>();
            message.Setup(m => m.ModifyAsync(It.IsAny<Action<MessageProperties>>(), It.IsAny<RequestOptions>())).Returns(Task.CompletedTask);

            Assert.True(await slots.Play(message.Object, session));
            message.Verify(m => m.ModifyAsync(It.IsAny<Action<MessageProperties>>(), It.IsAny<RequestOptions>()), Times.Exactly(SlotResult.Reels + 1));

            await session.Gate.WaitAsync();
            Assert.False(await slots.Play(message.Object, session)); //still spinning
            session.Gate.Release();
        }

        [Fact]
        public void Sessions_AreOnePerPlayer_AndCloseWhenIdle()
        {
            var (slots, _, _, _, first) = Setup();
            slots.Attach(100, first);
            Assert.True(slots.TryGet(100, out _));

            var second = slots.Open(5, "spinner", 10);
            slots.Attach(200, second); //their earlier machine closes
            Assert.False(slots.TryGet(100, out _));
            Assert.True(slots.TryGet(200, out _));

            second.LastUsed = DateTime.UtcNow - SlotMachineManager.IdleLimit - TimeSpan.FromMinutes(1);
            Assert.False(slots.TryGet(200, out _));
        }

        [Theory]
        [InlineData(0.5, 1_000, 1)]
        [InlineData(1_000, 10_000, 1_000)]
        [InlineData(50_000, 10_000, 10_000)]
        [InlineData(5, 0.4, 1)]
        public void ClampBet_StaysBetweenOneDollarAndYourCash(double wanted, double cash, double expected)
        {
            Assert.Equal(expected, SlotMachineManager.ClampBet(wanted, cash));
        }

        [Fact]
        public void DefaultBet_IsOnePercentOfCash()
        {
            Assert.Equal(100, SlotMachineManager.DefaultBet(10_000));
            Assert.Equal(1, SlotMachineManager.DefaultBet(30));
        }

        //---- the look and the buttons ----

        [Fact]
        public void Window_IsThreeRows_WithArrowsOnTheMiddleOne()
        {
            string[] rows = SlotView.Window(SlotMachine.At(new[] { 0, 0, 0 }).Window).Split('\n').Select(r => r.TrimEnd('\r')).ToArray();
            Assert.Equal(3, rows.Length);
            Assert.StartsWith("▶️", rows[1]);
            Assert.EndsWith("◀️", rows[1]);
            Assert.DoesNotContain("▶️", rows[0] + rows[2]);
            Assert.Contains("❔", SlotView.Window(null)); //a machine that hasn't spun
        }

        [Fact]
        public void Outcome_SaysWhatHappened()
        {
            SlotResult loss = SlotMachine.At(StopsFor(r => r.Multiple == 0));
            SlotResult win = SlotMachine.At(StopsFor(r => r.Multiple == 8));
            SlotResult back = SlotMachine.At(StopsFor(r => r.Multiple == 1));
            SlotResult jackpot = SlotMachine.At(StopsFor(r => r.Jackpot));

            Assert.Contains("-$100", SlotView.Outcome(loss, 100, 0, 0).Status);
            Assert.Equal(Color.Red, SlotView.Outcome(loss, 100, 0, 0).Colour);
            Assert.Contains("+$700", SlotView.Outcome(win, 100, 800, 0).Status);
            Assert.Equal(Color.Gold, SlotView.Outcome(win, 100, 800, 0).Colour);
            Assert.Contains("bet back", SlotView.Outcome(back, 100, 100, 0).Status);
            Assert.Contains("JACKPOT", SlotView.Outcome(jackpot, 100, 50_000, 0).Status);
            Assert.Equal(Color.Magenta, SlotView.Outcome(jackpot, 100, 50_000, 0).Colour);
        }

        [Fact]
        public void Embed_ShowsTheBetCashAndSession()
        {
            var embed = SlotView.Build(new SlotScreen { Bet = 1_000, Cash = 9_000, Spins = 3, SessionNet = -2_500, Status = "x", Name = "Sam" });
            Assert.Contains("Bet $1,000", embed.Footer.Value.Text);
            Assert.Contains("Cash $9,000", embed.Footer.Value.Text);
            Assert.Contains("-$2,500", embed.Footer.Value.Text);
            Assert.Contains("3 spins", embed.Footer.Value.Text);
            Assert.Contains("x250", embed.Fields.Single().Value);
        }

        [Fact]
        public void Buttons_FitAndCarryTheOwner()
        {
            const ulong owner = ulong.MaxValue;
            var buttons = SlotView.Buttons(owner, 1_000, 5_000).Components.OfType<ActionRowComponent>().SelectMany(r => r.Components).OfType<ButtonComponent>().ToList();

            Assert.Equal(6, buttons.Count);
            Assert.All(buttons, b =>
            {
                Assert.True(b.CustomId.Length <= ComponentId.MaxLength);
                Assert.True(ComponentId.TryParse(b.CustomId, out string game, out _, out string[] args));
                Assert.Equal(MoneyComponents.Slots, game);
                Assert.True(ButtonOwner.TryRead(args, 0, out ulong id) && id == owner);
            });
            Assert.False(buttons.First(b => b.CustomId.Contains(":spin:")).IsDisabled);
        }

        [Fact]
        public void Spin_IsDisabledWhenBroke()
        {
            var buttons = SlotView.Buttons(1, 1, 0.5).Components.OfType<ActionRowComponent>().SelectMany(r => r.Components).OfType<ButtonComponent>();
            Assert.True(buttons.First(b => b.CustomId.Contains(":spin:")).IsDisabled);
        }

        [Theory]
        [InlineData("half", 1_000, 10_000, 500)]
        [InlineData("half", 1, 10_000, 1)]
        [InlineData("double", 6_000, 10_000, 10_000)]
        [InlineData("double", 100, 10_000, 200)]
        [InlineData("max", 100, 7_500.9, 7_500)]
        public void BetButtons_ChangeTheBetWithinYourCash(string action, double bet, double cash, double expected)
        {
            Assert.Equal(expected, SlotView.NextBet(action, bet, cash));
        }
    }
}
