using Common.Services;
using DiscordBot.Games.Models.Cards;
using DiscordBot.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static DiscordBot.Models.CoinAccounts;

namespace DiscordBot.Services
{
    public class CoinService
    {
        private const string _fileName = "coinaccounts.json";
        private readonly LocalFileService _fileService;
        private readonly AppSettings _appSettings;
        private CoinAccounts _coinAccounts;

        public CoinService(LocalFileService fileService, AppSettings appSettings)
        {
            _fileService = fileService;
            _appSettings = appSettings;
            _coinAccounts = _fileService.GetContent<CoinAccounts>(_fileName).Result;
        }
        public CoinAccounts GetAll()
        {
            return _coinAccounts;
        }

        public double JackpotPool => _coinAccounts.JackpotPool;

        /// <summary>Adds a tax to the jackpot. It is saved with the next Update, which every tax-paying command already does.</summary>
        public void AddToJackpot(double amount)
        {
            if (amount > 0)
                _coinAccounts.JackpotPool += amount;
        }

        /// <summary>Takes up to this much out of the jackpot and returns what was there to take. Saved with the next Update.</summary>
        public double RemoveFromJackpot(double amount)
        {
            double taken = Math.Min(Math.Max(0, amount), _coinAccounts.JackpotPool);
            _coinAccounts.JackpotPool -= taken;
            return taken;
        }

        /// <summary>Takes a share of the jackpot and returns it. Saved with the next Update.</summary>
        public double TakeFromJackpot(double fraction)
        {
            double amount = Math.Floor(_coinAccounts.JackpotPool * fraction);
            if (amount <= 0) return 0;
            _coinAccounts.JackpotPool -= amount;
            return amount;
        }

        /// <summary>Notes that the player just did something with money, and where. Saved with the next Update.</summary>
        public void MarkActive(ulong userId, ulong channelId)
        {
            var account = _coinAccounts.Accounts.Find(a => a.UserId == userId);
            if (account == null) return;
            account.LastActiveUtcTicks = DateTime.UtcNow.Ticks;
            _coinAccounts.LastActiveChannelId = channelId;

            var recent = _coinAccounts.RecentActiveChannelIds;
            recent.Remove(channelId);
            recent.Insert(0, channelId);
            if (recent.Count > CoinAccounts.RecentChannelCount)
                recent.RemoveRange(CoinAccounts.RecentChannelCount, recent.Count - CoinAccounts.RecentChannelCount);
        }

        /// <summary>The last few channels money was used in, newest first. Before any are recorded, the last active channel alone.</summary>
        public IReadOnlyList<ulong> RecentChannels()
        {
            if (_coinAccounts.RecentActiveChannelIds.Count > 0)
                return _coinAccounts.RecentActiveChannelIds.ToList();
            return _coinAccounts.LastActiveChannelId != 0 ? new List<ulong> { _coinAccounts.LastActiveChannelId } : new List<ulong>();
        }

        /// <summary>What the player's houses would sell for right now, after selling costs.</summary>
        public double PropertyValue(CoinAccount account) =>
            account.Properties.Sum(p => PropertyCatalog.SaleValue(p.Kind, _coinAccounts.PropertyIndex));

        /// <summary>Empties the jackpot and returns what was in it. Saved with the next Update.</summary>
        public double TakeJackpot()
        {
            double pool = _coinAccounts.JackpotPool;
            _coinAccounts.JackpotPool = 0;
            return pool;
        }

        public async Task<CoinAccount> Get(ulong userId, string name, double amount = 10000)
        {
            if (!_coinAccounts.Accounts.Exists(a => a.UserId == userId))
                await Add(userId, amount, name);

            return _coinAccounts.Accounts.First(a => a.UserId == userId);
        }

        public async Task<CoinAccount> Add(ulong userId, double netWorth, string name)
        {
            var coinAccount = new CoinAccount()
            {
                Name = name,
                UserId = userId,
                NetWorth = netWorth,
                MostRecentDateBonusMet = DateTimeOffset.UtcNow.ToString("yyyyMMdd")
            };
            if (_coinAccounts.CarriedCards.Remove(userId, out var card))
                coinAccount.BattlePerson = card; //their card from last season, at level 0

            _coinAccounts.Accounts.Add(coinAccount);

            string content = Serialize();
            await _fileService.UpdateContent(_fileName, content);
            return coinAccount;
        }

        public async Task Update(ulong userId, double netWorth, string name, bool updateRemote = true)
        {
            bool bonusGranted = false;
            var account = _coinAccounts.Accounts.First(a => a.UserId == userId);
            account.NetWorth = netWorth;
            account.Name = name;

            //update remote with local changes when update remote is true
            if (updateRemote)
            {
                string content = Serialize();
                await _fileService.UpdateContent(_fileName, content);
            }
        }

        /// <summary>
        /// Pays off debt from whatever spare cash each player has, into the jackpot. Called every minute. Players in <paramref name="skipUserIds"/>
        /// have a bet open, so their cash is on the table and is left alone until it resolves.
        /// </summary>
        public async Task RepayDebtsFromCash(IEnumerable<ulong> skipUserIds)
        {
            var skip = new HashSet<ulong>(skipUserIds);
            bool changed = false;
            foreach (var account in _coinAccounts.Accounts.Where(a => a.Debt > 0 && !skip.Contains(a.UserId)))
                changed |= _coinAccounts.RepayFromCash(account) > 0;
            if (changed) await Update();
        }

