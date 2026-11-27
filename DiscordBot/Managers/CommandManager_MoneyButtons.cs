using Discord;
using Discord.WebSocket;
using DiscordBot.Exceptions;
using DiscordBot.Games.Models;
using DiscordBot.Models;
using DiscordBot.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using static DiscordBot.Models.CoinAccounts;

namespace DiscordBot.Managers
{
    /// <summary>
    /// Buttons and menus for money commands: stocks, power-ups, property and auto-sell. They call the same methods as the
    /// typed commands (BuyStockText, UsePowerUpText...), so the rules and wording match. Buttons on shared things act for
    /// whoever clicks; buttons on someone's own view carry their id and only work for them (<see cref="ButtonOwner"/>).
    /// Results post publicly like the typed command; refusals go back privately.
    /// </summary>
    public partial class CommandManager
    {
        // ---- builders

        private static string Id(string game, string action, params string[] args) => ComponentId.Build(game, action, args);

        /// <summary>Buy and sell buttons for one stock. They act for whoever clicks.</summary>
        private static MessageComponent StockTradeButtons(string symbol) => new ComponentBuilder()
            .WithButton("Buy…", Id(MoneyComponents.Stock, "buy", symbol), ButtonStyle.Success)
            .WithButton("Buy max", Id(MoneyComponents.Stock, "buymax", symbol), ButtonStyle.Success)
            .WithButton("Sell…", Id(MoneyComponents.Stock, "sell", symbol), ButtonStyle.Danger)
            .WithButton("Sell all", Id(MoneyComponents.Stock, "sellall", symbol), ButtonStyle.Danger)
            .Build();

        /// <summary>A menu under `.stocks` to open one stock's card privately, with its trade buttons.</summary>
        private static MessageComponent StockPickMenu(IEnumerable<Stock> stocks)
        {
            var menu = new SelectMenuBuilder()
                .WithCustomId(Id(MoneyComponents.Stock, "pick"))
                .WithPlaceholder("Trade a stock…");
            foreach (var stock in stocks.Take(25))
                menu.AddOption(Trim($"{stock.Symbol} · ${Stock.FormatPrice(stock.Price)}", 100), stock.Symbol, Trim(stock.Name, 100));
            return menu.Options.Count == 0 ? null : new ComponentBuilder().WithSelectMenu(menu).Build();
        }

        /// <summary>A menu of everything for sale with today's price. Picking one buys it for whoever picked (`prop:buykind`).</summary>
        private static MessageComponent PropertyBuyButton(double index)
        {
            var menu = new SelectMenuBuilder().WithCustomId(Id(MoneyComponents.Property, "buykind")).WithPlaceholder("Buy a property…");
            foreach (var type in PropertyCatalog.Buyable)
                menu.AddOption(Trim($"{type.Name} · ${Common.Helpers.FormatHelper.GetCommaNumber(Math.Round(PropertyCatalog.BuyCost(type.Kind, index)))} with stamp duty", 100),
                    type.Kind.ToString(), Trim($"About ${Common.Helpers.FormatHelper.GetCommaNumber(Math.Round(PropertyCatalog.ProfitPerHour(type.Kind, index)))} an hour after costs, security {type.Security:0}", 100), new Emoji(type.Emoji));
            return new ComponentBuilder().WithSelectMenu(menu).Build();
        }

        /// <summary>Turns auto-sell the other way. Only for the account's owner.</summary>
        private static MessageComponent AutoSellButton(CoinAccount account) =>
            new ComponentBuilder().WithButton(AutoSellButtonBuilder(account)).Build();

        private static ButtonBuilder AutoSellButtonBuilder(CoinAccount account) => new ButtonBuilder()
            .WithLabel(account.AutoSellForDebt ? "Turn auto-sell off" : "Turn auto-sell on")
            .WithCustomId(Id(MoneyComponents.Debt, "autosell", account.UserId.ToString(), account.AutoSellForDebt ? "off" : "on"))
            .WithStyle(account.AutoSellForDebt ? ButtonStyle.Secondary : ButtonStyle.Primary);

