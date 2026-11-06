using Common.Helpers;
using Discord;
using DiscordBot.Exceptions;
using DiscordBot.Games.Models.Dungeon;
using DiscordBot.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static DiscordBot.Models.CoinAccounts;

namespace DiscordBot.Games.Managers
{
    /// <summary>A reply for the command handler. The handler sends the card, the loot image, or both when they are set.</summary>
    public class DungeonReply
    {
        public string Text { get; set; }
        public DungeonRun Run { get; set; }
        /// <summary>Footer drawn on the run card, such as the time left in the crawl.</summary>
        public string CardFooter { get; set; }
        public MemoryStream LootImage { get; set; }
    }

    /// <summary>
    /// One session at a time, across all channels. Players join a lobby, then each crawls a solo run for
    /// <see cref="CrawlLength"/>. When the timer ends, the survivors who haven't cashed out enter a winner-takes-all PvP.
    /// The session is in memory, so a bot restart ends it.
    /// </summary>
    public class DungeonManager
    {
        public const double MinBuyIn = 100;
        public const int MaxPlayers = 4;
        public static readonly TimeSpan LobbyWait = TimeSpan.FromSeconds(60);
        public static readonly TimeSpan CrawlLength = TimeSpan.FromMinutes(5);
        private const string NoRunMessage = "No dungeon is open. Open a lobby with `.dng 1000`, where 1000 is the buy-in for each player.";

        private sealed class Session
        {
            public readonly Dictionary<ulong, DungeonRun> Runs = new Dictionary<ulong, DungeonRun>();
            public IMessageChannel Channel;
            public ulong HostId;
            public bool Started;
            public int Generation;
            public DateTime OpenedUtc;
            public DateTime CrawlEndsUtc;
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

        /// <summary>Opens the lobby. Refused while another session is open.</summary>
        public async Task<DungeonReply> Open(CoinAccount host, double buyIn, IMessageChannel channel)
        {
            await _gate.WaitAsync();
            try
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
                    OpenedUtc = DateTime.UtcNow
                };
                _session.Runs[host.UserId] = new DungeonRun(host.UserId, host.Name, buyIn, _random);
                _ = AutoStartLater(_session.Generation);

