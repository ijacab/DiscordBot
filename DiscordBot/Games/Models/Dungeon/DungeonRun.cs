using System;
using System.Collections.Generic;
using System.Linq;

namespace DiscordBot.Games.Models.Dungeon
{
    public enum RoomType { Monster, Boss, Treasure, Trap, Merchant, Shrine, Gear, Empty }

    /// <summary>What the merchant or shrine is offering right now. Cleared as soon as the next room is entered.</summary>
    public enum PendingOffer { None, Potion, Blessing }

    public class RoomResult
    {
        public RoomType Type { get; set; }
        public List<string> Lines { get; } = new List<string>();
        /// <summary>True when every party member is dead. The run is over and the haul is lost.</summary>
        public bool Died { get; set; }
    }

    /// <summary>
    /// A shared dungeon run for a party of up to <see cref="MaxPartySize"/> players. Every member's buy-in goes into
    /// one haul. Cash out splits the haul among the living. Members who die forfeit their share, and the run
    /// ends only when everyone is dead.
    /// Pure game logic with no Discord or money calls, so it can be tested with a seeded Random.
    /// </summary>
    public class DungeonRun
    {
        public const int MaxPartySize = 4;
        public const int BossEvery = 5;
        public const int PotionHealing = 12;
        public const int BlessingAttackBonus = 2;
        public const double BlessingHaulCost = 0.20;
        public const int MaxRoundsPerFight = 20;

        private static readonly string[] MonsterNames = { "Cave Rat", "Skeleton", "Goblin Thief", "Slime", "Bog Wight", "Giant Bat", "Orc Scout", "Crypt Ghoul" };
        private static readonly string[] BossNames = { "The Loan Shark", "Margin Call Golem", "The Tax Dragon", "Hedge Fund Hydra", "Stock Crash Wraith" };
        private static readonly string[] WeaponNames = { "Rusty Sword", "Notched Axe", "Bent Spear" };
        private static readonly string[] ArmorNames = { "Dented Shield", "Patched Leather", "Cracked Helm" };

        private readonly Random _random;
        private readonly List<DungeonMember> _members = new List<DungeonMember>();

        public DungeonRun(double buyInPerMember, Random random = null)
        {
            BuyIn = buyInPerMember;
            _random = random ?? new Random();
            LastActivityUtc = DateTime.UtcNow;
        }

        /// <summary>Buy-in each member paid.</summary>
        public double BuyIn { get; }
        public IReadOnlyList<DungeonMember> Members => _members;
        public IEnumerable<DungeonMember> Living => _members.Where(m => m.Alive);
        public int LivingCount => _members.Count(m => m.Alive);
        /// <summary>Gold found so far, shared by the party. Paid out at cash out.</summary>
        public double Haul { get; private set; }
        /// <summary>Rooms entered so far. The next room is Floor + 1.</summary>
        public int Floor { get; private set; }
        /// <summary>Type of the most recent room, or null before the first room.</summary>
        public RoomType? LastRoom { get; private set; }
        public int Kills { get; private set; }
        public PendingOffer Offer { get; private set; } = PendingOffer.None;
        /// <summary>Living members who have voted to cash out since the last room.</summary>
        public IReadOnlyCollection<ulong> CashOutVotes => _cashOutVotes;
        private readonly HashSet<ulong> _cashOutVotes = new HashSet<ulong>();
        /// <summary>True once the run has ended, by everyone dying or by cashing out.</summary>
        public bool IsOver { get; private set; }
        public DateTime LastActivityUtc { get; set; }

        public int NextFloor => Floor + 1;
        public bool NextIsBoss => NextFloor % BossEvery == 0;

        /// <summary>Potions cost a slice of the buy-in, so a bigger stake buys more useful potions.</summary>
        public double PotionPrice => Math.Max(1, Math.Round(BuyIn * 0.04, 2));
        public double BlessingPrice => Math.Round(Haul * BlessingHaulCost, 2);

        public DungeonMember Member(ulong userId) => _members.FirstOrDefault(m => m.UserId == userId);

