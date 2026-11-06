using Common.Helpers;
using Discord;
using DiscordBot.Exceptions;
using DiscordBot.Games.Models.Dungeon;
using DiscordBot.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static DiscordBot.Models.CoinAccounts;

namespace DiscordBot.Games.Managers
{
    /// <summary>A reply for the command handler. When <see cref="Run"/> is set, the handler also sends the room card image.</summary>
    public class DungeonReply
    {
        public string Text { get; set; }
        public DungeonRun Run { get; set; }
    }

    /// <summary>
    /// Runs one shared dungeon party at a time, across all channels. Each member's buy-in comes out of cash.
    /// The haul is shared, cash out splits it among the living, and a wiped party loses the haul.
    /// The session is in memory, so a bot restart ends it.
    /// </summary>
    public class DungeonManager
    {
        public const double MinBuyIn = 100;
        public static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(15);
        public static readonly TimeSpan LobbyWait = TimeSpan.FromSeconds(60);
        private const string NoRunMessage = "No dungeon is open. Open a lobby with `.dng 1000`, where 1000 is the buy-in for each player.";

        private sealed class Session
        {
            public DungeonRun Run;
            public IMessageChannel Channel;
            public ulong HostId;
            public bool Started;
            public int Generation;
            public DateTime OpenedUtc;
        }

        private readonly CoinService _coinService;
        /// <summary>The only dungeon open at a time, or null when none is open.</summary>
        private Session _session;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private int _generationCounter;

        public DungeonManager(CoinService coinService)
        {
            _coinService = coinService;
        }