        /// <summary>Menus to sell a stock or a house, and the auto-sell switch, under the owner's own `.portfolio`.</summary>
        private MessageComponent PortfolioComponents(CoinAccount account, IReadOnlyDictionary<string, int> holdings)
        {
            string owner = account.UserId.ToString();
            var builder = new ComponentBuilder();
            bool any = false;
            int row = 0;

            if (holdings.Count > 0)
            {
                var menu = new SelectMenuBuilder().WithCustomId(Id(MoneyComponents.Stock, "sellpick", owner)).WithPlaceholder("Sell a stock…");
                foreach (var holding in holdings.OrderBy(h => h.Key).Take(25))
                    menu.AddOption(Trim($"{holding.Key} · {holding.Value:N0} shares", 100), holding.Key);
                builder.WithSelectMenu(menu, row++);
                any = true;
            }

            if (account.Properties.Count > 0)
            {
                double index = _coinService.GetAll().PropertyIndex;
                var menu = new SelectMenuBuilder().WithCustomId(Id(MoneyComponents.Property, "sell", owner)).WithPlaceholder("Sell a property…");
                for (int i = 0; i < account.Properties.Count && i < 25; i++)
                {
                    var property = account.Properties[i];
                    menu.AddOption(Trim($"{i + 1}. {PropertyCatalog.Label(property.Kind)} · sells for ${Common.Helpers.FormatHelper.GetCommaNumber(PropertyCatalog.SaleValue(property.Kind, index))}", 100), property.Id);
                }
                builder.WithSelectMenu(menu, row++);
                any = true;
            }

            if (account.Debt > 0 || account.AutoSellForDebt)
            {
                builder.WithButton(AutoSellButtonBuilder(account), row);
                any = true;
            }
            return any ? builder.Build() : null;
        }

        /// <summary>A Use button for each kept power-up (only for their owner), and one to list them all.</summary>
        private static MessageComponent PowerUpButtons(CoinAccount account)
        {
            string owner = account.UserId.ToString();
            var builder = new ComponentBuilder();
            for (int i = 0; i < account.HeldPowerUps.Count && i < PowerUpCatalog.MaxHeld; i++)
            {
                var held = account.HeldPowerUps[i];
                string name = PowerUpCatalog.Find(held.Id)?.Name ?? held.Id;
                builder.WithButton(Trim($"Use {i + 1} · {name}", 80), Id(MoneyComponents.PowerUp, "use", owner, (i + 1).ToString(), held.Id), ButtonStyle.Primary, row: i / 5);
            }
            builder.WithButton("All power-ups", Id(MoneyComponents.PowerUp, "list"), ButtonStyle.Secondary, row: 2);
            return builder.Build();
        }

        private static string Trim(string text, int max) => text.Length <= max ? text : text.Substring(0, max - 1) + "…";

        // ---- clicks

        private async Task MoneyComponent(SocketMessageComponent component, string game, string action, string[] args)
        {
            switch (game)
            {
                case MoneyComponents.Stock: await StockComponent(component, action, args); break;
                case MoneyComponents.PowerUp: await PowerUpComponent(component, action, args); break;
                case MoneyComponents.Property: await PropertyComponent(component, action, args); break;
                case MoneyComponents.Debt: await DebtComponent(component, action, args); break;
                default: await ReplyPrivately(component, "That button doesn't do anything any more."); break;
            }
        }

        /// <summary>False, with a private refusal, when someone presses a button on another person's own view.</summary>
        private static async Task<bool> IsOwner(SocketMessageComponent component, string[] args)
        {
            if (ButtonOwner.TryRead(args, 0, out ulong ownerId) && ButtonOwner.Allows(ownerId, component.User.Id))
                return true;
            await ReplyPrivately(component, ButtonOwner.NotYours);
            return false;
        }

