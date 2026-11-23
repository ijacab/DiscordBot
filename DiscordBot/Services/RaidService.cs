using Common.Helpers;
using DiscordBot.Exceptions;
using DiscordBot.Games.Models.Cards;
using DiscordBot.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using static DiscordBot.Models.CoinAccounts;

namespace DiscordBot.Services
{
    /// <summary>
    /// Raids (`.raid`): one player's Battle Person attacks another's for money, resolved at once (the defender's card fights
    /// with the stance they set). Rules and numbers are in <see cref="RaidRules"/>; card levels in <see cref="CardLevels"/>.
    /// Also hires guards and trains cards. Changes accounts in memory; the caller saves with <see cref="CoinService.Update()"/>.
    /// </summary>
    public class RaidService
    {
        private static readonly object _lock = new object();
        private readonly CoinService _coinService;
        private readonly Func<CoinAccount, double> _totalWorth;
        private readonly Random _random;

        public RaidService(CoinService coinService, StockService stockService, AppSettings appSettings)
            : this(coinService, new WorthService(coinService, stockService, appSettings).TotalWorth, new Random())
        {
        }

        public RaidService(CoinService coinService, Func<CoinAccount, double> totalWorth, Random random)
        {
            _coinService = coinService;
            _totalWorth = totalWorth;
            _random = random;
        }

        public double TotalWorth(CoinAccount account) => _totalWorth(account);

        /// <summary>Whether a raid can happen and on what terms, before a stake is chosen.</summary>
        public sealed class RaidCheck
        {
            /// <summary>Why it can't happen, or null when it can.</summary>
            public string Refusal { get; set; }
            public bool IsRevenge { get; set; }
            public double AttackPower { get; set; }
            public double DefencePower { get; set; }
            public double Underdog { get; set; }
            public bool Guarded { get; set; }
            public double Chance { get; set; }
            public double MinStake { get; set; }
            public double MaxStake { get; set; }
            public double AttackerWorth { get; set; }
            public double TargetWorth { get; set; }
            /// <summary>Who has the edge from the archetypes: "" when nobody, else a line like "🥊 Brute beats 🦝 Thief: +20% power for the Brute".</summary>
            public string Matchup { get; set; } = "";
        }

        public sealed class RaidResult
        {
            public CoinAccount Attacker { get; set; }
            public CoinAccount Target { get; set; }
            public RaidCheck Check { get; set; }
            public double Stake { get; set; }
            public bool Won { get; set; }
            /// <summary>The d100 roll, 1 to 100, higher is better. The raid wins when it is at least <see cref="Needed"/>.</summary>
            public int Roll { get; set; }
            public int Needed { get; set; }
            public bool Rerolled { get; set; }
            /// <summary>A Gambler's coin flip: 1.25 or 0.75 times their power this raid, or 0 for anyone else.</summary>
            public double Swing { get; set; }
            /// <summary>How many times the stake a win took (a Thief's is bigger, a Gambler's natural 100 doubles it).</summary>
            public double LootMultiple { get; set; }
            /// <summary>Extra the raider lost on a failed raid on top of the stake (a Thief's, or a Gambler's natural 1).</summary>
            public double ExtraLoss { get; set; }
            public double ExtraLossToDebt { get; set; }
            /// <summary>Raids the raider has now won in a row.</summary>
            public int Streak { get; set; }
            /// <summary>What the target lost (a win), part of which may have gone on their debt.</summary>
            public double Loot { get; set; }
            public double LootToDebt { get; set; }
            public bool Parachuted { get; set; }
            /// <summary>The share of the loot set aside as a bounty on the raider.</summary>
            public double BountySet { get; set; }
            /// <summary>A bounty collected by whoever won.</summary>
            public double BountyCollected { get; set; }
            /// <summary>A Counter defender's hit back on a failed raider, part of which may have gone on their debt.</summary>
            public double CounterHit { get; set; }
            public double CounterToDebt { get; set; }
            public string AttackerXp { get; set; }
            public string DefenderXp { get; set; }
            public TimeSpan ProtectedFor { get; set; }
        }

