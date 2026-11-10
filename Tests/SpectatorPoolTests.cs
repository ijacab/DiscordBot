using DiscordBot.Games.Models.BattleArena;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Tests
{
    public class SpectatorPoolTests
    {
        private static readonly ulong[] Fighters = { 1, 2 };

        [Fact]
        public void Payouts_AreSharedByStake_AmongBackersOfTheWinner()
        {
            var pool = new SpectatorPool();
            Assert.Null(pool.Back(10, "A", 1, 300, Fighters));
            Assert.Null(pool.Back(11, "B", 1, 100, Fighters));
            Assert.Null(pool.Back(12, "C", 2, 600, Fighters));

            Dictionary<ulong, double> payouts = pool.Settle(new ulong[] { 1 });

            Assert.Equal(750, payouts[10]);
            Assert.Equal(250, payouts[11]);
            Assert.False(payouts.ContainsKey(12));
            Assert.Equal(pool.Total, payouts.Values.Sum(), 2);
        }

        [Fact]
        public void NobodyBackedTheWinner_EveryoneGetsTheirStakeBack()
        {
            var pool = new SpectatorPool();
            pool.Back(10, "A", 2, 300, Fighters);
            pool.Back(11, "B", 2, 100, Fighters);

            Dictionary<ulong, double> payouts = pool.Settle(new ulong[] { 1 });

            Assert.Equal(300, payouts[10]);
            Assert.Equal(100, payouts[11]);
        }

        [Fact]
        public void TiedWinners_BothCountAsWinningBackings()
        {
            var pool = new SpectatorPool();
            pool.Back(10, "A", 1, 100, Fighters);
            pool.Back(11, "B", 2, 100, Fighters);

            Dictionary<ulong, double> payouts = pool.Settle(new ulong[] { 1, 2 });

            Assert.Equal(100, payouts[10]);
            Assert.Equal(100, payouts[11]);
        }

        [Fact]
        public void Rounding_NeverLosesOrMakesACent()
        {
            var pool = new SpectatorPool();
            pool.Back(10, "A", 1, 1, Fighters);
            pool.Back(11, "B", 1, 1, Fighters);
            pool.Back(12, "C", 1, 1, Fighters);
            pool.Back(13, "D", 2, 0.01 + 1, Fighters);

            Dictionary<ulong, double> payouts = pool.Settle(new ulong[] { 1 });

            Assert.Equal(pool.Total, payouts.Values.Sum(), 2);
        }

        [Fact]
        public void BettingCloses()
        {
            var pool = new SpectatorPool();
            pool.Close();

            Assert.NotNull(pool.Back(10, "A", 1, 100, Fighters));
        }

        [Fact]
        public void FightersCantBack_AndBackersCantSwitchSides()
        {
            var pool = new SpectatorPool();

            Assert.NotNull(pool.Back(1, "Fighter", 2, 100, Fighters));
            Assert.Null(pool.Back(10, "A", 1, 100, Fighters));
            Assert.NotNull(pool.Back(10, "A", 2, 100, Fighters));
            Assert.Null(pool.Back(10, "A", 1, 50, Fighters));
            Assert.Equal(150, pool.TotalOn(1));
            Assert.NotNull(pool.Back(11, "B", 99, 100, Fighters));
            Assert.NotNull(pool.Back(11, "B", 1, 0.5, Fighters));
        }

        [Fact]
        public void PayoutMultiple_IsTheWholePoolOverTheFightersBacking()
        {
            var pool = new SpectatorPool();
            pool.Back(10, "A", 1, 100, Fighters);
            pool.Back(11, "B", 2, 300, Fighters);

            Assert.Equal(4, pool.PayoutMultiple(1));
            Assert.Equal(4.0 / 3, pool.PayoutMultiple(2).Value, 3);
        }

        [Fact]
        public void RemoveFighter_ReturnsTheirBackingsForRefund()
        {
            var pool = new SpectatorPool();
            pool.Back(10, "A", 1, 100, Fighters);
            pool.Back(11, "B", 2, 300, Fighters);

            var refunds = pool.RemoveFighter(1);

            Assert.Equal(10UL, refunds.Single().BackerId);
            Assert.Equal(300, pool.Total);
        }
    }
}
