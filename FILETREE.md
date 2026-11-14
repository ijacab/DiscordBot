# FILETREE.md — navigation map (keep updated when adding/moving files)

```
azure-pipelines.yml            CI/CD on the Pi: .NET 10 SDK, staged publish, deploy, Ready check, rollback
AGENTS.md / FILETREE.md        agent docs

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
    CommandManager_Methods.cs  all other command handlers (stats, prestige, donate, stocks/buy/sell/portfolio, img, remindme, convo, lang, ban/unban, ...)
    CommandManager_Bot.cs      bot-only cmds: initiatebet, resolvebet, lbjson
    CommandManager_Components.cs  buttons / select menus / modals: gates, ComponentId routing, private replies
    BetManager.cs              bet init/resolve, bonuses, stats updates
  Games/
    BaseMultiplayerGame.cs     game base class
    Blackjack.cs  Roulette.cs  BattleArena.cs     game rules (BattleArena = Face Off: targets, d20 tiers, round cap, shared wins)
    DuckRace.cs                  duck race rules (5 ducks, winner-takes-all)
    Managers/
      BaseMultiplayerManager.cs   lobby/session base (largest game file)
      BlackjackManager.cs  BattleArenaManager.cs  DuckRaceManager.cs   (BattleArenaManager: Face Off lobby, hidden picks, reveal, backers, 1 s timer loop)
      DungeonManager.cs            the one open session (global): money, 1.5 s tick loop, dashboard with buttons (LiveMessage)
    Models/
      Dungeon/DungeonSession.cs    pure session rules: lobby, floor-per-tick crawl, boss doors, checkpoints, PvP pool, idle cash out
      Dungeon/DungeonRun.cs        one solo run: RunFloor, boss doors (fight/sneak/potion), potions, checkpoints, loot, haul
      Dungeon/Combat.cs            shared attack rules: hit, d10 damage, crit, lifesteal, thorns, Second Wind, round cap
      Dungeon/PvpArena.cs          auto-resolved winner-takes-all PvP over the survivors' hauls (10 rounds max)
      Dungeon/Loot.cs              random loot: tiers, sprite kinds, effect pool and drop chances
      BasePlayer.cs IPlayer.cs BlackjackPlayer.cs BattleArenaPlayer.cs DuckRacePlayer.cs
      Card.cs CardDeck.cs DiceRoller.cs RouletteBet.cs
      BattleArena/             BattleAction, BattlePerson, BattleStats, CardTraits, RoundOutcome, SpectatorPool (side bets)
  Services/
    CoinService.cs             accounts, interest, leaderboard, top account
    RaffleService.cs           hourly rubber-banded raffle; the power-up won goes off straight away (kept for `.pu use` if it can't)
    PropertyService.cs         houses: buy/sell with cash, rent, daily market step; buys back old apartments
    DebtService.cs             `.autosell`: sells stocks, then property, every minute to pay off debt
    StockService.cs            fake stock market: 5-min price ticks, buy/sell, portfolios (stockmarket.json)
    StockChart.cs              text price graph for `.stocks {symbol}`
    StockTickerCard.cs         one stock's graph + stats over a window (`.stocks SYMBOL`, live tickers)
    StockThreadService.cs      `.stocksthread`: live ticker thread per server, refreshed each stock tick (stockthreads.json)
    PixelFont.cs               5x7 bitmap font + rectangle fill, shared by the charts and dungeon pictures
    PixelSprites.cs            16x16 pixel-art monsters, bosses and items, drawn at scale
    DungeonSceneRenderer.cs    dashboard picture: current enemy or room, and one panel per player (HP, floor pips, potions)
    FaceOffRenderer.cs         Face Off arena picture: card art, rarity borders, HP bars, picked/reveal/winner states
    LiveMessage.cs             a dashboard message edited in place: images swapped per frame, embeds, buttons, bump
    LootCardRenderer.cs        PNG of a loot item: tier band, icon, effects
    ConvoService.cs            LLM conversation (Ollama)
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
    StockThread.cs             saved `.stocksthread` state: thread, header and ticker message ids per server
    MarketCycle.cs             rare market-wide crashes: phases, per-tick drift for stocks, property moves
    PatchNotes.cs              one-line change log shown by .patchnotes
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
| LLM chat | `Services/ConvoService.cs`, `GPTService.cs`, `Common/Helpers/GPTHelper.cs`, `Installers/OllamaInstaller.cs` |
| Deploy | `azure-pipelines.yml`, `DiscordBot/DiscordBot.service` |