        /// <summary>Works out the odds and stake limits, and why the raid can't happen if it can't. Changes nothing but expired entries.</summary>
        public RaidCheck Check(CoinAccount attacker, CoinAccount target, DateTime now)
        {
            Prune(attacker, now);
            Prune(target, now);
            var check = new RaidCheck
            {
                AttackerWorth = _totalWorth(attacker),
                TargetWorth = _totalWorth(target),
                IsRevenge = attacker.RevengeTokens.Any(t => t.UserId == target.UserId),
                Guarded = target.GuardsUntilUtcTicks > now.Ticks,
            };
            check.Underdog = check.IsRevenge ? 1 : RaidRules.UnderdogMultiplier(check.AttackerWorth, check.TargetWorth);
            check.AttackPower = RaidRules.AttackPower(attacker.BattlePerson, target.BattlePerson, check.IsRevenge);
            check.DefencePower = RaidRules.DefencePower(target.BattlePerson, attacker.BattlePerson, target.RaidStance, check.Guarded, check.Underdog);
            check.Matchup = MatchupLine(attacker.BattlePerson, target.BattlePerson);
            check.Chance = RaidRules.WinChance(check.AttackPower, check.DefencePower);
            var revenge = check.IsRevenge ? attacker.RevengeTokens.First(t => t.UserId == target.UserId) : null;
            (check.MinStake, check.MaxStake) = revenge != null
                ? RaidRules.RevengeStakeLimits(check.AttackerWorth, check.TargetWorth, revenge.Stake)
                : RaidRules.StakeLimits(check.AttackerWorth, check.TargetWorth);
            check.Refusal = Refusal(attacker, target, check, now);
            return check;
        }

        /// <summary>Who the archetypes favour in this raid, as a line, or "" when it is even.</summary>
        public static string MatchupLine(BattlePerson attacker, BattlePerson defender)
        {
            if (attacker == null || defender == null) return "";
            if (Archetypes.Counters(attacker.Archetype, defender.Archetype))
                return $"{Archetypes.Label(attacker.Archetype)} beats {Archetypes.Label(defender.Archetype)}: +{Archetypes.CounterBonus * 100:0}% power for the raider";
            if (Archetypes.Counters(defender.Archetype, attacker.Archetype))
                return $"{Archetypes.Label(defender.Archetype)} beats {Archetypes.Label(attacker.Archetype)}: +{Archetypes.CounterBonus * 100:0}% power for the defender";
            return "";
        }

        private static string Refusal(CoinAccount attacker, CoinAccount target, RaidCheck check, DateTime now)
        {
            if (attacker.UserId == target.UserId) return "You can't raid yourself.";
            if (attacker.BattlePerson == null) return "You need a Battle Person to raid with. Type `.cardpull` (your first pull each day is free).";
            if (target.IsShielded(now)) return $"{target.Name} has a 🛡️ Shield running, so the raid would bounce off. Try again later.";

            var recent = attacker.RecentRaidUtcTicks.Where(t => now.Ticks - t < TimeSpan.TicksPerHour).OrderBy(t => t).ToList();
            if (recent.Count >= RaidRules.RaidsPerHour)
                return $"You've raided {RaidRules.RaidsPerHour} times in the last hour. Your next raid is ready in {Wait(new DateTime(recent[0]).AddHours(1) - now)}.";

            if (!check.IsRevenge)
            {
                if (attacker.LastRaidOnUtcTicks.TryGetValue(target.UserId, out long last) && now.Ticks - last < RaidRules.PairCooldown.Ticks)
                    return $"You raided {target.Name} recently. You can raid them again in {Wait(new DateTime(last) + RaidRules.PairCooldown - now)}.";
                if (target.RaidProtectedUntilUtcTicks > now.Ticks)
                    return $"{target.Name} was just raided and is protected for {Wait(new DateTime(target.RaidProtectedUntilUtcTicks) - now)}.";
            }

            if (check.TargetWorth < 1) return $"{target.Name} has nothing to take.";
            if (check.MinStake > check.MaxStake)
                return $"{target.Name} is too small for you to raid: your smallest stake ({Money(check.MinStake)}, 1% of your worth) is more than the most anyone can stake against them ({Money(check.MaxStake)}, 10% of theirs).";
            if (attacker.NetWorth < check.MinStake)
                return $"You need at least {Money(check.MinStake)} in cash to stake on a raid.";
            return null;
        }

