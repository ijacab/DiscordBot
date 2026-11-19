using Common.Services;
using Discord;
using Discord.WebSocket;
using DiscordBot;
using DiscordBot.Exceptions;
using DiscordBot.Games.Managers;
using DiscordBot.Games.Models;
using DiscordBot.Managers;
using DiscordBot.Models;
using DiscordBot.Services;
using Moq;
using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using static DiscordBot.Models.CoinAccounts;

namespace Tests
{
    /// <summary>The buttons on money commands: their ids, who may press them, and the game managers driven without a message.</summary>
    public class MoneyButtonTests
    {
        private const string LongestOwner = "18446744073709551615"; //ulong.MaxValue, 20 digits

        [Fact]
        public void ComponentIds_FitAndRoundTrip()
        {
            string longestPowerUp = PowerUpCatalog.All.Select(p => p.Id).OrderByDescending(id => id.Length).First();
            string[] ids =
            {
                ComponentId.Build(MoneyComponents.Stock, "buy", "LONGSYMBOL"),
                ComponentId.Build(MoneyComponents.Stock, "sellamt", "LONGSYMBOL"),
                ComponentId.Build(MoneyComponents.Stock, "sellpick", LongestOwner),
                ComponentId.Build(MoneyComponents.PowerUp, "use", LongestOwner, "10", longestPowerUp),
                ComponentId.Build(MoneyComponents.Property, "sell", LongestOwner),
                ComponentId.Build(MoneyComponents.Debt, "autosell", LongestOwner, "off"),
                ComponentId.Build("bj", "joinamt"),
            };
            foreach (string id in ids)
            {
                Assert.True(id.Length <= ComponentId.MaxLength);
                Assert.True(ComponentId.TryParse(id, out _, out _, out _));
            }

            Assert.True(ComponentId.TryParse(ids[3], out string game, out string action, out string[] args));
            Assert.Equal((MoneyComponents.PowerUp, "use"), (game, action));
            Assert.Equal(new[] { LongestOwner, "10", longestPowerUp }, args);
        }

        [Fact]
        public void OwnerOnlyButtons_OnlyWorkForTheirOwner()
        {
            Assert.True(ButtonOwner.Allows(5, 5));
            Assert.False(ButtonOwner.Allows(5, 6));

            Assert.True(ButtonOwner.TryRead(new[] { LongestOwner }, 0, out ulong owner));
            Assert.Equal(ulong.MaxValue, owner);
            Assert.False(ButtonOwner.TryRead(Array.Empty<string>(), 0, out _));
            Assert.False(ButtonOwner.TryRead(new[] { "nope" }, 0, out _));
        }

        [Fact]
        public void FindHeld_FollowsAPowerUpWhenTheListMoves()
        {
            var account = new CoinAccount();
            foreach (var id in new[] { PowerUpIds.Shield, PowerUpIds.HotStreak, PowerUpIds.SafetyNet })
                account.HeldPowerUps.Add(new HeldPowerUp { Id = id, ExpiresAtTicks = DateTime.MaxValue.Ticks });

            Assert.Equal(2, account.FindHeld(2, PowerUpIds.HotStreak)); //still where it was
            account.HeldPowerUps.RemoveAt(0); //the first one was used
            Assert.Equal(1, account.FindHeld(2, PowerUpIds.HotStreak)); //moved up one
            Assert.Equal(0, account.FindHeld(1, PowerUpIds.Shield)); //gone
            Assert.Equal(0, account.FindHeld(9, PowerUpIds.GoldenTicket));
        }

        [Fact]
        public async Task SellById_RefusesAHouseThatIsntTheirs()
        {
            var coins = new CoinService(new LocalFileService(), new AppSettings { PrestigeDisabled = true });
            var property = new PropertyService(coins);
            var account = new CoinAccount { UserId = 910_001, Name = "owner" };
            var house = new PropertyHolding { Kind = PropertyKind.House, PurchaseCost = 1 };
            account.Properties.Add(house);

            await Assert.ThrowsAsync<BadInputException>(() => property.SellById(account, "someone-elses"));
            Assert.Single(account.Properties);
        }

        [Fact]
        public async Task Blackjack_JoinsFromAButtonContextAndOffersButtons()
        {
            var coins = new CoinService(new LocalFileService(), new AppSettings { PrestigeDisabled = true });
            var stocks = new StockService(new LocalFileService(), coins);
            var bets = new BetManager(coins, stocks, new AppSettings { PrestigeDisabled = true });
            var blackjack = new BlackjackManager(new DiscordSocketClient(), bets, coins);
            foreach (ulong id in new ulong[] { 920_001, 920_002 })
                coins.GetAll().Accounts.Add(new CoinAccount { UserId = id, Name = $"p{id}", NetWorth = 10_000 });

            var channel = new Mock<IMessageChannel>();
            channel.SetupGet(c => c.Id).Returns(42);
            var table = new Mock<IUserMessage>();
            channel.Setup(c => c.SendMessageAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<Embed>(), It.IsAny<RequestOptions>(), It.IsAny<AllowedMentions>(),
                    It.IsAny<MessageReference>(), It.IsAny<MessageComponent>(), It.IsAny<ISticker[]>(), It.IsAny<Embed[]>(), It.IsAny<MessageFlags>(), It.IsAny<PollProperties>()))
                .ReturnsAsync(table.Object);
            GameContext Context(ulong id) => new GameContext { UserId = id, Username = $"p{id}", Channel = channel.Object, GuildId = 7 };

