using Common.Helpers;
using Discord;
using DiscordBot.Games.Models.Dungeon;
using DiscordBot.Models;
using DiscordBot.Services;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static DiscordBot.Models.CoinAccounts;

namespace DiscordBot.Games.Managers
{
    /// <summary>
    /// Runs the one dungeon session open at a time, across all channels, on one dashboard message with buttons.
    /// The rules live in <see cref="DungeonSession"/>. This class takes and pays money, ticks the session every
    /// <see cref="TickInterval"/> (each tick moves every crawling run one floor, which animates the dashboard), and edits
    /// the dashboard in place through <see cref="LiveMessage"/>. Every action returns a refusal, or null when it worked:
    /// buttons show refusals privately, and typed commands post them briefly in the channel.
    /// The session is in memory, so a bot restart ends it.
    /// </summary>
    public class DungeonManager
    {
        public const string ComponentGame = "dng";
        public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1.5);
        /// <summary>Clicks publish straight away unless the dashboard was edited this recently; the next tick catches up.</summary>
        private static readonly TimeSpan MinPublishGap = TimeSpan.FromSeconds(1);
        private const string NoRunMessage = "No dungeon is open. Open a lobby with `.dng 1000`, where 1000 is the buy-in for each player.";

        private readonly CoinService _coinService;
        private readonly ILogger<DungeonManager> _logger;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly Random _random = new Random();

        private DungeonSession _session;
        private LiveMessage _dashboard;
        private CancellationTokenSource _loop;
        private DateTime _lastPublish = DateTime.MinValue;

        public DungeonManager(CoinService coinService, ILogger<DungeonManager> logger)
        {
            _coinService = coinService;
            _logger = logger;
        }

        /// <summary>True when the message is the live dashboard. Buttons on any other message belong to a finished dungeon.</summary>
        public bool IsDashboard(ulong messageId) => _dashboard != null && _dashboard.Owns(messageId);

        /// <summary>Opens the lobby with the host's buy-in and posts the dashboard. Refused while another session is open.</summary>
        public async Task<string> Open(CoinAccount host, double buyIn, IMessageChannel channel)
        {
            await _gate.WaitAsync();
            try
            {
                if (_session != null)
                    return $"A dungeon is already {(_session.Started ? "running" : "in the lobby")} in <#{_dashboard.Channel.Id}>. Join it there, or wait for it to end.";
                if (buyIn < DungeonSession.MinBuyIn)
                    return $"The dungeon needs at least ${FormatHelper.GetCommaNumber(DungeonSession.MinBuyIn)} to enter.";
                if (buyIn > host.NetWorth)
                    return $"You only have ${FormatHelper.GetCommaNumber(host.NetWorth)}.";

                host.NetWorth -= buyIn;
                await _coinService.Update(host.UserId, host.NetWorth, host.Name);

                _session = new DungeonSession(host.UserId, host.Name, buyIn, DateTime.UtcNow, _random);
                _dashboard = new LiveMessage(channel);
                await Publish(force: true);
                StartLoop();
                return null;
            }
            finally
            {
                _gate.Release();
            }
        }

        public Task<string> Join(CoinAccount account)
        {
            return Act(async session =>
            {
                string refusal = session.Join(account.UserId, account.Name, account.NetWorth, DateTime.UtcNow);
                if (refusal == null)
                {
                    account.NetWorth -= session.BuyIn;
                    await _coinService.Update(account.UserId, account.NetWorth, account.Name);
                }
                return refusal;
            });
        }

        public Task<string> Leave(ulong userId) => Act(session => Task.FromResult(session.Leave(userId, DateTime.UtcNow)));
        public Task<string> Start(ulong userId) => Act(session => Task.FromResult(session.Start(userId, DateTime.UtcNow)));
        public Task<string> Go(ulong userId) => Act(session => Task.FromResult(session.Go(userId, DateTime.UtcNow)));
        public Task<string> Cash(ulong userId) => Act(session => Task.FromResult(session.Cash(userId, DateTime.UtcNow)));
        public Task<string> EnterPool(ulong userId) => Act(session => Task.FromResult(session.EnterPool(userId, DateTime.UtcNow)));
        public Task<string> Boss(ulong userId, BossChoice choice) => Act(session => Task.FromResult(session.Boss(userId, choice, DateTime.UtcNow)));
        public Task<string> Potion(ulong userId) => Act(session => Task.FromResult(session.Potion(userId, DateTime.UtcNow)));

