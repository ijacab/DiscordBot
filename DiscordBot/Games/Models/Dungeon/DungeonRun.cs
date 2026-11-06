using System;
using System.Collections.Generic;

namespace DiscordBot.Games.Models.Dungeon
{
    public enum RoomType { Monster, Boss, Treasure, Trap, Merchant, Shrine, Gear, Empty }

    /// <summary>What the merchant or shrine is offering right now. Cleared as soon as the next room is entered.</summary>
    public enum PendingOffer { None, Potion, Blessing }

    public class RoomResult
    {
        public RoomType Type { get; set; }
        public List<string> Lines { get; } = new List<string>();
        /// <summary>True when the player went down in this room. The run is over and the haul is lost.</summary>
        public bool Died { get; set; }
    }

    /// <summary>
    /// One player's solo crawl. The buy-in is paid up front, gold found on the way is the haul, and cashing out banks it.
    /// Dying loses the haul and the buy-in. Surviving runs can be entered into PvP when the session timer ends.
    /// Pure game logic with no Discord or money calls, so it can be tested with a seeded Random.
    /// </summary>
    public class DungeonRun
    {
        public const int BossEvery = 5;
        public const int StartingHp = 30;
        public const int PotionHealing = 12;
        public const int BlessingAttackBonus = 2;
        public const double BlessingHaulCost = 0.20;
        public const int MaxRoundsPerFight = 20;

        private static readonly string[] MonsterNames = { "Cave Rat", "Skeleton", "Goblin Thief", "Slime", "Bog Wight", "Giant Bat", "Orc Scout", "Crypt Ghoul" };
        private static readonly string[] BossNames = { "The Loan Shark", "Margin Call Golem", "The Tax Dragon", "Hedge Fund Hydra", "Stock Crash Wraith" };
        private static readonly string[] WeaponNames = { "Rusty Sword", "Notched Axe", "Bent Spear" };
        private static readonly string[] ArmorNames = { "Dented Shield", "Patched Leather", "Cracked Helm" };

        private readonly Random _random;

        public DungeonRun(ulong userId, string name, double buyIn, Random random = null)
        {
            UserId = userId;
            Name = name;
            BuyIn = buyIn;
            _random = random ?? new Random();
            Hp = StartingHp;
            MaxHp = StartingHp;
        }

        public ulong UserId { get; }
        public string Name { get; }
        public double BuyIn { get; }
        /// <summary>Gold found so far. Paid out on cash out, or split in PvP.</summary>
        public double Haul { get; private set; }
        /// <summary>Rooms entered so far. The next room is Floor + 1.</summary>
        public int Floor { get; private set; }
        /// <summary>Type of the most recent room, or null before the first room.</summary>
        public RoomType? LastRoom { get; private set; }
        public int Hp { get; private set; }
        public int MaxHp { get; private set; }
        public int Attack { get; private set; } = 4;
        public int Defense { get; private set; } = 1;
        public int Potions { get; private set; }
        public int Kills { get; private set; }
        public PendingOffer Offer { get; private set; } = PendingOffer.None;
        /// <summary>True when the player can open one loot box before the next room.</summary>
        public bool CanLoot { get; private set; }
        /// <summary>False once the player is down.</summary>
        public bool Alive { get; private set; } = true;
        /// <summary>True once the run is finished, by death or by cashing out.</summary>
        public bool IsOver { get; private set; }

        public int NextFloor => Floor + 1;
        public bool NextIsBoss => NextFloor % BossEvery == 0;

        /// <summary>Potions cost a slice of the buy-in, so a bigger stake buys more useful potions.</summary>
        public double PotionPrice => Math.Max(1, Math.Round(BuyIn * 0.04, 2));
        public double BlessingPrice => Math.Round(Haul * BlessingHaulCost, 2);