        private async Task StockComponent(SocketMessageComponent component, string action, string[] args)
        {
            var user = component.User;
            switch (action)
            {
                case "pick":
                {
                    string symbol = component.Data.Values?.FirstOrDefault();
                    await component.DeferAsync(ephemeral: true);
                    var stock = _stockService.GetMarket().Find(symbol ?? "") ?? throw new BadInputException("That stock isn't listed any more.");
                    var card = StockTickerCard.Build(stock, null);
                    if (card.Png == null)
                    {
                        await component.FollowupAsync(card.Text, components: StockTradeButtons(stock.Symbol), ephemeral: true);
                        return;
                    }
                    var embed = Common.Helpers.DiscordHelper.GetEmbedBuilder(card.Title, card.Text).WithImageUrl("attachment://stock.png").Build();
                    using var png = new MemoryStream(card.Png);
                    await component.FollowupWithFileAsync(png, "stock.png", embed: embed, components: StockTradeButtons(stock.Symbol), ephemeral: true);
                    return;
                }
                case "buy":
                    if (args.Length < 1) break;
                    await component.RespondWithModalAsync(AmountModal(Id(MoneyComponents.Stock, "buyamt", args[0]), $"Buy {args[0]}", "Shares", "100, 50% or max"));
                    return;
                case "sell":
                    if (args.Length < 1) break;
                    await component.RespondWithModalAsync(AmountModal(Id(MoneyComponents.Stock, "sellamt", args[0]), $"Sell {args[0]}", "Shares", "100, 50% or all"));
                    return;
                case "sellpick":
                {
                    if (!await IsOwner(component, args)) return;
                    string symbol = component.Data.Values?.FirstOrDefault();
                    if (string.IsNullOrEmpty(symbol)) break;
                    await component.RespondWithModalAsync(AmountModal(Id(MoneyComponents.Stock, "sellamt", symbol), $"Sell {symbol}", "Shares", "100, 50% or all"));
                    return;
                }
                case "buymax":
                case "sellall":
                {
                    if (args.Length < 1) break;
                    await component.DeferAsync();
                    var (text, symbol) = action == "buymax"
                        ? await BuyStockText(user.Id, user.Username, user.Mention, args[0], "max")
                        : await SellStockText(user.Id, user.Username, user.Mention, args[0], "all");
                    await component.FollowupAsync(text, components: StockTradeButtons(symbol));
                    MarkActiveFromInteraction(component);
                    return;
                }
            }
            await ReplyPrivately(component, "That button doesn't do anything any more.");
        }

        /// <summary>The amount forms from Buy… and Sell….</summary>
        private async Task StockModal(SocketModal modal, string action, string[] args)
        {
            if (args.Length < 1 || (action != "buyamt" && action != "sellamt"))
            {
                await ReplyPrivately(modal, "That form doesn't do anything any more.");
                return;
            }

            await modal.DeferAsync();
            var user = modal.User;
            string amount = modal.Data.Components.FirstOrDefault(c => c.CustomId == "amount")?.Value?.Trim().ToLowerInvariant() ?? "";
            if (action == "buyamt" && amount == "all") amount = "max"; //"all your cash" means the most you can buy
            try
            {
                var (text, symbol) = action == "buyamt"
                    ? await BuyStockText(user.Id, user.Username, user.Mention, args[0], amount)
                    : await SellStockText(user.Id, user.Username, user.Mention, args[0], amount);
                await modal.FollowupAsync(text, components: StockTradeButtons(symbol));
                MarkActiveFromInteraction(modal);
            }
            catch (BadSyntaxException)
            {
                await ReplyPrivately(modal, $"That amount doesn't look right. Use a whole number of shares, a percentage like 50%, or {(action == "buyamt" ? "max" : "all")}.");
            }
        }

