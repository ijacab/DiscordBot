using Common.Helpers;
using Discord;
using DiscordBot.Exceptions;
using DiscordBot.Games.Models.Dungeon;
using DiscordBot.Services;
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
    /// One session at a time, across all channels. Players join a lobby, then each crawls their own run in blocks of 10 floors.
    /// After each block a run waits at a checkpoint to cash out or fight. Fighters wait in a pool until every run in the
    /// session has decided, then a winner-takes-all PvP runs. Winners crawl on. A run that stalls for 15 minutes is cashed out.
    /// The whole session is shown on one dashboard message, reposted with its picture after each action.
    /// The session is in memory, so a bot restart ends it.
    /// </summary>
    public class DungeonManager
    {
        public const double MinBuyIn = 100;
        public const int MaxPlayers = 4;
        public const int LogLines = 8;
        public static readonly TimeSpan LobbyWait = TimeSpan.FromSeconds(60);
        public static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan TransientLifetime = TimeSpan.FromSeconds(15);
        private const string NoRunMessage = "No dungeon is open. Open a lobby with `.dng 1000`, where 1000 is the buy-in for each player.";

        private sealed class Session
        {
            public readonly Dictionary<ulong, DungeonRun> Runs = new Dictionary<ulong, DungeonRun>();
            /// <summary>When each player last acted. Runs that stall too long are cashed out.</summary>
            public readonly Dictionary<ulong, DateTime> LastAction = new Dictionary<ulong, DateTime>();
            /// <summary>Players who cashed out, went down, or lost a pool. Shown on the dashboard.</summary>
            public readonly List<ScenePlayer> Departed = new List<ScenePlayer>();
            public readonly List<string> Log = new List<string>();
            public IMessageChannel Channel;
            public IUserMessage Dashboard;
            public ulong HostId;
            public bool Started;
            public int Generation;
            public DateTime OpenedUtc;
            public double BuyIn;
            public SpriteKind Sprite;
            public string Caption;
            public int? EnemyHp;
            public int? EnemyMaxHp;
            /// <summary>A loot card shown for one refresh after a drop.</summary>
            public byte[] PictureOverride;
        }

        private readonly CoinService _coinService;
        /// <summary>The only session open at a time, or null when none is open.</summary>
        private Session _session;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly Random _random = new Random();
        private int _generationCounter;

        public DungeonManager(CoinService coinService)
        {
            _coinService = coinService;
        }

        /// <summary>Opens the lobby and posts the dashboard. Refused while another session is open.</summary>
        public Task Open(CoinAccount host, double buyIn, IMessageChannel channel)
        {
            return Attempt(host.UserId, host.Name, channel, async () =>
            {
                if (_session != null)
                    throw new BadInputException($"A dungeon is already {StateOf(_session)} in <#{_session.Channel.Id}>. Join it with `.dng join`, or wait for it to end.");
                if (buyIn < MinBuyIn)
                    throw new BadInputException($"The dungeon needs at least ${FormatHelper.GetCommaNumber(MinBuyIn)} to enter.");
                if (buyIn > host.NetWorth)
                    throw new BadInputException($"You only have ${FormatHelper.GetCommaNumber(host.NetWorth)}.");

                host.NetWorth -= buyIn;
                await _coinService.Update(host.UserId, host.NetWorth, host.Name);

                _session = new Session
                {
                    Channel = channel,
                    HostId = host.UserId,
                    Generation = ++_generationCounter,
                    OpenedUtc = DateTime.UtcNow,
                    BuyIn = buyIn
                };
                _session.Runs[host.UserId] = new DungeonRun(host.UserId, host.Name, buyIn, _random);
                _session.LastAction[host.UserId] = DateTime.UtcNow;
                _ = AutoStartLater(_session.Generation);
                return $"{host.Name} opens a lobby with a ${FormatHelper.GetCommaNumber(buyIn)} buy-in.";
            });
        }

        public Task Join(CoinAccount account, IMessageChannel channel)
        {
            return Attempt(account.UserId, account.Name, channel, async () =>
            {
                if (_session == null)
                    throw new BadInputException("No dungeon is open. Open one with `.dng 1000`.");
                if (_session.Started)
                    throw new BadInputException("The crawl has already started.");
                if (_session.Runs.ContainsKey(account.UserId))
                    throw new BadInputException("You're already in this dungeon.");
                if (_session.Runs.Count >= MaxPlayers)
                    throw new BadInputException($"The lobby is full at {MaxPlayers} players.");

                double buyIn = _session.BuyIn;
                if (buyIn > account.NetWorth)
                    throw new BadInputException($"The buy-in is ${FormatHelper.GetCommaNumber(buyIn)} and you only have ${FormatHelper.GetCommaNumber(account.NetWorth)}.");

                account.NetWorth -= buyIn;
                await _coinService.Update(account.UserId, account.NetWorth, account.Name);
                _session.Runs[account.UserId] = new DungeonRun(account.UserId, account.Name, buyIn, _random);
                _session.LastAction[account.UserId] = DateTime.UtcNow;
                return $"{account.Name} joins and pays the ${FormatHelper.GetCommaNumber(buyIn)} buy-in.";
            });
        }

        public Task Start(ulong userId, string name, IMessageChannel channel)
        {
            return Attempt(userId, name, channel, () =>
            {
                if (_session == null)
                    throw new BadInputException(NoRunMessage);
                if (_session.HostId != userId)
                    throw new BadInputException("Only the host can start the lobby early. Otherwise it starts on its own.");
                if (_session.Started)
                    return Task.FromResult<string>(null);
                return Task.FromResult(BeginCrawl(_session));
            });
        }

        /// <summary>Crawls the next block of 10 floors. The run then waits at a checkpoint.</summary>
        public Task Go(ulong userId, string name, IMessageChannel channel)
        {
            return Attempt(userId, name, channel, async () =>
            {
                DungeonRun run = RunOf(userId) ?? throw new BadInputException(NotInDungeonText());
                if (!_session.Started)
                    throw new BadInputException("The lobby is still open. Wait for it to start, or the host types `.dng start`.");
                if (run.Phase == RunPhase.AtCheckpoint)
                    throw new BadInputException("You're at a checkpoint. Use `.dng cash` to bank your haul, or `.dng fight` to enter the PvP pool.");
                if (run.Phase == RunPhase.InPool)
                    throw new BadInputException("You're waiting in the PvP pool for the others to decide.");

                int blockNumber = run.NextBlockNumber;
                List<RoomResult> rooms = run.RunBlock();
                RoomResult last = rooms[rooms.Count - 1];
                _session.Sprite = last.Enemy;
                _session.Caption = last.EnemyName ?? RoomLabel(last.Type);
                _session.EnemyHp = last.EnemyHp;
                _session.EnemyMaxHp = last.EnemyMaxHp;

                RoomResult drop = rooms.LastOrDefault(r => r.Drop != null);
                if (drop != null)
                    _session.PictureOverride = LootCardRenderer.Render(drop.Drop).ToArray();

                foreach (RoomResult room in rooms)
                    foreach (string line in room.Lines)
                        AddLog($"{run.Name}: {line}");

                if (!run.Alive)
                {
                    _session.Runs.Remove(userId);
                    _session.LastAction.Remove(userId);
                    _session.Departed.Add(new ScenePlayer { Name = run.Name, Floor = run.Floor, Hp = 0, MaxHp = run.MaxHp, Haul = 0, Status = "DOWN" });
                    AddLog($"{run.Name}'s haul of ${FormatHelper.GetCommaNumber(run.Haul)} and buy-in are lost.");
                    await TryResolvePool();
                    return null;
                }

                return $"{run.Name} clears block {blockNumber}. Use `.dng cash` to bank ${run.Haul:N2}, or `.dng fight` to enter the PvP pool.";
            });
        }

        /// <summary>Banks your haul and leaves. Allowed while crawling or at a checkpoint.</summary>
        public Task Cash(ulong userId, string name, IMessageChannel channel)
        {
            return Attempt(userId, name, channel, async () =>
            {
                DungeonRun run = RunOf(userId) ?? throw new BadInputException(NotInDungeonText());
                if (!_session.Started)
                    throw new BadInputException("The lobby hasn't started yet, so there's nothing to cash out.");
                if (run.Phase == RunPhase.InPool)
                    throw new BadInputException("You're in the PvP pool, so you can't cash out now.");

                double payout = run.CashOut();
                _session.Runs.Remove(userId);
                _session.LastAction.Remove(userId);
                _session.Departed.Add(new ScenePlayer { Name = run.Name, Floor = run.Floor, Hp = run.Hp, MaxHp = run.MaxHp, Haul = payout, Status = "CASHED OUT" });
                await PayOut(userId, run.Name, payout);
                AddLog($"{run.Name} cashes out ${payout:N2} after {run.Floor} floors and {run.Kills} kills.");
                await TryResolvePool();
                return null;
            });
        }

        /// <summary>At a checkpoint, the run enters the PvP pool.</summary>
        public Task Fight(ulong userId, string name, IMessageChannel channel)
        {
            return Attempt(userId, name, channel, async () =>
            {
                DungeonRun run = RunOf(userId) ?? throw new BadInputException(NotInDungeonText());
                if (run.Phase != RunPhase.AtCheckpoint)
                    throw new BadInputException("You can only fight at a checkpoint, after clearing a block with `.dng go`.");

                run.EnterPool();
                AddLog($"{run.Name} enters the PvP pool with ${run.Haul:N2} at stake.");
                await TryResolvePool();
                return null;
            });
        }

        public Task Buy(ulong userId, string name, IMessageChannel channel)
        {
            return Attempt(userId, name, channel, () =>
            {
                DungeonRun run = RunOf(userId) ?? throw new BadInputException(NotInDungeonText());
                if (!_session.Started)
                    throw new BadInputException("The lobby hasn't started yet.");
                if (run.Phase == RunPhase.InPool)
                    throw new BadInputException("You're in the PvP pool, so you can't shop.");

                run.TryBuyOffer(out string message);
                return Task.FromResult($"{run.Name}: {message}");
            });
        }

        public Task Drink(ulong userId, string name, IMessageChannel channel)
        {
            return Attempt(userId, name, channel, () =>
            {
                DungeonRun run = RunOf(userId) ?? throw new BadInputException(NotInDungeonText());
                if (!_session.Started)
                    throw new BadInputException("The lobby hasn't started yet.");
                if (run.Phase == RunPhase.InPool)
                    throw new BadInputException("You're in the PvP pool, so you can't drink now.");

                run.TryDrink(out string message);
                return Task.FromResult($"{run.Name}: {message}");
            });
        }

        /// <summary>Refreshes the dashboard. Anyone can use it, and it works from any channel.</summary>
        public Task Status(string name, IMessageChannel channel)
        {
            return Attempt(0, name, channel, () => Task.FromResult<string>(null));
        }

        /// <summary>
        /// Runs one player action under the lock, adds its result to the event log, and refreshes the dashboard.
        /// Stalled runs are cashed out first. A refused action becomes a log line. If there's no session, the refusal is posted briefly and then deleted.
        /// </summary>
        private async Task Attempt(ulong userId, string actor, IMessageChannel channel, Func<Task<string>> action)
        {
            await _gate.WaitAsync();
            try
            {
                await ExpireIdleRuns();

                if (_session != null && userId != 0 && _session.LastAction.ContainsKey(userId))
                    _session.LastAction[userId] = DateTime.UtcNow;

                string line;
                try
                {
                    line = await action();
                }
                catch (BadInputException ex)
                {
                    line = $"{actor}: {ex.Message}";
                }
                catch (InvalidOperationException ex)
                {
                    line = $"{actor}: {ex.Message}";
                }

                if (line != null)
                {
                    if (_session == null)
                        _ = PostTransientAsync(channel, line);
                    else
                        AddLog(line);
                }

                if (_session != null)
                    await RenderAndEdit(_session, ended: false, resultText: null);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task AutoStartLater(int generation)
        {
            await Task.Delay(LobbyWait);

            await _gate.WaitAsync();
            try
            {
                if (_session == null || _session.Started || _session.Generation != generation)
                    return;

                AddLog(BeginCrawl(_session));
                await RenderAndEdit(_session, ended: false, resultText: null);
            }
            finally
            {
                _gate.Release();
            }
        }

        private string BeginCrawl(Session session)
        {
            session.Started = true;
            foreach (ulong id in session.Runs.Keys.ToList())
                session.LastAction[id] = DateTime.UtcNow;
            return "The crawl begins. Use `.dng go` to crawl your first block of 10 floors.";
        }

        /// <summary>
        /// Cashes out runs that have sat in crawling or at a checkpoint for too long, so a stalled player doesn't hold up the pool.
        /// Runs waiting in the pool are not expired, because they are waiting on the others.
        /// </summary>
        private async Task ExpireIdleRuns()
        {
            if (_session == null || !_session.Started)
                return;

            var stalled = _session.Runs.Values
                .Where(r => r.Phase != RunPhase.InPool && _session.LastAction.TryGetValue(r.UserId, out DateTime last) && DateTime.UtcNow - last >= IdleLimit)
                .ToList();
            if (stalled.Count == 0)
                return;

            foreach (DungeonRun run in stalled)
            {
                double payout = run.CashOut();
                _session.Runs.Remove(run.UserId);
                _session.LastAction.Remove(run.UserId);
                _session.Departed.Add(new ScenePlayer { Name = run.Name, Floor = run.Floor, Hp = run.Hp, MaxHp = run.MaxHp, Haul = payout, Status = "IDLE CASH OUT" });
                await PayOut(run.UserId, run.Name, payout);
                AddLog($"{run.Name} has been idle for {IdleLimit.TotalMinutes} minutes, so their ${payout:N2} is banked.");
            }
            await TryResolvePool();
        }

        /// <summary>
        /// Runs the PvP pool once every run in the session has decided. The pot is the fighters' hauls, and the winner or
        /// winners crawl on with it. Losers are out. The session ends when no runs are left.
        /// </summary>
        private async Task TryResolvePool()
        {
            Session session = _session;
            if (session == null)
                return;
            if (session.Runs.Count == 0)
            {
                await EndSession(session, "Nobody is left. Nothing else is paid out.");
                return;
            }
            if (!session.Runs.Values.All(r => r.Phase == RunPhase.InPool))
                return;

            var fighters = session.Runs.Values.Select(run => new PvpFighter
            {
                UserId = run.UserId,
                Name = run.Name,
                Stats = run.CloneStats(),
                Haul = run.TakeHaulIntoPool()
            }).ToList();

            PvpResult result = PvpArena.Resolve(fighters, _random);
            foreach (string line in result.Lines.Skip(Math.Max(0, result.Lines.Count - 4)))
                AddLog(line);

            foreach (PvpFighter fighter in fighters)
            {
                DungeonRun run = session.Runs[fighter.UserId];
                if (result.Payouts.TryGetValue(fighter.UserId, out double won))
                {
                    run.ReturnFromPool(won, fighter.Stats.Hp);
                    AddLog($"{run.Name} wins ${won:N2} and crawls on.");
                }
                else
                {
                    run.Eliminate();
                    session.Runs.Remove(run.UserId);
                    session.LastAction.Remove(run.UserId);
                    session.Departed.Add(new ScenePlayer { Name = run.Name, Floor = run.Floor, Hp = 0, MaxHp = run.MaxHp, Haul = 0, Status = "LOST PVP" });
                }
            }

            if (session.Runs.Count == 0)
            {
                await EndSession(session, "Nobody is left standing. The pot is lost.");
                return;
            }
            foreach (ulong id in session.Runs.Keys)
                session.LastAction[id] = DateTime.UtcNow;
            await RenderAndEdit(session, ended: false, resultText: null);
        }

        private async Task EndSession(Session session, string resultText)
        {
            _session = null;
            await RenderAndEdit(session, ended: true, resultText: resultText);
        }

        private async Task RenderAndEdit(Session session, bool ended, string resultText)
        {
            byte[] picture = session.PictureOverride ?? RenderScene(session, ended).ToArray();
            session.PictureOverride = null;

            //Discord can't swap an attached file on an existing message, so each refresh posts a new dashboard and removes the old one
            EmbedBuilder embed = BuildDashboard(session, ended, resultText).WithImageUrl("attachment://dungeon.png");
            IUserMessage previous = session.Dashboard;
            try
            {
                using (var stream = new System.IO.MemoryStream(picture))
                    session.Dashboard = await session.Channel.SendFileAsync(stream, "dungeon.png", embed: embed.Build());
            }
            catch (Exception)
            {
                //the new dashboard couldn't be posted, so keep the old one and the game state is still correct
                return;
            }

            if (previous != null)
            {
                try
                {
                    await previous.DeleteAsync();
                }
                catch (Exception)
                {
                    //the old dashboard may already be gone
                }
            }
        }

        private System.IO.MemoryStream RenderScene(Session session, bool ended)
        {
            var scene = new DungeonScene
            {
                Title = ended ? "DUNGEON OVER" : session.Started ? "CRAWL" : "LOBBY",
                Caption = session.Started ? session.Caption : "DUNGEON DOOR",
                Sprite = session.Started ? session.Sprite : SpriteKind.None,
                EnemyHp = session.EnemyHp,
                EnemyMaxHp = session.EnemyMaxHp,
                Footer = ended ? "Thanks for playing" : session.Started ? "Use .dng go, .dng cash, .dng fight, .dng drink, .dng buy" : $"Use .dng join. Buy-in ${session.BuyIn:N0}"
            };
            foreach (DungeonRun run in session.Runs.Values)
                scene.Players.Add(new ScenePlayer { Name = run.Name, Hp = run.Hp, MaxHp = run.MaxHp, Floor = run.Floor, Haul = run.Haul, Status = PhaseLabel(run) });
            scene.Players.AddRange(session.Departed);
            return DungeonSceneRenderer.Render(scene);
        }

        private EmbedBuilder BuildDashboard(Session session, bool ended, string resultText)
        {
            string title = ended ? "Dungeon over"
                : session.Started ? $"Dungeon crawl: {session.Runs.Count} still in"
                : $"Dungeon lobby: {session.Runs.Count}/{MaxPlayers} players";

            var embed = new EmbedBuilder()
                .WithTitle(title)
                .WithColor(ended ? Color.LightGrey : Color.DarkPurple);

            if (!session.Started && !ended)
                embed.WithDescription($"Type `.dng join` for the next {LobbyWait.TotalSeconds} seconds. The host can type `.dng start` to begin early. Buy-in ${session.BuyIn:N2} each.");
            if (resultText != null)
                embed.AddField("Result", resultText);

            var players = new StringBuilder();
            foreach (DungeonRun run in session.Runs.Values)
                players.AppendLine($"**{run.Name}** · block {run.NextBlockNumber} · floor {run.Floor} · HP {run.Hp}/{run.MaxHp} · ${run.Haul:N2} · {PhaseLabel(run)}");
            foreach (ScenePlayer player in session.Departed)
                players.AppendLine($"**{player.Name}** · {player.Status}");
            if (players.Length == 0)
                players.Append("No one yet.");
            embed.AddField("Players", Trim(players.ToString(), 1000));

            if (session.Log.Count > 0)
                embed.AddField("Log", Trim(string.Join("\n", session.Log), 1000));

            embed.WithFooter(ended ? "Session finished" : "Commands: .dng go · .dng cash · .dng fight · .dng drink · .dng buy · .dng status");
            return embed;
        }

        private static string PhaseLabel(DungeonRun run)
        {
            switch (run.Phase)
            {
                case RunPhase.AtCheckpoint: return "CHECKPOINT";
                case RunPhase.InPool: return "IN POOL";
                default: return "";
            }
        }

        private static string Trim(string text, int max)
        {
            return text.Length <= max ? text : "…" + text.Substring(text.Length - max + 1);
        }

        private void AddLog(string line)
        {
            if (_session == null || string.IsNullOrEmpty(line))
                return;
            _session.Log.Add(line);
            while (_session.Log.Count > LogLines)
                _session.Log.RemoveAt(0);
        }

        private DungeonRun RunOf(ulong userId)
        {
            return _session != null && _session.Runs.TryGetValue(userId, out DungeonRun run) ? run : null;
        }

        private string NotInDungeonText()
        {
            return _session == null
                ? NoRunMessage
                : $"You're not in this dungeon. It is {StateOf(_session)} in <#{_session.Channel.Id}>.";
        }

        private static string StateOf(Session session)
        {
            return session.Started ? "running" : "in the lobby";
        }

        private static string RoomLabel(RoomType type)
        {
            switch (type)
            {
                case RoomType.Treasure: return "TREASURE";
                case RoomType.Trap: return "TRAP";
                case RoomType.Merchant: return "MERCHANT";
                case RoomType.Shrine: return "SHRINE";
                case RoomType.Gear: return "GEAR";
                default: return "EMPTY ROOM";
            }
        }

        private async Task PayOut(ulong userId, string name, double amount)
        {
            if (amount <= 0) return;
            CoinAccount account = await _coinService.Get(userId, name);
            account.NetWorth += amount;
            await _coinService.Update(userId, account.NetWorth, name);
        }

        /// <summary>Posts a short message, then deletes it, so refusals don't stay in the channel.</summary>
        private static async Task PostTransientAsync(IMessageChannel channel, string text)
        {
            try
            {
                IUserMessage note = await channel.SendMessageAsync(text);
                await Task.Delay(TransientLifetime);
                await note.DeleteAsync();
            }
            catch (Exception)
            {
                //the note is only a hint, so failing to post or delete it doesn't matter
            }
        }
    }
}
