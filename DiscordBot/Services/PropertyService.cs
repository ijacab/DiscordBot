using DiscordBot.Exceptions;
using DiscordBot.Models;
using System;
using System.Linq;
using System.Threading.Tasks;
using static DiscordBot.Models.CoinAccounts;

namespace DiscordBot.Services
{
    /// <summary>
    /// Houses and apartments. Bought and sold with cash only, valued on a shared market index, and rented out for a daily
    /// income after the agency's cut. Holding costs apply every day, let or not.
    /// </summary>
    public class PropertyService
    {
        private static readonly Random _random = new Random();
        private readonly CoinService _coinService;

        public PropertyService(CoinService coinService)
        {
            _coinService = coinService;
        }

        public async Task<(PropertyHolding Holding, double Cost)> Buy(CoinAccount account, PropertyKind kind)
        {
            double index = _coinService.GetAll().PropertyIndex;
            double cost = PropertyCatalog.BuyCost(kind, index);
            if (cost > account.NetWorth)
                throw new BadInputException($"A {PropertyCatalog.DisplayName(kind).ToLower()} costs ${cost:N2} with stamp duty and conveyancing, but you only have ${account.NetWorth:N2}.");

            account.NetWorth -= cost;
            var holding = new PropertyHolding { Kind = kind, PurchaseCost = cost, PurchasedUtcTicks = DateTime.UtcNow.Ticks };
            account.Properties.Add(holding);
            await _coinService.Update();
            return (holding, cost);
        }

        /// <summary>Sells the <paramref name="number"/>th property (1 is the first) at market value less selling costs.</summary>
        public async Task<(PropertyHolding Holding, double Proceeds)> Sell(CoinAccount account, int number)
        {
            var holding = Get(account, number);
            double index = _coinService.GetAll().PropertyIndex;
            double proceeds = PropertyCatalog.SaleValue(holding.Kind, index);

            account.Properties.Remove(holding);
            account.NetWorth += account.PayDebtFrom(proceeds); //sale money pays any debt first
            await _coinService.Update();
            return (holding, proceeds);
        }

        /// <summary>Turns renting out on or off for the <paramref name="number"/>th property.</summary>
        public async Task<PropertyHolding> SetLetOut(CoinAccount account, int number, bool letOut)
        {
            var holding = Get(account, number);
            if (holding.LetOut == letOut)
                throw new BadInputException(letOut ? "That property is already let out." : "That property isn't let out.");

            holding.LetOut = letOut;
            await _coinService.Update();
            return holding;
        }

        public PropertyHolding Get(CoinAccount account, int number)
        {
            if (account.Properties.Count == 0)
                throw new BadInputException("You don't own any property. Buy one with `.buyproperty house` or `.buyproperty apartment`.");
            if (number < 1 || number > account.Properties.Count)
                throw new BadInputException($"Pick a number from 1 to {account.Properties.Count}.");
            return account.Properties[number - 1];
        }

        /// <summary>Called every minute. Steps the market once a UTC day and pays rent and holding costs for each day that has passed.</summary>
        public Task TickIfDue() => TickIfDue(DateTime.UtcNow, _random.NextDouble);

        public async Task TickIfDue(DateTime now, Func<double> random)
        {
            var data = _coinService.GetAll();
            long today = PropertyMarket.DayNumber(now);
            bool changed = false;

            if (data.LastPropertyDay != today)
            {
                double index = data.PropertyIndex;
                int direction = data.PropertyTrendDirection;
                int daysLeft = data.PropertyTrendDaysLeft;
                PropertyMarket.Step(ref index, ref direction, ref daysLeft, random);
                data.PropertyIndex = index;
                data.PropertyTrendDirection = direction;
                data.PropertyTrendDaysLeft = daysLeft;
                data.LastPropertyDay = today;
                changed = true;
            }

            if (data.LastRentDay == 0)
            {
                data.LastRentDay = today; //nothing to catch up on for a brand new market
                changed = true;
            }
            else
            {
                //one day at a time, but never more than a week if the bot was down
                long days = Math.Min(PropertyCatalog.MaxRentCatchUpDays, today - data.LastRentDay);
                for (long i = 0; i < days; i++)
                    PayOneDay(data);
                if (days > 0)
                {
                    data.LastRentDay = today;
                    changed = true;
                }
            }

            if (changed) await _coinService.Update();
        }

        /// <summary>Rent less the agency's cut goes to each landlord's cash. Holding costs come out of cash every day.</summary>
        public static void PayOneDay(CoinAccounts data)
        {
            double index = data.PropertyIndex;
            foreach (var account in data.Accounts.Where(a => a.Properties.Count > 0))
            {
                double gross = account.Properties.Where(p => p.LetOut).Sum(p => PropertyCatalog.DailyGrossRent(p.Kind));
                double agencyFee = gross * PropertyCatalog.AgencyFeeRate;
                double holding = account.Properties.Sum(p => PropertyCatalog.DailyHoldingCost(p.Kind, index));
                account.NetWorth += gross - agencyFee - holding;
            }
        }
    }
}
