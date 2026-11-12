using DiscordBot.Exceptions;
using DiscordBot.Models;
using System;
using System.Linq;
using System.Threading.Tasks;
using static DiscordBot.Models.CoinAccounts;

namespace DiscordBot.Services
{
    /// <summary>
    /// Houses and apartments. Bought and sold with cash only, valued on a shared market index, and always rented out for an
    /// hourly income after the agency's cut. Holding costs apply every day.
    /// </summary>
    public class PropertyService
    {
        private static readonly Random _random = new Random();
        private readonly CoinService _coinService;
        private readonly System.Collections.Generic.List<string> _news = new System.Collections.Generic.List<string>();

        /// <summary>Market headlines since the last call (crash phases starting and ending). Posted by the command manager.</summary>
        public System.Collections.Generic.List<string> TakeNews()
        {
            var news = new System.Collections.Generic.List<string>(_news);
            _news.Clear();
            return news;
        }

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

        public PropertyHolding Get(CoinAccount account, int number)
        {
            if (account.Properties.Count == 0)
                throw new BadInputException("You don't own any property. Buy one with `.buyproperty house` or `.buyproperty apartment`.");
            if (number < 1 || number > account.Properties.Count)
                throw new BadInputException($"Pick a number from 1 to {account.Properties.Count}.");
            return account.Properties[number - 1];
        }

        /// <summary>Called every minute. Steps the market once a UTC day and pays rent for each hour that has passed and holding costs for each day.</summary>
        public Task TickIfDue() => TickIfDue(DateTime.UtcNow, _random.NextDouble);

        public async Task TickIfDue(DateTime now, Func<double> random)
        {
            var data = _coinService.GetAll();
            long today = PropertyMarket.DayNumber(now);
            bool changed = false;

            if (data.LastPropertyDay != today)
            {
                //the market crisis calendar moves first, so today's step already follows the new phase
                string headline = MarketCycle.AdvanceDay(data, today, random);
                if (headline != null) _news.Add(headline);

                double index = data.PropertyIndex;
                if (data.MarketPhase != CrisisPhase.None)
                {
                    MarketCycle.StepProperty(ref index, data.MarketPhase);
                }
                else
                {
                    int direction = data.PropertyTrendDirection;
                    int daysLeft = data.PropertyTrendDaysLeft;
                    PropertyMarket.Step(ref index, ref direction, ref daysLeft, random);
                    data.PropertyTrendDirection = direction;
                    data.PropertyTrendDaysLeft = daysLeft;
                }
                data.PropertyIndex = index;
                data.LastPropertyDay = today;
                changed = true;
            }

            long hour = PropertyMarket.HourNumber(now);
            if (data.LastRentHour == 0)
            {
                data.LastRentHour = hour; //nothing to catch up on for a brand new market
                changed = true;
            }
            else
            {
                //one hour at a time, but never more than a week if the bot was down
                long hours = Math.Min(PropertyCatalog.MaxRentCatchUpHours, hour - data.LastRentHour);
                for (long i = 0; i < hours; i++)
                    PayRentOneHour(data);
                if (hours > 0)
                {
                    data.LastRentHour = hour;
                    changed = true;
                }
            }

            //holding costs are charged once a day
            if (data.LastRentDay == 0)
            {
                data.LastRentDay = today;
                changed = true;
            }
            else
            {
                long days = Math.Min(PropertyCatalog.MaxHoldingCatchUpDays, today - data.LastRentDay);
                for (long i = 0; i < days; i++)
                    PayHoldingOneDay(data);
                if (days > 0)
                {
                    data.LastRentDay = today;
                    changed = true;
                }
            }

            if (changed) await _coinService.Update();
        }

        /// <summary>Rent for one hour on every property, less the agency's cut, goes to each landlord's cash. Every property is rented out.</summary>
        public static void PayRentOneHour(CoinAccounts data) => PayRentOneHour(data, DateTime.UtcNow);

        /// <summary>As <see cref="PayRentOneHour(CoinAccounts)"/>. A landlord with Squatters in pays their rent to the squatter instead.</summary>
        public static void PayRentOneHour(CoinAccounts data, DateTime now)
        {
            foreach (var account in data.Accounts.Where(a => a.Properties.Count > 0).ToList())
            {
                double gross = account.Properties.Sum(p => PropertyCatalog.GrossRentPerHour(p.Kind, data.PropertyIndex));
                double net = gross * (1 - PropertyCatalog.AgencyFeeRate);
                var squat = account.Running(PowerUpIds.Squatted, now).OrderBy(s => s.ExpiresAtTicks).FirstOrDefault();
                var squatter = squat == null ? null : data.Accounts.FirstOrDefault(a => a.UserId == squat.BeneficiaryUserId);
                (squatter ?? account).NetWorth += net;
            }
        }

        /// <summary>One day of holding costs (rates and maintenance) comes out of cash.</summary>
        public static void PayHoldingOneDay(CoinAccounts data)
        {
            double index = data.PropertyIndex;
            foreach (var account in data.Accounts.Where(a => a.Properties.Count > 0))
                account.NetWorth -= account.Properties.Sum(p => PropertyCatalog.DailyHoldingCost(p.Kind, index));
        }
    }
}
