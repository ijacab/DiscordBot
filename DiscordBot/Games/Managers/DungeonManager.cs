using Common.Helpers;
using DiscordBot.Exceptions;
using DiscordBot.Games.Models.Dungeon;
using DiscordBot.Services;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using static DiscordBot.Models.CoinAccounts;

namespace DiscordBot.Games.Managers
{
    /// <summary>
    /// Runs solo dungeon crawls. The buy-in comes out of cash, the haul is banked on cash out,
    /// and death forfeits both. Runs are in memory, so a bot restart ends any run in progress.
    /// </summary>
    public class DungeonManager
    {
        public const double MinBuyIn = 100;
        public static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(15);

        private readonly CoinService _coinService;
        private readonly Dictionary<ulong, DungeonRun> _runs = new Dictionary<ulong, DungeonRun>();
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        public DungeonManager(CoinService coinService)
        {
            _coinService = coinService;
        }

        public bool IsActive(ulong userId)
        {
            return _runs.ContainsKey(userId);
        }

        public async Task<string> Start(CoinAccount account, double buyIn)
        {
            await _gate.WaitAsync();
            try
            {
                string notice = await ExpireIfIdle(account.UserId, account.Name);
                if (_runs.ContainsKey(account.UserId))
                    throw new BadInputException("You're already in a dungeon. Use `.dungeon` to see where you are.");
                if (buyIn < MinBuyIn)
                    throw new BadInputException($"The dungeon needs at least ${FormatHelper.GetCommaNumber(MinBuyIn)} to enter.");
                if (buyIn > account.NetWorth)
                    throw new BadInputException($"You only have ${FormatHelper.GetCommaNumber(account.NetWorth)}.");

                account.NetWorth -= buyIn;
                await _coinService.Update(account.UserId, account.NetWorth, account.Name);
                _runs[account.UserId] = new DungeonRun(buyIn);

                return notice + $"You pay the ${FormatHelper.GetCommaNumber(buyIn)} buy-in and step through the door.\n" +
                    "Nothing is banked until you cash out, so every floor you clear is money at risk.\n" +
                    $"Use `.dungeon go` to enter the next room. {StatusLine(_runs[account.UserId])}";
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<string> Go(ulong userId, string name)
        {
            await _gate.WaitAsync();
            try
            {
                string notice = await ExpireIfIdle(userId, name);
                if (!TryGetRun(userId, out DungeonRun run))
                    return notice + NoRunMessage;

                run.LastActivityUtc = DateTime.UtcNow;
                RoomResult room = run.Descend();
                string body = string.Join("\n", room.Lines);

                if (room.Died)
                {
                    _runs.Remove(userId);
                    return notice + body + $"\nYou lose your haul of ${FormatHelper.GetCommaNumber(run.Haul)} and your ${FormatHelper.GetCommaNumber(run.BuyIn)} buy-in.";
                }

                return notice + body + "\n" + StatusLine(run) + "\n" + ActionsLine(run);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<string> Cash(ulong userId, string name)
        {
            await _gate.WaitAsync();
            try
            {
                string notice = await ExpireIfIdle(userId, name);
                if (!TryGetRun(userId, out DungeonRun run))
                    return notice + NoRunMessage;

                double payout = run.CashOut();
                _runs.Remove(userId);
                await PayOut(userId, name, payout);

                return notice + $"You climb out alive with ${payout:N2} of haul after {run.Floor} rooms and {run.Kills} kills. " +
                    $"Your buy-in was ${run.BuyIn:N2}, so you {(payout >= run.BuyIn ? "made" : "lost")} " +
                    $"${Math.Abs(payout - run.BuyIn):N2} overall.";
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<string> Buy(ulong userId, string name)
        {
            await _gate.WaitAsync();
            try
            {
                string notice = await ExpireIfIdle(userId, name);
                if (!TryGetRun(userId, out DungeonRun run))
                    return notice + NoRunMessage;

                run.LastActivityUtc = DateTime.UtcNow;
                bool bought = run.TryBuyOffer(out string message);
                return notice + message + (bought ? "\n" + StatusLine(run) : "");
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<string> Drink(ulong userId, string name)
        {
            await _gate.WaitAsync();
            try
            {
                string notice = await ExpireIfIdle(userId, name);
                if (!TryGetRun(userId, out DungeonRun run))
                    return notice + NoRunMessage;

                run.LastActivityUtc = DateTime.UtcNow;
                run.TryDrink(out string message);
                return notice + message + "\n" + StatusLine(run);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<string> Status(ulong userId, string name)
        {
            await _gate.WaitAsync();
            try
            {
                string notice = await ExpireIfIdle(userId, name);
                if (!TryGetRun(userId, out DungeonRun run))
                    return notice + NoRunMessage;

                return notice + StatusLine(run) + "\n" + ActionsLine(run);
            }
            finally
            {
                _gate.Release();
            }
        }

        private const string NoRunMessage = "You're not in a dungeon. Start one with `.dungeon 1000`, where 1000 is your buy-in.";

        private bool TryGetRun(ulong userId, out DungeonRun run)
        {
            return _runs.TryGetValue(userId, out run);
        }

        /// <summary>Cashes out a run that has sat idle too long, so an abandoned run still pays what it earned.</summary>
        private async Task<string> ExpireIfIdle(ulong userId, string name)
        {
            if (!TryGetRun(userId, out DungeonRun run))
                return "";
            if (DateTime.UtcNow - run.LastActivityUtc < IdleLimit)
                return "";

            double payout = run.CashOut();
            _runs.Remove(userId);
            await PayOut(userId, name, payout);
            return $"Your last run timed out after {IdleLimit.TotalMinutes} idle minutes, so you were pulled out with ${payout:N2}.\n\n";
        }

        private async Task PayOut(ulong userId, string name, double payout)
        {
            CoinAccount account = await _coinService.Get(userId, name);
            account.NetWorth += payout;
            await _coinService.Update(userId, account.NetWorth, name);
        }

        private static string StatusLine(DungeonRun run)
        {
            return $"HP {run.Hp}/{run.MaxHp} | Attack {run.Attack} | Defense {run.Defense} | Potions {run.Potions} | " +
                $"Haul ${run.Haul:N2} | Floor {run.Floor}";
        }

        private static string ActionsLine(DungeonRun run)
        {
            var actions = new List<string> { "`.dungeon go`", $"`.dungeon cash` to bank ${run.Haul:N2}" };
            if (run.Potions > 0 && run.Hp < run.MaxHp)
                actions.Add("`.dungeon drink`");
            if (run.Offer != PendingOffer.None)
                actions.Add("`.dungeon buy`");
            return "Next: " + string.Join(", ", actions) + ".";
        }
    }
}