        /// <summary>Raids <paramref name="target"/> with <paramref name="stake"/> of the attacker's cash. Throws <see cref="BadInputException"/> when it can't.</summary>
        public RaidResult Raid(CoinAccount attacker, CoinAccount target, double stake, DateTime now)
        {
            lock (_lock)
            {
                var check = Check(attacker, target, now);
                if (check.Refusal != null) throw new BadInputException(check.Refusal);
                stake = Math.Floor(stake);
                if (stake < check.MinStake)
                    throw new BadInputException($"The smallest stake you can put up is {Money(check.MinStake)} (1% of your worth).");
                if (stake > check.MaxStake)
                    throw new BadInputException(check.IsRevenge
                        ? $"The most you can stake on a revenge raid is {Money(check.MaxStake)}: what {target.Name} raided you with."
                        : $"The most you can stake against {target.Name} is {Money(check.MaxStake)} (10% of their worth).");
                if (stake > attacker.NetWorth)
                    throw new BadInputException($"You only have {Money(Math.Max(0, attacker.NetWorth))} in cash to stake.");

                attacker.RecentRaidUtcTicks.Add(now.Ticks);
                attacker.LastRaidOnUtcTicks[target.UserId] = now.Ticks;
                if (check.IsRevenge)
                    attacker.RevengeTokens.RemoveAll(t => t.UserId == target.UserId);
                DiscordBot.Models.Money.Debit(attacker, stake);

                var result = new RaidResult { Attacker = attacker, Target = target, Check = check, Stake = stake };
                var kind = Archetypes.Normalise(attacker.BattlePerson?.Archetype ?? Archetype.None);
                if (kind == Archetype.Gambler)
                {
                    //hot or cold: a coin flip swings their power for this raid
                    result.Swing = _random.NextDouble() < 0.5 ? 1 + Archetypes.GamblerSwing : 1 - Archetypes.GamblerSwing;
                    check.AttackPower *= result.Swing;
                    check.Chance = RaidRules.WinChance(check.AttackPower, check.DefencePower);
                }
                result.Needed = NeededRoll(check.Chance);
                result.Roll = RollD100();
                result.Won = result.Roll >= result.Needed;
                string today = now.ToString("yyyyMMdd");
                if (!result.Won && kind == Archetype.Trickster && attacker.LastRaidRerollDate != today)
                {
                    attacker.LastRaidRerollDate = today;
                    result.Rerolled = true;
                    result.Roll = RollD100();
                    result.Won = result.Roll >= result.Needed;
                }

                if (result.Won) Win(result, now);
                else Lose(result, now);

                //the raided player can hit back once, unless this was itself the hit back
                if (!check.IsRevenge)
                {
                    target.RevengeTokens.RemoveAll(t => t.UserId == attacker.UserId);
                    target.RevengeTokens.Add(new RevengeToken { UserId = attacker.UserId, Name = attacker.Name, ExpiresUtcTicks = (now + RaidRules.RevengeWindow).Ticks, Stake = stake });
                }
                return result;
            }
        }

        /// <summary>A d100 roll, 1 to 100.</summary>
        private int RollD100() => 100 - (int)Math.Floor(Math.Min(_random.NextDouble(), 0.999999) * 100);

        /// <summary>The chance as a whole percent, which is what the raid really uses, so the shown odds and the roll always agree.</summary>
        public static int ChancePercent(double chance) => (int)Math.Round(chance * 100);

        /// <summary>The d100 roll needed to win: a 60% chance needs 41 or more.</summary>
        public static int NeededRoll(double chance) => 101 - ChancePercent(chance);

