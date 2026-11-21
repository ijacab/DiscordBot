using Microsoft.Extensions.Logging;
using Discord;
using Discord.WebSocket;
using DiscordBot.Exceptions;
using DiscordBot.Games;
using DiscordBot.Games.Models;
using Common.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using static DiscordBot.Models.CoinAccounts;
using System.IO;
using Newtonsoft.Json;
using Common.Services;
using System.Reflection;
using DiscordBot.Models;
using DiscordBot.Services;
using DiscordBot.Text;
using DiscordBot.Games.Models.Cards;

namespace DiscordBot.Managers
{
    /// <summary>Property and debt commands: `.property`, `.buy property`, `.sell property` (the old `.buyproperty` and `.sellproperty` still work), `.autosell`.</summary>
    public partial class CommandManager
    {

        private async Task Property(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            var data = _coinService.GetAll();
            CoinAccount account = await _coinService.Get(message.Author.Id, message.Author.Username);
            double index = data.PropertyIndex;

            string output = $"The market is {(data.PropertyTrendDirection > 0 ? "rising" : "falling")}. Prices drift about 2% a day in booms and busts of 4 to 8 days, " +
                "with a random move on top each day that can be 10% to 30% or more on a shock day.\n" +
                MarketCycle.RiskLine(data, _stockService.RiskyAssetShare(), PropertyMarket.DayNumber(DateTime.UtcNow)) + "\n\n";
            var house = PropertyKind.House;
            output += $"🏠 **House**: ${FormatHelper.GetCommaNumber(PropertyCatalog.Price(house, index))} " +
                $"(${FormatHelper.GetCommaNumber(PropertyCatalog.BuyCost(house, index))} to buy with stamp duty), " +
                $"worth ${FormatHelper.GetCommaNumber(PropertyCatalog.SaleValue(house, index))} to sell, rent {PropertyCatalog.RentPerHour(house, index):N2} an hour\n";

            output += "\n**Your property**\n";
            if (account.Properties.Count == 0)
                output += "None yet. Buy a house with `.buy property`.\n";
            for (int i = 0; i < account.Properties.Count; i++)
            {
                var property = account.Properties[i];
                double netRentPerHour = PropertyCatalog.NetRentPerHour(property.Kind, index);
                output += $"{i + 1}. {PropertyCatalog.DisplayName(property.Kind)} · worth ${FormatHelper.GetCommaNumber(PropertyCatalog.SaleValue(property.Kind, index))} · " +
                    $"rented out, about {netRentPerHour:N2} an hour after the agency\n";
            }

            output += "\nRent goes to your cash each hour after the agency's 8% and a 4% vacancy allowance. Rates and maintenance (1% of value an hour) come out hourly with the rent. " +
                "Buying costs 4.5% stamp duty and conveyancing, and selling costs 3%.\n" +
                "Every property is rented out automatically. `.sell property N` sells.";
            await message.Channel.SendMessageAsync(embed: DiscordHelper.GetEmbedBuilder("Property", output).Build(), components: PropertyBuyButton());
        }

        private async Task BuyProperty(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            //houses are the only kind now; "house" is still accepted
            if (args.Count > 1) throw new BadSyntaxException();
            if (args.Count == 1 && args[0].StartsWith("ap", StringComparison.OrdinalIgnoreCase))
                throw new BadInputException("Apartments are gone: there are only houses now. Buy one with `.buy property`.");
            if (args.Count == 1 && !args[0].Equals("house", StringComparison.OrdinalIgnoreCase)) throw new BadSyntaxException();
            await message.Channel.SendMessageAsync(await BuyPropertyText(message.Author.Id, message.Author.Username, message.Author.Mention), components: PropertyBuyButton());
        }

        /// <summary>Buys a house for a player and returns the public line, for `.buy property` and the Buy a house button.</summary>
        private async Task<string> BuyPropertyText(ulong userId, string username, string mention)
        {
            var kind = PropertyKind.House;
            EnsureNotBetting(userId, "buying or selling property");
            CoinAccount account = await _coinService.Get(userId, username);
            double price = PropertyCatalog.Price(kind, _coinService.GetAll().PropertyIndex);
            var (_, cost) = await _propertyService.Buy(account, kind);
            return $"{mention} bought a {PropertyCatalog.DisplayName(kind).ToLower()} for ${FormatHelper.GetCommaNumber(price)}, " +
                $"${FormatHelper.GetCommaNumber(cost - price)} stamp duty and conveyancing included in ${FormatHelper.GetCommaNumber(cost)} total. " +
                $"Cash is now ${FormatHelper.GetCommaNumber(account.NetWorth)}. It's rented out straight away, and rent comes in every hour.";
        }

        private async Task SellProperty(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args.Count > 1 || (args.Count == 1 && !int.TryParse(args[0], out _))) throw new BadSyntaxException();
            EnsureNotBetting(message.Author.Id, "buying or selling property");
            CoinAccount account = await _coinService.Get(message.Author.Id, message.Author.Username);
            //with one house there's no need to say which
            int number = args.Count == 1 ? int.Parse(args[0]) : account.Properties.Count == 1 ? 1
                : throw new BadInputException("Which one? `.sell property 1` (the numbers are on `.property`).");
            var (holding, proceeds) = await _propertyService.Sell(account, number);
            await message.Channel.SendMessageAsync(SoldPropertyText(message.Author.Mention, account, holding, proceeds));
        }

        private static string SoldPropertyText(string mention, CoinAccount account, PropertyHolding holding, double proceeds) =>
            $"{mention} sold your {PropertyCatalog.DisplayName(holding.Kind).ToLower()} for ${FormatHelper.GetCommaNumber(proceeds)} after agent commission and marketing. " +
            $"Cash is now ${FormatHelper.GetCommaNumber(account.NetWorth)}.";

        /// <summary>`.autosell on|off`: sell stocks and then property automatically to pay off debt.</summary>
        private async Task AutoSell(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            CoinAccount account = await _coinService.Get(message.Author.Id, message.Author.Username);
            if (args.Count == 1 && (args[0].Equals("on", StringComparison.OrdinalIgnoreCase) || args[0].Equals("off", StringComparison.OrdinalIgnoreCase)))
            {
                account.AutoSellForDebt = args[0].Equals("on", StringComparison.OrdinalIgnoreCase);
                await _coinService.Update();
            }
            else if (args.Count != 0)
                throw new BadSyntaxException();

            await message.Channel.SendMessageAsync(AutoSellText(message.Author.Mention, account), components: AutoSellButton(account));
        }

        private static string AutoSellText(string mention, CoinAccount account)
        {
            string debt = account.Debt > 0 ? $"You owe ${FormatHelper.GetCommaNumber(account.Debt)}." : "You have no debt.";
            string state = account.AutoSellForDebt
                ? "💳 Auto-sell is **on**: while you owe money, your stocks, then shorts, options and property are sold within a minute to pay it off. `.autosell off` to stop."
                : "💳 Auto-sell is **off**: debt is only paid from spare cash and the sales you make, and it grows 10% every hour. `.autosell on` to sell assets automatically to pay it off.";
            return $"{mention} {state} {debt}";
        }

        /// <summary>Old .letout / .unlet: every property is rented out automatically now.</summary>
        private async Task RentingIsAutomatic(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            await message.Channel.SendMessageAsync("Every property is rented out automatically now, so there's nothing to switch. See `.property` for your rent.");
        }
    }
}