            try
            {
                await blackjack.CreateOrJoin(920_001, 1_000, Context(920_001));
                await blackjack.CreateOrJoin(920_002, 1_000, Context(920_002));

                Assert.True(blackjack.TryGetPlayer(920_002, out var player));
                Assert.Equal(42UL, player.ChannelId);
                Assert.Equal(7UL, player.ServerId);
                Assert.Single(blackjack.Games);

                //one table message, posted with Join… and Start, then edited when the second player sat down
                var sends = channel.Invocations.Where(i => i.Method.Name == nameof(IMessageChannel.SendMessageAsync)).ToList();
                var send = Assert.Single(sends);
                var components = send.Arguments.OfType<MessageComponent>().Single();
                var ids = components.Components.OfType<ActionRowComponent>().SelectMany(row => row.Components).OfType<ButtonComponent>().Select(b => b.CustomId).ToList();
                Assert.Equal(new[] { "bj:join", "bj:joinmax", "bj:start" }, ids);
                Assert.Single(table.Invocations, i => i.Method.Name == nameof(IUserMessage.ModifyAsync));

                //a second join and a hit before the start are refused
                await Assert.ThrowsAsync<BadInputException>(() => blackjack.CreateOrJoin(920_001, 1_000, Context(920_001)));
                await Assert.ThrowsAsync<BadInputException>(() => blackjack.Hit(920_001, Context(920_001)));

                //a server channel is needed
                await Assert.ThrowsAsync<BadInputException>(() => blackjack.CreateOrJoin(920_003, 1_000, new GameContext { UserId = 920_003, Username = "dm", Channel = channel.Object, GuildId = 0 }));
            }
            finally
            {
                bets.InitiatedBetUserIds.Remove(920_001);
                bets.InitiatedBetUserIds.Remove(920_002);
            }
        }

        [Fact]
        public async Task Blackjack_PlayAgainUsesTheLastBetRaisedToTheMinimumAndHeldToYourCash()
        {
            var coins = new CoinService(new LocalFileService(), new AppSettings { PrestigeDisabled = true });
            var bets = new BetManager(coins, new StockService(new LocalFileService(), coins), new AppSettings { PrestigeDisabled = true });
            var blackjack = new BlackjackManager(new DiscordSocketClient(), bets, coins);
            const ulong id = 920_301;
            var account = new CoinAccount { UserId = id, Name = "again", NetWorth = 10_000 };
            coins.GetAll().Accounts.RemoveAll(a => a.UserId == id); //a save left by an earlier run would be found first
            coins.GetAll().Accounts.Add(account);

            var channel = new Mock<IMessageChannel>();
            channel.SetupGet(c => c.Id).Returns(43);
            var table = new Mock<IUserMessage>();
            channel.Setup(c => c.SendMessageAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<Embed>(), It.IsAny<RequestOptions>(), It.IsAny<AllowedMentions>(),
                    It.IsAny<MessageReference>(), It.IsAny<MessageComponent>(), It.IsAny<ISticker[]>(), It.IsAny<Embed[]>(), It.IsAny<MessageFlags>(), It.IsAny<PollProperties>()))
                .ReturnsAsync(table.Object);
            var context = new GameContext { UserId = id, Username = "again", Channel = channel.Object, GuildId = 7 };

            Assert.Null(blackjack.PlayAgainBet(id, 10_000)); //no finished game yet

            try
            {
                await blackjack.CreateOrJoin(id, 1_000, context);
                await blackjack.Start(id, context);
                if (blackjack.TryGetPlayer(id, out var player) && !player.IsFinishedPlaying)
                    await blackjack.Stay(id, context);

                Assert.True(blackjack.TryGetLastBet(id, out double last));
                Assert.Equal(1_000, last);
                Assert.Equal(1_000, blackjack.PlayAgainBet(id, 5_000)); //the same bet
                Assert.Equal(3_000, blackjack.PlayAgainBet(id, 30_000)); //raised to 10% of a bigger bank
                Assert.Equal(400, blackjack.PlayAgainBet(id, 400.9)); //held to what you have
                Assert.Null(blackjack.PlayAgainBet(id, 0.5));
            }
            finally
            {
                bets.InitiatedBetUserIds.Remove(id);
            }
        }

        [Fact]
        public void Blackjack_HasHitAndStayButtons()
        {
            var coins = new CoinService(new LocalFileService(), new AppSettings { PrestigeDisabled = true });
            var blackjack = new BlackjackManager(new DiscordSocketClient(), new BetManager(coins, new StockService(new LocalFileService(), coins), new AppSettings()), coins);
            var ids = blackjack.PlayButtons.Components.OfType<ActionRowComponent>().SelectMany(r => r.Components).OfType<ButtonComponent>().Select(b => b.CustomId);
            Assert.Equal(new[] { "bj:hit", "bj:stay" }, ids);
        }
    }
}