        private void Win(RaidResult result, DateTime now)
        {
            var (attacker, target, check) = (result.Attacker, result.Target, result.Check);
            var kind = Archetypes.Normalise(attacker.BattlePerson?.Archetype ?? Archetype.None);
            result.LootMultiple = Archetypes.LootMultiple(kind, RaidRules.LootMultiple) * (kind == Archetype.Gambler && result.Roll == 100 ? 2 : 1);
            double loot = RaidRules.Loot(result.Stake, target.RaidStance, check.TargetWorth, result.LootMultiple);
            double cash = Math.Max(0, target.NetWorth);
            if (loot > cash && target.TryConsumeCharge(PowerUpIds.GoldenParachute, now))
            {
                loot = Math.Floor(cash);
                result.Parachuted = true;
            }
            var (taken, lootToDebt) = Take(target, loot);
            result.Loot = loot = taken;
            result.LootToDebt = lootToDebt;
            result.BountySet = Math.Round(loot * RaidRules.BountyShare, 2);
            DiscordBot.Models.Money.Credit(_coinService.GetAll(), attacker, result.Stake + loot - result.BountySet);
            attacker.Bounty = Math.Round(attacker.Bounty + result.BountySet, 2);

            //beating someone with a bounty on them collects it
            result.BountyCollected = target.Bounty;
            target.Bounty = 0;
            DiscordBot.Models.Money.Credit(_coinService.GetAll(), attacker, result.BountyCollected);

            AttackHistory.Record(target, attacker.UserId, attacker.Name, check.IsRevenge ? "Revenge raid" : "Raid", loot,
                lootToDebt > 0 ? $"{Money(lootToDebt)} of it went on your debt" : "it worked", now);
            result.ProtectedFor = Archetypes.Normalise(target.BattlePerson?.Archetype ?? Archetype.None) == Archetype.Guardian ? Archetypes.GuardianProtection : RaidRules.Protection;
            target.RaidProtectedUntilUtcTicks = (now + result.ProtectedFor).Ticks;

            attacker.Stats.RaidsWon++;
            result.Streak = ++attacker.RaidStreak;
            attacker.Stats.BestRaidStreak = Math.Max(attacker.Stats.BestRaidStreak, attacker.RaidStreak);
            attacker.Stats.RaidMoneyWon += loot - result.BountySet;
            attacker.Stats.BountiesCollected += result.BountyCollected;
            target.Stats.DefencesLost++;
            target.Stats.RaidMoneyLost += loot;
            bool raidedUp = check.TargetWorth > check.AttackerWorth;
            result.AttackerXp = CardLevels.AddXp(attacker.BattlePerson, CardLevels.RaidWinXp + (raidedUp ? CardLevels.RaidUpBonusXp : 0));
            result.DefenderXp = CardLevels.AddXp(target.BattlePerson, CardLevels.DefenceLossXp);
            if (attacker.BattlePerson != null) attacker.BattlePerson.Wins++;
            if (target.BattlePerson != null) target.BattlePerson.Losses++;
        }

        private void Lose(RaidResult result, DateTime now)
        {
            var (attacker, target) = (result.Attacker, result.Target);
            DiscordBot.Models.Money.Credit(_coinService.GetAll(), target, result.Stake);
            var kind = Archetypes.Normalise(attacker.BattlePerson?.Archetype ?? Archetype.None);
            //a Thief pays extra for getting caught, and a Gambler's snake eyes doubles the loss
            double extra = kind == Archetype.Thief ? Math.Floor(result.Stake * Archetypes.ThiefFailedRaidExtra)
                : kind == Archetype.Gambler && result.Roll == 1 ? result.Stake : 0;
            if (extra > 0)
            {
                (result.ExtraLoss, result.ExtraLossToDebt) = Take(attacker, extra);
                DiscordBot.Models.Money.Credit(_coinService.GetAll(), target, result.ExtraLoss);
            }
            if (target.RaidStance == RaidStance.Counter)
            {
                (result.CounterHit, result.CounterToDebt) = Take(attacker, Math.Floor(result.Stake * RaidRules.CounterHitMultiple));
                DiscordBot.Models.Money.Credit(_coinService.GetAll(), target, result.CounterHit);
            }

            result.BountyCollected = attacker.Bounty;
            attacker.Bounty = 0;
            DiscordBot.Models.Money.Credit(_coinService.GetAll(), target, result.BountyCollected);

            AttackHistory.Record(target, attacker.UserId, attacker.Name, result.Check.IsRevenge ? "Revenge raid" : "Raid",
                -(result.Stake + result.ExtraLoss + result.CounterHit + result.BountyCollected), "it failed, you kept their stake", now);
            attacker.Stats.RaidsLost++;
            attacker.RaidStreak = 0;
            attacker.Stats.RaidMoneyLost += result.Stake + result.ExtraLoss + result.CounterHit;
            target.Stats.DefencesWon++;
            target.Stats.RaidMoneyWon += result.Stake + result.ExtraLoss + result.CounterHit;
            target.Stats.BountiesCollected += result.BountyCollected;
            result.AttackerXp = CardLevels.AddXp(attacker.BattlePerson, CardLevels.RaidLossXp);
            result.DefenderXp = CardLevels.AddXp(target.BattlePerson, CardLevels.DefenceWinXp);
            if (attacker.BattlePerson != null) attacker.BattlePerson.Losses++;
            if (target.BattlePerson != null) target.BattlePerson.Wins++;
        }