        /// <summary>Adds a player before the run starts. The buy-in is already paid by the caller.</summary>
        public DungeonMember AddMember(ulong userId, string name)
        {
            if (Floor > 0 || IsOver)
                throw new InvalidOperationException("The run has already started.");
            if (_members.Count >= MaxPartySize)
                throw new InvalidOperationException("The party is full.");
            if (Member(userId) != null)
                throw new InvalidOperationException("Already in this party.");

            var member = new DungeonMember(userId, name);
            _members.Add(member);
            return member;
        }

        /// <summary>Enters the next room for the whole party and resolves it.</summary>
        public RoomResult Descend()
        {
            if (IsOver)
                throw new InvalidOperationException("This run is over.");
            if (_members.Count == 0)
                throw new InvalidOperationException("No one is in this run.");

            Offer = PendingOffer.None;
            _cashOutVotes.Clear();
            Floor++;
            var result = new RoomResult { Type = PickRoom() };
            LastRoom = result.Type;

            switch (result.Type)
            {
                case RoomType.Boss:
                    result.Lines.Add($"**Floor {Floor}: BOSS.** {Pick(BossNames)} blocks the way.");
                    Fight(result, boss: true);
                    break;
                case RoomType.Monster:
                    result.Lines.Add($"**Floor {Floor}:** a {Pick(MonsterNames)} lunges out of the dark.");
                    Fight(result, boss: false);
                    break;
                case RoomType.Treasure:
                    FindTreasure(result);
                    break;
                case RoomType.Trap:
                    SpringTrap(result);
                    break;
                case RoomType.Merchant:
                    Offer = PendingOffer.Potion;
                    result.Lines.Add($"**Floor {Floor}:** a merchant sells potions for ${PotionPrice:N2}. Use `.dng buy` to take one.");
                    break;
                case RoomType.Shrine:
                    if (Haul >= 1)
                    {
                        Offer = PendingOffer.Blessing;
                        result.Lines.Add($"**Floor {Floor}:** a shrine offers +{BlessingAttackBonus} attack for ${BlessingPrice:N2} of the haul. Use `.dng buy` to accept.");
                    }
                    else
                    {
                        result.Lines.Add($"**Floor {Floor}:** an empty shrine. It wants gold you don't have, so you walk on.");
                    }
                    break;
                case RoomType.Gear:
                    FindGear(result);
                    break;
                default:
                    foreach (DungeonMember m in Living)
                    {
                        int rested = Math.Min(4, m.MaxHp - m.Hp);
                        m.Hp += rested;
                    }
                    result.Lines.Add($"**Floor {Floor}:** an empty room. The party rests.");
                    break;
            }

            if (LivingCount == 0)
            {
                IsOver = true;
                result.Died = true;
                result.Lines.Add($"Everyone is down on floor {Floor}.");
            }
            return result;
        }

        /// <summary>
        /// A living member spends the pending merchant or shrine offer. Returns false if nothing is for sale,
        /// the member is down, or the haul can't cover it.
        /// </summary>
        public bool TryBuyOffer(ulong userId, out string message)
        {
            message = null;
            DungeonMember buyer = Member(userId);
            if (buyer == null || !buyer.Alive)
            {
                message = "Only living party members can buy.";
                return false;
            }

            switch (Offer)
            {
                case PendingOffer.Potion:
                    if (Haul < PotionPrice)
                    {
                        message = $"The haul needs at least ${PotionPrice:N2} to buy a potion. It has ${Haul:N2}.";
                        return false;
                    }
                    Haul = Math.Round(Haul - PotionPrice, 2);
                    buyer.Potions++;
                    Offer = PendingOffer.None;
                    message = $"{buyer.Name} buys a potion for ${PotionPrice:N2}. They now have {buyer.Potions}.";
                    return true;
                case PendingOffer.Blessing:
                    double cost = BlessingPrice;
                    Haul = Math.Round(Haul - cost, 2);
                    buyer.Attack += BlessingAttackBonus;
                    Offer = PendingOffer.None;
                    message = $"The shrine takes ${cost:N2} from the haul. {buyer.Name}'s attack rises to {buyer.Attack}.";
                    return true;
                default:
                    message = "Nothing is for sale right now.";
                    return false;
            }
        }