        private async Task PowerUpComponent(SocketMessageComponent component, string action, string[] args)
        {
            var now = DateTime.UtcNow;
            switch (action)
            {
                case "mine":
                {
                    //your own `.pu`, privately, from the button under a raffle result
                    await component.DeferAsync(ephemeral: true);
                    CoinAccount account = await _coinService.Get(component.User.Id, component.User.Username);
                    account.Prune(now);
                    var parts = SplitForMessages(PowerUpView(account, now));
                    for (int i = 0; i < parts.Count; i++)
                        await component.FollowupAsync(parts[i], components: i == parts.Count - 1 ? PowerUpButtons(account) : null, ephemeral: true);
                    return;
                }
                case "list":
                    await component.DeferAsync(ephemeral: true);
                    foreach (var embed in PagedEmbeds(PowerUpCatalogTitle, PowerUpCatalogLines()))
                        await component.FollowupAsync(embed: embed, ephemeral: true);
                    return;
                case "use":
                {
                    if (!await IsOwner(component, args)) return;
                    if (args.Length < 3 || !int.TryParse(args[1], out int number)) break;
                    await component.DeferAsync();
                    CoinAccount account = await _coinService.Get(component.User.Id, component.User.Username);
                    account.Prune(now);
                    int found = account.FindHeld(number, args[2]);
                    if (found == 0)
                    {
                        await ReplyPrivately(component, "That power-up is gone: it was used or it expired. Type `.pu` to see what you have.");
                        return;
                    }
                    await component.FollowupAsync(await UsePowerUpText(account, component.User.Mention, found, now));
                    return;
                }
            }
            await ReplyPrivately(component, "That button doesn't do anything any more.");
        }

        private async Task PropertyComponent(SocketMessageComponent component, string action, string[] args)
        {
            var user = component.User;
            switch (action)
            {
                case "buykind":
                {
                    //a menu pick only asks, privately: a mis-tap shouldn't buy an $80M bank
                    if (!Enum.TryParse(component.Data.Values?.FirstOrDefault(), out PropertyKind picked) || !PropertyCatalog.TypeOf(picked).Buyable) break;
                    double cost = PropertyCatalog.BuyCost(picked, _coinService.GetAll().PropertyIndex);
                    await component.RespondAsync($"Buy a {PropertyCatalog.Label(picked).ToLower()} for about ${Common.Helpers.FormatHelper.GetCommaNumber(Math.Round(cost))} with stamp duty?",
                        components: new ComponentBuilder().WithButton("Buy it", Id(MoneyComponents.Property, "buyconfirm", picked.ToString()), ButtonStyle.Success).Build(), ephemeral: true);
                    return;
                }
                case "buy": //the old Buy a house button
                case "buyconfirm":
                {
                    var kind = PropertyKind.House;
                    if (action == "buyconfirm" && (args.Length < 1 || !Enum.TryParse(args[0], out kind))) break;
                    if (action == "buyconfirm") //take the button away first, so a double tap can't buy two
                        await component.UpdateAsync(m => { m.Content = "Buying…"; m.Components = new ComponentBuilder().Build(); });
                    else
                        await component.DeferAsync();
                    await component.FollowupAsync(await BuyPropertyText(user.Id, user.Username, user.Mention, kind), components: PropertyBuyButton(_coinService.GetAll().PropertyIndex));
                    MarkActiveFromInteraction(component);
                    return;
                }
                case "sell":
                {
                    if (!await IsOwner(component, args)) return;
                    string holdingId = component.Data.Values?.FirstOrDefault();
                    if (string.IsNullOrEmpty(holdingId)) break;
                    await component.DeferAsync();
                    EnsureNotBetting(user.Id, "buying or selling property");
                    CoinAccount account = await _coinService.Get(user.Id, user.Username);
                    var (holding, proceeds) = await _propertyService.SellById(account, holdingId);
                    await component.FollowupAsync(SoldPropertyText(user.Mention, account, holding, proceeds));
                    MarkActiveFromInteraction(component);
                    return;
                }
            }
            await ReplyPrivately(component, "That button doesn't do anything any more.");
        }

        // ---- blackjack and duck race

        /// <summary>True when the click or form belongs to blackjack or the duck race.</summary>
        private bool IsLobbyGame(string game) => game == _blackjackManager.ButtonPrefix || game == _duckRaceManager.ButtonPrefix;