        /// <summary>Takes up to <paramref name="amount"/> from the account: cash first, the rest on their debt, but only as much debt as their other assets back. Returns (taken, debt part).</summary>
        private (double Taken, double ToDebt) Take(CoinAccount account, double amount) =>
            DiscordBot.Models.Money.Seize(account, null, amount, DiscordBot.Models.Money.DebtRoom(account, _totalWorth(account)));

        /// <summary>Hires guards for <see cref="RaidRules.GuardsDuration"/> more, paid from cash into the jackpot. Returns the cost.</summary>
        public double HireGuards(CoinAccount account, DateTime now)
        {
            lock (_lock)
            {
                double worth = _totalWorth(account);
                if (worth < 1) throw new BadInputException("You have nothing worth guarding.");
                long from = Math.Max(now.Ticks, account.GuardsUntilUtcTicks);
                if (from + RaidRules.GuardsDuration.Ticks - now.Ticks > RaidRules.MaxGuardsAhead.Ticks)
                    throw new BadInputException($"Your guards are already booked for {Wait(new DateTime(account.GuardsUntilUtcTicks) - now)}. You can book them up to 24 hours ahead.");
                double cost = GuardsCost(account);
                if (account.NetWorth < cost)
                    throw new BadInputException($"Guards cost {Money(cost)} (2% of your worth) and you have {Money(Math.Max(0, account.NetWorth))} in cash.");
                DiscordBot.Models.Money.Debit(account, cost);
                account.GuardsUntilUtcTicks = from + RaidRules.GuardsDuration.Ticks;
                account.Stats.GuardsSpent += cost;
                _coinService.AddToJackpot(cost);
                return cost;
            }
        }

        public double GuardsCost(CoinAccount account) => Math.Max(1, Math.Ceiling(_totalWorth(account) * RaidRules.GuardsCostShare));

        /// <summary>Trains the player's card up to <paramref name="maxLevels"/> levels with their cash, into the jackpot.</summary>
        public (int Levels, double Cost) Train(CoinAccount account, int maxLevels)
        {
            lock (_lock)
            {
                var card = account.BattlePerson ?? throw new BadInputException("You don't have a Battle Person to train. Type `.cardpull` to get one.");
                if (card.Level >= CardLevels.MaxLevel) throw new BadInputException($"{card.Name} is already max level ({CardLevels.MaxLevel}).");
                double next = CardLevels.TrainCost(card);
                if (account.NetWorth < next)
                    throw new BadInputException($"Training {card.Name} to level {card.Level + 1} costs {Money(next)} and you have {Money(Math.Max(0, account.NetWorth))} in cash.");
                var (levels, cost) = CardLevels.Train(card, account.NetWorth, maxLevels);
                DiscordBot.Models.Money.Debit(account, cost);
                account.Stats.CardTrainingSpent += cost;
                _coinService.AddToJackpot(cost);
                return (levels, cost);
            }
        }

        private static void Prune(CoinAccount account, DateTime now)
        {
            account.RecentRaidUtcTicks.RemoveAll(t => now.Ticks - t >= TimeSpan.TicksPerHour);
            account.RevengeTokens.RemoveAll(t => t.ExpiresUtcTicks <= now.Ticks);
            foreach (var key in account.LastRaidOnUtcTicks.Where(p => now.Ticks - p.Value >= RaidRules.PairCooldown.Ticks).Select(p => p.Key).ToList())
                account.LastRaidOnUtcTicks.Remove(key);
        }

        /// <summary>The result as a message: who won, the odds and roll, the money that moved, XP and what happens next.</summary>
        /// <summary>One side's card as a short phrase: "🥊 Gary "Knuckles" Pemberton (Lv 4)", or the player's name when they have no card.</summary>
        public static string CardLabel(CoinAccount account) =>
            account.BattlePerson == null ? $"{account.Name} (no card)" : $"{Archetypes.Emoji(account.BattlePerson.Archetype)} {account.BattlePerson.Name} (Lv {account.BattlePerson.Level})";