        /// <summary>A living member drinks one of their own potions. Returns false if they have none or are at full health.</summary>
        public bool TryDrink(ulong userId, out string message)
        {
            message = null;
            DungeonMember member = Member(userId);
            if (member == null || !member.Alive)
            {
                message = "Only living party members can drink.";
                return false;
            }
            if (member.Potions <= 0)
            {
                message = $"{member.Name} has no potions.";
                return false;
            }
            if (member.Hp >= member.MaxHp)
            {
                message = $"{member.Name} is already at full health.";
                return false;
            }

            member.Potions--;
            int before = member.Hp;
            member.Hp = Math.Min(member.MaxHp, member.Hp + PotionHealing);
            message = $"{member.Name} drinks a potion and heals {member.Hp - before} HP.";
            return true;
        }

        /// <summary>
        /// Records a living member's vote to cash out. Returns true once more than half of the living members have voted.
        /// </summary>
        public bool VoteCashOut(ulong userId)
        {
            DungeonMember member = Member(userId);
            if (member == null || !member.Alive)
                throw new InvalidOperationException("Only living party members can vote.");
            _cashOutVotes.Add(userId);
            return _cashOutVotes.Count * 2 > LivingCount;
        }

        /// <summary>
        /// Ends the run and returns each living member's share of the haul. Dead members get nothing.
        /// Any rounding remainder goes to the first living member so the payouts add up exactly.
        /// </summary>
        public IReadOnlyDictionary<ulong, double> CashOut()
        {
            if (IsOver)
                throw new InvalidOperationException("This run is over.");
            IsOver = true;
            return SplitHaul(Haul, Living.Select(m => m.UserId).ToList());
        }

        /// <summary>Splits an amount evenly among the given players, rounded to cents, with the remainder to the first.</summary>
        public static IReadOnlyDictionary<ulong, double> SplitHaul(double haul, IReadOnlyList<ulong> recipients)
        {
            var payouts = new Dictionary<ulong, double>();
            if (recipients.Count == 0)
                return payouts;

            double share = Math.Floor(haul / recipients.Count * 100) / 100;
            double remainder = Math.Round(haul - share * recipients.Count, 2);
            for (int i = 0; i < recipients.Count; i++)
                payouts[recipients[i]] = share + (i == 0 ? remainder : 0);
            return payouts;
        }

        private RoomType PickRoom()
        {
            if (NextIsBoss)
                return RoomType.Boss;

            double roll = _random.NextDouble();
            if (roll < 0.40) return RoomType.Monster;
            if (roll < 0.52) return RoomType.Treasure;
            if (roll < 0.64) return RoomType.Trap;
            if (roll < 0.72) return RoomType.Merchant;
            if (roll < 0.80) return RoomType.Shrine;
            if (roll < 0.88) return RoomType.Gear;
            return RoomType.Empty;
        }

        /// <summary>Gold scales with party size, so each player's expected return stays close to solo play.</summary>
        private double Gold(double fraction)
        {
            return Math.Round(BuyIn * _members.Count * fraction * (1 + Floor * 0.08), 2);
        }

        private void AddGold(RoomResult result, double amount)
        {
            Haul = Math.Round(Haul + amount, 2);
            result.Lines.Add($"The party picks up ${amount:N2}.");
        }

        private DungeonMember RandomLiving()
        {
            var living = Living.ToList();
            return living[_random.Next(living.Count)];
        }

        private void FindTreasure(RoomResult result)
        {
            if (_random.NextDouble() < 0.15)
            {
                result.Lines.Add($"**Floor {Floor}:** a treasure chest. It bites. It's a mimic!");
                Fight(result, boss: false);
                return;
            }
            result.Lines.Add($"**Floor {Floor}:** a treasure chest, unlocked.");
            AddGold(result, Gold(0.12 + _random.NextDouble() * 0.18));
        }

