using Common.Services;
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
        }

        /// <summary>What the player's houses and apartments would sell for right now, after selling costs.</summary>
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

            _coinAccounts.Accounts.Add(coinAccount);

            string content = JsonConvert.SerializeObject(_coinAccounts);
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
                string content = JsonConvert.SerializeObject(_coinAccounts);
                await _fileService.UpdateContent(_fileName, content);
            }
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
            string content = JsonConvert.SerializeObject(_coinAccounts);
            await _fileService.UpdateContent(_fileName, content);
        }

        public async Task ClearAll()
        {
            _coinAccounts = new CoinAccounts();
            string content = JsonConvert.SerializeObject(_coinAccounts);
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

            if (!string.IsNullOrWhiteSpace(_coinAccounts.DateDailyIncrementPaidFor)
                || dateString != _coinAccounts.DateDailyIncrementPaidFor)
            {
                _coinAccounts.Accounts.ForEach(a => a.NetWorth += _appSettings.PrestigeDisabled ? 1000 : 1000 * (a.PrestigeLevel + 1)); //add 1000 if it is the start of the day
                _coinAccounts.DateDailyIncrementPaidFor = dateString;
            }

            var accountsToAction = _coinAccounts.Accounts.Where(a => a.MostRecentDateBonusMet == dateString);
            foreach (var account in accountsToAction)
            {
                double multiplier = account.IsActive(PowerUpIds.CompoundInterest, DateTime.UtcNow) ? PowerUpCatalog.CompoundInterestMultiplier : 1;
                account.NetWorth += CalculateAddedInterest(account) * multiplier; //add interest per hour if they are granted the bonus
            }
            _coinAccounts.TimesInterestPaidForList.Add(hourDateString);
            string content = JsonConvert.SerializeObject(_coinAccounts);
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
