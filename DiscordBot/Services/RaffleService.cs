using Discord;
using Discord.WebSocket;
using DiscordBot.Exceptions;
using DiscordBot.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static DiscordBot.Models.CoinAccounts;

namespace DiscordBot.Services
{
    /// <summary>
    /// A raffle every hour. Anyone who did something with money in the 6 hours before the draw is entered, and the winner's
    /// power-up goes off the moment it's won (one that can't be used right then is kept for `.pu use`). The draw rubber-bands like kart racing items: the top of the leaderboard only draws boosts,
    /// and the further down you are the more likely attacks and catch-up items are (more so when 1st place runs away).
    /// Also handles using held power-ups.
    /// </summary>
    public class RaffleService
    {
        public const int SlotMinutes = 60;
        /// <summary>A money action within this long before a draw enters you in it.</summary>
        public static readonly TimeSpan EntryWindow = TimeSpan.FromHours(6);
        /// <summary>Players count as active for attack and sharing pools (Pickpocket, Robin Hood, Tax the Rich...) for this long.</summary>
        public static readonly TimeSpan ActiveWindow = TimeSpan.FromHours(24);
        private static readonly Random _random = new Random();

        private readonly CoinService _coinService;
        private readonly AppSettings _appSettings;
        private readonly Func<CoinAccount, double> _totalWorth;

        public RaffleService(CoinService coinService, StockService stockService, AppSettings appSettings)
            : this(coinService, appSettings, a => a.NetWorth + stockService.GetPortfolioValue(a.UserId) + coinService.PropertyValue(a) - a.Debt)
        {
        }

        public RaffleService(CoinService coinService, AppSettings appSettings, Func<CoinAccount, double> totalWorth)
        {
            _coinService = coinService;
            _appSettings = appSettings;
            _totalWorth = totalWorth;
        }

        public static long SlotOf(DateTime utc) =>
            new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds() / (SlotMinutes * 60L);

        public static DateTime NextRaffleUtc(DateTime utc) =>
            DateTimeOffset.FromUnixTimeSeconds((SlotOf(utc) + 1) * SlotMinutes * 60L).UtcDateTime;

        /// <summary>The time the draw for the slot holding <paramref name="utc"/> is due.</summary>
        public static DateTime SlotStartUtc(DateTime utc) =>
            DateTimeOffset.FromUnixTimeSeconds(SlotOf(utc) * SlotMinutes * 60L).UtcDateTime;

        /// <summary>
        /// Entered in the draw due at <paramref name="drawTime"/>: a money action in the 6 hours before it. Measured from the
        /// draw's due time, not the minute the loop gets to it, so acting just after a draw still enters you in the next.
        /// </summary>
        public static bool IsEnteredIn(CoinAccount account, DateTime drawTime) =>
            account.LastActiveUtcTicks > 0 && drawTime.Ticks - account.LastActiveUtcTicks <= EntryWindow.Ticks;

        /// <summary>When the last money action stops entering the player in draws.</summary>
        public static DateTime EntryEndsUtc(CoinAccount account) =>
            new DateTime(account.LastActiveUtcTicks + EntryWindow.Ticks, DateTimeKind.Utc);

        /// <summary>How many draws from the next one on the player is entered in without doing anything else.</summary>
        public static int EnteredDrawCount(CoinAccount account, DateTime now)
        {
            if (account.LastActiveUtcTicks <= 0) return 0;
            int count = 0;
            for (DateTime draw = NextRaffleUtc(now); IsEnteredIn(account, draw); draw = draw.AddMinutes(SlotMinutes))
                count++;
            return count;
        }

        public static bool IsActive(CoinAccount account, DateTime now) =>
            account.LastActiveUtcTicks > 0 && now.Ticks - account.LastActiveUtcTicks < ActiveWindow.Ticks;

        /// <summary>True when the account would be in the next raffle draw.</summary>
        public static bool IsEnteredInNextDraw(CoinAccount account, DateTime now) => IsEnteredIn(account, NextRaffleUtc(now));

        public static List<CoinAccount> Eligible(IEnumerable<CoinAccount> accounts, DateTime now) =>
            accounts.Where(a => IsActive(a, now)).ToList();

