using DiscordBot.Services;
using System;
using System.Collections.Generic;

namespace DiscordBot.Games.Models.Dungeon
{
    public enum RoomType { Monster, Boss, Treasure, Trap, Quiet, Gear, Empty }

    /// <summary>
    /// Where a run is. A run crawls a block of 10 floors, then waits at a checkpoint to cash out or fight.
    /// Fighters wait in the pool until every run in the session has decided. Losers are out, and winners crawl on.
    /// </summary>
    public enum RunPhase { Crawling, AtCheckpoint, InPool, Out }

    public class RoomResult
    {
        public RoomType Type { get; set; }
        public List<string> Lines { get; } = new List<string>();
        /// <summary>True when the player went down in this room. The run is over and the haul is lost.</summary>
        public bool Died { get; set; }
        /// <summary>The enemy this room fought, for the scene picture. None when the room has no fight.</summary>
        public SpriteKind Enemy { get; set; } = SpriteKind.None;
        public string EnemyName { get; set; }
        /// <summary>The enemy's HP when the fight ended, and its maximum, for the HP bar on the dashboard.</summary>
        public int? EnemyHp { get; set; }
        public int? EnemyMaxHp { get; set; }
        /// <summary>Loot that dropped in this room, or null.</summary>
        public LootItem Drop { get; set; }
    }

    /// <summary>
    /// One player's solo crawl, in blocks of 10 floors. The buy-in is paid up front, gold found on the way is the haul,
    /// and cashing out banks it. Monsters get tougher with each block, which keeps every block's odds near 50%.
    /// Fights use the shared <see cref="Combat"/> rules. Loot drops automatically and applies its effects straight away.
    /// Pure game logic with no Discord or money calls, so it can be tested with a seeded Random.
    /// </summary>
    public class DungeonRun
    {
        public const int FloorsPerBlock = 10;
        public const int BossEvery = 5;
        public const int StartingHp = 30;
        public const int StartingAttack = 6;
        /// <summary>
        /// Extra monster strength per block. Loot piles up for players who survive, so each block gets harder to hold the odds near 50%, 25%, 12.5%.
        /// Tuned with the balance sim, and checked by the seeded odds test.
        /// </summary>
        public const double BlockDifficultyStep = 1.12;

        private static readonly (string Name, SpriteKind Sprite)[] Monsters =
        {
            ("Cave Rat", SpriteKind.Rat), ("Skeleton", SpriteKind.Skeleton), ("Goblin Thief", SpriteKind.Skeleton),
            ("Slime", SpriteKind.Slime), ("Bog Wight", SpriteKind.Slime), ("Giant Bat", SpriteKind.Bat),
            ("Orc Scout", SpriteKind.Skeleton), ("Crypt Ghoul", SpriteKind.Skeleton),
        };
        private static readonly (string Name, SpriteKind Sprite)[] Bosses =
        {
            ("The Loan Shark", SpriteKind.Skull), ("Margin Call Golem", SpriteKind.Golem), ("The Tax Dragon", SpriteKind.Dragon),
            ("Hedge Fund Hydra", SpriteKind.Dragon), ("Stock Crash Wraith", SpriteKind.Skull),
        };
        private static readonly string[] WeaponNames = { "Rusty Sword", "Notched Axe", "Bent Spear" };
        private static readonly string[] ArmorNames = { "Dented Shield", "Patched Leather", "Cracked Helm" };

        private readonly Random _random;
        private readonly Combatant _me;
        private readonly List<LootItem> _items = new List<LootItem>();

        public DungeonRun(ulong userId, string name, double buyIn, Random random = null)
        {
            UserId = userId;
            Name = name;
            BuyIn = buyIn;
            _random = random ?? new Random();
            _me = new Combatant { Name = name, Hp = StartingHp, MaxHp = StartingHp, Attack = StartingAttack, Defense = 1 };
        }

        public ulong UserId { get; }
        public string Name { get; }
        public double BuyIn { get; }
        /// <summary>Gold found so far. Paid out on cash out, or split in PvP.</summary>
        public double Haul { get; private set; }
        /// <summary>Rooms entered so far. The next room is Floor + 1.</summary>
        public int Floor { get; private set; }
        /// <summary>Blocks of 10 floors survived so far.</summary>
        public int BlocksCleared { get; private set; }
        /// <summary>The block that the next <see cref="RunBlock"/> will crawl.</summary>
        public int NextBlockNumber => BlocksCleared + 1;
        /// <summary>Where the run is: crawling, at a checkpoint, in the pool, or out.</summary>
        public RunPhase Phase { get; private set; } = RunPhase.Crawling;
        /// <summary>Type of the most recent room, or null before the first room.</summary>
        public RoomType? LastRoom { get; private set; }
        public int Hp => _me.Hp;
        public int MaxHp => _me.MaxHp;
        public int Attack => _me.Attack;
        public int Defense => _me.Defense;
        public int Kills { get; private set; }
        /// <summary>Loot picked up so far, in drop order.</summary>
        public IReadOnlyList<LootItem> Items => _items;
        /// <summary>Percent more gold from rooms, from loot.</summary>
        public int GoldFinderPercent { get; private set; }
        public bool Alive => _me.Alive;
        /// <summary>True once the run is finished, by death or by cashing out.</summary>
        public bool IsOver { get; private set; }

