using DiscordBot.Models;
using System.Collections.Generic;
using System.Linq;
using static DiscordBot.Models.CoinAccounts;

namespace DiscordBot.Services
{
    /// <summary>What a player is worth: cash + stocks and bets + property - debt. The one copy of it; the leaderboard, raffle, raids and graphs all use this.</summary>
    public class WorthService
    {
        private readonly CoinService _coinService;
        private readonly StockService _stockService;
        private readonly AppSettings _appSettings;

        public WorthService(CoinService coinService, StockService stockService, AppSettings appSettings)
        {
            _coinService = coinService;
            _stockService = stockService;
            _appSettings = appSettings;
        }

        public double TotalWorth(CoinAccount account) =>
            account.NetWorth + _stockService.GetPortfolioValue(account.UserId) + _coinService.PropertyValue(account) - account.Debt;

        /// <summary>Everyone, richest first (by prestige first when prestige is on).</summary>
        public List<CoinAccount> Ranked() => Ranked(_coinService.GetAll().Accounts);

        public List<CoinAccount> Ranked(IEnumerable<CoinAccount> accounts) => (_appSettings.PrestigeDisabled
            ? accounts.OrderByDescending(TotalWorth)
            : accounts.OrderByDescending(a => a.PrestigeLevel).ThenByDescending(TotalWorth)).ToList();
    }
}