        /// <summary>
        /// Draws the raffle for the current slot if it hasn't been drawn. Returns null when it was already drawn or nobody is
        /// entered. The winner's prize goes off straight away (<see cref="Activate"/>); one that can't be used right now, or
        /// while the winner has a bet open (<paramref name="betOpenUserIds"/>), is kept for `.pu use`. The caller saves and
        /// announces. An entrant holding a Rigged Raffle wins.
        /// </summary>
        public DrawResult Draw(DateTime now, Func<double> random, IEnumerable<ulong> betOpenUserIds = null)
        {
            var data = _coinService.GetAll();
            long slot = SlotOf(now);
            if (data.LastRaffleSlot > slot) data.LastRaffleSlot = 0; //saved by shorter slots, whose numbers run ahead of these
            if (slot <= data.LastRaffleSlot) return null;
            data.LastRaffleSlot = slot;

            DateTime drawTime = SlotStartUtc(now);
            var entrants = data.Accounts.Where(a => IsEnteredIn(a, drawTime)).ToList();
            if (entrants.Count == 0) return null;

            double winnerRoll = random();
            var winner = entrants.FirstOrDefault(a => a.TryConsumeCharge(PowerUpIds.RiggedRaffle, now))
                ?? entrants[Math.Min(entrants.Count - 1, (int)(winnerRoll * entrants.Count))];
            var prize = PowerUpCatalog.Pick(random(), ContextFor(winner, entrants));
            winner.Grant(prize.Id, now);

            var result = new DrawResult { Winner = winner, Prize = prize, Entrants = entrants.Count };
            if (betOpenUserIds != null && betOpenUserIds.Contains(winner.UserId))
                result.Kept = "you have a bet open";
            else
                (result.Effect, result.Kept) = Activate(winner, now, random);
            return result;
        }

        public class DrawResult
        {
            public CoinAccount Winner { get; set; }
            public PowerUpDefinition Prize { get; set; }
            public int Entrants { get; set; }
            /// <summary>What the prize did when it went off, or null if it was kept.</summary>
            public string Effect { get; set; }
            /// <summary>Why the prize couldn't go off and was kept for `.pu use`, or null.</summary>
            public string Kept { get; set; }
        }

        /// <summary>
        /// Uses the power-up just added to the end of the player's held list. If it can't be used right now it stays held and
        /// the reason is returned. A Copycat's copy goes off straight away too.
        /// </summary>
        public (string Effect, string Kept) Activate(CoinAccount user, DateTime now, Func<double> random)
        {
            try
            {
                return (UseAndFollowCopy(user, user.HeldPowerUps.Count, now, random), null);
            }
            catch (BadInputException ex)
            {
                return (null, ex.Message);
            }
        }

        /// <summary>As <see cref="ApplyUse"/>, and a Copycat's copy (added to the end of the held list) goes off straight away too.</summary>
        private string UseAndFollowCopy(CoinAccount user, int number, DateTime now, Func<double> random)
        {
            user.Prune(now);
            bool copycat = number >= 1 && number <= user.HeldPowerUps.Count && user.HeldPowerUps[number - 1].Id == PowerUpIds.Copycat;
            string effect = ApplyUse(user, number, now, random);
            if (copycat && user.HeldPowerUps.Count > 0)
            {
                var (copyEffect, copyKept) = Activate(user, now, random);
                effect += copyKept == null ? $"\n{copyEffect}" : $"\nThe copy couldn't go off yet, so it's kept: {copyKept}";
            }
            return effect;
        }

        /// <summary>Where the winner stands on the leaderboard, for the rubber-banded draw.</summary>
        public PickContext ContextFor(CoinAccount winner, IReadOnlyCollection<CoinAccount> active)
        {
            var ranked = Ranked();
            int index = ranked.FindIndex(a => a.UserId == winner.UserId);
            if (index < 0 || ranked.Count == 0) return null;

            var worths = active.Select(_totalWorth).OrderBy(w => w).ToList();
            double median = worths.Count == 0 ? 0 : worths[worths.Count / 2];
            double leaderWorth = _totalWorth(ranked[0]);
            return new PickContext
            {
                IsTopTier = index < PowerUpCatalog.TopTier(ranked.Count),
                Percentile = ranked.Count <= 1 ? 0 : (double)index / (ranked.Count - 1),
                Runaway = leaderWorth > PowerUpCatalog.RunawayMultiple * Math.Max(1, median),
            };
        }