        /// <summary>Floor within the current block, 1 to 10. Monsters are tuned on this, so every block plays the same way.</summary>
        public int FloorInBlock => (Floor - 1) % FloorsPerBlock + 1;
        /// <summary>Zero-based index of the block the floor is in.</summary>
        public int BlockIndex => Floor <= 0 ? 0 : (Floor - 1) / FloorsPerBlock;
        public int NextFloor => Floor + 1;
        public bool NextIsBoss => NextFloor % BossEvery == 0;

        /// <summary>A copy of this run's combat stats, for PvP. Changing the copy doesn't change the run.</summary>
        public Combatant CloneStats()
        {
            return new Combatant
            {
                Name = _me.Name, Hp = _me.Hp, MaxHp = _me.MaxHp, Attack = _me.Attack, Defense = _me.Defense,
                CritBonus = _me.CritBonus, Dodge = _me.Dodge, Accuracy = _me.Accuracy, Lifesteal = _me.Lifesteal,
                Thorns = _me.Thorns, Regen = _me.Regen, SecondWindHeal = _me.SecondWindHeal
            };
        }

        /// <summary>
        /// Crawls the next block of 10 floors in one go. If the player goes down, the run is out.
        /// Otherwise the run waits at a checkpoint for a choice to cash out or fight.
        /// </summary>
        public List<RoomResult> RunBlock()
        {
            if (Phase != RunPhase.Crawling)
                throw new InvalidOperationException("This run isn't ready for another block.");

            var rooms = new List<RoomResult>();
            for (int i = 0; i < FloorsPerBlock && Alive; i++)
                rooms.Add(Descend());

            if (!Alive)
            {
                Phase = RunPhase.Out;
            }
            else
            {
                Phase = RunPhase.AtCheckpoint;
                BlocksCleared++;
            }
            return rooms;
        }

        /// <summary>The player chooses to fight at the checkpoint, which puts the run in the pool.</summary>
        public void EnterPool()
        {
            if (Phase != RunPhase.AtCheckpoint)
                throw new InvalidOperationException("Only a run at a checkpoint can fight.");
            Phase = RunPhase.InPool;
        }

        /// <summary>Takes the haul into the pool. The haul is the fighter's pot share, so it's emptied here.</summary>
        public double TakeHaulIntoPool()
        {
            if (Phase != RunPhase.InPool)
                throw new InvalidOperationException("Only a fighter in the pool can take a haul.");
            double taken = Haul;
            Haul = 0;
            return taken;
        }

        /// <summary>A winner returns to crawling with their winnings and the HP they have left after the fight.</summary>
        public void ReturnFromPool(double winnings, int hp)
        {
            if (Phase != RunPhase.InPool)
                throw new InvalidOperationException("Only a fighter in the pool can return.");
            Haul = Math.Round(Haul + winnings, 2);
            _me.Hp = Math.Clamp(hp, 1, MaxHp);
            Phase = RunPhase.Crawling;
        }

        /// <summary>A loser in the pool is out. Their haul is already in the pot.</summary>
        public void Eliminate()
        {
            if (Phase != RunPhase.InPool)
                throw new InvalidOperationException("Only a fighter in the pool can be eliminated.");
            Phase = RunPhase.Out;
            IsOver = true;
        }

