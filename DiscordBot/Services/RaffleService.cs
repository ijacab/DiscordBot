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
    /// A raffle every 5 hours. Anyone who did something with money in the last 24 hours is entered, and the winner is given
    /// a power-up to hold. Also handles using held power-ups.
    /// </summary>
    public class RaffleService
    {
        public const int SlotHours = 5;
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
            new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds() / (SlotHours * 3600L);

        public static DateTime NextRaffleUtc(DateTime utc) =>
            DateTimeOffset.FromUnixTimeSeconds((SlotOf(utc) + 1) * SlotHours * 3600L).UtcDateTime;

        public static bool IsActive(CoinAccount account, DateTime now) =>
            account.LastActiveUtcTicks > 0 && now.Ticks - account.LastActiveUtcTicks < ActiveWindow.Ticks;

        /// <summary>True when the account would be in the next raffle draw, which is the same test <see cref="IsActive"/> applies at draw time.</summary>
        public static bool IsEnteredInNextDraw(CoinAccount account, DateTime now)
        {
            DateTime next = NextRaffleUtc(now);
            return account.LastActiveUtcTicks > 0 && next.Ticks - account.LastActiveUtcTicks < ActiveWindow.Ticks;
        }

        public static List<CoinAccount> Eligible(IEnumerable<CoinAccount> accounts, DateTime now) =>
            accounts.Where(a => IsActive(a, now)).ToList();

        /// <summary>
        /// Draws the raffle for the current slot if it hasn't been drawn. Returns null when it was already drawn or nobody is
        /// eligible. The winner is given the prize; the caller saves and announces.
        /// </summary>
        public (CoinAccount Winner, PowerUpDefinition Prize, int Entrants)? Draw(DateTime now, Func<double> random)
        {
            var data = _coinService.GetAll();
            long slot = SlotOf(now);
            if (slot <= data.LastRaffleSlot) return null;
            data.LastRaffleSlot = slot;

            var entrants = Eligible(data.Accounts, now);
            if (entrants.Count == 0) return null;

            var winner = entrants[Math.Min(entrants.Count - 1, (int)(random() * entrants.Count))];
            var prize = PowerUpCatalog.Pick(random());
            winner.Grant(prize.Id, now);
            return (winner, prize, entrants.Count);
        }

        /// <summary>Called every minute. Draws the raffle once per slot and announces the winner in the last active channel.</summary>
        public async Task RunIfDue(DiscordSocketClient client)
        {
            long slotBefore = _coinService.GetAll().LastRaffleSlot;
            var result = Draw(DateTime.UtcNow, _random.NextDouble);
            if (_coinService.GetAll().LastRaffleSlot == slotBefore) return; //nothing due
            await _coinService.Update(); //records the slot even if nobody entered

            if (result == null) return;
            var (winner, prize, entrants) = result.Value;
            ulong channelId = _coinService.GetAll().LastActiveChannelId;
            if (!(client.GetChannel(channelId) is IMessageChannel channel)) return;

            await channel.SendMessageAsync(
                $"🎟️ **Raffle!** {entrants} player(s) entered and <@{winner.UserId}> won **{prize.Name}**: {prize.Description}\n" +
                $"Type `.pu` to see your power-ups and `.pu use <number>` to use one. It expires in 24 hours.");
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
            string message = ApplyUse(user, number, now, random);
            await _coinService.Update();
            return message;
        }

        /// <summary>As <see cref="Use"/> but without saving.</summary>
        public string ApplyUse(CoinAccount user, int number, DateTime now, Func<double> random)
        {
            user.Prune(now);
            if (user.HeldPowerUps.Count == 0)
                throw new BadInputException("You don't have any power-ups. They come from the raffle every 5 hours.");
            if (number < 1 || number > user.HeldPowerUps.Count)
                throw new BadInputException($"Pick a number from 1 to {user.HeldPowerUps.Count}.");

            var held = user.HeldPowerUps[number - 1];
            var power = PowerUpCatalog.Find(held.Id);
            if (power == null)
            {
                user.HeldPowerUps.RemoveAt(number - 1);
                throw new BadInputException("That power-up no longer exists, so it was removed.");
            }

            string message;
            if (power.Kind == PowerUpKind.Instant)
                message = ApplyInstant(power, user, now, random);
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

        private string ApplyInstant(PowerUpDefinition power, CoinAccount user, DateTime now, Func<double> random)
        {
            switch (power.Id)
            {
                case PowerUpIds.BlueShell:
                {
                    var leader = Ranked().FirstOrDefault();
                    if (leader == null || leader.UserId == user.UserId)
                        throw new BadInputException("You're in 1st place, so a Blue Shell has nobody to hit. Keep it for later.");
                    if (leader.IsShielded(now))
                        throw new BadInputException($"{leader.Name} has a Shield running, so the Blue Shell would bounce off. Try again later.");
                    double amount = Math.Floor(_totalWorth(leader) * PowerUpCatalog.BlueShellPercent);
                    if (amount <= 0)
                        throw new BadInputException($"{leader.Name} has nothing to steal.");
                    var (_, toDebt) = Seize(leader, user, amount);
                    return $"💥 **Blue Shell!** You hit {leader.Name} in 1st place and took ${Common.Helpers.FormatHelper.GetCommaNumber(amount)}." +
                        (toDebt > 0 ? $" They didn't have enough cash, so ${Common.Helpers.FormatHelper.GetCommaNumber(toDebt)} went on their debt." : "");
                }
                case PowerUpIds.Pickpocket:
                {
                    var targets = RaffleService.Eligible(_coinService.GetAll().Accounts, now)
                        .Where(a => a.UserId != user.UserId && _totalWorth(a) >= 1 && !a.IsShielded(now))
                        .OrderBy(_ => random())
                        .Take(PowerUpCatalog.PickpocketVictims)
                        .ToList();
                    var lines = new List<string>();
                    double total = 0;
                    foreach (var target in targets)
                    {
                        double amount = Math.Floor(_totalWorth(target) * PowerUpCatalog.PickpocketPercent);
                        if (amount <= 0) continue;
                        Seize(target, user, amount);
                        total += amount;
                        lines.Add($"{target.Name} (${Common.Helpers.FormatHelper.GetCommaNumber(amount)})");
                    }
                    if (lines.Count == 0)
                        throw new BadInputException("There's nobody active to pickpocket right now. Keep it for later.");
                    return $"🕵️ **Pickpocket!** You lifted ${Common.Helpers.FormatHelper.GetCommaNumber(total)} from {string.Join(", ", lines)}.";
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
                    var (_, toDebt) = Seize(target, user, amount);
                    return $"🔴 **Red Shell!** You hit {target.Name}, just above you, and took ${Common.Helpers.FormatHelper.GetCommaNumber(amount)}." +
                        (toDebt > 0 ? $" They didn't have enough cash, so ${Common.Helpers.FormatHelper.GetCommaNumber(toDebt)} went on their debt." : "");
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
                    var lines = new List<string>();
                    double total = 0;
                    foreach (var target in targets)
                    {
                        double amount = Math.Floor(_totalWorth(target) * PowerUpCatalog.LightningBoltPercent);
                        if (amount <= 0) continue;
                        Seize(target, user, amount);
                        total += amount;
                        lines.Add($"{target.Name} (${Common.Helpers.FormatHelper.GetCommaNumber(amount)})");
                    }
                    if (lines.Count == 0)
                        throw new BadInputException("Everyone above you is shielded or broke right now. Keep it for later.");
                    return $"⚡ **Lightning Bolt!** You struck ${Common.Helpers.FormatHelper.GetCommaNumber(total)} from {string.Join(", ", lines)}.";
                }
                case PowerUpIds.RobinHood:
                {
                    var richest = Ranked()
                        .Where(a => a.UserId != user.UserId && !a.IsShielded(now) && _totalWorth(a) >= 1)
                        .Take(PowerUpCatalog.RobinHoodVictims)
                        .ToList();
                    var lines = new List<string>();
                    double total = 0;
                    foreach (var target in richest)
                    {
                        double amount = Math.Floor(_totalWorth(target) * PowerUpCatalog.RobinHoodPercent);
                        if (amount <= 0) continue;
                        Seize(target, user, amount);
                        total += amount;
                        lines.Add($"{target.Name} (${Common.Helpers.FormatHelper.GetCommaNumber(amount)})");
                    }
                    if (lines.Count == 0)
                        throw new BadInputException("Nobody is rich enough to rob right now. Keep it for later.");

                    // the poorest half of the active players, you included if you're among them, share it out
                    var pool = Eligible(_coinService.GetAll().Accounts, now).OrderBy(_totalWorth).ToList();
                    var poorest = pool.Take(pool.Count / 2).ToList();
                    double share = poorest.Count == 0 ? 0 : Math.Floor(total / poorest.Count);
                    string outcome;
                    if (share >= 1)
                    {
                        foreach (var poor in poorest)
                        {
                            user.NetWorth -= share;
                            poor.NetWorth += share;
                        }
                        outcome = $" The poorest {poorest.Count} active player(s) each got ${Common.Helpers.FormatHelper.GetCommaNumber(share)}.";
                    }
                    else
                    {
                        outcome = " Nobody poor enough to share it with, so you kept it all.";
                    }
                    return $"🏹 **Robin Hood!** You took ${Common.Helpers.FormatHelper.GetCommaNumber(total)} from {string.Join(", ", lines)}.{outcome}";
                }
                case PowerUpIds.HostileTakeover:
                {
                    var leader = Ranked().FirstOrDefault();
                    if (leader == null || leader.UserId == user.UserId)
                        throw new BadInputException("You're in 1st place, so there's nobody to take from. Keep it for later.");
                    if (leader.IsShielded(now))
                        throw new BadInputException($"{leader.Name} has a Shield running, so the takeover would bounce off. Try again later.");
                    double amount = Math.Floor(Math.Max(0, leader.NetWorth) * PowerUpCatalog.HostileTakeoverPercent);
                    if (amount <= 0)
                        throw new BadInputException($"{leader.Name} has no cash to take. Keep it for later.");
                    leader.NetWorth -= amount;
                    user.NetWorth += amount;
                    return $"🏴 **Hostile Takeover!** You seized ${Common.Helpers.FormatHelper.GetCommaNumber(amount)} of {leader.Name}'s cash.";
                }
                case PowerUpIds.Stimulus:
                {
                    double amount = Math.Floor(Math.Max(PowerUpCatalog.StimulusMin, _totalWorth(user) * PowerUpCatalog.StimulusPercent));
                    user.NetWorth += amount;
                    return $"💵 **Stimulus!** You received ${Common.Helpers.FormatHelper.GetCommaNumber(amount)}.";
                }
                case PowerUpIds.JackpotDip:
                {
                    double amount = _coinService.TakeFromJackpot(PowerUpCatalog.JackpotDipPercent);
                    if (amount <= 0)
                        throw new BadInputException("The jackpot is empty. Keep it for later.");
                    user.NetWorth += amount;
                    return $"💰 **Jackpot Dip!** You scooped ${Common.Helpers.FormatHelper.GetCommaNumber(amount)} out of the jackpot.";
                }
                case PowerUpIds.FreeCardPull:
                    user.LastFreePullDate = null;
                    return "🃏 **Free Card Pull!** Your free daily `.cardpull` is available again.";
                case PowerUpIds.InterestPass:
                    user.MostRecentDateBonusMet = now.ToString("yyyyMMdd");
                    return "📈 **Interest Pass!** Today counts as a bonus day, so you'll earn interest every hour.";
                default:
                    throw new BadInputException("That power-up can't be used.");
            }
        }
    }
}