        /// <summary>The result as a message: who won, the odds and roll, the money that moved, a battle cry, XP and what happens next.</summary>
        public static string Describe(RaidResult r) => Describe(r, Random.Shared);

        public static string Describe(RaidResult r, Random random)
        {
            var (a, t, c) = (r.Attacker, r.Target, r.Check);
            var kind = Archetypes.Normalise(a.BattlePerson?.Archetype ?? Archetype.None);

            //the picture shows both cards and the dice, so the text only has who, the roll, the money and one cry
            var lines = new List<string>
            {
                $"{(c.IsRevenge ? "🔥 **Revenge raid!** " : "⚔️ ")}**{a.Name}** raided **{t.Name}** · {Money(r.Stake)} stake · {t.RaidStance} stance{(c.Guarded ? " + guards" : "")}",
            };

            var dice = new List<string> { $"🎲 Needed **{r.Needed}+** ({ChancePercent(c.Chance)}%), rolled **{r.Roll}** {(r.Won ? "✅" : "❌")}" };
            if (r.Rerolled) dice.Add("🃏 rerolled");
            if (c.Matchup.Length > 0) dice.Add(c.Matchup);
            if (r.Swing > 0) dice.Add($"🎰 Gambler {(r.Swing > 1 ? "hot +" : "cold -")}{Archetypes.GamblerSwing * 100:0}%");
            if (c.Underdog > 1) dice.Add($"{t.Name} defends {Pct(c.Underdog - 1)} harder for being richer");
            if (kind == Archetype.Gambler && r.Roll == 100) dice.Add("**NATURAL 100**: loot doubled");
            if (kind == Archetype.Gambler && r.Roll == 1) dice.Add("**SNAKE EYES**: loss doubled");
            lines.Add(string.Join(" · ", dice));

            var money = new List<string>();
            if (r.Won)
            {
                money.Add($"💰 Took **{Money(r.Loot)}** ({r.LootMultiple:0.#}x){(r.LootToDebt > 0 ? $", {Money(r.LootToDebt)} on their debt" : "")}{(r.Parachuted ? " (🪂 parachute)" : "")}");
                money.Add($"{Money(r.BountySet)} is now a bounty on {a.Name}");
                if (r.BountyCollected > 0) money.Add($"collected the {Money(r.BountyCollected)} bounty on {t.Name}");
                money.Add($"{t.Name} is safe for {Wait(r.ProtectedFor)}");
            }
            else
            {
                money.Add($"🛡️ Lost the {Money(r.Stake)} stake to {t.Name}");
                if (r.CounterHit > 0) money.Add($"countered for {Money(r.CounterHit)} more{(r.CounterToDebt > 0 ? $" ({Money(r.CounterToDebt)} on debt)" : "")}");
                if (r.ExtraLoss > 0) money.Add($"{Money(r.ExtraLoss)} extra for {(kind == Archetype.Thief ? "getting caught" : "rolling snake eyes")}");
                if (r.BountyCollected > 0) money.Add($"{t.Name} collected the {Money(r.BountyCollected)} bounty on {a.Name}");
            }
            lines.Add(string.Join(" · ", money));

            var winner = r.Won ? a : t;
            if (winner.BattlePerson != null)
                lines.Add($"{Archetypes.Emoji(winner.BattlePerson.Archetype)} **{winner.BattlePerson.Name}**: “{Archetypes.Cry(winner.BattlePerson.Archetype, true, random)}”");

            var footer = new List<string> { $"XP +{r.AttackerXp} / {(t.BattlePerson == null ? "none (no card, `.cardpull`)" : "+" + r.DefenderXp)}" };
            if (r.Won && r.Streak >= 2) footer.Add($"🔥 {r.Streak} raids in a row");
            if (!c.IsRevenge) footer.Add($"{t.Name} can take revenge for {RaidRules.RevengeWindow.TotalHours:0}h");
            lines.Add(string.Join(" · ", footer));
            return string.Join("\n", lines);
        }

        public static string Money(double amount) => "$" + FormatHelper.GetCommaNumber(Math.Round(amount, 2));
        public static string Pct(double share) => (share * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%";

        public static string Wait(TimeSpan span)
        {
            if (span < TimeSpan.FromMinutes(1)) return "under a minute";
            if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
            return $"{(int)Math.Ceiling(span.TotalMinutes)} min";
        }
    }
}