        /// <summary>Ends the run and returns the haul to bank. The caller pays it out.</summary>
        public double CashOut()
        {
            if (IsOver || Phase == RunPhase.InPool)
                throw new InvalidOperationException("This run can't cash out right now.");
            IsOver = true;
            Phase = RunPhase.Out;
            return Haul;
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

        /// <summary>
        /// Combat stats for a monster or boss on the current floor. Tuned by floor within the block,
        /// then scaled by block so each block keeps its odds. Tuned with the balance sim.
        /// </summary>
        public Combatant MonsterFor(string name, bool boss)
        {
            int f = FloorInBlock;
            double scale = 1 + BlockDifficultyStep * BlockIndex;
            int hp = (int)Math.Round((boss ? 10 + 2 * f : 6 + 2 * f) * scale) + _random.Next(0, 6);
            int attack = (int)Math.Round((boss ? 3 + f / 3 : f / 4) * scale);
            return new Combatant
            {
                Name = name,
                Hp = hp,
                MaxHp = hp,
                Attack = attack,
                Defense = f / 3
            };
        }

        /// <summary>Enters the next room and resolves it. Only the block crawl calls this.</summary>
        private RoomResult Descend()
        {
            if (IsOver)
                throw new InvalidOperationException("This run is over.");

            Floor++;
            var result = new RoomResult { Type = PickRoom() };
            LastRoom = result.Type;

            switch (result.Type)
            {
                case RoomType.Boss:
                    (string bossName, SpriteKind bossSprite) = Pick(Bosses);
                    result.Enemy = bossSprite;
                    result.EnemyName = bossName;
                    result.Lines.Add($"**Floor {Floor}: BOSS.** {bossName} blocks the way.");
                    Fight(result, bossName, bossSprite, boss: true, dropChance: 1);
                    break;
                case RoomType.Monster:
                    (string monsterName, SpriteKind monsterSprite) = Pick(Monsters);
                    result.Enemy = monsterSprite;
                    result.EnemyName = monsterName;
                    result.Lines.Add($"**Floor {Floor}:** a {monsterName} lunges out of the dark.");
                    Fight(result, monsterName, monsterSprite, boss: false, dropChance: Loot.MonsterDropChance);
                    break;
                case RoomType.Treasure:
                    FindTreasure(result);
                    break;
                case RoomType.Trap:
                    SpringTrap(result);
                    break;
                case RoomType.Quiet:
                    result.Lines.Add($"**Floor {Floor}:** a quiet corridor. Nothing here.");
                    break;
                case RoomType.Gear:
                    FindGear(result);
                    break;
                default:
                    int rested = Math.Min(4, MaxHp - Hp);
                    _me.Heal(4);
                    result.Lines.Add($"**Floor {Floor}:** an empty room. You rest for {rested} HP.");
                    break;
            }

            if (!Alive)
            {
                IsOver = true;
                result.Died = true;
                result.Lines.Add($"You went down on floor {Floor}.");
            }
            return result;
        }

        private RoomType PickRoom()
        {
            if (NextIsBoss)
                return RoomType.Boss;

            double roll = _random.NextDouble();
            if (roll < 0.40) return RoomType.Monster;
            if (roll < 0.52) return RoomType.Treasure;
            if (roll < 0.64) return RoomType.Trap;
            if (roll < 0.80) return RoomType.Quiet;
            if (roll < 0.88) return RoomType.Gear;
            return RoomType.Empty;
        }

        private double Gold(double fraction)
        {
            double gold = BuyIn * fraction * (1 + Floor * 0.08) * (1 + GoldFinderPercent / 100.0);
            return Math.Round(gold, 2);
        }

        private void AddGold(RoomResult result, double amount)
        {
            Haul = Math.Round(Haul + amount, 2);
            result.Lines.Add($"You pick up ${amount:N2}.");
        }

        /// <summary>Rolls for a drop and applies it straight away.</summary>
        private void MaybeDrop(RoomResult result, double chance, bool bossDrop = false)
        {
            if (chance < 1 && _random.NextDouble() >= chance)
                return;

            LootItem item = Loot.Roll(_random, bossDrop);
            ApplyItem(item);
            result.Drop = item;
            result.Lines.Add($"You find {item.Name} ({item.Tier}): {string.Join(", ", item.Effects.ConvertAll(e => e.Describe()))}.");
        }

        private void ApplyItem(LootItem item)
        {
            _items.Add(item);
            foreach (LootEffectRoll roll in item.Effects)
            {
                switch (roll.Effect)
                {
                    case LootEffect.Attack: _me.Attack += roll.Amount; break;
                    case LootEffect.Defense: _me.Defense += roll.Amount; break;
                    case LootEffect.MaxHp:
                        _me.MaxHp += roll.Amount;
                        _me.Heal(roll.Amount);
                        break;
                    case LootEffect.CritBonus: _me.CritBonus += roll.Amount; break;
                    case LootEffect.Dodge: _me.Dodge += roll.Amount; break;
                    case LootEffect.Accuracy: _me.Accuracy += roll.Amount; break;
                    case LootEffect.Lifesteal: _me.Lifesteal += roll.Amount; break;
                    case LootEffect.Thorns: _me.Thorns += roll.Amount; break;
                    case LootEffect.Regen: _me.Regen += roll.Amount; break;
                    case LootEffect.GoldFinder: GoldFinderPercent += roll.Amount; break;
                    case LootEffect.SecondWind: _me.SecondWindHeal = Math.Max(_me.SecondWindHeal, roll.Amount); break;
                }
            }
        }

        private void FindTreasure(RoomResult result)
        {
            if (_random.NextDouble() < 0.15)
            {
                result.Enemy = SpriteKind.Slime;
                result.EnemyName = "Mimic";
                result.Lines.Add($"**Floor {Floor}:** a treasure chest. It bites. It's a mimic!");
                Fight(result, "Mimic", SpriteKind.Slime, boss: false, dropChance: Loot.TreasureDropChance);
                return;
            }
            result.Lines.Add($"**Floor {Floor}:** a treasure chest, unlocked.");
            AddGold(result, Gold(0.12 + _random.NextDouble() * 0.18));
            MaybeDrop(result, Loot.TreasureDropChance);
        }

        private void SpringTrap(RoomResult result)
        {
            if (_random.NextDouble() < 0.5)
            {
                int damage = Math.Max(1, 3 + FloorInBlock / 2 + _random.Next(1, 11) - Defense / 2);
                _me.Hp -= damage;
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
                _me.Attack++;
                result.Lines.Add($"**Floor {Floor}:** a {Pick(WeaponNames)}. Attack +1 (now {Attack}).");
            }
            else
            {
                _me.Defense++;
                _me.MaxHp += 2;
                _me.Heal(2);
                result.Lines.Add($"**Floor {Floor}:** a {Pick(ArmorNames)}. Defense +1 (now {Defense}), max HP +2.");
            }
        }

        /// <summary>
        /// Fights round by round using the shared combat rules. The player strikes first each round.
        /// Stops when the enemy dies, the player goes down, or the round cap is reached.
        /// </summary>
        private void Fight(RoomResult result, string enemyName, SpriteKind enemySprite, bool boss, double dropChance)
        {
            Combatant enemy = MonsterFor(enemyName, boss);
            bool enraged = false;
            int dealt = 0, taken = 0, rounds = 0, misses = 0, crits = 0;

            for (int round = 1; round <= Combat.MaxRounds && Alive && enemy.Alive; round++)
            {
                rounds = round;
                Combat.StartRound(_me);

                AttackOutcome mine = Combat.Attack(_me, enemy, _random);
                Tally(mine, ref dealt, ref misses, ref crits);
                if (!enemy.Alive)
                    break;

                if (boss && !enraged && enemy.Hp <= enemy.MaxHp / 2)
                {
                    enraged = true;
                    enemy.Attack += 1;
                    result.Lines.Add($"{enemyName} enrages at {enemy.Hp} HP!");
                }

                AttackOutcome theirs = Combat.Attack(enemy, _me, _random);
                taken += theirs.Damage;
                if (Combat.TrySecondWind(_me))
                    result.Lines.Add($"Second Wind heals you to {Hp} HP.");
            }

            result.EnemyHp = Math.Max(0, enemy.Hp);
            result.EnemyMaxHp = enemy.MaxHp;

            string roundsText = $"{rounds} round{(rounds == 1 ? "" : "s")}";
            string critText = crits > 0 ? $", {crits} crit{(crits == 1 ? "" : "s")}" : "";
            if (!enemy.Alive)
            {
                Kills++;
                result.Lines.Add($"{enemyName} falls in {roundsText}. You dealt {dealt} and took {taken}{critText}.");
                if (boss)
                {
                    _me.MaxHp += 5;
                    _me.Hp = _me.MaxHp;
                    result.Lines.Add("The boss's hoard heals you to full, and max HP rises by 5.");
                    AddGold(result, Gold(0.6 + _random.NextDouble() * 0.3));
                    MaybeDrop(result, 1, bossDrop: true);
                }
                else
                {
                    AddGold(result, Gold(0.05 + _random.NextDouble() * 0.07));
                    MaybeDrop(result, dropChance);
                }
            }
            else if (!Alive)
            {
                result.Lines.Add($"{enemyName} wears you down in {roundsText}. You dealt {dealt} and took {taken}{critText}.");
            }
            else
            {
                result.Lines.Add($"The fight drags on for {roundsText}. You slip away from {enemyName}.");
            }
        }

        private static void Tally(AttackOutcome outcome, ref int dealt, ref int misses, ref int crits)
        {
            if (!outcome.Hit)
            {
                misses++;
                return;
            }
            dealt += outcome.Damage;
            if (outcome.Crit) crits++;
        }

        private (string, SpriteKind) Pick((string Name, SpriteKind Sprite)[] options)
        {
            var choice = options[_random.Next(options.Length)];
            return (choice.Name, choice.Sprite);
        }

        private string Pick(string[] options)
        {
            return options[_random.Next(options.Length)];
        }
    }
}