        private void SpringTrap(RoomResult result)
        {
            if (_random.NextDouble() < 0.5)
            {
                DungeonMember victim = RandomLiving();
                int damage = Math.Max(1, 3 + Floor / 2 + _random.Next(1, 7) - victim.Defense);
                victim.Hp -= damage;
                result.Lines.Add($"**Floor {Floor}:** a spike trap. {victim.Name} takes {damage} damage.");
                if (victim.Hp <= 0) Fall(victim, result);
            }
            else
            {
                double lost = Math.Round(Haul * 0.15, 2);
                Haul = Math.Round(Haul - lost, 2);
                result.Lines.Add($"**Floor {Floor}:** a pickpocket trap. The party loses ${lost:N2} of the haul.");
            }
        }

        private void FindGear(RoomResult result)
        {
            DungeonMember finder = RandomLiving();
            if (_random.NextDouble() < 0.5)
            {
                finder.Attack++;
                result.Lines.Add($"**Floor {Floor}:** a {Pick(WeaponNames)}. {finder.Name}'s attack +1 (now {finder.Attack}).");
            }
            else
            {
                finder.Defense++;
                finder.MaxHp += 2;
                finder.Hp += 2;
                result.Lines.Add($"**Floor {Floor}:** a {Pick(ArmorNames)}. {finder.Name}'s defense +1 (now {finder.Defense}), max HP +2.");
            }
        }

        private void Fall(DungeonMember member, RoomResult result)
        {
            member.Alive = false;
            result.Lines.Add($"{member.Name} falls.");
        }

        /// <summary>
        /// Auto-resolves a fight. Each round every living member attacks, then the monster hits every living member.
        /// Monster HP scales with party size, so each member takes about the same damage as in a solo fight.
        /// Stops when the monster dies, everyone is down, or the round cap is reached.
        /// </summary>
        private void Fight(RoomResult result, bool boss)
        {
            int monsterHp = (boss ? 16 + Floor * 2 : 5 + Floor * 2) + _random.Next(0, 5);
            monsterHp *= _members.Count;
            int monsterAttack = boss ? 2 + Floor / 4 : 1 + Floor / 3;
            int enrageAt = monsterHp / 2;
            bool enraged = false;
            int dealt = 0, taken = 0, rounds = 0;

            while (rounds < MaxRoundsPerFight && LivingCount > 0)
            {
                rounds++;
                foreach (DungeonMember attacker in Living.ToList())
                {
                    int hit = attacker.Attack + _random.Next(1, 7);
                    monsterHp -= hit;
                    dealt += hit;
                    if (monsterHp <= 0) break;
                }

                if (monsterHp <= 0)
                    break;

                if (boss && !enraged && monsterHp <= enrageAt)
                {
                    enraged = true;
                    monsterAttack += 1;
                    result.Lines.Add($"The boss enrages at {monsterHp} HP!");
                }

                foreach (DungeonMember target in Living.ToList())
                {
                    int counter = Math.Max(1, monsterAttack + _random.Next(1, 5) - target.Defense);
                    target.Hp -= counter;
                    taken += counter;
                    if (target.Hp <= 0)
                        Fall(target, result);
                }
            }

            if (monsterHp <= 0)
            {
                Kills++;
                foreach (DungeonMember m in Living) m.Kills++;
                result.Lines.Add($"Won in {rounds} round{(rounds == 1 ? "" : "s")}. The party dealt {dealt} and took {taken}.");
                if (boss)
                {
                    foreach (DungeonMember m in Living)
                    {
                        m.MaxHp += 5;
                        m.Hp = m.MaxHp;
                    }
                    RandomLiving().Potions++;
                    result.Lines.Add("The boss drops a potion, the party is fully healed, and max HP rises by 5.");
                    AddGold(result, Gold(0.6 + _random.NextDouble() * 0.3));
                }
                else
                {
                    AddGold(result, Gold(0.05 + _random.NextDouble() * 0.07));
                    if (_random.NextDouble() < 0.25)
                    {
                        RandomLiving().Potions++;
                        result.Lines.Add("It drops a potion.");
                    }
                }
            }
            else if (LivingCount == 0)
            {
                result.Lines.Add($"The party was wiped out in {rounds} round{(rounds == 1 ? "" : "s")}.");
            }
            else
            {
                result.Lines.Add($"The fight drags on. The party slips away after {rounds} rounds.");
            }
        }

        private string Pick(string[] options)
        {
            return options[_random.Next(options.Length)];
        }
    }
}