        /// <summary>Records each account's total worth once per UTC hour, for the .networth graph.</summary>
        public async Task RecordNetWorthHistory(Func<CoinAccount, double> totalWorthOf)
        {
            var hourString = DateTimeOffset.UtcNow.ToString("yyyyMMddHH");
            if (_coinAccounts.LastNetWorthSampleHour == hourString) return;

            foreach (var account in _coinAccounts.Accounts)
            {
                account.NetWorthHistory.Add(totalWorthOf(account));
                int excess = account.NetWorthHistory.Count - CoinAccount.MaxNetWorthHistoryPoints;
                if (excess > 0)
                    account.NetWorthHistory.RemoveRange(0, excess);
            }
            _coinAccounts.LastNetWorthSampleHour = hourString;
            await Update();
        }

        public async Task Update()
        {
            await _fileService.UpdateContent(_fileName, Serialize());
        }

        /// <summary>
        /// The save as JSON. Accounts change on several threads (commands, buttons, game loops), so a list can change while it's
        /// being written out; that's retried rather than crashing the save.
        /// </summary>
        private string Serialize()
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    lock (Money.Sync) return JsonConvert.SerializeObject(_coinAccounts);
                }
                catch (InvalidOperationException) when (attempt < 5)
                {
                    System.Threading.Thread.Sleep(5);
                }
            }
        }

        /// <summary>
        /// INTENTIONAL, don't "fix": every account gets this every hour, with the hourly interest (it has always been called the
        /// daily $1,000 but is paid hourly, and the owner likes it that way).
        /// </summary>
        public const double HourlyIncome = 1_000;

        /// <summary>A new season. Everyone's Battle Person card is kept, back at level 0, and given back when their account is made again.</summary>
        public async Task ClearAll()
        {
            var carried = new Dictionary<ulong, BattlePerson>();
            foreach (var account in _coinAccounts.Accounts.Where(a => a.BattlePerson != null))
            {
                CardLevels.ResetForNewSeason(account.BattlePerson);
                carried[account.UserId] = account.BattlePerson;
            }
            foreach (var pair in _coinAccounts.CarriedCards.Where(p => !carried.ContainsKey(p.Key)))
                carried[pair.Key] = pair.Value; //kept from an earlier season and not picked up yet
            _coinAccounts = new CoinAccounts { CarriedCards = carried };
            string content = Serialize();
            await _fileService.UpdateContent(_fileName, content);
        }

        public async Task AddInterest()
        {
            var hourDateString = DateTimeOffset.UtcNow.ToString("yyyyMMddHH");
            var dateString = DateTimeOffset.UtcNow.ToString("yyyyMMdd");
            if (_coinAccounts.TimesInterestPaidForList.Contains(hourDateString)) return; //if already paid for the hour, exit

            //debt grows every hour until it is paid off from asset sales or repaid
            foreach (var debtor in _coinAccounts.Accounts.Where(a => a.Debt > 0))
                debtor.Debt = CoinAccount.GrowDebtOneHour(debtor.Debt);

            //INTENTIONAL: the $1,000 handout is paid to everyone every hour, not once a day
            foreach (var a in _coinAccounts.Accounts)
            {
                double income = _appSettings.PrestigeDisabled ? HourlyIncome : HourlyIncome * (a.PrestigeLevel + 1);
                Money.Credit(_coinAccounts, a, income);
                a.Stats.DailyIncomeEarned += income;
            }
            _coinAccounts.DateDailyIncrementPaidFor = dateString;

            var accountsToAction = _coinAccounts.Accounts.Where(a => a.MostRecentDateBonusMet == dateString);
            foreach (var account in accountsToAction)
            {
                if (account.IsActive(PowerUpIds.Audited, DateTime.UtcNow))
                    continue; //an Audit power-up stops their interest
                double multiplier = account.IsActive(PowerUpIds.CompoundInterest, DateTime.UtcNow) ? PowerUpCatalog.CompoundInterestMultiplier : 1;
                double interest = CalculateAddedInterest(account) * multiplier;
                Money.Credit(_coinAccounts, account, interest); //interest per hour for those who bet big today
                account.Stats.InterestEarned += interest;
            }
            _coinAccounts.TimesInterestPaidForList.Add(hourDateString);
            string content = Serialize();
            await _fileService.UpdateContent(_fileName, content);
        }

        public double CalculateAddedInterest(CoinAccount account, int fixedAmount = 1000)
        {
            //no prestige means no threshold to scale against: flat interest on net worth
            if (_appSettings.PrestigeDisabled)
                return account.NetWorth * Constants.InterestPercentage / 100;

            double p = account.GetAmountRequiredForNextLevel();
            double x = account.NetWorth;
            double c = Constants.InterestPercentage;
            double y = c - c * x / p;
            double multiplier = y / 100; //0.1 at 0 nw and 0 at p nw
            if (multiplier < 0) multiplier = 0;
            return (fixedAmount * (account.PrestigeLevel + 1)) + (multiplier * account.NetWorth);
        }
    }
}
