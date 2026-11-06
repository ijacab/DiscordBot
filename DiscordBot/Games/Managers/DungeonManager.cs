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
    /// Runs shared dungeon parties. Each member's buy-in comes out of cash. The haul is shared, cash out splits it
    /// among the living, and a wiped party loses the haul. Sessions are in memory, so a bot restart ends them.
    /// </summary>
    public class DungeonManager
    {
        public const double MinBuyIn = 100;
        public static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(15);
        public static readonly TimeSpan LobbyWait = TimeSpan.FromSeconds(60);
        private const string NoRunMessage = "You're not in a dungeon. Open a lobby with `.dng 1000`, where 1000 is the buy-in for each player.";

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
        private readonly Dictionary<ulong, Session> _sessionByUser = new Dictionary<ulong, Session>();
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private int _generationCounter;

        public DungeonManager(CoinService coinService)
        {
            _coinService = coinService;
        }

        /// <summary>Opens a lobby in this channel. The host pays the buy-in. The lobby starts by itself after <see cref="LobbyWait"/>.</summary>
        public async Task<DungeonReply> Open(CoinAccount host, double buyIn, IMessageChannel channel)
        {
            await _gate.WaitAsync();
            try
            {
                string notice = await ExpireIfIdle(host.UserId);
                if (_sessionByUser.ContainsKey(host.UserId))
                    throw new BadInputException("You're already in a dungeon. Use `.dng` to see where you are.");
                if (buyIn < MinBuyIn)
                    throw new BadInputException($"The dungeon needs at least ${FormatHelper.GetCommaNumber(MinBuyIn)} to enter.");
                if (buyIn > host.NetWorth)
                    throw new BadInputException($"You only have ${FormatHelper.GetCommaNumber(host.NetWorth)}.");

                host.NetWorth -= buyIn;
                await _coinService.Update(host.UserId, host.NetWorth, host.Name);

                var run = new DungeonRun(buyIn);
                run.AddMember(host.UserId, host.Name);
                var session = new Session
                {
                    Run = run,
                    Channel = channel,
                    HostId = host.UserId,
                    Generation = ++_generationCounter,
                    OpenedUtc = DateTime.UtcNow
                };
                _sessionByUser[host.UserId] = session;
                _ = AutoStartLater(session.Generation, host.UserId);

                return new DungeonReply
                {
                    Text = notice + $"{host.Name} opens a dungeon lobby with a ${FormatHelper.GetCommaNumber(buyIn)} buy-in.\n" +
                        $"Others can type `.dng join` for the next {LobbyWait.TotalSeconds} seconds, or the host can type `.dng start` to begin early.\n" +
                        "Nothing is banked until the party votes to cash out, so every floor is money at risk."
                };
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Joins the open lobby in this channel, paying the same buy-in as the host.</summary>
        public async Task<DungeonReply> Join(CoinAccount account, IMessageChannel channel)
        {
            await _gate.WaitAsync();
            try
            {
                string notice = await ExpireIfIdle(account.UserId);
                if (_sessionByUser.ContainsKey(account.UserId))
                    throw new BadInputException("You're already in a dungeon.");

                Session session = _sessionByUser.Values.FirstOrDefault(s => !s.Started && s.Channel.Id == channel.Id);
                if (session == null)
                    throw new BadInputException("No dungeon lobby is open here. Open one with `.dng 1000`.");
                if (session.Run.Members.Count >= DungeonRun.MaxPartySize)
                    throw new BadInputException($"The party is full at {DungeonRun.MaxPartySize} players.");

                double buyIn = session.Run.BuyIn;
                if (buyIn > account.NetWorth)
                    throw new BadInputException($"The buy-in is ${FormatHelper.GetCommaNumber(buyIn)} and you only have ${FormatHelper.GetCommaNumber(account.NetWorth)}.");

                account.NetWorth -= buyIn;
                await _coinService.Update(account.UserId, account.NetWorth, account.Name);
                session.Run.AddMember(account.UserId, account.Name);
                _sessionByUser[account.UserId] = session;

                return new DungeonReply
                {
                    Text = notice + $"{account.Name} joins the party and pays the ${FormatHelper.GetCommaNumber(buyIn)} buy-in. " +
                        $"Party: {session.Run.Members.Count}/{DungeonRun.MaxPartySize}."
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
                string notice = await ExpireIfIdle(userId);
                Session session = SessionOf(userId);
                if (session == null)
                    return new DungeonReply { Text = notice + NoRunMessage };
                if (session.Started)
                    return new DungeonReply { Text = notice + "The dungeon has already started." };
                if (session.HostId != userId)
                    throw new BadInputException("Only the host can start the lobby early. Otherwise it starts on its own.");

                return new DungeonReply { Text = notice + BeginRun(session) };
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
                string notice = await ExpireIfIdle(userId);
                Session session = SessionOf(userId);
                if (session == null)
                    return new DungeonReply { Text = notice + NoRunMessage };
                if (!session.Started)
                    return new DungeonReply { Text = notice + "The lobby is still open. Wait for it to start, or type `.dng start`." };
                if (!IsLiving(session.Run, userId))
                    return new DungeonReply { Text = notice + "You are down. You can only watch from here." };

                session.Run.LastActivityUtc = DateTime.UtcNow;
                RoomResult room = session.Run.Descend();
                string body = string.Join("\n", room.Lines);

                if (session.Run.IsOver)
                {
                    Remove(session);
                    return new DungeonReply
                    {
                        Text = notice + body + $"\nThe haul of ${FormatHelper.GetCommaNumber(session.Run.Haul)} and every buy-in are lost.",
                        Run = session.Run
                    };
                }

                return new DungeonReply
                {
                    Text = notice + body + "\n" + StatusLine(session.Run) + "\n" + ActionsLine(session.Run),
                    Run = session.Run
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
                string notice = await ExpireIfIdle(userId);
                Session session = SessionOf(userId);
                if (session == null)
                    return new DungeonReply { Text = notice + NoRunMessage };
                if (!session.Started)
                    return new DungeonReply { Text = notice + "The lobby hasn't started yet, so there's nothing to cash out." };
                if (!IsLiving(session.Run, userId))
                    return new DungeonReply { Text = notice + "You are down, so you can't vote." };

                session.Run.LastActivityUtc = DateTime.UtcNow;
                if (!session.Run.VoteCashOut(userId))
                {
                    return new DungeonReply
                    {
                        Text = notice + $"Cash out vote recorded: {session.Run.CashOutVotes.Count} of {session.Run.LivingCount} living members. " +
                            $"More than half must vote. Votes reset when the party enters the next room.",
                        Run = session.Run
                    };
                }

                IReadOnlyDictionary<ulong, double> payouts = session.Run.CashOut();
                Remove(session);
                await PayOut(session.Run, payouts);

                var lines = payouts.Select(p => $"{session.Run.Member(p.Key).Name} takes ${FormatHelper.GetCommaNumber(p.Value)}");
                return new DungeonReply
                {
                    Text = notice + $"The party climbs out alive after {session.Run.Floor} rooms and {session.Run.Kills} kills.\n" +
                        string.Join("\n", lines) + "\n" +
                        $"Buy-ins were ${FormatHelper.GetCommaNumber(session.Run.BuyIn)} each. Living members split the ${FormatHelper.GetCommaNumber(session.Run.Haul)} haul."
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
                string notice = await ExpireIfIdle(userId);
                Session session = SessionOf(userId);
                if (session == null)
                    return new DungeonReply { Text = notice + NoRunMessage };
                if (!session.Started)
                    return new DungeonReply { Text = notice + "The lobby hasn't started yet." };

                session.Run.LastActivityUtc = DateTime.UtcNow;
                bool bought = session.Run.TryBuyOffer(userId, out string message);
                return new DungeonReply
                {
                    Text = notice + message + (bought ? "\n" + StatusLine(session.Run) : ""),
                    Run = bought ? session.Run : null
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
                string notice = await ExpireIfIdle(userId);
                Session session = SessionOf(userId);
                if (session == null)
                    return new DungeonReply { Text = notice + NoRunMessage };
                if (!session.Started)
                    return new DungeonReply { Text = notice + "The lobby hasn't started yet." };

                session.Run.LastActivityUtc = DateTime.UtcNow;
                session.Run.TryDrink(userId, out string message);
                return new DungeonReply
                {
                    Text = notice + message + "\n" + StatusLine(session.Run),
                    Run = session.Run
                };
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<DungeonReply> Status(ulong userId)
        {
            await _gate.WaitAsync();
            try
            {
                string notice = await ExpireIfIdle(userId);
                Session session = SessionOf(userId);
                if (session == null)
                    return new DungeonReply { Text = notice + NoRunMessage };

                if (!session.Started)
                {
                    double secondsLeft = Math.Max(0, (LobbyWait - (DateTime.UtcNow - session.OpenedUtc)).TotalSeconds);
                    return new DungeonReply
                    {
                        Text = notice + $"Lobby open: {session.Run.Members.Count}/{DungeonRun.MaxPartySize} players, ${FormatHelper.GetCommaNumber(session.Run.BuyIn)} buy-in each. " +
                            $"Starts in {Math.Ceiling(secondsLeft)} seconds. Type `.dng join` to enter."
                    };
                }

                return new DungeonReply
                {
                    Text = notice + StatusLine(session.Run) + "\n" + ActionsLine(session.Run),
                    Run = session.Run
                };
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Starts the lobby after <see cref="LobbyWait"/>, unless it was already started or replaced by a newer lobby.</summary>
        private async Task AutoStartLater(int generation, ulong hostId)
        {
            await Task.Delay(LobbyWait);

            string text;
            IMessageChannel channel;
            await _gate.WaitAsync();
            try
            {
                Session session = SessionOf(hostId);
                if (session == null || session.Started || session.Generation != generation)
                    return;

                channel = session.Channel;
                text = BeginRun(session);
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

        private Session SessionOf(ulong userId)
        {
            return _sessionByUser.TryGetValue(userId, out Session session) ? session : null;
        }

        private static bool IsLiving(DungeonRun run, ulong userId)
        {
            DungeonMember member = run.Member(userId);
            return member != null && member.Alive;
        }

        private void Remove(Session session)
        {
            foreach (DungeonMember member in session.Run.Members)
                _sessionByUser.Remove(member.UserId);
        }

        /// <summary>
        /// Ends a started run that has sat idle too long and pays out the living members, so an abandoned party keeps what it earned.
        /// This is checked lazily when a party member next uses a dungeon command, so the payout can wait that long.
        /// </summary>
        private async Task<string> ExpireIfIdle(ulong userId)
        {
            Session session = SessionOf(userId);
            if (session == null || !session.Started || session.Run.IsOver)
                return "";
            if (DateTime.UtcNow - session.Run.LastActivityUtc < IdleLimit)
                return "";

            IReadOnlyDictionary<ulong, double> payouts = session.Run.CashOut();
            Remove(session);
            await PayOut(session.Run, payouts);
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