        /// <summary>Called every minute. Draws the raffle once per slot and announces the winner in one line in the last active channel.</summary>
        public async Task RunIfDue(DiscordSocketClient client, IEnumerable<ulong> betOpenUserIds)
        {
            long slotBefore = _coinService.GetAll().LastRaffleSlot;
            var result = Draw(DateTime.UtcNow, _random.NextDouble, betOpenUserIds);
            if (_coinService.GetAll().LastRaffleSlot == slotBefore) return; //nothing due
            await _coinService.Update(); //records the slot even if nobody entered, and what the prize did

            if (result == null) return;
            ulong channelId = _coinService.GetAll().LastActiveChannelId;
            if (!(client.GetChannel(channelId) is IMessageChannel channel)) return;

            //named, not pinged: there's a draw every hour
            string text = Announcement(result);
            if (text.Length > 1900) text = text.Substring(0, 1900) + "…";
            await channel.SendMessageAsync(text, allowedMentions: AllowedMentions.None);
        }

        public static string Announcement(DrawResult result)
        {
            var prize = result.Prize;
            string tag = prize.Category == PowerUpCategory.CatchUp ? "🧾 catch-up, " : prize.Category == PowerUpCategory.Attack ? "⚔️ attack, " : "";
            string line = $"🎟️ Raffle: <@{result.Winner.UserId}> won **{prize.Name}** ({tag}{PowerUpCatalog.RarityOf(prize)}, {result.Entrants} entered)";
            if (result.Kept == null)
                return $"{line} and it went off: {result.Effect}";
            string reason = result.Kept.Replace(" Keep it for later.", "").Trim();
            return $"{line}. It couldn't go off yet, so it's kept: {reason} `.pu use` it when you can.";
        }

        private List<CoinAccount> Ranked()
        {
            var accounts = _coinService.GetAll().Accounts;
            return (_appSettings.PrestigeDisabled
                ? accounts.OrderByDescending(_totalWorth)
                : accounts.OrderByDescending(a => a.PrestigeLevel).ThenByDescending(_totalWorth)).ToList();
        }

        /// <summary>
        /// Uses the user's <paramref name="number"/>th held power-up (1 is the first). Returns what happened. A power-up that
        /// can't be used right now throws a <see cref="BadInputException"/> and is kept.
        /// </summary>
        public async Task<string> Use(CoinAccount user, int number, DateTime now, Func<double> random)
        {
            string message = UseAndFollowCopy(user, number, now, random);
            await _coinService.Update();
            return message;
        }

        /// <summary>As <see cref="Use"/> but without saving.</summary>
        public string ApplyUse(CoinAccount user, int number, DateTime now, Func<double> random)
        {
            user.Prune(now);
            if (user.HeldPowerUps.Count == 0)
                throw new BadInputException("You don't have any power-ups. They come from the raffle every hour.");
            if (number < 1 || number > user.HeldPowerUps.Count)
                throw new BadInputException($"Pick a number from 1 to {user.HeldPowerUps.Count}.");
            var frozen = user.Running(PowerUpIds.Frozen, now).OrderByDescending(f => f.ExpiresAtTicks).FirstOrDefault();
            if (frozen != null)
                throw new BadInputException($"🧊 You're frozen by a Freeze Ray for another {Remaining(new DateTime(frozen.ExpiresAtTicks) - now)}. Your power-ups are kept.");

            var held = user.HeldPowerUps[number - 1];
            var power = PowerUpCatalog.Find(held.Id);
            if (power == null || power.Category == PowerUpCategory.Debuff)
            {
                user.HeldPowerUps.RemoveAt(number - 1);
                throw new BadInputException("That power-up no longer exists, so it was removed.");
            }

            string message;
            if (power.Kind == PowerUpKind.Instant)
            {
                message = ApplyInstant(power, user, held, now, random);
                if (power.Id != PowerUpIds.Copycat && user.TryConsumeCharge(PowerUpIds.Echo, now))
                {
                    try
                    {
                        message += $"\n🔁 **Echo!** {ApplyInstant(power, user, held, now, random)}";
                    }
                    catch (BadInputException ex)
                    {
                        message += $"\n🔁 The echo fizzled: {ex.Message}";
                    }
                }
            }
            else
            {
                user.Start(power, now);
                if (power.Id == PowerUpIds.LoadedDice)
                    user.LastGambleDate = null; //the next gamble counts as the first of the day again
                message = power.Kind == PowerUpKind.Timed
                    ? $"**{power.Name}** is running for {power.Duration.TotalHours:0.#} hours. {power.Description}"
                    : $"**{power.Name}** is ready. {power.Description}";
            }

            user.HeldPowerUps.Remove(held);
            if (power.Id != PowerUpIds.Copycat)
            {
                var data = _coinService.GetAll();
                data.LastUsedPowerUpId = power.Id;
                data.LastUsedPowerUpUserId = user.UserId;
            }
            return message;
        }