        /// <summary>Join…, Join max, Start, and for blackjack Hit and Stay. They act for whoever clicks; the game posts its own messages.</summary>
        private async Task LobbyGameComponent(SocketMessageComponent component, string game, string action)
        {
            bool blackjack = game == _blackjackManager.ButtonPrefix;
            string gameName = blackjack ? _blackjackManager.GameName : _duckRaceManager.GameName;
            if (component.Channel == null || !component.GuildId.HasValue)
            {
                await ReplyPrivately(component, $"{gameName} is played in a server channel.");
                return;
            }

            ulong userId = component.User.Id;
            var context = GameContext.From(component);
            switch (action)
            {
                case "join":
                    await component.RespondWithModalAsync(AmountModal(Id(game, "joinamt"), $"Join {gameName}", "Your bet"));
                    return;
                case "joinmax":
                {
                    //all your cash, like typing max: no form to fill in
                    CoinAccount account = await _coinService.Get(userId, component.User.Username);
                    await component.DeferAsync();
                    if (blackjack) await _blackjackManager.CreateOrJoin(userId, account.NetWorth, context);
                    else await _duckRaceManager.CreateOrJoin(userId, account.NetWorth, context);
                    MarkActiveFromInteraction(component);
                    return;
                }
                case "again":
                {
                    //Play again: a new game at once with your last bet. With no last bet to go on, ask for one
                    CoinAccount account = await _coinService.Get(userId, component.User.Username);
                    double? bet = blackjack ? _blackjackManager.PlayAgainBet(userId, account.NetWorth) : null;
                    if (bet == null)
                    {
                        await component.RespondWithModalAsync(AmountModal(Id(game, "joinamt"), $"Join {gameName}", "Your bet"));
                        return;
                    }
                    await component.DeferAsync();
                    await _blackjackManager.CreateOrJoin(userId, bet.Value, context);
                    await _blackjackManager.Start(userId, context);
                    MarkActiveFromInteraction(component);
                    return;
                }
                case "start":
                    await component.DeferAsync();
                    try
                    {
                        if (blackjack) await _blackjackManager.Start(userId, context);
                        else await _duckRaceManager.Start(userId, context);
                        MarkActiveFromInteraction(component);
                    }
                    catch (NotInGameException)
                    {
                        await ReplyPrivately(component, $"You're not in a {gameName} game. Press Join… first.");
                    }
                    return;
                case "hit":
                case "stay":
                    if (!blackjack) break;
                    await component.DeferAsync();
                    if (action == "hit") await _blackjackManager.Hit(userId, context);
                    else await _blackjackManager.Stay(userId, context);
                    MarkActiveFromInteraction(component);
                    return;
            }
            await ReplyPrivately(component, "That button doesn't do anything any more.");
        }

        /// <summary>The bet form from Join… / Play again….</summary>
        private async Task LobbyGameModal(SocketModal modal, string game, string action)
        {
            bool blackjack = game == _blackjackManager.ButtonPrefix;
            if (action != "joinamt" || modal.Channel == null || !modal.GuildId.HasValue)
            {
                await ReplyPrivately(modal, "That form doesn't do anything any more.");
                return;
            }

            await modal.DeferAsync();
            string text = modal.Data.Components.FirstOrDefault(c => c.CustomId == "amount")?.Value?.Trim().ToLowerInvariant() ?? "";
            CoinAccount account = await _coinService.Get(modal.User.Id, modal.User.Username);
            if (!TryExtractBetAmount(new List<string> { text }, account, out double amount) || amount <= 0)
            {
                await ReplyPrivately(modal, "That amount doesn't look right. Use a number like 1000, a percentage like 10%, or max.");
                return;
            }

            var context = GameContext.From(modal);
            if (blackjack) await _blackjackManager.CreateOrJoin(modal.User.Id, amount, context);
            else await _duckRaceManager.CreateOrJoin(modal.User.Id, amount, context);
            MarkActiveFromInteraction(modal);
        }

        private async Task DebtComponent(SocketMessageComponent component, string action, string[] args)
        {
            if (action != "autosell" || args.Length < 2)
            {
                await ReplyPrivately(component, "That button doesn't do anything any more.");
                return;
            }
            if (!await IsOwner(component, args)) return;

            await component.DeferAsync();
            CoinAccount account = await _coinService.Get(component.User.Id, component.User.Username);
            account.AutoSellForDebt = args[1] == "on";
            await _coinService.Update();
            await component.FollowupAsync(AutoSellText(component.User.Mention, account), components: AutoSellButton(account));
        }
    }
}
