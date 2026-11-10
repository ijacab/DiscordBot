using Common.Helpers;
using DiscordBot.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DiscordBot.Games.Models.Dungeon
{
    /// <summary>Money the manager owes a player: a refund, a cash out, or nothing else. The session never touches accounts.</summary>
    public sealed class DungeonPayout
    {
        public ulong UserId { get; set; }
        public string Name { get; set; }
        public double Amount { get; set; }
    }

    /// <summary>
    /// One dungeon session: a lobby, then every player's run crawling floor by floor. Pure game state with an injected clock
    /// (every call takes "now") and Random, so it can be tested without Discord or money.
    /// The manager calls <see cref="Tick"/> every second or so. Each tick moves every crawling run one floor, which is what
    /// animates the dashboard. A run stops at each boss door for its player to fight, sneak or drink a potion, and after
    /// floor 10 of a block at the checkpoint to cash out or enter the PvP pool. Actions return a refusal, or null when they worked.
    /// </summary>
    public class DungeonSession
    {
        public const double MinBuyIn = 100;
        public const int MaxPlayers = 4;
        public const int LogLines = 8;
        public static readonly TimeSpan LobbyWait = TimeSpan.FromSeconds(60);
        public static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(15);
        /// <summary>A boss door left alone this long is fought.</summary>
        public static readonly TimeSpan BossDoorWait = TimeSpan.FromSeconds(60);
        /// <summary>How long a loot card stays on the dashboard.</summary>
        public static readonly TimeSpan DropShownFor = TimeSpan.FromSeconds(8);
        /// <summary>The PvP id of a solo run's shadow rival. It's never a player's id, since no player is 0.</summary>
        public const ulong ShadowId = 0;

        private readonly Random _random;
        private readonly Dictionary<ulong, DungeonRun> _runs = new Dictionary<ulong, DungeonRun>();
        private readonly Dictionary<ulong, DateTime> _lastAction = new Dictionary<ulong, DateTime>();
        private readonly Dictionary<ulong, DateTime> _atDoorSince = new Dictionary<ulong, DateTime>();
        private readonly HashSet<ulong> _descending = new HashSet<ulong>();
        private readonly List<ScenePlayer> _departed = new List<ScenePlayer>();
        private readonly List<string> _log = new List<string>();
        private readonly List<DungeonPayout> _payouts = new List<DungeonPayout>();

        public DungeonSession(ulong hostId, string hostName, double buyIn, DateTime now, Random random = null)
        {
            if (buyIn < MinBuyIn)
                throw new ArgumentException($"The buy-in must be at least {MinBuyIn}.");
            _random = random ?? new Random();
            HostId = hostId;
            BuyIn = buyIn;
            OpenedUtc = now;
            AddRun(hostId, hostName, now);
            AddLog($"{hostName} opens a lobby with a ${FormatHelper.GetCommaNumber(buyIn)} buy-in.");
        }

        public ulong HostId { get; private set; }
        public double BuyIn { get; }
        public DateTime OpenedUtc { get; }
        public bool Started { get; private set; }
        public bool Ended { get; private set; }
        /// <summary>Why the session ended, for the final dashboard.</summary>
        public string ResultText { get; private set; }
        /// <summary>True when something on the dashboard changed since <see cref="MarkPublished"/>.</summary>
        public bool Changed { get; private set; } = true;

        /// <summary>Live runs in join order.</summary>
        public IReadOnlyList<DungeonRun> Runs => _runs.Values.ToList();
        /// <summary>Players who cashed out, went down, left or lost a pool.</summary>
        public IReadOnlyList<ScenePlayer> Departed => _departed;
        public IReadOnlyList<string> Log => _log;

        //what the scene picture shows: the latest room any run entered
        public SpriteKind Sprite { get; private set; } = SpriteKind.None;
        public string Caption { get; private set; }
        public int? EnemyHp { get; private set; }
        public int? EnemyMaxHp { get; private set; }
        /// <summary>The latest loot drop, shown beside the scene until <see cref="DropShownUntil"/>.</summary>
        public LootItem Drop { get; private set; }
        public string DropOwner { get; private set; }
        public DateTime DropShownUntil { get; private set; }

        public DungeonRun RunOf(ulong userId) => _runs.TryGetValue(userId, out DungeonRun run) ? run : null;
        public bool Has(ulong userId) => _runs.ContainsKey(userId);
        /// <summary>True while a run is crawling floor by floor on its own, between Go and the next stop.</summary>
        public bool IsDescending(ulong userId) => _descending.Contains(userId);

        /// <summary>When a run at a boss door gets fought for it, or null if it isn't at one.</summary>
        public DateTime? BossDoorDeadline(ulong userId) =>
            _atDoorSince.TryGetValue(userId, out DateTime since) ? since + BossDoorWait : (DateTime?)null;

        /// <summary>A short state label for the dashboard.</summary>
        public string StateOf(DungeonRun run)
        {
            switch (run.Phase)
            {
                case RunPhase.AtBossDoor: return "BOSS DOOR";
                case RunPhase.AtCheckpoint: return "CHECKPOINT";
                case RunPhase.InPool: return "IN POOL";
                case RunPhase.Crawling: return !Started ? "READY" : IsDescending(run.UserId) ? "CRAWLING" : "WAITING";
                default: return "";
            }
        }

        /// <summary>Payouts and refunds since the last call. The manager pays them.</summary>
        public List<DungeonPayout> TakePayouts()
        {
            var taken = _payouts.ToList();
            _payouts.Clear();
            return taken;
        }

        public void MarkPublished() => Changed = false;

        /// <summary>Joins the lobby. The caller checks the player can afford <see cref="BuyIn"/> and takes it on success.</summary>
        public string Join(ulong userId, string name, double netWorth, DateTime now)
        {
            if (Ended) return "This dungeon has ended.";
            if (Started) return "The crawl has already started.";
            if (_runs.ContainsKey(userId)) return "You're already in this dungeon.";
            if (_runs.Count >= MaxPlayers) return $"The lobby is full at {MaxPlayers} players.";
            if (BuyIn > netWorth) return $"The buy-in is ${FormatHelper.GetCommaNumber(BuyIn)} and you only have ${FormatHelper.GetCommaNumber(netWorth)}.";

            AddRun(userId, name, now);
            AddLog($"{name} joins and pays the ${FormatHelper.GetCommaNumber(BuyIn)} buy-in.");
            return null;
        }

        /// <summary>Leaves the lobby with a refund. The host's place passes on; an empty lobby closes.</summary>
        public string Leave(ulong userId, DateTime now)
        {
            DungeonRun run = RunOf(userId);
            if (run == null) return "You're not in this dungeon.";
            if (Started) return "The crawl has started, so you can't leave. Cash out instead.";

            _runs.Remove(userId);
            _lastAction.Remove(userId);
            Pay(run.UserId, run.Name, BuyIn);
            AddLog($"{run.Name} leaves the lobby and gets the buy-in back.");
            if (_runs.Count == 0)
            {
                End("The lobby closed. Everyone got their buy-in back.");
            }
            else if (HostId == userId)
            {
                HostId = _runs.Keys.First();
                AddLog($"{_runs[HostId].Name} is the host now.");
            }
            return null;
        }

        /// <summary>The host starts the crawl before the lobby timer runs out.</summary>
        public string Start(ulong userId, DateTime now)
        {
            if (!_runs.ContainsKey(userId)) return "You're not in this dungeon.";
            if (Started) return "The crawl has already started.";
            if (userId != HostId) return "Only the host can start early. Otherwise it starts on its own.";
            Begin(now);
            return null;
        }

        /// <summary>Starts crawling the next block. The run then moves one floor a tick until its next stop.</summary>
        public string Go(ulong userId, DateTime now)
        {
            DungeonRun run = RunOf(userId);
            if (run == null) return "You're not in this dungeon.";
            if (!Started) return "The lobby is still open. Wait for it to start, or the host starts it.";
            switch (run.Phase)
            {
                case RunPhase.AtBossDoor: return "A boss blocks the way. Fight it, sneak past, or drink a potion first.";
                case RunPhase.AtCheckpoint: return "You're at a checkpoint. Cash out to bank your haul, or enter the PvP pool.";
                case RunPhase.InPool: return "You're waiting in the PvP pool for the others to decide.";
            }
            if (_descending.Contains(userId)) return "You're already crawling.";

            Touch(userId, now);
            _descending.Add(userId);
            AddLog($"{run.Name} heads down into block {run.NextBlockNumber}.");
            return null;
        }

        /// <summary>Banks the haul and leaves the dungeon.</summary>
        public string Cash(ulong userId, DateTime now)
        {
            DungeonRun run = RunOf(userId);
            if (run == null) return "You're not in this dungeon.";
            if (!Started) return "The crawl hasn't started, so there's nothing to cash out. Leave the lobby to get your buy-in back.";
            if (run.Phase == RunPhase.InPool) return "You're in the PvP pool, so you can't cash out now.";

            double payout = run.CashOut();
            Depart(run, "CASHED OUT", payout, run.Hp);
            Pay(run.UserId, run.Name, payout);
            AddLog($"{run.Name} cashes out ${payout:N2} after {run.Floor} floors and {run.Kills} kills.");
            TryResolvePool(now);
            return null;
        }

        /// <summary>At a checkpoint, enters the PvP pool with the haul at stake.</summary>
        public string EnterPool(ulong userId, DateTime now)
        {
            DungeonRun run = RunOf(userId);
            if (run == null) return "You're not in this dungeon.";
            if (run.Phase != RunPhase.AtCheckpoint) return "You can only enter the pool at a checkpoint, after clearing a block.";

            Touch(userId, now);
            run.EnterPool();
            AddLog($"{run.Name} enters the PvP pool with ${run.Haul:N2} at stake.");
            TryResolvePool(now);
            return null;
        }

        /// <summary>Fights, sneaks past, or drinks a potion and fights, at a boss door.</summary>
        public string Boss(ulong userId, BossChoice choice, DateTime now)
        {
            DungeonRun run = RunOf(userId);
            if (run == null) return "You're not in this dungeon.";
            if (run.Phase != RunPhase.AtBossDoor) return "You're not at a boss door.";
            if (choice == BossChoice.Potion && run.Potions == 0) return "You have no potions.";

            Touch(userId, now);
            FaceBoss(run, choice, now);
            return null;
        }

        /// <summary>Drinks a potion. At a boss door this also starts the fight, so the heal isn't wasted.</summary>
        public string Potion(ulong userId, DateTime now)
        {
            DungeonRun run = RunOf(userId);
            if (run == null) return "You're not in this dungeon.";
            if (!Started) return "The crawl hasn't started yet.";
            if (run.Potions == 0) return "You have no potions. Treasure chests and quiet corridors sometimes have one.";
            if (run.Phase == RunPhase.AtBossDoor) return Boss(userId, BossChoice.Potion, now);
            if (run.Phase == RunPhase.InPool) return "You're in the PvP pool, so you can't drink now.";
            if (run.Hp >= run.MaxHp) return "You're already at full HP.";

            Touch(userId, now);
            int healed = run.DrinkPotion();
            AddLog($"{run.Name} drinks a potion and heals {healed} HP ({run.Potions} left).");
            return null;
        }

        /// <summary>
        /// Moves the session on: starts the lobby when its timer runs out, moves every crawling run one floor, fights boss
        /// doors left too long, cashes out idle runs, resolves the pool, and clears an old loot card. Returns true if anything changed.
        /// </summary>
        public bool Tick(DateTime now)
        {
            if (Ended)
                return false;

            if (!Started)
            {
                if (now - OpenedUtc >= LobbyWait)
                    Begin(now);
                return Changed;
            }

            foreach (DungeonRun run in _runs.Values.ToList())
            {
                if (Ended)
                    break;
                if (!_runs.ContainsKey(run.UserId))
                    continue; //left during this tick, for example eliminated by a pool another death set off
                if (run.Phase == RunPhase.Crawling && _descending.Contains(run.UserId))
                {
                    RoomResult room = run.RunFloor();
                    Show(run, room, now);
                    if (room.AtBossDoor)
                        _atDoorSince[run.UserId] = now;
                    AfterRoom(run, room, now);
                }
                else if (run.Phase == RunPhase.AtBossDoor && now - _atDoorSince[run.UserId] >= BossDoorWait)
                {
                    AddLog($"{run.Name} waited too long at the door, so the boss attacks.");
                    FaceBoss(run, BossChoice.Fight, now);
                }
                else if (run.Phase != RunPhase.InPool && !_descending.Contains(run.UserId)
                    && _lastAction.TryGetValue(run.UserId, out DateTime last) && now - last >= IdleLimit)
                {
                    double payout = run.CashOut();
                    Depart(run, "IDLE CASH OUT", payout, run.Hp);
                    Pay(run.UserId, run.Name, payout);
                    AddLog($"{run.Name} has been idle for {IdleLimit.TotalMinutes} minutes, so their ${payout:N2} is banked.");
                }
            }

            TryResolvePool(now);

            if (Drop != null && now >= DropShownUntil)
            {
                Drop = null;
                DropOwner = null;
                Changed = true;
            }
            return Changed;
        }

        private void Begin(DateTime now)
        {
            Started = true;
            foreach (ulong id in _runs.Keys.ToList())
                _lastAction[id] = now;
            AddLog("The crawl begins. Press Go to crawl your first block of 10 floors.");
        }

        private void FaceBoss(DungeonRun run, BossChoice choice, DateTime now)
        {
            _atDoorSince.Remove(run.UserId);
            RoomResult room = run.FaceBoss(choice);
            Show(run, room, now);
            AfterRoom(run, room, now);
        }

        /// <summary>After a floor: a downed run departs, and a run that reached the checkpoint stops crawling.</summary>
        private void AfterRoom(DungeonRun run, RoomResult room, DateTime now)
        {
            if (room.Died)
            {
                Depart(run, "DOWN", 0, 0);
                AddLog($"{run.Name}'s haul of ${FormatHelper.GetCommaNumber(run.Haul)} and buy-in are lost.");
                TryResolvePool(now);
                return;
            }
            if (run.Phase == RunPhase.AtCheckpoint)
            {
                _descending.Remove(run.UserId);
                AddLog($"{run.Name} clears block {run.BlocksCleared} with ${run.Haul:N2}. Cash out, or enter the PvP pool.");
            }
        }

        /// <summary>Puts a room on the scene and in the log.</summary>
        private void Show(DungeonRun run, RoomResult room, DateTime now)
        {
            Sprite = room.Enemy;
            Caption = room.EnemyName ?? RoomLabel(room.Type);
            EnemyHp = room.EnemyHp;
            EnemyMaxHp = room.EnemyMaxHp;
            if (room.Drop != null)
            {
                Drop = room.Drop;
                DropOwner = run.Name;
                DropShownUntil = now + DropShownFor;
            }
            foreach (string line in room.Lines)
                AddLog($"{run.Name}: {line}");
            Changed = true;
        }

        /// <summary>
        /// Runs the PvP pool once every run in the session has decided. The pot is the fighters' hauls, and the winner or
        /// winners crawl on with it. Losers are out. The session ends when no runs are left.
        /// </summary>
        private void TryResolvePool(DateTime now)
        {
            if (Ended || !Started)
                return;
            if (_runs.Count == 0)
            {
                End("Nobody is left.");
                return;
            }
            if (!_runs.Values.All(r => r.Phase == RunPhase.InPool))
                return;

            //a solo run has no rival in the pool, so its shadow with the same stats and HP takes the other side
            var pooled = _runs.Values.ToList();
            var fighters = pooled.Select(run => new PvpFighter
            {
                UserId = run.UserId,
                Name = run.Name,
                Stats = run.CloneStats(),
                Haul = run.TakeHaulIntoPool()
            }).ToList();
            if (pooled.Count == 1)
            {
                DungeonRun solo = pooled[0];
                fighters.Add(new PvpFighter { UserId = ShadowId, Name = $"{solo.Name}'s shadow", Stats = solo.CloneStats() });
                AddLog($"{solo.Name} has no rival in the pool, so they face their own shadow.");
            }

            PvpResult result = PvpArena.Resolve(fighters, _random);
            foreach (string line in result.Lines.Skip(Math.Max(0, result.Lines.Count - 4)))
                AddLog(line);

            foreach (DungeonRun run in pooled)
            {
                PvpFighter fighter = fighters.First(f => f.UserId == run.UserId);
                if (result.Payouts.TryGetValue(run.UserId, out double won))
                {
                    run.ReturnFromPool(won, fighter.Stats.Hp);
                    AddLog($"{run.Name} wins ${won:N2}. Press Go to crawl on.");
                }
                else
                {
                    run.Eliminate();
                    Depart(run, "LOST PVP", 0, 0);
                }
            }

            if (_runs.Count == 0)
            {
                End("Nobody is left standing. The pot is lost.");
                return;
            }
            foreach (ulong id in _runs.Keys)
                _lastAction[id] = now;
        }

        private void AddRun(ulong userId, string name, DateTime now)
        {
            _runs[userId] = new DungeonRun(userId, name, BuyIn, _random);
            _lastAction[userId] = now;
            Changed = true;
        }

        private void Depart(DungeonRun run, string status, double haul, int hp)
        {
            _runs.Remove(run.UserId);
            _lastAction.Remove(run.UserId);
            _atDoorSince.Remove(run.UserId);
            _descending.Remove(run.UserId);
            _departed.Add(new ScenePlayer { Name = run.Name, Floor = run.Floor, Hp = hp, MaxHp = run.MaxHp, Haul = haul, Status = status });
            Changed = true;
        }

        private void End(string result)
        {
            Ended = true;
            ResultText = result;
            Changed = true;
        }

        private void Pay(ulong userId, string name, double amount)
        {
            if (amount > 0)
                _payouts.Add(new DungeonPayout { UserId = userId, Name = name, Amount = amount });
        }

        private void Touch(ulong userId, DateTime now)
        {
            if (_lastAction.ContainsKey(userId))
                _lastAction[userId] = now;
            Changed = true;
        }

        private void AddLog(string line)
        {
            if (string.IsNullOrEmpty(line))
                return;
            _log.Add(line);
            while (_log.Count > LogLines)
                _log.RemoveAt(0);
            Changed = true;
        }

        private static string RoomLabel(RoomType type)
        {
            switch (type)
            {
                case RoomType.Treasure: return "TREASURE";
                case RoomType.Trap: return "TRAP";
                case RoomType.Quiet: return "QUIET";
                case RoomType.Gear: return "GEAR";
                default: return "EMPTY ROOM";
            }
        }
    }
}