        /// <summary>Enters the next room and resolves it.</summary>
        public RoomResult Descend()
        {
            if (IsOver || !Alive)
                throw new InvalidOperationException("This run is over.");

            Offer = PendingOffer.None;
            Floor++;
            var result = new RoomResult { Type = PickRoom() };
            LastRoom = result.Type;
            CanLoot = true;

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
                        result.Lines.Add($"**Floor {Floor}:** a shrine offers +{BlessingAttackBonus} attack for ${BlessingPrice:N2} of your haul. Use `.dng buy` to accept.");
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
                    int rested = Math.Min(4, MaxHp - Hp);
                    Hp += rested;
                    result.Lines.Add($"**Floor {Floor}:** an empty room. You rest for {rested} HP.");
                    break;
            }

            if (Hp <= 0)
            {
                Alive = false;
                IsOver = true;
                result.Died = true;
                result.Lines.Add($"You went down on floor {Floor}.");
            }
            return result;
        }

        /// <summary>Spends the pending merchant or shrine offer. Returns false if nothing is for sale or the haul can't cover it.</summary>
        public bool TryBuyOffer(out string message)
        {
            message = null;
            if (!Alive)
            {
                message = "You are down. Nothing can be bought.";
                return false;
            }

            switch (Offer)
            {
                case PendingOffer.Potion:
                    if (Haul < PotionPrice)
                    {
                        message = $"You need ${PotionPrice:N2} of haul for a potion. You have ${Haul:N2}.";
                        return false;
                    }
                    Haul = Math.Round(Haul - PotionPrice, 2);
                    Potions++;
                    Offer = PendingOffer.None;
                    message = $"You buy a potion for ${PotionPrice:N2}. You now have {Potions}.";
                    return true;
                case PendingOffer.Blessing:
                    double cost = BlessingPrice;
                    Haul = Math.Round(Haul - cost, 2);
                    Attack += BlessingAttackBonus;
                    Offer = PendingOffer.None;
                    message = $"The shrine takes ${cost:N2} from your haul. Your attack rises to {Attack}.";
                    return true;
                default:
                    message = "Nothing is for sale right now.";
                    return false;
            }
        }

        /// <summary>Drinks a potion. Returns false if there are none or you're already at full health.</summary>
        public bool TryDrink(out string message)
        {
            message = null;
            if (!Alive)
            {
                message = "You are down. You can't drink.";
                return false;
            }
            if (Potions <= 0)
            {
                message = "You have no potions.";
                return false;
            }
            if (Hp >= MaxHp)
            {
                message = "You're already at full health.";
                return false;
            }

            Potions--;
            int before = Hp;
            Hp = Math.Min(MaxHp, Hp + PotionHealing);
            message = $"You drink a potion and heal {Hp - before} HP.";
            return true;
        }

        /// <summary>Applies a loot box's boosts to this run and uses up this room's box.</summary>
        public void ApplyLoot(LootResult loot)
        {
            if (!Alive)
                throw new InvalidOperationException("You are down.");
            if (!CanLoot)
                throw new InvalidOperationException("You already opened a loot box this room.");

            Attack += loot.AttackGain;
            Defense += loot.DefenseGain;
            MaxHp += loot.MaxHpGain;
            Hp = Math.Min(MaxHp, Hp + loot.MaxHpGain);
            CanLoot = false;
        }

        /// <summary>Ends the run and returns the haul to bank. The caller pays it out.</summary>
        public double CashOut()
        {
            if (IsOver || !Alive)
                throw new InvalidOperationException("This run is over.");
            IsOver = true;
            return Haul;
        }

        /// <summary>Takes the haul out of this run, for a PvP pot. The run is finished afterwards.</summary>
        public double TakeHaulForPvp()
        {
            double taken = Haul;
            Haul = 0;
            IsOver = true;
            return taken;
        }

