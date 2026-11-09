# AGENTS.md

Discord bot (C#, .NET Core 3.1, Discord.Net 2.4) with virtual-money games, custom commands, reminders, and a local-LLM chat (Ollama). See [FILETREE.md](FILETREE.md) for a navigable file map — read it before searching.

## Solution layout (`DiscordBot/DiscordBot.sln`)
- `DiscordBot/` — main app (exe). Bot, commands, games, services, ASP.NET host.
- `Common/` — shared netstandard2.1 lib: exceptions, helpers, Gist/local file storage. `Common/_nanogpt/` is unrelated Python (nanoGPT); ignore.
- `WebAlerter/` — Strawman trade checker (`StrawmanChecker`); its worker is currently disabled in `Startup.cs`.
- `Tests/` — xunit + Moq (`BlackjackTests`, `GPTHelperTests`, `WebAlerterTests`).

## Commands
```
dotnet build DiscordBot/DiscordBot.sln
dotnet test Tests/Tests.csproj
dotnet run --project DiscordBot
```
CI/CD: `azure-pipelines.yml` publishes linux-arm64 to a Pi and restarts the `DiscordBot` systemd service (`DiscordBot/DiscordBot.service`). Pushing to `master` deploys. The pipeline stops the service before `dotnet publish`, so a failed build leaves the bot offline: build locally before pushing to `master`.

## Env vars (see `Common/Helpers/EnvironmentHelper.cs`)
`DiscordBotToken`, `GITHUB_PAT_TOKEN`, `GITHUB_USERNAME`, `GITHUB_GIST_ID`, `S_U`, `S_P`, `PORT` (default 5000), `IS_HEROKU`. Never commit secrets.
The dungeon deletes players' command messages to keep the channel clean. That needs the bot's **Manage Messages** permission in the server. Without it the commands stay, and the dashboard still works.

## Architecture
- Entry: `Program.cs` (Kestrel web host + generic host) → `Startup.cs` (**all DI registration**) → `ChatWorker` (hosted service; starts Ollama installer + `MessageHandler`).
- `MessageHandler` subscribes to Discord events and forwards messages to `CommandManager.RunCommand`.
- `CommandManager` is a **partial class** split across `Managers/CommandManager_*.cs`:
  - `_Main` — constructor, the `_commands` list, `RunCommand` parsing/dispatch.
  - `_Games` — roulette / blackjack / faceoff handlers.
  - `_Methods` — all other command handlers (largest file).
  - `_Bot` — bot-to-bot commands (`.initiatebet`, `.resolvebet`, `.lbjson`).
- Commands use prefix `.`; each is `Command(name, handler)` with `Description`, `Syntax`, `Hidden`, `RequiresAdmin`. Handler signature: `Task X(DiscordSocketClient, SocketMessage, List<string> args)`.
- Games: `Games/<Game>.cs` (rules) + `Games/Managers/<Game>Manager.cs` (session/lobby, derives `BaseMultiplayerManager`) + `Games/Models/` (players, cards, bets).
- Feature flag: `AppSettings.PrestigeDisabled` (appsettings.json, default `true`) disables `.prestige`, prestige scaling of interest/daily income/bonus threshold. Interest is `Constants.InterestPercentage` (2%).
- First gamble of the day (`BetManager.ResolveBet`, tracked by `CoinAccount.LastGambleDate`): d10 roll, a 10 steals 10% of the cash of whoever is 1st on the leaderboard (cash + stocks), unless the roller is already 1st; result is read via `BetManager.TakeDailyRoll`.
- Stocks: `Services/StockService` (singleton) ticks every 5-min UTC slot from the `ChatWorker` minute loop via `CommandManager.RunBackgroundTasks`; risk tiers/swings in `Models/Stock.cs`; `.stocks .buy .sell .portfolio`. Leaderboard ranks cash + stock value. Trades move the quoted price (`Stock.Price` = `FairPrice` × (1 + `Impact`)): impact grows with the square root of the shares traded against the stock's `SharesOutstanding`, fills at the midpoint of before and after plus a fixed 1% spread (buys pay half above, sells receive half below), and impact fades by half each tick. Sells pay a 2% tax on gross value (`Stock.SellTaxRate`) that goes into the jackpot, so churning costs. Tiers 3-6 each hold one random stock (tier 6 is crypto with moon/crash bursts and a strong rebound after a crash), picked at random from `StockMarket.Pool` whenever the market is created (`.clear coin`). `.stocks` graphs are green or red by `Stock.AllTimeChange` and take a window like `48h` (`DurationParser`). `.short`/`.cover` lock margin (closed at 2x entry); `.call`/`.put` are cash-settled at-the-money options on the fair price (100 shares each, settled in `StockService.TickIfDue`). Shorts and options count in `GetPortfolioValue` and block the jackpot via `HasPositions`.
- Raffle: `Services/RaffleService` draws every 5-hour UTC slot (from `CommandManager.RunBackgroundTasks`). Anyone whose last money command (`_moneyCommands` in `CommandManager_Main.cs`, stamped by `CoinService.MarkActive`) was within 24h is entered, and the winner is announced in the channel of the last money command and holds a power-up from `PowerUpCatalog` (`Models/PowerUp.cs`, 19 of them) for 24h. `.pu` lists them, `.pu use N` uses one. Buffs live on `CoinAccount.ActivePowerUps` and are read by `BetManager.ResolveBet`, `StockService.Buy/Sell`, `CoinService.AddInterest` and the daily steal; instant ones (blue shell, red shell, lightning bolt, robin hood, hostile takeover, pickpocket, stimulus, jackpot dip, free card pull, interest pass) fire in `RaffleService.ApplyUse`.
- Property: `Services/PropertyService` + `Models/Property.cs`. Houses and apartments bought with cash only (`.buyproperty`, `.sellproperty`, `.letout`/`.unlet`, `.property`). Prices follow one shared index on `CoinAccounts` (stepped once a UTC day, booms and busts, clamped 0.6–1.6). Buying costs 4.5% stamp duty, selling costs 3%. Rent is paid daily after an 8% agency fee and 4% vacancy, and holding costs (1.5% of value a year) are charged daily. Property counts in net worth at its after-selling-costs value (`CoinService.PropertyValue`) and blocks the jackpot, like stocks. Constants are in `PropertyCatalog`. Debt: `CoinAccount.Debt` is counted against net worth and grows 1% a day, compounded hourly in `CoinService.AddInterest`. Blue Shell and Pickpocket take a share of the victim's net worth: their cash first (`RaffleService.Seize`), and any shortfall goes on their debt. Stock and property sale proceeds pay debt first (`CoinAccount.PayDebtFrom`), and any spare cash pays debt automatically every minute (`CoinService.RepayDebtsFromCash`, skipping players with a bet open).
- Market crashes: `Models/MarketCycle.cs`. Roughly once every 2 years (`MarketCycle.DailyChance`, rolled once a UTC day in `PropertyService.TickIfDue`), a crash runs Decline (14 days), Bottom (5) and Recovery (120). Ordinary stocks fall 26–50% by tier (crypto 60%) and property falls 25% over the decline; `StockService.TickIfDue` applies the drift each tick via `MarketCycle.DriftFor`, with mean reversion off. Once the crash ends, stocks keep their new level as their base (`ResetBasePrices`). Headlines are posted to the last active channel. Phase state lives on `CoinAccounts` (`MarketPhase`, `NextCrisisAllowedDay`).
- Tiers 1 and 2 hold only safe havens: `BOND` is tier 1 and `GOLD` is tier 2. Old markets that still had an ordinary stock there are migrated on load (`StockMarket.SchemaVersion`, `StockMarket.Retire` pays holders out at current value).
- Safe havens: `GOLD` rises 20% during a decline and fades back to its base after; `BOND` pays about 0.02% a day with almost no swing. Both are always listed (`StockMarket.SafeHavens`, `Stock.Role`), not drawn from the random pool, and trade with `.buy`/`.sell`.
- Money: `Services/CoinService` (accounts, interest, leaderboard) + `Managers/BetManager` (initiate/resolve bets, bonuses, stats). Jackpot: sell tax and the 20% donation tax go into `CoinAccounts.JackpotPool`. When a `max` bet covers all cash with no stocks or property held, `BetManager.ResolveBet` rolls `JackpotWinChance` (3%) to pay the pool out. Persistence via `Common/Services` (`GistService` remote, `LocalFileService` local).

## Adding things
- **New command**: write handler in the matching `CommandManager_*.cs`, then register it in the `_commands` list in `CommandManager_Main.cs` (comment there says so).
- **New service**: create in `DiscordBot/Services/`, register in `Startup.ConfigureServices`, inject into `CommandManager` ctor if commands need it.
- **New game**: Game + Manager + Player model, register Manager singleton in `Startup`, add handler in `CommandManager_Games.cs`.

## Conventions / gotchas
- Files use UTF-8 BOM and namespaces matching folders (note: `DiscordBot.Exceptions` namespace is used for exceptions that live in `Common/Exceptions`).
- Hardcoded admin/bot IDs live in `CommandManager_Main.cs` (`_adminIds`, `_botId`); blacklists in `appsettings.json` (`AppSettings.cs`).
- Discord message limit 2000 chars (`_messageCharLimit`); keep replies under it. Embeds are capped at 6000 chars total and 1024 per field, so long lists go through `TextPaginator.Paginate` (`Common/Helpers/TextPaginator.cs`) and send one embed per message, as `.help` does.
- `mappings.json` / `reminders.json` are runtime data copied to output.
- Patch notes: a significant change gets a one-line entry at the top of `PatchNotes.All` (`Models/PatchNotes.cs`); `.patchnotes` shows them.
- Branch `gambling-rework` is active; `master` is the deploy branch.
