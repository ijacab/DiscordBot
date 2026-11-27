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
    /// Raids (`.raid`): a player sends a crew from their gang to rob one of another player's properties, resolved at once against the
    /// property's security and the owner's best cards. Rules and numbers are in <see cref="RaidRules"/>, gangs in <see cref="GangRules"/>,
    /// levels in <see cref="CardLevels"/>. Also hires guards and trains cards. Changes accounts in memory; the caller saves with
    /// <see cref="CoinService.Update()"/>.
    /// </summary>
    public class RaidService
    {
        private static readonly object _lock = new object();

        /// <summary>The lock raids take. Packs, fusing and scrapping change the card list under it too.</summary>
        public static object Sync => _lock;

        /// <summary>What a revenge raid picks to hit a raider who owns no property: their stash of cash.</summary>
        public const string StashId = "stash";
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

        /// <summary>What a raid on one property would be: the crews, the odds, the loot and stake, or why it can't happen.</summary>
        public sealed class RaidCheck
        {
            /// <summary>Why the raid can't happen, or null when it can.</summary>
            public string Refusal { get; set; }
            public bool IsRevenge { get; set; }
            public PropertyHolding Property { get; set; }
            /// <summary>A revenge raid on a raider with no property: it robs their cash instead (<see cref="StashId"/>).</summary>
            public bool IsStash { get; set; }
            public List<BattlePerson> Crew { get; set; } = new List<BattlePerson>();
            public List<BattlePerson> Defenders { get; set; } = new List<BattlePerson>();
            public double Security { get; set; }
            public double AttackPower { get; set; }
            public double DefencePower { get; set; }
            public double PunchDown { get; set; } = 1;
            public bool Guarded { get; set; }
            public double Chance { get; set; }
            public double Loot { get; set; }
            public double Stake { get; set; }
            public double AttackerWorth { get; set; }
            public double TargetWorth { get; set; }
        }

        public sealed class RaidResult
        {
            public CoinAccount Attacker { get; set; }
            public CoinAccount Target { get; set; }
            public RaidCheck Check { get; set; }
            public double Stake { get; set; }
            public bool Won { get; set; }
            /// <summary>The d100 roll (1-100) and what it needed: the raid works on <see cref="Needed"/> or more.</summary>
            public int Roll { get; set; }
            public int Needed { get; set; }
            public int Streak { get; set; }
            /// <summary>What a winning raid took, and how much of that went on the owner's debt.</summary>
            public double Loot { get; set; }
            public double LootToDebt { get; set; }
            public bool Parachuted { get; set; }
            /// <summary>Kept back from the loot as a bounty on the raider.</summary>
            public double BountySet { get; set; }
            /// <summary>A bounty on the loser that the winner collected.</summary>
            public double BountyCollected { get; set; }
            public int AttackerXpEach { get; set; }
            public int DefenderXpEach { get; set; }
            /// <summary>"⭐ Gary 4 → 5" for every card that levelled up.</summary>
            public List<string> LevelUps { get; set; } = new List<string>();
        }

        /// <summary>
        /// The check for raiding <paramref name="property"/> (one of the target's). With no property it only checks who can raid whom
        /// (the gang, shield, limits and that the target owns anything).
        /// </summary>
        public RaidCheck Check(CoinAccount attacker, CoinAccount target, PropertyHolding property, DateTime now, bool stash = false)
        {
            Prune(attacker, now);
            Prune(target, now);
            var check = new RaidCheck
            {
                AttackerWorth = _totalWorth(attacker),
                TargetWorth = _totalWorth(target),
                IsRevenge = attacker.RevengeTokens.Any(t => t.UserId == target.UserId),
                Guarded = target.GuardsUntilUtcTicks > now.Ticks,
                Property = property,
                IsStash = stash && property == null,
                Crew = GangRules.Crew(attacker.Cards, now),
                Defenders = GangRules.Defenders(target.Cards),
            };
            check.PunchDown = check.IsRevenge ? 1 : RaidRules.PunchDownMultiplier(check.AttackerWorth, check.TargetWorth);
            check.AttackPower = RaidRules.AttackPower(check.Crew, check.IsRevenge);
            if (property != null)
            {
                double index = _coinService.GetAll().PropertyIndex;
                check.Security = PropertyCatalog.TypeOf(property.Kind).Security;
                check.Loot = RaidRules.Loot(property.Kind, index, check.TargetWorth);
                check.Stake = RaidRules.Stake(check.Loot);
            }
            else if (check.IsStash)
            {
                //what they took from you, at most a fifth of what they're worth; no security on a stash
                var token = attacker.RevengeTokens.FirstOrDefault(t => t.UserId == target.UserId);
                double took = token == null ? 0 : token.Stake / RaidRules.StakeShare;
                check.Loot = Math.Floor(Math.Max(0, Math.Min(took, Math.Max(0, check.TargetWorth) * RaidRules.MaxLootShare)));
                check.Stake = RaidRules.Stake(check.Loot);
            }
            check.DefencePower = RaidRules.DefencePower(check.Defenders, check.Security, check.Guarded, check.PunchDown);
            check.Chance = RaidRules.WinChance(check.AttackPower, check.DefencePower);
            check.Refusal = Refusal(attacker, target, check, now);
            return check;
        }

        private static string Refusal(CoinAccount attacker, CoinAccount target, RaidCheck check, DateTime now)
        {
            if (attacker.UserId == target.UserId) return "You can't rob yourself, genius.";
            if (attacker.Cards.Count == 0) return "You need a gang to raid with. `.pack` opens a street pack (the first one each day is free).";
            if (check.Crew.Count == 0)
            {
                long ready = attacker.Cards.Min(c => c.RestedUntilUtcTicks);
                return $"Your whole gang is lying low after the last job. The first one is back in {Wait(new DateTime(ready) - now)}.";
            }
            if (target.IsShielded(now)) return $"{target.Name} has a 🛡️ Shield running, so the raid would bounce off. Try again later.";

            var recent = attacker.RecentRaidUtcTicks.Where(t => now.Ticks - t < TimeSpan.TicksPerHour).OrderBy(t => t).ToList();
            if (recent.Count >= RaidRules.RaidsPerHour)
                return $"You've raided {RaidRules.RaidsPerHour} times in the last hour. Your next raid is ready in {Wait(new DateTime(recent[0]).AddHours(1) - now)}.";

            if (!check.IsRevenge && attacker.LastRaidOnUtcTicks.TryGetValue(target.UserId, out long last) && now.Ticks - last < RaidRules.PairCooldown.Ticks)
                return $"You raided {target.Name} recently. You can hit them again in {Wait(new DateTime(last) + RaidRules.PairCooldown - now)}.";
            if (target.Properties.Count == 0 && !check.IsRevenge)
                return $"{target.Name} owns no property, so there's nothing to rob. Stocks and cash can't be raided.";
            if (check.IsStash && target.Properties.Count > 0)
                return $"{target.Name} owns property, so rob that.";
            if (check.Property == null && !check.IsStash) return null;

            string place = check.IsStash ? "stash" : PropertyCatalog.DisplayName(check.Property.Kind).ToLower();
            if (!check.IsRevenge && check.Property?.RaidProtectedUntilUtcTicks > now.Ticks)
                return $"{target.Name}'s {place} was just robbed and the cops are watching it for {Wait(new DateTime(check.Property.RaidProtectedUntilUtcTicks) - now)}.";
            if (check.Loot < 1) return $"{target.Name}'s {place} has nothing worth taking.";
            if (attacker.NetWorth < check.Stake)
                return $"Robbing {target.Name}'s {place} means putting up a {Money(check.Stake)} stake ({RaidRules.StakeShare:0%} of the loot) in cash, and you have {Money(Math.Max(0, attacker.NetWorth))}.";
            return null;
        }

        /// <summary>Robs the target's property <paramref name="propertyId"/>. Throws <see cref="BadInputException"/> when it can't.</summary>
        public RaidResult Raid(CoinAccount attacker, CoinAccount target, string propertyId, DateTime now)
        {
            lock (_lock)
            {
                bool stash = propertyId == StashId;
                var property = stash ? null : target.Properties.FirstOrDefault(p => p.Id == propertyId)
                    ?? throw new BadInputException($"{target.Name} doesn't own that any more. `.raid {target.Name}` shows what they've got.");
                var check = Check(attacker, target, property, now, stash);
                if (check.Refusal != null) throw new BadInputException(check.Refusal);

                attacker.RecentRaidUtcTicks.Add(now.Ticks);
                attacker.LastRaidOnUtcTicks[target.UserId] = now.Ticks;
                if (check.IsRevenge)
                    attacker.RevengeTokens.RemoveAll(t => t.UserId == target.UserId);
                DiscordBot.Models.Money.Debit(attacker, check.Stake);
                foreach (var card in check.Crew)
                    card.RestedUntilUtcTicks = (now + GangRules.RestTime).Ticks;

                var result = new RaidResult { Attacker = attacker, Target = target, Check = check, Stake = check.Stake };
                result.Needed = NeededRoll(check.Chance);
                result.Roll = RollD100();
                result.Won = result.Roll >= result.Needed;

                if (result.Won) Win(result, now);
                else Lose(result, now);

                //the robbed player can hit back once, unless this was itself the hit back
                if (!check.IsRevenge)
                {
                    target.RevengeTokens.RemoveAll(t => t.UserId == attacker.UserId);
                    target.RevengeTokens.Add(new RevengeToken { UserId = attacker.UserId, Name = attacker.Name, ExpiresUtcTicks = (now + RaidRules.RevengeWindow).Ticks, Stake = check.Stake });
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
            double loot = check.Loot;
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

            string place = PlaceName(check);
            AttackHistory.Record(target, attacker.UserId, attacker.Name, check.IsRevenge ? "Revenge raid" : "Raid", loot,
                lootToDebt > 0 ? $"robbed your {place}, {Money(lootToDebt)} of it went on your debt" : $"robbed your {place}", now);
            if (check.Property != null)
                check.Property.RaidProtectedUntilUtcTicks = (now + RaidRules.PropertyProtection).Ticks;

            attacker.Stats.RaidsWon++;
            result.Streak = ++attacker.RaidStreak;
            attacker.Stats.BestRaidStreak = Math.Max(attacker.Stats.BestRaidStreak, attacker.RaidStreak);
            attacker.Stats.RaidMoneyWon += loot - result.BountySet;
            attacker.Stats.BountiesCollected += result.BountyCollected;
            target.Stats.DefencesLost++;
            target.Stats.RaidMoneyLost += loot;
            bool raidedUp = check.TargetWorth > check.AttackerWorth;
            result.AttackerXpEach = CardLevels.RaidWinXp + (raidedUp ? CardLevels.RaidUpBonusXp : 0);
            result.DefenderXpEach = CardLevels.DefenceLossXp;
            GiveXp(check.Crew, result.AttackerXpEach, won: true, result.LevelUps);
            GiveXp(check.Defenders, result.DefenderXpEach, won: false, result.LevelUps);
        }

        private void Lose(RaidResult result, DateTime now)
        {
            var (attacker, target, check) = (result.Attacker, result.Target, result.Check);
            DiscordBot.Models.Money.Credit(_coinService.GetAll(), target, result.Stake);

            result.BountyCollected = attacker.Bounty;
            attacker.Bounty = 0;
            DiscordBot.Models.Money.Credit(_coinService.GetAll(), target, result.BountyCollected);

            AttackHistory.Record(target, attacker.UserId, attacker.Name, check.IsRevenge ? "Revenge raid" : "Raid",
                -(result.Stake + result.BountyCollected), $"they tried your {PlaceName(check)} and failed, you kept their stake", now);
            attacker.Stats.RaidsLost++;
            attacker.RaidStreak = 0;
            attacker.Stats.RaidMoneyLost += result.Stake;
            target.Stats.DefencesWon++;
            target.Stats.RaidMoneyWon += result.Stake;
            target.Stats.BountiesCollected += result.BountyCollected;
            result.AttackerXpEach = CardLevels.RaidLossXp;
            result.DefenderXpEach = CardLevels.DefenceWinXp;
            GiveXp(check.Crew, result.AttackerXpEach, won: false, result.LevelUps);
            GiveXp(check.Defenders, result.DefenderXpEach, won: true, result.LevelUps);
        }

        /// <summary>"mansion", or "stash" for a revenge raid on cash.</summary>
        public static string PlaceName(RaidCheck check) => check.IsStash ? "stash" : PropertyCatalog.DisplayName(check.Property.Kind).ToLower();

        private static void GiveXp(List<BattlePerson> cards, int xp, bool won, List<string> levelUps)
        {
            foreach (var card in cards)
            {
                int before = card.Level;
                CardLevels.AddXp(card, xp);
                if (won) card.Wins++;
                else card.Losses++;
                if (card.Level > before) levelUps.Add($"⭐ {card.Name} {before} → {card.Level}");
            }
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
                    throw new BadInputException($"Your guards are already booked for {Wait(new DateTime(account.GuardsUntilUtcTicks) - now)}. You can book them up to {RaidRules.MaxGuardsAhead.TotalHours:0} hours ahead.");
                double cost = GuardsCost(account);
                if (account.NetWorth < cost)
                    throw new BadInputException($"Guards cost {Money(cost)} ({RaidRules.GuardsCostShare:0%} of your worth) and you have {Money(Math.Max(0, account.NetWorth))} in cash.");
                DiscordBot.Models.Money.Debit(account, cost);
                account.GuardsUntilUtcTicks = from + RaidRules.GuardsDuration.Ticks;
                account.Stats.GuardsSpent += cost;
                _coinService.AddToJackpot(cost);
                return cost;
            }
        }

        public double GuardsCost(CoinAccount account) => Math.Max(1, Math.Ceiling(_totalWorth(account) * RaidRules.GuardsCostShare));

        /// <summary>Trains one of the player's cards up to <paramref name="maxLevels"/> levels with their cash, into the jackpot.</summary>
        public (int Levels, double Cost) Train(CoinAccount account, BattlePerson card, int maxLevels)
        {
            lock (_lock)
            {
                if (card == null || !account.Cards.Contains(card)) throw new BadInputException("You don't have that card. `.gang` lists yours.");
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

        /// <summary>A player's gang as a short phrase: "a crew of 3 (power 142)", or that they have none.</summary>
        public static string GangLabel(CoinAccount account, DateTime now)
        {
            if (account.Cards.Count == 0) return "no gang";
            var crew = GangRules.Crew(account.Cards, now);
            return crew.Count == 0 ? $"{account.Cards.Count} cards, all lying low" : $"a crew of {crew.Count} (power {RaidRules.TotalPower(crew):0})";
        }

        public static string Describe(RaidResult r) => Describe(r, Random.Shared);

        /// <summary>The result as a short message: who robbed what, the roll, the money, one battle cry and the XP.</summary>
        public static string Describe(RaidResult r, Random random)
        {
            var (a, t, c) = (r.Attacker, r.Target, r.Check);
            string place = c.IsStash ? "💰 stash" : PropertyCatalog.Label(c.Property.Kind);
            var lines = new List<string>
            {
                $"{(c.IsRevenge ? "🔥 **Revenge!** " : "⚔️ ")}**{a.Name}** sent a crew of {c.Crew.Count} to rob **{t.Name}**'s {place} · {Money(r.Stake)} stake",
            };

            var dice = new List<string>
            {
                $"🎲 Needed **{r.Needed}+** ({ChancePercent(c.Chance)}%), rolled **{r.Roll}** {(r.Won ? "✅" : "❌")}",
                $"crew {c.AttackPower:0} vs 🔒{c.Security:0} + gang {RaidRules.TotalPower(c.Defenders):0}{(c.Guarded ? " with guards" : "")}",
            };
            if (c.PunchDown > 1) dice.Add($"{Pct(c.PunchDown - 1)} harder for punching down");
            lines.Add(string.Join(" · ", dice));

            var money = new List<string>();
            if (r.Won)
            {
                money.Add($"💰 Took **{Money(r.Loot)}**{(r.LootToDebt > 0 ? $", {Money(r.LootToDebt)} on their debt" : "")}{(r.Parachuted ? " (🪂 parachute)" : "")}");
                money.Add($"{Money(r.BountySet)} is now a bounty on {a.Name}");
                if (r.BountyCollected > 0) money.Add($"collected the {Money(r.BountyCollected)} bounty on {t.Name}");
                if (!c.IsStash) money.Add($"the {PlaceName(c)} is watched for {Wait(RaidRules.PropertyProtection)}");
            }
            else
            {
                money.Add($"🛡️ {t.Name} keeps the {Money(r.Stake)} stake");
                if (r.BountyCollected > 0) money.Add($"and the {Money(r.BountyCollected)} bounty on {a.Name}");
            }
            lines.Add(string.Join(" · ", money));

            var lead = (r.Won ? c.Crew : c.Defenders).FirstOrDefault();
            if (lead != null)
                lines.Add($"{lead.Emoji} **{lead.Name}**: “{GangRules.Cry(true, random)}”");

            var footer = new List<string> { $"XP +{r.AttackerXpEach} each / {(c.Defenders.Count == 0 ? "no gang to defend (`.pack`)" : $"+{r.DefenderXpEach} each")}" };
            footer.AddRange(r.LevelUps.Take(3));
            if (r.Won && r.Streak >= 2) footer.Add($"🔥 {r.Streak} jobs in a row");
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
