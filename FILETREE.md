# FILETREE.md — navigation map (keep updated when adding/moving files)

```
azure-pipelines.yml            CI/CD on the Pi: .NET 10 SDK, staged publish, deploy, Ready check, rollback
AGENTS.md / FILETREE.md        agent docs
.claude/agents/code-reviewer.md  read-only reviewer agent with this repo's checklist; run before pushing to master

DiscordBot/                    main app (DiscordBot.csproj, .sln lives here)
  Program.cs                   entry; Kestrel + generic host
  Startup.cs                   DI registration, HttpClients, middleware  ← register services here
  ChatWorker.cs                BackgroundService: Ollama install + start MessageHandler
  MessageHandler.cs            Discord event hooks → CommandManager (messages, buttons, select menus, modals); background tasks
  WebAlerterWorker.cs          worker for WebAlerter (disabled in Startup)
  AppSettings.cs               blacklist config + PrestigeDisabled feature flag
  Constants.cs                 shared constants
  appsettings.json             logging + AppSettings (blacklists)
  mappings.json                custom `.add` commands data
  reminders.json               reminders data
  DiscordBot.service           systemd unit
  Managers/
    CommandManager_Main.cs     ctor, _commands registry, RunCommand dispatch/parsing
    CommandManager_Games.cs    .roulette .blackjack .faceoff .duckrace .dungeon handlers (typed paths)
    CommandManager_Stocks.cs   .stocks .buy .sell .portfolio .stocksthread, old shorts (.cover)
    CommandManager_Bets.cs     .call .put .options: leveraged bets, payout tables, settled-bet announcements
    CommandManager_Property.cs .property .buyproperty .sellproperty .autosell
    CommandManager_Economy.cs  leaderboard, net worth graphs, .donate, .jackpot, .stats
    CommandManager_PowerUps.cs .pu: power-ups, catalog, using one
    CommandManager_Cards.cs    .cardpull and the Keep button
    CommandManager_Attacks.cs  .attacks: recap of the latest attacks on you
    CommandManager_Crash.cs    .crash and its Cash out / Again buttons
    CommandManager_Fun.cs      .roll .time .img .dbz (searches DuckDuckGo, downloads a working picture, posts it as a file) .face .lang .convo .scrapeconvo
    CommandManager_Admin.cs    .clear .start .stop .ban .unban, disk space
    CommandManager_Utility.cs  custom commands, reminders, .help, patch notes, paged embeds
    CommandManager_Bot.cs      bot-only cmds: initiatebet, resolvebet, lbjson
    CommandManager_Components.cs  buttons / select menus / modals: gates, ComponentId routing, private replies
    CommandManager_MoneyButtons.cs  money buttons/menus: stocks, power-ups, property, auto-sell, blackjack, duck race
    CommandManager_Slots.cs    .slots and the slot machine's buttons / bet form
    CommandManager_Raids.cs    .raid, .train, .stance, .guards, the `.card` control panel and their buttons
    BetManager.cs              bet init/resolve, bonuses, stats updates
  Games/
    BaseMultiplayerGame.cs     game base class
    BlackjackView.cs           the blackjack table embed (lobby, hands, results), edited in place
    SlotMachine.cs  SlotView.cs   slot machine rules (reels, paytable, jackpot payout) and its embed/buttons
    Blackjack.cs  Roulette.cs  BattleArena.cs     game rules (BattleArena = Face Off: targets, d20 tiers, round cap, shared wins)
    DuckRace.cs                  duck race rules (5 ducks, winner-takes-all)
    CrashGame.cs                 crash rules: crash point (97% payback for any target), multiplier over time
    Managers/
      BaseMultiplayerManager.cs   lobby/session base (largest game file); takes a GameContext, one lock for every change
      SlotMachineManager.cs        slot sessions, spin settlement and result display
      CrashManager.cs              crash rounds: launch, live multiplier edits, cash out, settle through BetManager
      BlackjackManager.cs  BattleArenaManager.cs  DuckRaceManager.cs   (BattleArenaManager: Face Off lobby, hidden picks, reveal, backers, 1 s timer loop)
      DungeonManager.cs            the one open session (global): money, 1.5 s tick loop, dashboard with buttons (LiveMessage)
    Models/
      Dungeon/DungeonSession.cs    pure session rules: lobby, floor-per-tick crawl, boss doors, checkpoints, PvP pool, idle cash out
      Dungeon/DungeonRun.cs        one solo run: RunFloor, boss doors (fight/sneak/potion), potions, checkpoints, loot, haul
      Dungeon/Combat.cs            shared attack rules: hit, d10 damage, crit, lifesteal, thorns, Second Wind, round cap
      Dungeon/PvpArena.cs          auto-resolved winner-takes-all PvP over the survivors' hauls (10 rounds max)
      Dungeon/Loot.cs              random loot: tiers, sprite kinds, effect pool and drop chances
      BasePlayer.cs IPlayer.cs BlackjackPlayer.cs BattleArenaPlayer.cs DuckRacePlayer.cs
      GameContext.cs             who acts and where (from a typed command or a button click)
      Card.cs CardDeck.cs DiceRoller.cs RouletteBet.cs
      BattleArena/             BattleAction, BattlePerson, BattleStats, CardTraits, CardLevels (XP, training, focus), RoundOutcome, SpectatorPool (side bets)
  Services/
    CoinService.cs             accounts, interest, leaderboard, top account
    RaffleService.cs           hourly rubber-banded raffle; the power-up won goes off straight away (kept for `.pu use` if it can't)
    PropertyService.cs         houses: buy/sell with cash, rent, daily market step; buys back old apartments
    DebtService.cs             `.autosell`: sells stocks, covers shorts, cashes out options, then sells property, every minute to pay off debt
    RaidService.cs             `.raid` (odds, stakes, loot, limits, revenge, bounties), guards and card training
    WorthService.cs            the one total-worth formula (cash + stocks/bets + property - debt) and leaderboard order
    StockService.cs            fake stock market: 5-min price ticks, buy/sell, portfolios (stockmarket.json)
    StockChart.cs              text price graph for `.stocks {symbol}`
    HangWatchdog.cs            restarts the bot (fail fast, systemd restarts) after 10 min offline or with the minute loop stuck
    StockTickerCard.cs         one stock's graph + stats over a window (`.stocks SYMBOL`), and the one-image market board for the live thread
    StatsReport.cs             the nine `.stats` pages, built from CoinAccountStats (pure text)
    StockThreadService.cs      `.stocksthread`: live one-message, one-image market board per server, refreshed each stock tick (stockthreads.json)
    PixelFont.cs               5x7 bitmap font + rectangle fill, shared by the charts and dungeon pictures
    PixelSprites.cs            16x16 pixel-art monsters, bosses and items, drawn at scale
    DungeonSceneRenderer.cs    dashboard picture: current enemy or room, and one panel per player (HP, floor pips, potions)
    FaceOffRenderer.cs         Face Off arena picture: card art, rarity borders, HP bars, picked/reveal/winner states
    LiveMessage.cs             a dashboard message edited in place: images swapped per frame, embeds, buttons, bump
    LootCardRenderer.cs        PNG of a loot item: tier band, icon, effects
    ConvoService.cs            LLM conversation (Ollama), the `.convo` fallback outside the scraped server
    ConvoCorpusService.cs      `.scrapeconvo`: background, resumable scrape of the server's history to convo_data/ on the Pi; loads the ConvoModel
    GPTService.cs              GPT/LLM calls
    DuckDuckGoService.cs       image search
    FaceService.cs             thispersondoesnotexist
    MappingService.cs          custom command storage
    ReminderService.cs         reminders storage
    TranslationService.cs      .lang: multi-hop translation back to English (Google gtx endpoint)
    BanService.cs              banned user IDs storage (bans.json in gist)
  Models/
    Command.cs                 command definition
    CoinAccounts.cs CoinAccountStats.cs PrestigeLevel.cs Reminder.cs
    Stock.cs StockMarket.cs    stock + risk tiers (max swing per tick), market/portfolios
    Money.cs                   THE money rules: amount parsing, Credit/Debit/Charge/Seize, held cash, save lock
    LeveragedBet.cs            calls and puts: bet model, LeverageRules, LeveragePricer (simulated fees)
    AttackHistory.cs           the per-player attack log (`.attacks`): records and how they read
    SyncSet.cs                 small thread-safe set (open bets)
    MoneyComponents.cs         money button prefixes (stk/pu/prop/debt/card/raid) and ButtonOwner (owner-only buttons)
    RaidRules.cs               raid numbers: card power, stances, guards, underdog penalty, stake limits, loot
    StockThread.cs             saved `.stocksthread` state: thread and board message ids per server
    MarketCycle.cs             rare market-wide crashes: phases, per-tick drift for stocks, property moves
    PatchNotes.cs              one-line change log shown by .patchnotes
    ConvoModel.cs              `.convo` generator: word Markov chain over the scraped messages, "user: text" lines
    PowerUp.cs                 raffle power-up catalog and the buffs on accounts
    Property.cs                house prices, costs, rent and the shared market index (apartments: old saves only)
    ConvoService/LLMOptions.cs
    DuckDuckGoService/         ImageResult, ImageSearch
  Installers/OllamaInstaller.cs
  Text/                        NameGenerator.cs, adjectives.txt, names.txt

Common/                        shared net10.0 lib
  Exceptions/                  BadInput, BadSyntax, EnvironmentVariableMissing, NotInGame
  Helpers/                     Discord, Environment, Format, GPT, List, Random, String, TextPaginator helpers
  Models/GistSettings.cs
  Services/                    GistService (remote), IFileService, LocalFileService
  _nanogpt/                    unrelated Python nanoGPT; ignore

WebAlerter/                    StrawmanChecker.cs, Models/StrawmanTrade.cs

Tests/                         xunit: BlackjackTests, GPTHelperTests, WebAlerterTests, PropertyTests, DebtTests, AutoSellTests, TextPaginatorTests, DiscordConfigTests
```

## Where to look
| Task | File |
|---|---|
| Add/change a command | `Managers/CommandManager_Main.cs` (registry) + handler in `_Games`/`_Methods` |
| Command parsing, prefix, bot filtering | `CommandManager_Main.cs` `RunCommand` |
| Register a service | `Startup.cs` |
| Money/balances/interest | `Services/CoinService.cs`, `Managers/BetManager.cs` |
| Blackjack / roulette / arena logic | `Games/<Game>.cs`, `Games/Managers/` |
| Persistence (Gist/local) | `Common/Services/` |
| Env var access | `Common/Helpers/EnvironmentHelper.cs` |
| `.convo` / server history | `Models/ConvoModel.cs`, `Services/ConvoCorpusService.cs` |
| LLM chat | `Services/ConvoService.cs`, `GPTService.cs`, `Common/Helpers/GPTHelper.cs`, `Installers/OllamaInstaller.cs` |
| Deploy | `azure-pipelines.yml`, `DiscordBot/DiscordBot.service` |
