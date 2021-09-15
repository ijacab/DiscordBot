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
        private readonly GistService _gistService;
        private CoinAccounts _coinAccounts;

        public CoinService(GistService gistService)
        {
            _gistService = gistService;
            GetAll().Wait();
        }
        public async Task<CoinAccounts> GetAll()
        {
            _coinAccounts = await _gistService.GetContent<CoinAccounts>(_fileName);
            return _coinAccounts;
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
                DateHourlyBonusPaidFor = DateTimeOffset.UtcNow.ToString("yyyyMMdd")
            };

            _coinAccounts.Accounts.Add(coinAccount);

            string content = JsonConvert.SerializeObject(_coinAccounts);
            await _gistService.UpdateContent(_fileName, content);
            return coinAccount;
        }

        public async Task<bool> Update(ulong userId, double netWorth, string name, bool hourlyBonusGranted = false)
        {
            bool bonusGranted = false;
            var account = _coinAccounts.Accounts.First(a => a.UserId == userId);
            account.NetWorth = netWorth;
            account.Name = name;

            var todayString = DateTimeOffset.UtcNow.ToString("yyyyMMdd");
            if (account.DateHourlyBonusPaidFor != todayString && hourlyBonusGranted)
            {
                account.DateHourlyBonusPaidFor = todayString;
                bonusGranted = true;
            }

            string content = JsonConvert.SerializeObject(_coinAccounts);
            await _gistService.UpdateContent(_fileName, content);
            return bonusGranted;
        }

        public async Task ClearAll()
        {
            _coinAccounts = new CoinAccounts();
            string content = JsonConvert.SerializeObject(_coinAccounts);
            await _gistService.UpdateContent(_fileName, content);
        }

        public async Task AddInterest()
        {
            var hourDateString = DateTimeOffset.UtcNow.ToString("yyyyMMddHH");
            var dateString = hourDateString.Substring(0, 8);
            if (_coinAccounts.TimesInterestPaidForList.Contains(hourDateString)) return;

            if (!string.IsNullOrWhiteSpace(_coinAccounts.DateDailyIncrementPaidFor)
                || dateString != _coinAccounts.DateDailyIncrementPaidFor)
            {
                _coinAccounts.Accounts.ForEach(a => a.NetWorth += 1000); //add 1000 if it is the start of the day
                _coinAccounts.DateDailyIncrementPaidFor = dateString;
            }

            var accountsToAction = _coinAccounts.Accounts.Where(a => a.DateHourlyBonusPaidFor == dateString);
            foreach (var account in accountsToAction)
            {
                account.NetWorth += CalculateAddedInterest(account); //add 1000 + 10% per hour if they are granted the bonus
            }
            _coinAccounts.TimesInterestPaidForList.Add(hourDateString);
            string content = JsonConvert.SerializeObject(_coinAccounts);
            await _gistService.UpdateContent(_fileName, content);
        }

        public double CalculateAddedInterest(CoinAccount account, int fixedAmount = 1000)
        {
            double p = account.GetAmountRequiredForNextLevel();
            double x = account.NetWorth;
            double c = 10;
            double y = c - c * x / p;
            double multiplier = y / 100; //0.1 at 0 nw and 0 at p nw
            if (multiplier < 0) multiplier = 0;
            return 1000 + (multiplier * account.NetWorth);
        }
    }
}
