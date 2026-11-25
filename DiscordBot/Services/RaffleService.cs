using Discord;
using Discord.WebSocket;
using DiscordBot.Exceptions;
using DiscordBot.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static DiscordBot.Models.CoinAccounts;

namespace DiscordBot.Services
{
    /// <summary>
    /// A raffle every hour. Anyone who did something with money in the 6 hours before the draw is entered, and the winner's
    /// power-up goes off the moment it's won (one that can't be used right then is kept and goes off by itself as soon as it can, see `ActivateKept`). The draw rubber-bands like kart racing items: the top of the leaderboard only draws boosts,
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
        private readonly ILogger _logger;
        private readonly AnnouncementQueue _waiting = new AnnouncementQueue();

        public RaffleService(CoinService coinService, StockService stockService, AppSettings appSettings, ILogger<RaffleService> logger = null)
            : this(coinService, appSettings, new WorthService(coinService, stockService, appSettings).TotalWorth, logger)
        {
        }

        public RaffleService(CoinService coinService, AppSettings appSettings, Func<CoinAccount, double> totalWorth, ILogger logger = null)
        {
            _coinService = coinService;
            _appSettings = appSettings;
            _totalWorth = totalWorth;
            _logger = logger;
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
        /// while the winner has a bet open (<paramref name="betOpenUserIds"/>), is kept until `ActivateKept` can set it off. The caller saves and
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

            foreach (var entrant in entrants)
                entrant.Stats.RafflesEntered++;
            winner.Stats.RafflesWon++;

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
            /// <summary>Why the prize couldn't go off and was kept until it can, or null.</summary>
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

        /// <summary>The most power-ups tried for one player in one pass, so a loop of steals can't run away.</summary>
        public const int MaxActivationsPerPass = 40;

        /// <summary>
        /// Called every minute: tries every kept power-up and sets off the ones that can go off now (a target appeared, a freeze
        /// ended, a bet closed...), so nothing waits for `.pu use`. Players with a bet open are skipped, as at the draw. A power-up
        /// that still can't go off stays held and is tried again next minute. Returns what went off, in order, for the caller to
        /// save and announce.
        /// </summary>
        public List<(CoinAccount Owner, string Effect)> ActivateKept(DateTime now, Func<double> random, IEnumerable<ulong> betOpenUserIds = null)
        {
            var busy = new HashSet<ulong>(betOpenUserIds ?? Enumerable.Empty<ulong>());
            var went = new List<(CoinAccount, string)>();
            foreach (var account in _coinService.GetAll().Accounts.Where(a => a.HeldPowerUps.Count > 0 && !busy.Contains(a.UserId)).ToList())
            {
                account.Prune(now);
                if (account.Running(PowerUpIds.Frozen, now).Count > 0) continue; //frozen: nothing can be used until it ends

                int number = 1;
                for (int attempts = 0; number <= account.HeldPowerUps.Count && attempts < MaxActivationsPerPass; attempts++)
                {
                    int before = account.HeldPowerUps.Count;
                    try
                    {
                        went.Add((account, UseAndFollowCopy(account, number, now, random)));
                        //the one used is gone, so the next one is now at this number
                    }
                    catch (BadInputException)
                    {
                        if (account.HeldPowerUps.Count >= before) number++; //still held; one removed as obsolete leaves the next at this number
                    }
                }
            }
            return went;
        }

        /// <summary>As <see cref="ApplyUse"/>, and a Copycat's copy (added to the end of the held list) goes off straight away too.</summary>
        private string UseAndFollowCopy(CoinAccount user, int number, DateTime now, Func<double> random)
        {
            user.Prune(now);
            bool copycat = number >= 1 && number <= user.HeldPowerUps.Count && user.HeldPowerUps[number - 1].Id == PowerUpIds.Copycat;
            string effect = ApplyUse(user, number, now, random);
            user.Stats.PowerUpsUsed++;
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

        private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

        /// <summary>Raffle results go to every channel money was used in within this long.</summary>
        public static readonly TimeSpan AnnounceWindow = TimeSpan.FromDays(7);

        /// <summary>Called every minute. Draws the raffle once per slot and announces the winner in one line in every channel money was used in this week.</summary>
        public async Task RunIfDue(DiscordSocketClient client, IEnumerable<ulong> betOpenUserIds)
        {
            await FlushWaiting(client); //anything that couldn't be sent earlier goes first

            long slotBefore = _coinService.GetAll().LastRaffleSlot;
            var result = Draw(DateTime.UtcNow, _random.NextDouble, betOpenUserIds);
            if (_coinService.GetAll().LastRaffleSlot == slotBefore) return; //nothing due
            var dividend = PayJackpotDividend(SlotStartUtc(DateTime.UtcNow));
            await _coinService.Update(); //records the slot even if nobody entered, and what the prize did

            if (dividend != null)
                await Announce(client, DividendAnnouncement(dividend), null);
            if (result == null) return;

            //named, not pinged: there's a draw every hour
            string text = Announcement(result);
            if (text.Length > 1900) text = text.Substring(0, 1900) + "…";
            //anyone can open their own power-ups privately from here
            var mine = new ComponentBuilder().WithButton("My power-ups", ComponentId.Build(MoneyComponents.PowerUp, "mine"), ButtonStyle.Secondary).Build();
            await Announce(client, text, mine);
        }

        public class DividendResult
        {
            public double Total { get; set; }
            public double Each { get; set; }
            public int Recipients { get; set; }
            public double PoolLeft { get; set; }
        }

        /// <summary>
        /// Each hourly draw pays <see cref="PowerUpCatalog.JackpotDividendPercent"/> of the jackpot out, split evenly between the poorer
        /// half (rounded up, at least one) of the players entered in the draw due at <paramref name="drawTime"/>, so the pool drains
        /// back into the game instead of only growing. Null when nobody is entered or the share is under $1 each. The caller saves.
        /// </summary>
        public DividendResult PayJackpotDividend(DateTime drawTime)
        {
            var data = _coinService.GetAll();
            var entrants = data.Accounts.Where(a => IsEnteredIn(a, drawTime)).OrderBy(_totalWorth).ToList();
            if (entrants.Count == 0) return null;

            var poorer = entrants.Take((entrants.Count + 1) / 2).ToList();
            double each = Math.Floor(Math.Floor(data.JackpotPool * PowerUpCatalog.JackpotDividendPercent) / poorer.Count);
            if (each < 1) return null;

            _coinService.RemoveFromJackpot(each * poorer.Count);
            foreach (var account in poorer)
                DiscordBot.Models.Money.Credit(data, account, each);
            return new DividendResult { Total = each * poorer.Count, Each = each, Recipients = poorer.Count, PoolLeft = data.JackpotPool };
        }

        public static string DividendAnnouncement(DividendResult dividend) =>
            $"💰 Jackpot dividend: {PowerUpCatalog.JackpotDividendPercent:0%} of the pot ({DiscordBot.Models.Money.Format(dividend.Total)}) went to the poorer half of this draw's players, " +
            $"{dividend.Recipients} of them, {DiscordBot.Models.Money.Format(dividend.Each)} each. {DiscordBot.Models.Money.Format(dividend.PoolLeft)} is left in the pot.";

        /// <summary>Called every minute after the raffle: sets off kept power-ups that can go off now and announces each in the last active channel.</summary>
        public async Task ActivateKeptAndAnnounce(DiscordSocketClient client, IEnumerable<ulong> betOpenUserIds)
        {
            var went = ActivateKept(DateTime.UtcNow, _random.NextDouble, betOpenUserIds);
            if (went.Count == 0) return;
            await _coinService.Update();

            foreach (var group in went.GroupBy(w => w.Owner.UserId))
                await Announce(client, KeptAnnouncement(group.First().Owner, group.Select(g => g.Effect)), null);
        }

        /// <summary>
        /// Posts a message in every channel money was used in during the last <see cref="AnnounceWindow"/> (one that can't be reached
        /// or written to is skipped). If none takes it it waits in <see cref="AnnouncementQueue"/> and is tried again each minute, so a winner is
        /// never just lost. Never throws.
        /// </summary>
        private async Task Announce(DiscordSocketClient client, string text, MessageComponent components)
        {
            try
            {
                if (!await TrySend(client, text, components))
                {
                    _logger?.LogWarning("Raffle message couldn't be sent to any recent channel; it will be tried again: {Text}", text);
                    _waiting.Enqueue(text, components, DateTime.UtcNow);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Raffle announcement failed");
                _waiting.Enqueue(text, components, DateTime.UtcNow);
            }
        }

        private Task FlushWaiting(DiscordSocketClient client) =>
            _waiting.Count == 0 ? Task.CompletedTask : _waiting.Flush(item => TrySend(client, item.Text, item.Components), DateTime.UtcNow);

        /// <summary>Sends to each active channel, newest first (looking a channel up from Discord when it isn't cached). True when at least one took it.</summary>
        private async Task<bool> TrySend(DiscordSocketClient client, string text, MessageComponent components)
        {
            //all channels at once, each held to a time limit, so one slow channel can't stall the minute loop
            async Task<bool> SendTo(ulong id)
            {
                try
                {
                    var channel = client.GetChannel(id) as IMessageChannel ?? await client.GetChannelAsync(id) as IMessageChannel;
                    if (channel == null) return false;
                    await channel.SendMessageAsync(text, allowedMentions: AllowedMentions.None, components: components).WaitAsync(SendTimeout);
                    return true;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Couldn't send a raffle message to channel {Channel}: {Message}", id, ex.Message);
                    return false;
                }
            }
            var results = await Task.WhenAll(_coinService.ActiveChannels(AnnounceWindow, DateTime.UtcNow).Select(SendTo));
            return results.Any(r => r);
        }

        public static string KeptAnnouncement(CoinAccount owner, IEnumerable<string> effects)
        {
            string text = $"⏱️ <@{owner.UserId}>'s kept power-up went off:\n{string.Join("\n", effects)}";
            return text.Length > 1900 ? text.Substring(0, 1900) + "…" : text;
        }

        public static string Announcement(DrawResult result)
        {
            var prize = result.Prize;
            string tag = prize.Category == PowerUpCategory.CatchUp ? "🧾 catch-up, " : prize.Category == PowerUpCategory.Attack ? "⚔️ attack, " : "";
            string line = $"🎟️ Raffle: <@{result.Winner.UserId}> won **{prize.Name}** ({tag}{PowerUpCatalog.RarityOf(prize)}, {result.Entrants} entered)";
            if (result.Kept == null)
                return $"{line} and it went off: {result.Effect}";
            string reason = result.Kept.Replace(" Keep it for later.", "").Trim();
            return $"{line}. It couldn't go off yet, so it's kept: {reason} It goes off by itself as soon as it can.";
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
        public static (double FromCash, double ToDebt) Seize(CoinAccount victim, CoinAccount thief, double amount, double? debtRoom = null)
        {
            var (taken, toDebt) = DiscordBot.Models.Money.Seize(victim, thief, amount, debtRoom);
            thief.Stats.MoneyTakenByPowerUps += taken;
            victim.Stats.MoneyLostToPowerUps += taken;
            return (taken - toDebt, toDebt);
        }

        /// <summary>How much debt the victim's other assets can back, so an attack never takes money that isn't there.</summary>
        private double Room(CoinAccount victim) => DiscordBot.Models.Money.DebtRoom(victim, _totalWorth(victim));

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
        public static HitResult Hit(CoinAccount victim, CoinAccount attacker, double amount, DateTime now, double? debtRoom = null, double? attackerDebtRoom = null, string source = "Attack")
        {
            if (victim.TryConsumeCharge(PowerUpIds.Mirror, now))
            {
                var (reflectedCash, reflectedDebt) = Seize(attacker, victim, amount, attackerDebtRoom);
                amount = reflectedCash + reflectedDebt;
                victim.Stats.AttacksLanded++;
                attacker.Stats.AttacksSuffered++;
                AttackHistory.Record(victim, attacker.UserId, attacker.Name, source, 0, "bounced off your 🪞 Mirror", now);
                AttackHistory.Record(attacker, victim.UserId, victim.Name, source, amount, "your own attack, bounced back by their 🪞 Mirror", now);
                return new HitResult { Victim = victim, Gained = -amount, Reflected = true };
            }

            bool parachuted = false;
            double cash = Math.Max(0, victim.NetWorth);
            if (amount > cash && victim.TryConsumeCharge(PowerUpIds.GoldenParachute, now))
            {
                amount = Math.Floor(cash);
                parachuted = true;
            }
            var (fromCash, toDebt) = Seize(victim, attacker, amount, debtRoom);
            amount = fromCash + toDebt;
            attacker.Stats.AttacksLanded++;
            victim.Stats.AttacksSuffered++;
            var notes = new List<string>();
            if (toDebt > 0) notes.Add($"{Money(toDebt)} of it went on your debt");
            if (parachuted) notes.Add("🪂 your Golden Parachute kept it to your cash");
            AttackHistory.Record(victim, attacker.UserId, attacker.Name, source, amount, string.Join(", ", notes), now);
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
                user.NetWorth -= share; //the user is holding the money being shared out
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

        private string StealFromEach(IEnumerable<CoinAccount> targets, CoinAccount user, double percent, DateTime now, string source, out double total)
        {
            var lines = new List<string>();
            total = 0;
            foreach (var target in targets)
            {
                double amount = Math.Floor(_totalWorth(target) * percent);
                if (amount <= 0) continue;
                var hit = Hit(target, user, amount, now, Room(target), Room(user), source);
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
                    return $"💥 **Blue Shell!** You hit 1st place: {Hit(leader, user, amount, now, Room(leader), Room(user), power.Name).Describe()}.";
                }
                case PowerUpIds.Pickpocket:
                {
                    var targets = Eligible(_coinService.GetAll().Accounts, now)
                        .Where(a => a.UserId != user.UserId && _totalWorth(a) >= 1 && !a.IsShielded(now))
                        .OrderBy(_ => random())
                        .Take(PowerUpCatalog.PickpocketVictims)
                        .ToList();
                    string lines = StealFromEach(targets, user, PowerUpCatalog.PickpocketPercent, now, power.Name, out double total);
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
                    return $"🔴 **Red Shell!** You hit the player just above you: {Hit(target, user, amount, now, Room(target), Room(user), power.Name).Describe()}.";
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
                    string lines = StealFromEach(targets, user, PowerUpCatalog.LightningBoltPercent, now, power.Name, out double total);
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
                    string lines = StealFromEach(richest, user, PowerUpCatalog.RobinHoodPercent, now, power.Name, out double total);
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
                    return $"🏴 **Hostile Takeover!** You seized 1st place's money: {Hit(leader, user, amount, now, Room(leader), Room(user), power.Name).Describe()}.";
                }
                case PowerUpIds.Heist:
                {
                    var leader = LeaderToAttack(user, now, "the heist");
                    if (random() < 0.5)
                    {
                        double amount = Math.Floor(_totalWorth(leader) * PowerUpCatalog.HeistWinPercent);
                        if (amount <= 0)
                            throw new BadInputException($"{leader.Name} has nothing worth robbing.");
                        return $"💰 **Heist: heads!** You cracked 1st place's vault: {Hit(leader, user, amount, now, Room(leader), Room(user), power.Name).Describe()}.";
                    }
                    double fine = Math.Floor(Math.Max(PowerUpCatalog.HeistLoseMin, _totalWorth(user) * PowerUpCatalog.HeistLosePercent));
                    var (_, toDebt) = Seize(user, leader, fine, Room(user));
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
                    //a swap of cash, but nobody can walk off with more than twice what they're worth (at least $10,000), and
                    //negative cash isn't something you can hand over
                    double mine = Math.Max(0, user.NetWorth);
                    double gain = Math.Min(target.NetWorth - mine, Math.Max(PowerUpCatalog.MusicalChairsMinCap, 2 * Math.Max(0, _totalWorth(user))));
                    user.NetWorth += gain;
                    target.NetWorth -= gain;
                    user.Stats.MoneyTakenByPowerUps += gain;
                    target.Stats.MoneyLostToPowerUps += gain;
                    AttackHistory.Record(target, user.UserId, user.Name, power.Name, gain, "they swapped seats with you", now);
                    return $"🪑 **Musical Chairs!** The music stopped and you swapped seats with {target.Name}, nicking {Money(gain)} of their cash: you now have {Money(user.NetWorth)}, they have {Money(target.NetWorth)}.";
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
                    AttackHistory.Record(target, user.UserId, user.Name, power.Name, 0, $"they swiped your {PowerUpCatalog.Find(stolen.Id)?.Name ?? stolen.Id}", now);
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
                    AttackHistory.Record(leader, user.UserId, user.Name, power.Name, 0, "you can't use power-ups for 3 hours", now);
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
                        var (fromPayerCash, toDebt) = Seize(payer, user, amount, Room(payer));
                        AttackHistory.Record(payer, user.UserId, user.Name, power.Name, fromPayerCash + toDebt, toDebt > 0 ? $"{Money(toDebt)} of it went on your debt" : null, now);
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
                    AttackHistory.Record(leader, user.UserId, user.Name, power.Name, 0, "no interest and half your winnings to the jackpot for 6 hours", now);
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
                    var (fromLeaderCash, toDebt) = Seize(leader, user, paid + bonus, Room(leader));
                    AttackHistory.Record(leader, user.UserId, user.Name, power.Name, fromLeaderCash + toDebt, "they used a Bailout on you", now);
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
                    //the best rentals first; the cheapest few are spared, but there is always at least one to squat in
                    var byRent = leader.Properties.OrderByDescending(p => PropertyCatalog.GrossRentPerHour(p.Kind, index)).ToList();
                    int squattable = Math.Max(1, byRent.Count - PowerUpCatalog.SquattersSpared);
                    //every property they own that nobody is in yet; one already full of squatters is left to them
                    var taken = leader.Running(PowerUpIds.Squatted, now).Select(s => PropertyService.SquattedPropertyId(leader, s)).ToHashSet();
                    var homes = byRent.Take(squattable).Where(p => !taken.Contains(p.Id)).ToList();
                    if (homes.Count == 0)
                        throw new BadInputException($"Every property of {leader.Name}'s that squatters can reach already has squatters. Keep it for later.");
                    foreach (var home in homes)
                        leader.AddDebuff(PowerUpIds.Squatted, now, user.UserId, home.Id);
                    double rent = homes.Sum(h => PropertyCatalog.NetRentPerHour(h.Kind, index));
                    string places = homes.Count == 1 ? "a property" : $"{homes.Count} properties";
                    AttackHistory.Record(leader, user.UserId, user.Name, power.Name, 0, $"they moved into {(homes.Count == 1 ? "your property" : $"{homes.Count} of your properties")} for 12 hours", now);
                    int alreadyTaken = squattable - homes.Count;
                    string already = alreadyTaken > 0 ? $" ({alreadyTaken} more already had squatters.)" : "";
                    return $"🏚️ **Squatters!** You moved into {places} of {leader.Name}'s. Their rent (about {Money(rent)} an hour in all) comes to you for the next 12 hours.{already}";
                }
                case PowerUpIds.Leech:
                {
                    var (leader, _) = LeaderForCatchUp(user, "a Leech");
                    leader.AddDebuff(PowerUpIds.Leeched, now, user.UserId);
                    AttackHistory.Record(leader, user.UserId, user.Name, power.Name, 0, "15% of your winnings go to them for 6 hours", now);
                    return $"🦟 **Leech!** You latched onto {leader.Name}. For 6 hours, 15% of every bet they win comes to you.";
                }

                case PowerUpIds.Stimulus:
                {
                    double amount = Math.Floor(Math.Max(PowerUpCatalog.StimulusMin, _totalWorth(user) * PowerUpCatalog.StimulusPercent));
                    DiscordBot.Models.Money.Credit(_coinService.GetAll(), user, amount);
                    return $"💵 **Stimulus!** You received {Money(amount)}.";
                }
                case PowerUpIds.JackpotDip:
                {
                    double amount = _coinService.TakeFromJackpot(PowerUpCatalog.JackpotDipPercent);
                    if (amount <= 0)
                        throw new BadInputException("The jackpot is empty. Keep it for later.");
                    DiscordBot.Models.Money.Credit(_coinService.GetAll(), user, amount);
                    return $"💰 **Jackpot Dip!** You scooped {Money(amount)} out of the jackpot.";
                }
                case PowerUpIds.PotShot:
                {
                    double amount = _coinService.TakeFromJackpot(PowerUpCatalog.PotShotPercent);
                    if (amount <= 0)
                        throw new BadInputException("The jackpot is empty. Keep it for later.");
                    DiscordBot.Models.Money.Credit(_coinService.GetAll(), user, amount);
                    return $"🎯 **Pot Shot!** You took {Money(amount)} out of the jackpot.";
                }
                case PowerUpIds.PotShare:
                {
                    LeaderForCatchUp(user, "Pot Share");
                    var active = Eligible(_coinService.GetAll().Accounts, now).OrderBy(_totalWorth).ToList();
                    var poorest = active.Take(Math.Max(1, active.Count / 2)).ToList();
                    if (!poorest.Contains(user)) poorest.Add(user);
                    double amount = Math.Floor(_coinService.JackpotPool * PowerUpCatalog.PotSharePercent);
                    double each = Math.Floor(amount / poorest.Count);
                    if (each < 1)
                        throw new BadInputException("There's not enough in the jackpot to share out. Keep it for later.");
                    _coinService.RemoveFromJackpot(each * poorest.Count);
                    foreach (var recipient in poorest)
                        DiscordBot.Models.Money.Credit(_coinService.GetAll(), recipient, each);
                    return $"🍕 **Pot Share!** You split {Money(each * poorest.Count)} of the jackpot between the poorest {poorest.Count} active players: {Money(each)} each, you included.";
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
                    DiscordBot.Models.Money.Credit(_coinService.GetAll(), user, refund);
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
                            DiscordBot.Models.Money.Credit(_coinService.GetAll(), user, jackpot);
                            return $"🎫 **Scratchie: JACKPOT!!!** You scratched off the whole jackpot: {Money(jackpot)}!";
                        }
                        pick = last - 1; //an empty jackpot pays the next best prize
                    }
                    double prize = Math.Floor(Math.Max(prizes[pick].Flat, _totalWorth(user) * prizes[pick].WorthShare));
                    DiscordBot.Models.Money.Credit(_coinService.GetAll(), user, prize);
                    return $"🎫 **Scratchie!** You scratched {Money(prize)}.";
                }
                default:
                    throw new BadInputException("That power-up can't be used.");
            }
        }
    }
}
