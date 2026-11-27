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
            output += "**For sale** (price · rent or takings an hour after costs · security)\n";
            foreach (var type in PropertyCatalog.Buyable)
                output += $"{type.Emoji} **{type.Name}** `{type.Words[0]}`: ${FormatHelper.GetCommaNumber(Math.Round(PropertyCatalog.Price(type.Kind, index)))} · " +
                    $"${FormatHelper.GetCommaNumber(Math.Round(PropertyCatalog.ProfitPerHour(type.Kind, index)))}/h{(type.IsBusiness ? " (swings)" : "")} · 🔒{type.Security:0}\n";

            output += "\n**Your property**\n";
            if (account.Properties.Count == 0)
                output += "None yet. Buy with `.buy property house` or `.buy business cafe`, or pick from the menu.\n";
            for (int i = 0; i < account.Properties.Count && i < MaxPropertyLines; i++)
            {
                var property = account.Properties[i];
                output += $"{i + 1}. {PropertyCatalog.Label(property.Kind)} · worth ${FormatHelper.GetCommaNumber(PropertyCatalog.SaleValue(property.Kind, index))} · " +
                    $"about ${FormatHelper.GetCommaNumber(PropertyCatalog.ProfitPerHour(property.Kind, index))} an hour after costs\n";
            }
            if (account.Properties.Count > MaxPropertyLines)
                output += $"…and {account.Properties.Count - MaxPropertyLines} more.\n";

            output += $"\nRent and takings go to your cash each hour after the agency's {PropertyCatalog.AgencyFeeRate:0%} and a {PropertyCatalog.VacancyRate:0%} vacancy allowance; " +
                $"rates and upkeep ({PropertyCatalog.HoldingCostPerHour:0.##%} of value an hour for houses, {PropertyCatalog.BusinessHoldingCostPerHour:0.##%} for businesses) come out with it. " +
                $"Businesses earn more, their takings swing {PropertyCatalog.BusinessIncomeSwing:0%} either way each hour, and their prices boom and crash harder. " +
                $"Buying costs {PropertyCatalog.StampDutyRate:0.#%} stamp duty and conveyancing, and selling costs {PropertyCatalog.SellingCostRate:0.#%}. `.sell property N` sells.";
            await message.Channel.SendMessageAsync(embed: DiscordHelper.GetEmbedBuilder("Property", output).Build(), components: PropertyBuyButton(index));
        }

        /// <summary>How many of a player's properties `.property` lists before "and N more".</summary>
        private const int MaxPropertyLines = 15;

        /// <summary>`.buy property [kind]` / `.buy business kind` / `.buy house`: no kind buys a house, as it always did.</summary>
        private async Task BuyProperty(DiscordSocketClient client, SocketMessage message, List<string> args, bool business = false)
        {
            if (args.Count >= 1 && args[0].StartsWith("ap", StringComparison.OrdinalIgnoreCase))
                throw new BadInputException("Apartments are gone. `.property` lists what's for sale.");
            //"food truck" and "luxury home" are two words; Find ignores spaces
            var type = args.Count == 0 ? (business ? null : PropertyCatalog.TypeOf(PropertyKind.House)) : PropertyCatalog.Find(string.Join("", args));
            if (type != null && business && !type.IsBusiness)
                throw new BadInputException($"A {type.Name.ToLower()} isn't a business. `.buy property {type.Words[0]}` buys one.");
            if (type == null)
                throw new BadInputException("Buy what? " + string.Join(", ", PropertyCatalog.Buyable.Select(t => $"`{t.Words[0]}`")) + ", like `.buy property mansion` or `.buy business cafe`.");
            await message.Channel.SendMessageAsync(await BuyPropertyText(message.Author.Id, message.Author.Username, message.Author.Mention, type.Kind),
                components: PropertyBuyButton(_coinService.GetAll().PropertyIndex));
        }

        /// <summary>Buys a property for a player and returns the public line, for `.buy property` and the buy menu.</summary>
        private async Task<string> BuyPropertyText(ulong userId, string username, string mention, PropertyKind kind = PropertyKind.House)
        {
            EnsureNotBetting(userId, "buying or selling property");
            CoinAccount account = await _coinService.Get(userId, username);
            double price = PropertyCatalog.Price(kind, _coinService.GetAll().PropertyIndex);
            var (_, cost) = await _propertyService.Buy(account, kind);
            return $"{mention} bought a {PropertyCatalog.Label(kind).ToLower()} for ${FormatHelper.GetCommaNumber(price)}, " +
                $"${FormatHelper.GetCommaNumber(cost - price)} stamp duty and conveyancing included in ${FormatHelper.GetCommaNumber(cost)} total. " +
                $"Cash is now ${FormatHelper.GetCommaNumber(account.NetWorth)}. {(PropertyCatalog.TypeOf(kind).IsBusiness ? "It's open for business" : "It's rented out")} straight away, and money comes in every hour.";
        }

        private async Task SellProperty(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            bool all = args.Count == 1 && (args[0].Equals("all", StringComparison.OrdinalIgnoreCase) || args[0].Equals("max", StringComparison.OrdinalIgnoreCase));
            if (args.Count > 1 || (args.Count == 1 && !all && !int.TryParse(args[0], out _))) throw new BadSyntaxException();
            EnsureNotBetting(message.Author.Id, "buying or selling property");
            CoinAccount account = await _coinService.Get(message.Author.Id, message.Author.Username);
            if (all)
            {
                await message.Channel.SendMessageAsync(await SellAllPropertyText(message.Author.Mention, account));
                return;
            }
            //with one property there's no need to say which
            int number = args.Count == 1 ? int.Parse(args[0]) : account.Properties.Count == 1 ? 1
                : throw new BadInputException("Which one? `.sell property 1` (the numbers are on `.property`), or `.sell property all`.");
            var (holding, proceeds) = await _propertyService.Sell(account, number);
            await message.Channel.SendMessageAsync(SoldPropertyText(message.Author.Mention, account, holding, proceeds));
        }

        /// <summary>`.sell property all`: sells every property that can be sold (one with squatters in it can't be) and says what it all came to.</summary>
        private async Task<string> SellAllPropertyText(string mention, CoinAccount account)
        {
            var (sold, stuck, total) = await _propertyService.SellAll(account);
            string left = stuck == 0 ? "" : $" {stuck} {(stuck == 1 ? "property has" : "properties have")} squatters in, so you still own {(stuck == 1 ? "it" : "them")}.";
            return $"{mention} sold {sold} {(sold == 1 ? "property" : "properties")} for ${FormatHelper.GetCommaNumber(total)} in all, after agent commission and marketing. " +
                $"Cash is now ${FormatHelper.GetCommaNumber(account.NetWorth)}.{left}";
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