        /// <summary>
        /// Takes <paramref name="amount"/> from the victim's worth and gives it to the thief. The victim's cash is taken first;
        /// whatever they can't pay goes on their debt, which grows with interest until asset sales or repayment clear it.
        /// </summary>
        public static (double FromCash, double ToDebt) Seize(CoinAccount victim, CoinAccount thief, double amount)
        {
            double fromCash = Math.Min(Math.Max(0, victim.NetWorth), amount);
            victim.NetWorth -= fromCash;
            double toDebt = amount - fromCash;
            victim.Debt = Math.Round(victim.Debt + toDebt, 2);
            thief.NetWorth += amount;
            return (fromCash, toDebt);
        }

        /// <summary>What an attack did to one victim.</summary>
        public sealed class HitResult
        {
            public CoinAccount Victim { get; set; }
            /// <summary>What the attacker gained (negative when a Mirror made them pay).</summary>
            public double Gained { get; set; }
            public double ToDebt { get; set; }
            public bool Reflected { get; set; }
            public bool Parachuted { get; set; }

            public string Describe() =>
                Reflected ? $"{Victim.Name}'s 🪞 Mirror bounced it back and you paid them {Money(-Gained)}"
                : Victim.Name + $" ({Money(Gained)}" + (ToDebt > 0 ? $", {Money(ToDebt)} of it on their debt" : "") + (Parachuted ? ", 🪂 their Golden Parachute cancelled the debt part" : "") + ")";
        }

        /// <summary>
        /// One attack on one victim. A Mirror bounces it back so the attacker pays instead. A Golden Parachute limits it to
        /// the victim's cash. Otherwise it is a normal <see cref="Seize"/>: cash first, the rest on the victim's debt.
        /// Catch-up items don't come through here, so nothing blocks them.
        /// </summary>
        public static HitResult Hit(CoinAccount victim, CoinAccount attacker, double amount, DateTime now)
        {
            if (victim.TryConsumeCharge(PowerUpIds.Mirror, now))
            {
                Seize(attacker, victim, amount);
                return new HitResult { Victim = victim, Gained = -amount, Reflected = true };
            }

            bool parachuted = false;
            double cash = Math.Max(0, victim.NetWorth);
            if (amount > cash && victim.TryConsumeCharge(PowerUpIds.GoldenParachute, now))
            {
                amount = Math.Floor(cash);
                parachuted = true;
            }
            var (_, toDebt) = Seize(victim, attacker, amount);
            return new HitResult { Victim = victim, Gained = amount, ToDebt = toDebt, Parachuted = parachuted };
        }

        private static string Money(double amount) => "$" + Common.Helpers.FormatHelper.GetCommaNumber(amount);

        private static string Remaining(TimeSpan span) =>
            span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : $"{Math.Max(1, (int)Math.Ceiling(span.TotalMinutes))}m";

        /// <summary>Splits money the user is holding evenly between <paramref name="recipients"/> (the user may be one). Leftover cents stay with the user.</summary>
        private static double Redistribute(double total, CoinAccount user, IReadOnlyCollection<CoinAccount> recipients)
        {
            double share = recipients.Count == 0 ? 0 : Math.Floor(total / recipients.Count);
            if (share < 1) return 0;
            foreach (var recipient in recipients)
            {
                user.NetWorth -= share;
                recipient.NetWorth += share;
            }
            return share;
        }

        private CoinAccount LeaderToAttack(CoinAccount user, DateTime now, string itemName)
        {
            var leader = Ranked().FirstOrDefault();
            if (leader == null || leader.UserId == user.UserId)
                throw new BadInputException($"You're in 1st place, so {itemName} has nobody to hit. Keep it for later.");
            if (leader.IsShielded(now))
                throw new BadInputException($"{leader.Name} has a Shield running, so {itemName} would bounce off. Try again later.");
            return leader;
        }

        /// <summary>The leader, for a catch-up item. Refused when the user is in the top tier. Shields don't matter.</summary>
        private (CoinAccount Leader, List<CoinAccount> Ranked) LeaderForCatchUp(CoinAccount user, string itemName)
        {
            var ranked = Ranked();
            int index = ranked.FindIndex(a => a.UserId == user.UserId);
            if (index >= 0 && index < PowerUpCatalog.TopTier(ranked.Count))
                throw new BadInputException($"You're near the top of the leaderboard, so {itemName} isn't for you. It's for catching up. Keep it for later.");
            if (ranked.Count == 0 || ranked[0].UserId == user.UserId)
                throw new BadInputException($"There's nobody ahead of you for {itemName}. Keep it for later.");
            return (ranked[0], ranked);
        }

