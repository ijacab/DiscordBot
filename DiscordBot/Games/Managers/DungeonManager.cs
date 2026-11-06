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
    /// One session at a time, across all channels. Players join a lobby, then each crawls a solo run for
    /// <see cref="CrawlLength"/>. When the timer ends, the survivors who haven't cashed out enter a winner-takes-all PvP.
    /// The whole session is shown on one dashboard message that is edited after every action, so the channel stays quiet.
    /// The session is in memory, so a bot restart ends it.
    /// </summary>
    public class DungeonManager
    {
        public const double MinBuyIn = 100;
        public const int MaxPlayers = 4;
        public const int LogLines = 8;
        public static readonly TimeSpan LobbyWait = TimeSpan.FromSeconds(60);
        public static readonly TimeSpan CrawlLength = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan TransientLifetime = TimeSpan.FromSeconds(15);
        private const string NoRunMessage = "No dungeon is open. Open a lobby with `.dng 1000`, where 1000 is the buy-in for each player.";

        private sealed class Session
        {
            public readonly Dictionary<ulong, DungeonRun> Runs = new Dictionary<ulong, DungeonRun>();
            /// <summary>Players who cashed out, died, or went into PvP. Shown on the dashboard.</summary>
            public readonly List<ScenePlayer> Departed = new List<ScenePlayer>();
            public readonly List<string> Log = new List<string>();
            public IMessageChannel Channel;
            public IUserMessage Dashboard;
            public ulong HostId;
            public bool Started;
            public int Generation;
            public DateTime OpenedUtc;
            public DateTime CrawlEndsUtc;
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
            return Attempt(host.Name, channel, async () =>
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
                _ = AutoStartLater(_session.Generation);
                return $"{host.Name} opens a lobby with a ${FormatHelper.GetCommaNumber(buyIn)} buy-in.";
            });
        }

        public Task Join(CoinAccount account, IMessageChannel channel)
        {
            return Attempt(account.Name, channel, async () =>
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
                return $"{account.Name} joins and pays the ${FormatHelper.GetCommaNumber(buyIn)} buy-in.";
            });
        }

        public Task Start(ulong userId, string name, IMessageChannel channel)
        {
            return Attempt(name, channel, () =>
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

        public Task Go(ulong userId, string name, IMessageChannel channel)
        {
            return Attempt(name, channel, async () =>
            {
                DungeonRun run = RunOf(userId) ?? throw new BadInputException(NotInDungeonText());
                if (!_session.Started)
                    throw new BadInputException("The lobby is still open. Wait for it to start, or the host types `.dng start`.");

                RoomResult room = run.Descend();
                _session.Sprite = room.Enemy;
                _session.Caption = room.EnemyName ?? RoomLabel(room.Type);
                _session.EnemyHp = room.EnemyHp;
                _session.EnemyMaxHp = room.EnemyMaxHp;
                if (room.Drop != null)
                    _session.PictureOverride = LootCardRenderer.Render(room.Drop).ToArray();

                foreach (string line in room.Lines)
                    AddLog($"{run.Name}: {line}");

                if (room.Died)
                {
                    _session.Runs.Remove(userId);
                    _session.Departed.Add(new ScenePlayer { Name = run.Name, Floor = run.Floor, Hp = 0, MaxHp = run.MaxHp, Haul = 0, Status = "DOWN" });
                    AddLog($"{run.Name}'s haul of ${FormatHelper.GetCommaNumber(run.Haul)} and buy-in are lost.");
                    await CheckEarlyEnd();
                }
                return null;
            });
        }

        /// <summary>Banks your own haul and leaves the crawl. You don't take part in PvP.</summary>
        public Task Cash(ulong userId, string name, IMessageChannel channel)
        {
            return Attempt(name, channel, async () =>
            {
                DungeonRun run = RunOf(userId) ?? throw new BadInputException(NotInDungeonText());
                if (!_session.Started)
                    throw new BadInputException("The lobby hasn't started yet, so there's nothing to cash out.");

                double payout = run.CashOut();
                _session.Runs.Remove(userId);
                _session.Departed.Add(new ScenePlayer { Name = run.Name, Floor = run.Floor, Hp = run.Hp, MaxHp = run.MaxHp, Haul = payout, Status = "CASHED OUT" });
                await PayOut(userId, run.Name, payout);
                AddLog($"{run.Name} cashes out ${payout:N2} after {run.Floor} rooms and {run.Kills} kills.");
                await CheckEarlyEnd();
                return null;
            });
        }

        public Task Buy(ulong userId, string name, IMessageChannel channel)
        {
            return Attempt(name, channel, () =>
            {
                DungeonRun run = RunOf(userId) ?? throw new BadInputException(NotInDungeonText());
                if (!_session.Started)
                    throw new BadInputException("The lobby hasn't started yet.");

                run.TryBuyOffer(out string message);
                return Task.FromResult($"{run.Name}: {message}");
            });
        }

        public Task Drink(ulong userId, string name, IMessageChannel channel)
        {
            return Attempt(name, channel, () =>
            {
                DungeonRun run = RunOf(userId) ?? throw new BadInputException(NotInDungeonText());
                if (!_session.Started)
                    throw new BadInputException("The lobby hasn't started yet.");

                run.TryDrink(out string message);
                return Task.FromResult($"{run.Name}: {message}");
            });
        }

        /// <summary>Refreshes the dashboard. Anyone can use it, and it works from any channel.</summary>
        public Task Status(string name, IMessageChannel channel)
        {
            return Attempt(name, channel, () => Task.FromResult<string>(null));
        }

        /// <summary>
        /// Runs one player action under the lock, adds its result to the event log, and refreshes the dashboard.
        /// A refused action becomes a log line. If there's no session, the refusal is posted briefly and then deleted.
        /// </summary>
        private async Task Attempt(string actor, IMessageChannel channel, Func<Task<string>> action)
        {
            await _gate.WaitAsync();
            try
            {
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

        /// <summary>Ends the crawl when its timer runs out, and starts PvP among the survivors.</summary>
        private async Task CrawlTimerLater(int generation)
        {
            await Task.Delay(CrawlLength);

            await _gate.WaitAsync();
            try
            {
                if (_session == null || _session.Generation != generation)
                    return;

                await ResolveSession();
            }
            finally
            {
                _gate.Release();
            }
        }

        private string BeginCrawl(Session session)
        {
            session.Started = true;
            session.CrawlEndsUtc = DateTime.UtcNow + CrawlLength;
            _ = CrawlTimerLater(session.Generation);
            return $"The crawl begins. Everyone has {CrawlLength.TotalMinutes} minutes. Use `.dng go` to enter the first room.";
        }

        /// <summary>
        /// Ends the session. Every run still going enters PvP, and the winner takes the pot of their hauls.
        /// Called when the crawl timer runs out, or early when fewer than two runs are still going.
        /// </summary>
        private async Task ResolveSession()
        {
            Session session = _session;
            var fighters = session.Runs.Values.Select(run => new PvpFighter
            {
                UserId = run.UserId,
                Name = run.Name,
                Stats = run.CloneStats(),
                Haul = run.TakeHaulForPvp()
            }).ToList();
            foreach (PvpFighter fighter in fighters)
                session.Departed.Add(new ScenePlayer { Name = fighter.Name, Hp = fighter.Stats.Hp, MaxHp = fighter.Stats.MaxHp, Haul = fighter.Haul, Status = "IN PVP" });
            session.Runs.Clear();
            _session = null;

            PvpResult result = PvpArena.Resolve(fighters, _random);
            foreach (KeyValuePair<ulong, double> payout in result.Payouts)
                await PayOut(payout.Key, fighters.First(f => f.UserId == payout.Key).Name, payout.Value);

            foreach (ScenePlayer player in session.Departed.Where(p => p.Status == "IN PVP").ToList())
            {
                double won = result.Payouts.Where(p => fighters.First(f => f.UserId == p.Key).Name == player.Name).Sum(p => p.Value);
                player.Status = won > 0 ? $"WON ${won:N0}" : "LOST";
            }

            foreach (string line in result.Lines.Skip(Math.Max(0, result.Lines.Count - 4)))
                AddLog(line);

            string winners = result.Winners.Count == 0 ? "Nobody is paid." : $"Winner{(result.Winners.Count > 1 ? "s" : "")}: {string.Join(", ", result.Winners)}. Pot ${result.Pot:N2}.";
            await RenderAndEdit(session, ended: true, resultText: winners);
        }

        /// <summary>Ends the session early when fewer than two runs are still going.</summary>
        private async Task CheckEarlyEnd()
        {
            if (_session != null && _session.Started && _session.Runs.Count < 2)
                await ResolveSession();
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
                Title = ended ? "DUNGEON OVER" : session.Started ? $"CRAWL: {MinutesLeft(session)} MIN" : "LOBBY",
                Caption = session.Started ? session.Caption : "DUNGEON DOOR",
                Sprite = session.Started ? session.Sprite : SpriteKind.None,
                EnemyHp = session.EnemyHp,
                EnemyMaxHp = session.EnemyMaxHp,
                Footer = ended ? "Thanks for playing" : session.Started ? "Use .dng go, .dng cash, .dng drink, .dng buy" : $"Use .dng join. Buy-in ${session.BuyIn:N0}"
            };
            foreach (DungeonRun run in session.Runs.Values)
                scene.Players.Add(new ScenePlayer { Name = run.Name, Hp = run.Hp, MaxHp = run.MaxHp, Floor = run.Floor, Haul = run.Haul });
            scene.Players.AddRange(session.Departed);
            return DungeonSceneRenderer.Render(scene);
        }

        private EmbedBuilder BuildDashboard(Session session, bool ended, string resultText)
        {
            string title = ended ? "Dungeon over"
                : session.Started ? $"Dungeon crawl: {MinutesLeft(session)} min left"
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
                players.AppendLine($"**{run.Name}** · floor {run.Floor} · HP {run.Hp}/{run.MaxHp} · ${run.Haul:N2}");
            foreach (ScenePlayer player in session.Departed)
                players.AppendLine($"**{player.Name}** · {player.Status}");
            if (players.Length == 0)
                players.Append("No one yet.");
            embed.AddField("Players", Trim(players.ToString(), 1000));

            if (session.Log.Count > 0)
                embed.AddField("Log", Trim(string.Join("\n", session.Log), 1000));

            embed.WithFooter(ended ? "Session finished" : "Commands: .dng go · .dng cash · .dng drink · .dng buy · .dng status");
            return embed;
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

        private static int MinutesLeft(Session session)
        {
            return (int)Math.Ceiling(Math.Max(0, (session.CrawlEndsUtc - DateTime.UtcNow).TotalMinutes));
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