        /// <summary>Opens the lobby. Only one dungeon can be open at a time, so this is refused while another is open.</summary>
        public async Task<DungeonReply> Open(CoinAccount host, double buyIn, IMessageChannel channel)
        {
            await _gate.WaitAsync();
            try
            {
                string notice = await ExpireIfIdle();
                if (_session != null)
                    throw new BadInputException($"A dungeon is already {StateOf(_session)} in <#{_session.Channel.Id}>. Join it with `.dng join`, or wait for it to end.");
                if (buyIn < MinBuyIn)
                    throw new BadInputException($"The dungeon needs at least ${FormatHelper.GetCommaNumber(MinBuyIn)} to enter.");
                if (buyIn > host.NetWorth)
                    throw new BadInputException($"You only have ${FormatHelper.GetCommaNumber(host.NetWorth)}.");

                host.NetWorth -= buyIn;
                await _coinService.Update(host.UserId, host.NetWorth, host.Name);

                var run = new DungeonRun(buyIn);
                run.AddMember(host.UserId, host.Name);
                _session = new Session
                {
                    Run = run,
                    Channel = channel,
                    HostId = host.UserId,
                    Generation = ++_generationCounter,
                    OpenedUtc = DateTime.UtcNow
                };
                _ = AutoStartLater(_session.Generation);

                return new DungeonReply
                {
                    Text = notice + $"{host.Name} opens a dungeon lobby with a ${FormatHelper.GetCommaNumber(buyIn)} buy-in.\n" +
                        $"Anyone can type `.dng join` from any channel for the next {LobbyWait.TotalSeconds} seconds, or the host can type `.dng start` to begin early.\n" +
                        "Nothing is banked until the party votes to cash out, so every floor is money at risk."
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
                string notice = await ExpireIfIdle();
                if (_session == null)
                    throw new BadInputException("No dungeon is open. Open one with `.dng 1000`.");
                if (_session.Started)
                    throw new BadInputException($"The dungeon in <#{_session.Channel.Id}> is already running.");
                if (_session.Run.Member(account.UserId) != null)
                    throw new BadInputException("You're already in this dungeon.");
                if (_session.Run.Members.Count >= DungeonRun.MaxPartySize)
                    throw new BadInputException($"The party is full at {DungeonRun.MaxPartySize} players.");

                double buyIn = _session.Run.BuyIn;
                if (buyIn > account.NetWorth)
                    throw new BadInputException($"The buy-in is ${FormatHelper.GetCommaNumber(buyIn)} and you only have ${FormatHelper.GetCommaNumber(account.NetWorth)}.");

                account.NetWorth -= buyIn;
                await _coinService.Update(account.UserId, account.NetWorth, account.Name);
                _session.Run.AddMember(account.UserId, account.Name);

                return new DungeonReply
                {
                    Text = notice + $"{account.Name} joins the party and pays the ${FormatHelper.GetCommaNumber(buyIn)} buy-in. " +
                        $"Party: {_session.Run.Members.Count}/{DungeonRun.MaxPartySize}."
                };
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>The host starts the lobby early.</summary>
        public async Task<DungeonReply> Start(ulong userId)
        {
            await _gate.WaitAsync();
            try
            {
                string notice = await ExpireIfIdle();
                if (_session == null)
                    return new DungeonReply { Text = notice + NoRunMessage };
                if (_session.Run.Member(userId) == null)
                    return new DungeonReply { Text = notice + NotInThisDungeon() };
                if (_session.Started)
                    return new DungeonReply { Text = notice + "The dungeon has already started." };
                if (_session.HostId != userId)
                    throw new BadInputException("Only the host can start the lobby early. Otherwise it starts on its own.");

                return new DungeonReply { Text = notice + BeginRun(_session) };
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
                string notice = await ExpireIfIdle();
                if (_session == null)
                    return new DungeonReply { Text = notice + NoRunMessage };
                if (_session.Run.Member(userId) == null)
                    return new DungeonReply { Text = notice + NotInThisDungeon() };
                if (!_session.Started)
                    return new DungeonReply { Text = notice + "The lobby is still open. Wait for it to start, or type `.dng start`." };
                if (!IsLiving(_session.Run, userId))
                    return new DungeonReply { Text = notice + "You are down. You can only watch from here." };

                _session.Run.LastActivityUtc = DateTime.UtcNow;
                RoomResult room = _session.Run.Descend();
                string body = string.Join("\n", room.Lines);

                if (_session.Run.IsOver)
                {
                    DungeonRun lost = _session.Run;
                    EndSession();
                    return new DungeonReply
                    {
                        Text = notice + body + $"\nThe haul of ${FormatHelper.GetCommaNumber(lost.Haul)} and every buy-in are lost.",
                        Run = lost
                    };
                }

                return new DungeonReply
                {
                    Text = notice + body + "\n" + StatusLine(_session.Run) + "\n" + ActionsLine(_session.Run),
                    Run = _session.Run
                };
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Records a living member's vote. The party cashes out once more than half of the living members have voted.</summary>
        public async Task<DungeonReply> Cash(ulong userId)
        {
            await _gate.WaitAsync();
            try
            {
                string notice = await ExpireIfIdle();
                if (_session == null)
                    return new DungeonReply { Text = notice + NoRunMessage };
                if (_session.Run.Member(userId) == null)
                    return new DungeonReply { Text = notice + NotInThisDungeon() };
                if (!_session.Started)
                    return new DungeonReply { Text = notice + "The lobby hasn't started yet, so there's nothing to cash out." };
                if (!IsLiving(_session.Run, userId))
                    return new DungeonReply { Text = notice + "You are down, so you can't vote." };

                _session.Run.LastActivityUtc = DateTime.UtcNow;
                if (!_session.Run.VoteCashOut(userId))
                {
                    return new DungeonReply
                    {
                        Text = notice + $"Cash out vote recorded: {_session.Run.CashOutVotes.Count} of {_session.Run.LivingCount} living members. " +
                            "More than half must vote. Votes reset when the party enters the next room.",
                        Run = _session.Run
                    };
                }

                DungeonRun run = _session.Run;
                IReadOnlyDictionary<ulong, double> payouts = run.CashOut();
                EndSession();
                await PayOut(run, payouts);

                var lines = payouts.Select(p => $"{run.Member(p.Key).Name} takes ${FormatHelper.GetCommaNumber(p.Value)}");
                return new DungeonReply
                {
                    Text = notice + $"The party climbs out alive after {run.Floor} rooms and {run.Kills} kills.\n" +
                        string.Join("\n", lines) + "\n" +
                        $"Buy-ins were ${FormatHelper.GetCommaNumber(run.BuyIn)} each. Living members split the ${FormatHelper.GetCommaNumber(run.Haul)} haul."
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
                string notice = await ExpireIfIdle();
                if (_session == null)
                    return new DungeonReply { Text = notice + NoRunMessage };
                if (_session.Run.Member(userId) == null)
                    return new DungeonReply { Text = notice + NotInThisDungeon() };
                if (!_session.Started)
                    return new DungeonReply { Text = notice + "The lobby hasn't started yet." };

                _session.Run.LastActivityUtc = DateTime.UtcNow;
                bool bought = _session.Run.TryBuyOffer(userId, out string message);
                return new DungeonReply
                {
                    Text = notice + message + (bought ? "\n" + StatusLine(_session.Run) : ""),
                    Run = bought ? _session.Run : null
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
                string notice = await ExpireIfIdle();
                if (_session == null)
                    return new DungeonReply { Text = notice + NoRunMessage };
                if (_session.Run.Member(userId) == null)
                    return new DungeonReply { Text = notice + NotInThisDungeon() };
                if (!_session.Started)
                    return new DungeonReply { Text = notice + "The lobby hasn't started yet." };

                _session.Run.LastActivityUtc = DateTime.UtcNow;
                _session.Run.TryDrink(userId, out string message);
                return new DungeonReply
                {
                    Text = notice + message + "\n" + StatusLine(_session.Run),
                    Run = _session.Run
                };
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Shows the open dungeon to anyone, from any channel.</summary>
        public async Task<DungeonReply> Status()
        {
            await _gate.WaitAsync();
            try
            {
                string notice = await ExpireIfIdle();
                if (_session == null)
                    return new DungeonReply { Text = notice + NoRunMessage };

                if (!_session.Started)
                {
                    double secondsLeft = Math.Max(0, (LobbyWait - (DateTime.UtcNow - _session.OpenedUtc)).TotalSeconds);
                    return new DungeonReply
                    {
                        Text = notice + $"Lobby open in <#{_session.Channel.Id}>: {_session.Run.Members.Count}/{DungeonRun.MaxPartySize} players, " +
                            $"${FormatHelper.GetCommaNumber(_session.Run.BuyIn)} buy-in each. Starts in {Math.Ceiling(secondsLeft)} seconds. Type `.dng join` to enter."
                    };
                }

                return new DungeonReply
                {
                    Text = notice + $"Running in <#{_session.Channel.Id}>.\n" + StatusLine(_session.Run) + "\n" + ActionsLine(_session.Run),
                    Run = _session.Run
                };
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Starts the lobby after <see cref="LobbyWait"/>, unless it was already started or replaced by a newer lobby.</summary>
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
                text = BeginRun(_session);
            }
            finally
            {
                _gate.Release();
            }

            try
            {
                await channel.SendMessageAsync(embed: DiscordHelper.GetEmbedBuilder("Dungeon", text).Build());
            }
            catch (Exception)
            {
                //the lobby still started, so a failed announcement just means nobody sees the message
            }
        }

        private string BeginRun(Session session)
        {
            session.Started = true;
            session.Run.LastActivityUtc = DateTime.UtcNow;
            string names = string.Join(", ", session.Run.Members.Select(m => m.Name));
            return $"The dungeon opens for {names}. Use `.dng go` to enter the first room.";
        }

        private string NotInThisDungeon()
        {
            return $"You're not in this dungeon. It is {StateOf(_session)} in <#{_session.Channel.Id}>.";
        }

        private static string StateOf(Session session)
        {
            return session.Started ? "running" : "in the lobby";
        }

        private static bool IsLiving(DungeonRun run, ulong userId)
        {
            DungeonMember member = run.Member(userId);
            return member != null && member.Alive;
        }

        private void EndSession()
        {
            _session = null;
        }

        /// <summary>
        /// Ends a started run that has sat idle too long and pays out the living members, so an abandoned party keeps what it earned.
        /// Runs at the top of every public method, so any dungeon command from anyone frees an idle slot.
        /// </summary>
        private async Task<string> ExpireIfIdle()
        {
            if (_session == null || !_session.Started || _session.Run.IsOver)
                return "";
            if (DateTime.UtcNow - _session.Run.LastActivityUtc < IdleLimit)
                return "";

            DungeonRun run = _session.Run;
            IReadOnlyDictionary<ulong, double> payouts = run.CashOut();
            EndSession();
            await PayOut(run, payouts);
            return $"The party's run timed out after {IdleLimit.TotalMinutes} idle minutes, so the living members were pulled out with their share of the haul.\n\n";
        }

        private async Task PayOut(DungeonRun run, IReadOnlyDictionary<ulong, double> payouts)
        {
            foreach (KeyValuePair<ulong, double> payout in payouts)
            {
                if (payout.Value <= 0) continue;
                string name = run.Member(payout.Key).Name;
                CoinAccount account = await _coinService.Get(payout.Key, name);
                account.NetWorth += payout.Value;
                await _coinService.Update(payout.Key, account.NetWorth, name);
            }
        }

        private static string StatusLine(DungeonRun run)
        {
            return $"Party {run.LivingCount}/{run.Members.Count} standing | Floor {run.Floor} | Haul ${run.Haul:N2} | Buy-in ${run.BuyIn:N2} each";
        }

        private static string ActionsLine(DungeonRun run)
        {
            int needed = run.LivingCount / 2 + 1;
            var actions = new List<string>
            {
                "`.dng go`",
                $"`.dng cash` ({run.CashOutVotes.Count}/{needed} votes to bank ${run.Haul:N2})",
                "`.dng drink`"
            };
            if (run.Offer != PendingOffer.None)
                actions.Add("`.dng buy`");
            return "Next: " + string.Join(", ", actions) + ".";
        }
    }
}