        /// <summary>Splits an amount evenly among the given players, rounded to cents, with the remainder to the first.</summary>
        public static IReadOnlyDictionary<ulong, double> SplitHaul(double amount, IReadOnlyList<ulong> recipients)
        {
            var payouts = new Dictionary<ulong, double>();
            if (recipients.Count == 0)
                return payouts;

            double share = Math.Floor(amount / recipients.Count * 100) / 100;
            double remainder = Math.Round(amount - share * recipients.Count, 2);
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

        private double Gold(double fraction)
        {
            return Math.Round(BuyIn * fraction * (1 + Floor * 0.08), 2);
        }

        private void AddGold(RoomResult result, double amount)
        {
            Haul = Math.Round(Haul + amount, 2);
            result.Lines.Add($"You pick up ${amount:N2}.");
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
                int damage = Math.Max(1, 3 + Floor / 2 + _random.Next(1, 7) - Defense);
                Hp -= damage;
                result.Lines.Add($"**Floor {Floor}:** a spike trap. You take {damage} damage.");
            }
            else
            {
                double lost = Math.Round(Haul * 0.15, 2);
                Haul = Math.Round(Haul - lost, 2);
                result.Lines.Add($"**Floor {Floor}:** a pickpocket trap. You lose ${lost:N2} of your haul.");
            }
        }

        private void FindGear(RoomResult result)
        {
            if (_random.NextDouble() < 0.5)
            {
                Attack++;
                result.Lines.Add($"**Floor {Floor}:** a {Pick(WeaponNames)}. Attack +1 (now {Attack}).");
            }
            else
            {
                Defense++;
                MaxHp += 2;
                Hp += 2;
                result.Lines.Add($"**Floor {Floor}:** a {Pick(ArmorNames)}. Defense +1 (now {Defense}), max HP +2.");
            }
        }

        /// <summary>
        /// Auto-resolves a fight, round by round. Stops when the monster dies, you go down, or the round cap is reached.
        /// </summary>
        private void Fight(RoomResult result, bool boss)
        {
            int monsterHp = (boss ? 16 + Floor * 2 : 5 + Floor * 2) + _random.Next(0, 5);
            int monsterAttack = boss ? 2 + Floor / 4 : 1 + Floor / 3;
            int enrageAt = monsterHp / 2;
            bool enraged = false;
            int dealt = 0, taken = 0, rounds = 0;

            while (rounds < MaxRoundsPerFight && Hp > 0)
            {
                rounds++;
                int hit = Attack + _random.Next(1, 7);
                monsterHp -= hit;
                dealt += hit;
                if (monsterHp <= 0)
                    break;

                if (boss && !enraged && monsterHp <= enrageAt)
                {
                    enraged = true;
                    monsterAttack += 1;
                    result.Lines.Add($"The boss enrages at {monsterHp} HP!");
                }

                int counter = Math.Max(1, monsterAttack + _random.Next(1, 5) - Defense);
                Hp -= counter;
                taken += counter;
            }

            if (monsterHp <= 0)
            {
                Kills++;
                result.Lines.Add($"Won in {rounds} round{(rounds == 1 ? "" : "s")}. You dealt {dealt} and took {taken}.");
                if (boss)
                {
                    MaxHp += 5;
                    Hp = MaxHp;
                    Potions++;
                    result.Lines.Add("The boss drops a potion, you are fully healed, and max HP rises by 5.");
                    AddGold(result, Gold(0.6 + _random.NextDouble() * 0.3));
                }
                else
                {
                    AddGold(result, Gold(0.05 + _random.NextDouble() * 0.07));
                    if (_random.NextDouble() < 0.25)
                    {
                        Potions++;
                        result.Lines.Add("It drops a potion.");
                    }
                }
            }
            else if (Hp <= 0)
            {
                result.Lines.Add($"It wore you down in {rounds} round{(rounds == 1 ? "" : "s")}.");
            }
            else
            {
                result.Lines.Add($"The fight drags on. You slip away after {rounds} rounds.");
            }
        }

        private string Pick(string[] options)
        {
            return options[_random.Next(options.Length)];
        }
    }
}