        private string StealFromEach(IEnumerable<CoinAccount> targets, CoinAccount user, double percent, DateTime now, out double total)
        {
            var lines = new List<string>();
            total = 0;
            foreach (var target in targets)
            {
                double amount = Math.Floor(_totalWorth(target) * percent);
                if (amount <= 0) continue;
                var hit = Hit(target, user, amount, now);
                total += hit.Gained;
                lines.Add(hit.Describe());
            }
            return string.Join(", ", lines);
        }

        private string ApplyInstant(PowerUpDefinition power, CoinAccount user, HeldPowerUp held, DateTime now, Func<double> random)
        {
            switch (power.Id)
            {
                case PowerUpIds.BlueShell:
                {
                    var leader = LeaderToAttack(user, now, "a Blue Shell");
                    double amount = Math.Floor(_totalWorth(leader) * PowerUpCatalog.BlueShellPercent);
                    if (amount <= 0)
                        throw new BadInputException($"{leader.Name} has nothing to steal.");
                    return $"💥 **Blue Shell!** You hit 1st place: {Hit(leader, user, amount, now).Describe()}.";
                }
                case PowerUpIds.Pickpocket:
                {
                    var targets = Eligible(_coinService.GetAll().Accounts, now)
                        .Where(a => a.UserId != user.UserId && _totalWorth(a) >= 1 && !a.IsShielded(now))
                        .OrderBy(_ => random())
                        .Take(PowerUpCatalog.PickpocketVictims)
                        .ToList();
                    string lines = StealFromEach(targets, user, PowerUpCatalog.PickpocketPercent, now, out double total);
                    if (lines.Length == 0)
                        throw new BadInputException("There's nobody active to pickpocket right now. Keep it for later.");
                    return $"🕵️ **Pickpocket!** You lifted {Money(total)}: {lines}.";
                }
                case PowerUpIds.RedShell:
                {
                    var ranked = Ranked();
                    int index = ranked.FindIndex(a => a.UserId == user.UserId);
                    if (index <= 0)
                        throw new BadInputException("You're in 1st place, so a Red Shell has nobody above you to hit. Keep it for later.");
                    var target = ranked[index - 1];
                    if (target.IsShielded(now))
                        throw new BadInputException($"{target.Name} has a Shield running, so the Red Shell would bounce off. Try again later.");
                    double amount = Math.Floor(_totalWorth(target) * PowerUpCatalog.RedShellPercent);
                    if (amount <= 0)
                        throw new BadInputException($"{target.Name} has nothing to steal.");
                    return $"🔴 **Red Shell!** You hit the player just above you: {Hit(target, user, amount, now).Describe()}.";
                }
                case PowerUpIds.LightningBolt:
                {
                    var ranked = Ranked();
                    int index = ranked.FindIndex(a => a.UserId == user.UserId);
                    if (index <= 0)
                        throw new BadInputException("You're in 1st place, so there's nobody above you to strike. Keep it for later.");
                    // the nearest players above you, closest first
                    var targets = ranked.Take(index).Reverse()
                        .Where(a => !a.IsShielded(now) && _totalWorth(a) >= 1)
                        .Take(PowerUpCatalog.LightningBoltVictims)
                        .ToList();
                    string lines = StealFromEach(targets, user, PowerUpCatalog.LightningBoltPercent, now, out double total);
                    if (lines.Length == 0)
                        throw new BadInputException("Everyone above you is shielded or broke right now. Keep it for later.");
                    return $"⚡ **Lightning Bolt!** You struck {Money(total)}: {lines}.";
                }
                case PowerUpIds.RobinHood:
                {
                    var richest = Ranked()
                        .Where(a => a.UserId != user.UserId && !a.IsShielded(now) && _totalWorth(a) >= 1)
                        .Take(PowerUpCatalog.RobinHoodVictims)
                        .ToList();
                    string lines = StealFromEach(richest, user, PowerUpCatalog.RobinHoodPercent, now, out double total);
                    if (lines.Length == 0)
                        throw new BadInputException("Nobody is rich enough to rob right now. Keep it for later.");

                    // the poorest half of the active players, you included if you're among them, share it out
                    var pool = Eligible(_coinService.GetAll().Accounts, now).OrderBy(_totalWorth).ToList();
                    var poorest = pool.Take(pool.Count / 2).ToList();
                    double share = total > 0 ? Redistribute(total, user, poorest) : 0;
                    string outcome = share >= 1
                        ? $" The poorest {poorest.Count} active player(s) each got {Money(share)}."
                        : " Nobody poor enough to share it with, so you kept it all.";
                    return $"🏹 **Robin Hood!** You took {Money(total)}: {lines}.{outcome}";
                }
                case PowerUpIds.HostileTakeover:
                {
                    var leader = LeaderToAttack(user, now, "the takeover");
                    double amount = Math.Floor(Math.Max(
                        Math.Max(0, leader.NetWorth) * PowerUpCatalog.HostileTakeoverCashPercent,
                        _totalWorth(leader) * PowerUpCatalog.HostileTakeoverWorthPercent));
                    if (amount <= 0)
                        throw new BadInputException($"{leader.Name} has nothing to take. Keep it for later.");
                    return $"🏴 **Hostile Takeover!** You seized 1st place's money: {Hit(leader, user, amount, now).Describe()}.";
                }
                case PowerUpIds.Heist:
                {
                    var leader = LeaderToAttack(user, now, "the heist");
                    if (random() < 0.5)
                    {
                        double amount = Math.Floor(_totalWorth(leader) * PowerUpCatalog.HeistWinPercent);
                        if (amount <= 0)
                            throw new BadInputException($"{leader.Name} has nothing worth robbing.");
                        return $"💰 **Heist: heads!** You cracked 1st place's vault: {Hit(leader, user, amount, now).Describe()}.";
                    }
                    double fine = Math.Floor(Math.Max(PowerUpCatalog.HeistLoseMin, _totalWorth(user) * PowerUpCatalog.HeistLosePercent));
                    var (_, toDebt) = Seize(user, leader, fine);
                    return $"🚨 **Heist: tails!** You got caught and paid {leader.Name} {Money(fine)}." +
                        (toDebt > 0 ? $" You didn't have the cash, so {Money(toDebt)} went on your debt." : "");
                }
                case PowerUpIds.MusicalChairs:
                {
                    var candidates = Eligible(_coinService.GetAll().Accounts, now)
                        .Where(a => a.UserId != user.UserId && a.NetWorth > user.NetWorth && !a.IsShielded(now))
                        .ToList();
                    if (candidates.Count == 0)
                        throw new BadInputException("Nobody active has more cash than you (or they're all shielded). Keep it for later.");
                    var target = candidates[Math.Min(candidates.Count - 1, (int)(random() * candidates.Count))];
                    if (target.TryConsumeCharge(PowerUpIds.Mirror, now))
                        return $"🪑 **Musical Chairs!** The music stopped on {target.Name}, but their 🪞 Mirror kept them in their seat. Nothing changed hands.";
                    double mine = user.NetWorth;
                    user.NetWorth = target.NetWorth;
                    target.NetWorth = mine;
                    return $"🪑 **Musical Chairs!** The music stopped and you swapped cash with {target.Name}: you now have {Money(user.NetWorth)}, they have {Money(target.NetWorth)}.";
                }
                case PowerUpIds.StickyFingers:
                {
                    var ranked = Ranked();
                    int index = ranked.FindIndex(a => a.UserId == user.UserId);
                    if (index <= 0)
                        throw new BadInputException("You're in 1st place, so there's nobody above you to rob. Keep it for later.");
                    var target = ranked[index - 1];
                    if (target.IsShielded(now))
                        throw new BadInputException($"{target.Name} has a Shield running. Try again later.");
                    target.Prune(now);
                    if (target.HeldPowerUps.Count == 0)
                        throw new BadInputException($"{target.Name} isn't holding any power-ups. Keep it for later.");
                    if (target.TryConsumeCharge(PowerUpIds.Mirror, now))
                        return $"🧤 **Sticky Fingers!** {target.Name}'s 🪞 Mirror caught your reflection with your hand in their pocket. Nothing taken.";
                    var stolen = target.HeldPowerUps[Math.Min(target.HeldPowerUps.Count - 1, (int)(random() * target.HeldPowerUps.Count))];
                    target.HeldPowerUps.Remove(stolen);
                    user.HeldPowerUps.Add(stolen); //keeps its expiry
                    return $"🧤 **Sticky Fingers!** You swiped **{PowerUpCatalog.Find(stolen.Id)?.Name ?? stolen.Id}** from {target.Name}. Check `.pu`.";
                }
                case PowerUpIds.FreezeRay:
                {
                    var leader = LeaderToAttack(user, now, "the Freeze Ray");
                    if (leader.TryConsumeCharge(PowerUpIds.Mirror, now))
                    {
                        user.AddDebuff(PowerUpIds.Frozen, now);
                        return $"🧊 **Freeze Ray!** {leader.Name}'s 🪞 Mirror bounced it back. You're frozen for 3 hours.";
                    }
                    leader.AddDebuff(PowerUpIds.Frozen, now);
                    return $"🧊 **Freeze Ray!** {leader.Name} is frozen: no power-ups for 3 hours.";
                }

                case PowerUpIds.TaxTheRich:
                {
                    var (_, ranked) = LeaderForCatchUp(user, "Tax the Rich");
                    var payers = ranked.Take(Math.Min(PowerUpCatalog.TaxTheRichPayers, PowerUpCatalog.TopTier(ranked.Count)))
                        .Where(a => a.UserId != user.UserId && _totalWorth(a) >= 1)
                        .ToList();
                    var lines = new List<string>();
                    double total = 0;
                    foreach (var payer in payers)
                    {
                        double amount = Math.Floor(_totalWorth(payer) * PowerUpCatalog.TaxTheRichPercent);
                        if (amount <= 0) continue;
                        var (_, toDebt) = Seize(payer, user, amount);
                        total += amount;
                        lines.Add($"{payer.Name} ({Money(amount)}" + (toDebt > 0 ? $", {Money(toDebt)} of it on their debt)" : ")"));
                    }
                    if (total <= 0)
                        throw new BadInputException("Nobody at the top has anything to tax. Keep it for later.");

                    var recipients = Eligible(_coinService.GetAll().Accounts, now).Where(a => !payers.Contains(a)).ToList();
                    if (!recipients.Contains(user)) recipients.Add(user);
                    double share = Redistribute(total, user, recipients);
                    return $"🧾 **Tax the Rich!** The top paid {Money(total)}: {string.Join(", ", lines)}. " +
                        $"{recipients.Count} player(s) each got {Money(share)}.";
                }
                case PowerUpIds.Audit:
                {
                    var (leader, _) = LeaderForCatchUp(user, "an Audit");
                    leader.AddDebuff(PowerUpIds.Audited, now);
                    return $"🧾 **Audit!** {leader.Name} is audited for 6 hours: no interest, and half of every bet they win goes to the jackpot.";
                }
                case PowerUpIds.Bailout:
                {
                    var (leader, _) = LeaderForCatchUp(user, "a Bailout");
                    double leaderWorth = Math.Max(0, _totalWorth(leader));
                    double paid = Math.Min(Math.Max(0, user.Debt), Math.Floor(leaderWorth * PowerUpCatalog.BailoutDebtCapPercent));
                    double bonus = Math.Floor(leaderWorth * PowerUpCatalog.BailoutBonusPercent);
                    if (paid + bonus <= 0)
                        throw new BadInputException($"{leader.Name} has nothing to bail you out with. Keep it for later.");
                    var (_, toDebt) = Seize(leader, user, paid + bonus);
                    user.NetWorth -= paid;
                    user.Debt = Math.Round(user.Debt - paid, 2);
                    _coinService.GetAll().AddDebtRepaymentToJackpot(paid);
                    return $"🧾 **Bailout!** {leader.Name} paid off {Money(paid)} of your debt and handed you {Money(bonus)}." +
                        (toDebt > 0 ? $" They were short, so {Money(toDebt)} went on their debt." : "");
                }
                case PowerUpIds.Squatters:
                {
                    var (leader, _) = LeaderForCatchUp(user, "Squatters");
                    if (leader.Properties.Count == 0)
                        throw new BadInputException($"{leader.Name} doesn't own any property to squat in. Keep it for later.");
                    double index = _coinService.GetAll().PropertyIndex;
                    var taken = leader.Running(PowerUpIds.Squatted, now).Select(s => PropertyService.SquattedPropertyId(leader, s)).ToHashSet();
                    var home = leader.Properties.Where(p => !taken.Contains(p.Id))
                        .OrderByDescending(p => PropertyCatalog.GrossRentPerHour(p.Kind, index)).FirstOrDefault();
                    if (home == null)
                        throw new BadInputException($"Every one of {leader.Name}'s properties already has squatters. Keep it for later.");
                    leader.AddDebuff(PowerUpIds.Squatted, now, user.UserId, home.Id);
                    double rent = PropertyCatalog.NetRentPerHour(home.Kind, index);
                    return $"🏚️ **Squatters!** You moved into {leader.Name}'s {PropertyCatalog.DisplayName(home.Kind).ToLower()}. Its rent (about {Money(rent)} an hour) comes to you for the next 12 hours.";
                }
                case PowerUpIds.Leech:
                {
                    var (leader, _) = LeaderForCatchUp(user, "a Leech");
                    leader.AddDebuff(PowerUpIds.Leeched, now, user.UserId);
                    return $"🦟 **Leech!** You latched onto {leader.Name}. For 6 hours, 15% of every bet they win comes to you.";
                }

                case PowerUpIds.Stimulus:
                {
                    double amount = Math.Floor(Math.Max(PowerUpCatalog.StimulusMin, _totalWorth(user) * PowerUpCatalog.StimulusPercent));
                    user.NetWorth += amount;
                    return $"💵 **Stimulus!** You received {Money(amount)}.";
                }
                case PowerUpIds.JackpotDip:
                {
                    double amount = _coinService.TakeFromJackpot(PowerUpCatalog.JackpotDipPercent);
                    if (amount <= 0)
                        throw new BadInputException("The jackpot is empty. Keep it for later.");
                    user.NetWorth += amount;
                    return $"💰 **Jackpot Dip!** You scooped {Money(amount)} out of the jackpot.";
                }
                case PowerUpIds.FreeCardPull:
                    user.LastFreePullDate = null;
                    return "🃏 **Free Card Pull!** Your free daily `.cardpull` is available again.";
                case PowerUpIds.InterestPass:
                    user.MostRecentDateBonusMet = now.ToString("yyyyMMdd");
                    return "📈 **Interest Pass!** Today counts as a bonus day, so you'll earn interest every hour.";
                case PowerUpIds.TimeMachine:
                {
                    bool recent = user.LastLossAmount > 0 && now.Ticks - user.LastLossUtcTicks <= PowerUpCatalog.TimeMachineWindow.Ticks;
                    if (!recent)
                        throw new BadInputException("You haven't lost a bet in the last hour, so there's nothing to undo. Keep it for later.");
                    double refund = Math.Floor(user.LastLossAmount);
                    user.NetWorth += refund;
                    user.LastLossAmount = 0;
                    return $"⏪ **Time Machine!** You went back and undid your last loss: {Money(refund)} returned.";
                }
                case PowerUpIds.Copycat:
                {
                    var data = _coinService.GetAll();
                    var copied = PowerUpCatalog.Find(data.LastUsedPowerUpId ?? "");
                    if (copied == null || copied.Category == PowerUpCategory.Debuff || copied.Id == PowerUpIds.Copycat)
                        throw new BadInputException("Nobody has used a power-up yet, so there's nothing to copy. Keep it for later.");
                    if (data.LastUsedPowerUpUserId == user.UserId)
                        throw new BadInputException($"The last power-up used was your own {copied.Name}. Wait for someone else to use one. Keep it for later.");
                    user.Grant(copied.Id, now);
                    return $"🐒 **Copycat!** You copied **{copied.Name}**.";
                }
                case PowerUpIds.Scratchie:
                {
                    var prizes = PowerUpCatalog.ScratchiePrizes;
                    int last = prizes.Count - 1, pick = last;
                    double roll = random(), running = 0;
                    for (int i = 0; i < prizes.Count; i++)
                    {
                        running += prizes[i].Chance;
                        if (roll < running) { pick = i; break; }
                    }
                    if (pick == last)
                    {
                        double jackpot = _coinService.TakeFromJackpot(1);
                        if (jackpot > 0)
                        {
                            user.NetWorth += jackpot;
                            return $"🎫 **Scratchie: JACKPOT!!!** You scratched off the whole jackpot: {Money(jackpot)}!";
                        }
                        pick = last - 1; //an empty jackpot pays the next best prize
                    }
                    double prize = Math.Floor(Math.Max(prizes[pick].Flat, _totalWorth(user) * prizes[pick].WorthShare));
                    user.NetWorth += prize;
                    return $"🎫 **Scratchie!** You scratched {Money(prize)}.";
                }
                default:
                    throw new BadInputException("That power-up can't be used.");
            }
        }
    }
}