                return new DungeonReply
                {
                    Text = $"{host.Name} opens a dungeon lobby with a ${FormatHelper.GetCommaNumber(buyIn)} buy-in.\n" +
                        $"Anyone can type `.dng join` from any channel for the next {LobbyWait.TotalSeconds} seconds, or the host can type `.dng start` to begin early.\n" +
                        $"Once the crawl starts, everyone crawls alone for {CrawlLength.TotalMinutes} minutes. The survivors who haven't cashed out then fight for the pot."
                };
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Joins the open lobby, from any channel, paying the same buy-in as the host.</summary>
        public async Task<DungeonReply> Join(CoinAccount account)
        {
            await _gate.WaitAsync();
            try
            {
                if (_session == null)
                    throw new BadInputException("No dungeon is open. Open one with `.dng 1000`.");
                if (_session.Started)
                    throw new BadInputException($"The dungeon in <#{_session.Channel.Id}> is already running.");
                if (_session.Runs.ContainsKey(account.UserId))
                    throw new BadInputException("You're already in this dungeon.");
                if (_session.Runs.Count >= MaxPlayers)
                    throw new BadInputException($"The lobby is full at {MaxPlayers} players.");

                double buyIn = _session.Runs.Values.First().BuyIn;
                if (buyIn > account.NetWorth)
                    throw new BadInputException($"The buy-in is ${FormatHelper.GetCommaNumber(buyIn)} and you only have ${FormatHelper.GetCommaNumber(account.NetWorth)}.");

                account.NetWorth -= buyIn;
                await _coinService.Update(account.UserId, account.NetWorth, account.Name);
                _session.Runs[account.UserId] = new DungeonRun(account.UserId, account.Name, buyIn, _random);

                return new DungeonReply
                {
                    Text = $"{account.Name} joins and pays the ${FormatHelper.GetCommaNumber(buyIn)} buy-in. Players: {_session.Runs.Count}/{MaxPlayers}."
                };
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>The host starts the crawl early.</summary>
        public async Task<DungeonReply> Start(ulong userId)
        {
            await _gate.WaitAsync();
            try
            {
                if (_session == null)
                    return new DungeonReply { Text = NoRunMessage };
                if (!_session.Runs.ContainsKey(userId))
                    return new DungeonReply { Text = NotInThisDungeon() };
                if (_session.Started)
                    return new DungeonReply { Text = "The crawl has already started." };
                if (_session.HostId != userId)
                    throw new BadInputException("Only the host can start the lobby early. Otherwise it starts on its own.");

                return new DungeonReply { Text = BeginCrawl(_session) };
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<DungeonReply> Go(ulong userId)
        {
            await _gate.WaitAsync();
            try
            {
                DungeonRun run = RunOf(userId);
                if (run == null)
                    return new DungeonReply { Text = _session == null ? NoRunMessage : NotInThisDungeon() };
                if (!_session.Started)
                    return new DungeonReply { Text = "The lobby is still open. Wait for it to start, or type `.dng start`." };

                RoomResult room = run.Descend();
                string body = string.Join("\n", room.Lines);

                if (run.IsOver)
                {
                    _session.Runs.Remove(userId);
                    string lost = $"{body}\nThe haul of ${FormatHelper.GetCommaNumber(run.Haul)} and your buy-in are lost.";
                    return new DungeonReply
                    {
                        Text = lost + "\n" + await CheckEarlyEnd(),
                        Run = run,
                        CardFooter = CrawlFooter()
                    };
                }

                return new DungeonReply
                {
                    Text = body + "\n" + StatusLine(run) + "\n" + ActionsLine(run),
                    Run = run,
                    CardFooter = CrawlFooter()
                };
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Banks your own haul and leaves the crawl. You don't take part in PvP.</summary>
        public async Task<DungeonReply> Cash(ulong userId)
        {
            await _gate.WaitAsync();
            try
            {
                DungeonRun run = RunOf(userId);
                if (run == null)
                    return new DungeonReply { Text = _session == null ? NoRunMessage : NotInThisDungeon() };
                if (!_session.Started)
                    return new DungeonReply { Text = "The lobby hasn't started yet, so there's nothing to cash out." };

                double payout = run.CashOut();
                _session.Runs.Remove(userId);
                await PayOut(userId, run.Name, payout);

                return new DungeonReply
                {
                    Text = $"You climb out alive with ${payout:N2} after {run.Floor} rooms and {run.Kills} kills. " +
                        $"Your buy-in was ${run.BuyIn:N2}, so you {(payout >= run.BuyIn ? "made" : "lost")} ${Math.Abs(payout - run.BuyIn):N2}.\n" +
                        await CheckEarlyEnd()
                };
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<DungeonReply> Buy(ulong userId)
        {
            await _gate.WaitAsync();
            try
            {
                DungeonRun run = RunOf(userId);
                if (run == null)
                    return new DungeonReply { Text = _session == null ? NoRunMessage : NotInThisDungeon() };
                if (!_session.Started)
                    return new DungeonReply { Text = "The lobby hasn't started yet." };

                bool bought = run.TryBuyOffer(out string message);
                return new DungeonReply
                {
                    Text = message + (bought ? "\n" + StatusLine(run) : ""),
                    Run = bought ? run : null,
                    CardFooter = CrawlFooter()
                };
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<DungeonReply> Drink(ulong userId)
        {
            await _gate.WaitAsync();
            try
            {
                DungeonRun run = RunOf(userId);
                if (run == null)
                    return new DungeonReply { Text = _session == null ? NoRunMessage : NotInThisDungeon() };
                if (!_session.Started)
                    return new DungeonReply { Text = "The lobby hasn't started yet." };

                run.TryDrink(out string message);
                return new DungeonReply
                {
                    Text = message + "\n" + StatusLine(run),
                    Run = run,
                    CardFooter = CrawlFooter()
                };
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Buys a loot box for your run, once per room. The cost comes from cash, not the haul.</summary>
        public async Task<DungeonReply> Loot(ulong userId)
        {
            await _gate.WaitAsync();
            try
            {
                DungeonRun run = RunOf(userId);
                if (run == null)
                    return new DungeonReply { Text = _session == null ? NoRunMessage : NotInThisDungeon() };
                if (!_session.Started)
                    return new DungeonReply { Text = "The lobby hasn't started yet." };
                if (!run.CanLoot)
                    return new DungeonReply { Text = "You've already opened a loot box in this room. Use `.dng go` to enter the next one." };

                double cost = LootBox.Cost(run.BuyIn);
                CoinAccount account = await _coinService.Get(userId, run.Name);
                if (account.NetWorth < cost)
                    throw new BadInputException($"A loot box costs ${FormatHelper.GetCommaNumber(cost)} and you only have ${FormatHelper.GetCommaNumber(account.NetWorth)}.");

                account.NetWorth -= cost;
                await _coinService.Update(userId, account.NetWorth, account.Name);

                LootResult loot = LootBox.Roll(_random);
                run.ApplyLoot(loot);

                return new DungeonReply
                {
                    Text = $"You spend ${cost:N2} on a loot box and find {loot.Name} ({loot.Tier}). " +
                        $"Attack +{loot.AttackGain}, defense +{loot.DefenseGain}, max HP +{loot.MaxHpGain}.\n" + StatusLine(run),
                    LootImage = LootCardRenderer.Render(loot)
                };
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Shows the session to anyone, and your own run as a card if you're in it.</summary>
        public async Task<DungeonReply> Status(ulong userId)
        {
            await _gate.WaitAsync();
            try
            {
                if (_session == null)
                    return new DungeonReply { Text = NoRunMessage };

                if (!_session.Started)
                {
                    double secondsLeft = Math.Max(0, (LobbyWait - (DateTime.UtcNow - _session.OpenedUtc)).TotalSeconds);
                    return new DungeonReply
                    {
                        Text = $"Lobby open in <#{_session.Channel.Id}>: {_session.Runs.Count}/{MaxPlayers} players, " +
                            $"${FormatHelper.GetCommaNumber(_session.Runs.Values.First().BuyIn)} buy-in each. Starts in {Math.Ceiling(secondsLeft)} seconds. Type `.dng join` to enter."
                    };
                }

                DungeonRun run = RunOf(userId);
                string text = $"Crawl running in <#{_session.Channel.Id}>. {CrawlFooter()}. {_session.Runs.Count} still crawling.";
                if (run == null)
                    return new DungeonReply { Text = text };

                return new DungeonReply
                {
                    Text = text + "\n" + StatusLine(run) + "\n" + ActionsLine(run),
                    Run = run,
                    CardFooter = CrawlFooter()
                };
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Starts the lobby after <see cref="LobbyWait"/>, unless it was already started or replaced.</summary>
        private async Task AutoStartLater(int generation)
        {
            await Task.Delay(LobbyWait);

            string text;
            IMessageChannel channel;
            await _gate.WaitAsync();
            try
            {
                if (_session == null || _session.Started || _session.Generation != generation)
                    return;

                channel = _session.Channel;
                text = BeginCrawl(_session);
            }
            finally
            {
                _gate.Release();
            }

            await PostToChannel(channel, text);
        }

        /// <summary>Ends the crawl when its timer runs out, and starts PvP among the survivors.</summary>
        private async Task CrawlTimerLater(int generation)
        {
            await Task.Delay(CrawlLength);

            string text;
            IMessageChannel channel;
            await _gate.WaitAsync();
            try
            {
                if (_session == null || _session.Generation != generation)
                    return;

                channel = _session.Channel;
                text = await ResolveSession();
            }
            finally
            {
                _gate.Release();
            }

            await PostToChannel(channel, text);
        }

        private static async Task PostToChannel(IMessageChannel channel, string text)
        {
            try
            {
                await channel.SendMessageAsync(embed: DiscordHelper.GetEmbedBuilder("Dungeon", text).Build());
            }
            catch (Exception)
            {
                //the session still ran, so a failed announcement just means nobody sees the message
            }
        }

        private string BeginCrawl(Session session)
        {
            session.Started = true;
            session.CrawlEndsUtc = DateTime.UtcNow + CrawlLength;
            _ = CrawlTimerLater(session.Generation);
            string names = string.Join(", ", session.Runs.Values.Select(r => r.Name));
            return $"The crawl begins for {names}. You have {CrawlLength.TotalMinutes} minutes. Use `.dng go` to enter your first room.";
        }

        /// <summary>
        /// Ends the session. Every run still going is an entrant, and the winner takes the pot of their hauls.
        /// Called when the crawl timer runs out, or early when fewer than two runs are still going.
        /// </summary>
        private async Task<string> ResolveSession()
        {
            var fighters = _session.Runs.Values.Select(run => new PvpFighter
            {
                UserId = run.UserId,
                Name = run.Name,
                Hp = run.Hp,
                Attack = run.Attack,
                Defense = run.Defense,
                Haul = run.TakeHaulForPvp()
            }).ToList();
            _session = null;

            PvpResult result = PvpArena.Resolve(fighters, _random);
            foreach (KeyValuePair<ulong, double> payout in result.Payouts)
                await PayOut(payout.Key, fighters.First(f => f.UserId == payout.Key).Name, payout.Value);

            var winnings = result.Payouts.Where(p => p.Value > 0)
                .Select(p => $"{fighters.First(f => f.UserId == p.Key).Name} takes ${FormatHelper.GetCommaNumber(p.Value)}");
            return "The crawl is over. PvP for the survivors:\n" +
                string.Join("\n", result.Lines) + "\n" +
                (winnings.Any() ? string.Join("\n", winnings) : "Nothing is paid out.");
        }

        /// <summary>Resolves the session early when fewer than two runs are still going.</summary>
        private async Task<string> CheckEarlyEnd()
        {
            if (_session == null || !_session.Started || _session.Runs.Count >= 2)
                return "";
            return await ResolveSession();
        }

        private string CrawlFooter()
        {
            int minutesLeft = (int)Math.Ceiling(Math.Max(0, (_session.CrawlEndsUtc - DateTime.UtcNow).TotalMinutes));
            return $"Crawl ends in {minutesLeft} min. {_session.Runs.Count} still crawling";
        }

        private DungeonRun RunOf(ulong userId)
        {
            return _session != null && _session.Runs.TryGetValue(userId, out DungeonRun run) ? run : null;
        }

        private string NotInThisDungeon()
        {
            return $"You're not in this dungeon. It is {StateOf(_session)} in <#{_session.Channel.Id}>.";
        }

        private static string StateOf(Session session)
        {
            return session.Started ? "running" : "in the lobby";
        }

        private async Task PayOut(ulong userId, string name, double amount)
        {
            if (amount <= 0) return;
            CoinAccount account = await _coinService.Get(userId, name);
            account.NetWorth += amount;
            await _coinService.Update(userId, account.NetWorth, name);
        }

        private static string StatusLine(DungeonRun run)
        {
            return $"Floor {run.Floor} | HP {Math.Max(0, run.Hp)} of {run.MaxHp} | Attack {run.Attack} | Defense {run.Defense} | " +
                $"Haul ${run.Haul:N2} | Buy-in ${run.BuyIn:N2}";
        }

        private static string ActionsLine(DungeonRun run)
        {
            var actions = new List<string> { "`.dng go`", $"`.dng cash` to bank ${run.Haul:N2}", "`.dng drink`" };
            if (run.CanLoot)
                actions.Add($"`.dng loot` for ${LootBox.Cost(run.BuyIn):N2}");
            if (run.Offer != PendingOffer.None)
                actions.Add("`.dng buy`");
            return "Next: " + string.Join(", ", actions) + ".";
        }
    }
}