        /// <summary>Moves the dashboard to the bottom of its channel. Typed from another channel, it says where the dungeon is.</summary>
        public async Task<string> Status(IMessageChannel channel)
        {
            await _gate.WaitAsync();
            try
            {
                if (_session == null)
                    return NoRunMessage;
                if (_dashboard.Channel.Id != channel.Id)
                    return $"The dungeon is in <#{_dashboard.Channel.Id}>.";
                await Publish(force: true, bump: true);
                return null;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>A private summary of the player's run: stats, items and potions.</summary>
        public async Task<string> DescribeRun(ulong userId)
        {
            await _gate.WaitAsync();
            try
            {
                DungeonRun run = _session?.RunOf(userId);
                if (run == null)
                    return _session == null ? NoRunMessage : "You're not in this dungeon.";

                var text = new StringBuilder();
                text.AppendLine($"**{run.Name}**: {Describe(_session.StateOf(run))}. Block {run.NextBlockNumber}, floor {run.Floor}.");
                text.AppendLine($"HP {run.Hp}/{run.MaxHp} · Attack {run.Attack} · Defense {run.Defense} · Kills {run.Kills}");
                text.AppendLine($"Haul ${run.Haul:N2} · Buy-in ${run.BuyIn:N2} · Potions {run.Potions}");
                if (run.GoldFinderPercent > 0)
                    text.AppendLine($"Gold finder +{run.GoldFinderPercent}%");
                if (run.Items.Count > 0)
                {
                    text.AppendLine("**Loot**");
                    foreach (LootItem item in run.Items.TakeLast(10))
                        text.AppendLine($"{item.Name} ({item.Tier}): {string.Join(", ", item.Effects.Select(e => e.Describe()))}");
                }
                text.AppendLine($"About half of runs clear a block. Sneaking past a boss works {DungeonRun.SneakChance:P0} of the time; a potion heals {DungeonRun.PotionHealShare:P0} of max HP.");
                return text.ToString();
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Runs one action on the open session under the lock, pays what it owes, and refreshes the dashboard.</summary>
        private async Task<string> Act(Func<DungeonSession, Task<string>> action)
        {
            await _gate.WaitAsync();
            try
            {
                if (_session == null)
                    return NoRunMessage;
                string refusal = await action(_session);
                await SettleAndPublish(fromClick: true);
                return refusal;
            }
            finally
            {
                _gate.Release();
            }
        }

        private void StartLoop()
        {
            _loop?.Cancel();
            var cts = new CancellationTokenSource();
            _loop = cts;
            _ = Task.Run(() => RunLoop(cts.Token));
        }

        private async Task RunLoop(CancellationToken token)
        {
            using var timer = new PeriodicTimer(TickInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(token))
                {
                    await _gate.WaitAsync(token);
                    try
                    {
                        if (_session == null)
                            return;
                        _session.Tick(DateTime.UtcNow);
                        await SettleAndPublish(fromClick: false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Dungeon tick failed: {Message}", ex.Message);
                    }
                    finally
                    {
                        _gate.Release();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                //the session ended
            }
        }

        /// <summary>Pays out what the session owes, publishes if it changed, and closes the session once it has ended.</summary>
        private async Task SettleAndPublish(bool fromClick)
        {
            foreach (DungeonPayout payout in _session.TakePayouts())
            {
                CoinAccount account = await _coinService.Get(payout.UserId, payout.Name);
                account.NetWorth += payout.Amount;
                await _coinService.Update(payout.UserId, account.NetWorth, payout.Name);
            }

            if (_session.Ended)
            {
                await Publish(force: true);
                _session = null;
                _dashboard = null;
                _loop?.Cancel();
                _loop = null;
                return;
            }

            if (_session.Changed && (!fromClick || DateTime.UtcNow - _lastPublish >= MinPublishGap))
                await Publish(force: false);
        }

        private async Task Publish(bool force, bool bump = false)
        {
            DungeonSession session = _session;
            if (session == null || (!force && !session.Changed))
                return;

            LiveMessage.Frame frame = _dashboard.NewFrame();
            string sceneUrl = frame.Add("dungeon", RenderScene(session));
            var embeds = new List<Embed> { BuildDashboard(session).WithImageUrl(sceneUrl).Build() };
            if (session.Drop != null && !session.Ended)
            {
                string lootUrl = frame.Add("loot", LootCardRenderer.Render(session.Drop).ToArray());
                embeds.Add(new EmbedBuilder()
                    .WithTitle($"{session.DropOwner} finds {session.Drop.Name}")
                    .WithColor(TierColor(session.Drop.Tier))
                    .WithImageUrl(lootUrl)
                    .Build());
            }

            MessageComponent components = session.Ended ? new ComponentBuilder().Build() : BuildButtons(session);
            bool published = bump
                ? await _dashboard.BumpAsync(frame, embeds.ToArray(), components)
                : await _dashboard.PublishAsync(frame, embeds.ToArray(), components);
            if (published)
            {
                session.MarkPublished();
                _lastPublish = DateTime.UtcNow;
            }
        }

        private static byte[] RenderScene(DungeonSession session)
        {
            bool atDoor = session.Runs.Any(r => r.Phase == RunPhase.AtBossDoor);
            var scene = new DungeonScene
            {
                Title = session.Ended ? "DUNGEON OVER" : session.Started ? "CRAWL" : "LOBBY",
                Caption = session.Started ? session.Caption : "DUNGEON DOOR",
                Sprite = session.Started ? session.Sprite : SpriteKind.None,
                EnemyHp = session.EnemyHp,
                EnemyMaxHp = session.EnemyMaxHp,
                Footer = session.Ended ? "THANKS FOR PLAYING"
                    : !session.Started ? $"BUY-IN ${session.BuyIn:N0}"
                    : atDoor ? "FIGHT, SNEAK OR DRINK A POTION"
                    : "PRESS GO TO CRAWL"
            };
            foreach (DungeonRun run in session.Runs)
            {
                scene.Players.Add(new ScenePlayer
                {
                    Name = run.Name,
                    Hp = run.Hp,
                    MaxHp = run.MaxHp,
                    Floor = run.Floor,
                    Haul = run.Haul,
                    Status = session.StateOf(run),
                    FloorInBlock = session.Started ? FloorsDoneInBlock(run) : -1,
                    AtBossDoor = run.Phase == RunPhase.AtBossDoor,
                    Potions = run.Potions
                });
            }
            scene.Players.AddRange(session.Departed);
            return DungeonSceneRenderer.Render(scene).ToArray();
        }

        private static int FloorsDoneInBlock(DungeonRun run)
        {
            if (run.Phase == RunPhase.AtCheckpoint || run.Phase == RunPhase.InPool)
                return DungeonRun.FloorsPerBlock;
            return run.Floor % DungeonRun.FloorsPerBlock;
        }

        private EmbedBuilder BuildDashboard(DungeonSession session)
        {
            string title = session.Ended ? "Dungeon over"
                : session.Started ? $"Dungeon crawl: {session.Runs.Count} still in"
                : $"Dungeon lobby: {session.Runs.Count}/{DungeonSession.MaxPlayers} players";

            var embed = new EmbedBuilder()
                .WithTitle(title)
                .WithColor(session.Ended ? Color.LightGrey : Color.DarkPurple);

            if (!session.Started && !session.Ended)
            {
                long startsAt = new DateTimeOffset(session.OpenedUtc + DungeonSession.LobbyWait).ToUnixTimeSeconds();
                embed.WithDescription($"Press **Join** to pay the ${session.BuyIn:N2} buy-in. The crawl starts <t:{startsAt}:R>, or when the host presses **Start**.");
            }
            if (session.ResultText != null)
                embed.AddField("Result", session.ResultText);

            var players = new StringBuilder();
            foreach (DungeonRun run in session.Runs)
            {
                players.Append($"**{run.Name}** · block {run.NextBlockNumber} · floor {run.Floor} · HP {run.Hp}/{run.MaxHp} · ${run.Haul:N2}");
                if (run.Potions > 0)
                    players.Append($" · 🧪{run.Potions}");
                string state = session.StateOf(run);
                if (state.Length > 0)
                    players.Append($" · {Describe(state)}");
                DateTime? deadline = session.BossDoorDeadline(run.UserId);
                if (deadline.HasValue)
                    players.Append($" (boss attacks <t:{new DateTimeOffset(deadline.Value).ToUnixTimeSeconds()}:R>)");
                players.AppendLine();
            }
            foreach (ScenePlayer player in session.Departed)
                players.AppendLine($"**{player.Name}** · {Describe(player.Status)}");
            if (players.Length == 0)
                players.Append("No one yet.");
            embed.AddField("Players", Trim(players.ToString(), 1000));

            if (session.Log.Count > 0)
                embed.AddField("Log", Trim(string.Join("\n", session.Log), 1000));

            embed.WithFooter(session.Ended ? "Session finished" : "Each button acts on your own run. 📜 My run shows your stats privately.");
            return embed;
        }

        private static MessageComponent BuildButtons(DungeonSession session)
        {
            var builder = new ComponentBuilder();
            if (!session.Started)
            {
                builder.WithButton($"Join (${session.BuyIn:N0})", Id("join"), ButtonStyle.Success, new Emoji("🚪"), row: 0);
                builder.WithButton("Start", Id("start"), ButtonStyle.Primary, new Emoji("▶️"), row: 0);
                builder.WithButton("Leave", Id("leave"), ButtonStyle.Secondary, new Emoji("↩️"), row: 0);
                return builder.Build();
            }

            var runs = session.Runs;
            bool anyReady = runs.Any(r => r.Phase == RunPhase.Crawling && !session.IsDescending(r.UserId));
            bool anyAtCheckpoint = runs.Any(r => r.Phase == RunPhase.AtCheckpoint);
            bool anyAtDoor = runs.Any(r => r.Phase == RunPhase.AtBossDoor);
            bool anyCanCash = runs.Any(r => r.Phase != RunPhase.InPool);
            bool anyPotions = runs.Any(r => r.Potions > 0 && r.Phase != RunPhase.InPool);

            builder.WithButton("Go", Id("go"), ButtonStyle.Primary, new Emoji("⛏️"), disabled: !anyReady, row: 0);
            builder.WithButton("Cash out", Id("cash"), ButtonStyle.Success, new Emoji("💰"), disabled: !anyCanCash, row: 0);
            builder.WithButton("Enter PvP", Id("pool"), ButtonStyle.Danger, new Emoji("⚔️"), disabled: !anyAtCheckpoint, row: 0);
            builder.WithButton("Fight boss", Id("boss"), ButtonStyle.Danger, new Emoji("👹"), disabled: !anyAtDoor, row: 1);
            builder.WithButton("Sneak", Id("sneak"), ButtonStyle.Secondary, new Emoji("🥷"), disabled: !anyAtDoor, row: 1);
            builder.WithButton("Potion", Id("potion"), ButtonStyle.Secondary, new Emoji("🧪"), disabled: !anyPotions, row: 1);
            builder.WithButton("My run", Id("me"), ButtonStyle.Secondary, new Emoji("📜"), row: 1);
            return builder.Build();
        }

        private static string Id(string action) => ComponentId.Build(ComponentGame, action);

        private static string Describe(string state)
        {
            switch (state)
            {
                case "BOSS DOOR": return "👹 at a boss door";
                case "CHECKPOINT": return "🏁 at a checkpoint";
                case "IN POOL": return "⚔️ in the PvP pool";
                case "CRAWLING": return "⛏️ crawling";
                case "WAITING": return "waiting to Go";
                case "READY": return "ready";
                case "CASHED OUT": return "💰 cashed out";
                case "IDLE CASH OUT": return "💤 cashed out while idle";
                case "LOST PVP": return "💀 lost the PvP pool";
                case "DOWN": return "💀 went down";
                default: return state.ToLowerInvariant();
            }
        }

        private static Color TierColor(LootTier tier)
        {
            switch (tier)
            {
                case LootTier.Epic: return Color.Purple;
                case LootTier.Rare: return Color.Blue;
                default: return Color.LightGrey;
            }
        }

        private static string Trim(string text, int max)
        {
            return text.Length <= max ? text : "…" + text.Substring(text.Length - max + 1);
        }
    }
}
