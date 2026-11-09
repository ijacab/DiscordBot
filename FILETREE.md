# FILETREE.md — navigation map (keep updated when adding/moving files)

```
azure-pipelines.yml            CI/CD → Pi (linux-arm64), systemd restart
AGENTS.md / FILETREE.md        agent docs

DiscordBot/                    main app (DiscordBot.csproj, .sln lives here)
  Program.cs                   entry; Kestrel + generic host
  Startup.cs                   DI registration, HttpClients, middleware  ← register services here
  ChatWorker.cs                BackgroundService: Ollama install + start MessageHandler
  MessageHandler.cs            Discord event hooks → CommandManager; background tasks (reminders, interest)
  WebAlerterWorker.cs          worker for WebAlerter (disabled in Startup)
  AppSettings.cs               blacklist config + PrestigeDisabled feature flag
  Constants.cs                 shared constants
  appsettings.json             logging + AppSettings (blacklists)
  mappings.json                custom `.add` commands data
  reminders.json               reminders data
  DiscordBot.service           systemd unit
  Managers/
    CommandManager_Main.cs     ctor, _commands registry, RunCommand dispatch/parsing
    CommandManager_Games.cs    .roulette .blackjack .faceoff .duckrace handlers
    CommandManager_Methods.cs  all other command handlers (stats, prestige, donate, stocks/buy/sell/portfolio, img, remindme, convo, lang, ban/unban, ...)
    CommandManager_Bot.cs      bot-only cmds: initiatebet, resolvebet, lbjson
    BetManager.cs              bet init/resolve, bonuses, stats updates
  Games/
    BaseMultiplayerGame.cs     game base class
    Blackjack.cs  Roulette.cs  BattleArena.cs     game rules
    DuckRace.cs                  duck race rules (5 ducks, winner-takes-all)
    Managers/
      BaseMultiplayerManager.cs   lobby/session base (largest game file)
      BlackjackManager.cs  BattleArenaManager.cs  DuckRaceManager.cs
      DungeonManager.cs            the one open session (global): lobby, 10-floor blocks, checkpoints, PvP pool, idle cash out
    Models/
      Dungeon/DungeonRun.cs        one solo run in 10-floor blocks: rooms, fights, checkpoints, automatic loot, haul
      Dungeon/Combat.cs            shared attack rules: hit, d10 damage, crit, lifesteal, thorns, Second Wind, round cap
      Dungeon/PvpArena.cs          auto-resolved winner-takes-all PvP over the survivors' hauls (10 rounds max)
      Dungeon/Loot.cs              random loot: tiers, sprite kinds, effect pool and drop chances
      BasePlayer.cs IPlayer.cs BlackjackPlayer.cs BattleArenaPlayer.cs DuckRacePlayer.cs
      Card.cs CardDeck.cs DiceRoller.cs RouletteBet.cs
      BattleArena/             AttackInfo, BattlePerson, BattleStats, CardTraits
  Services/
    CoinService.cs             accounts, interest, leaderboard, top account
    RaffleService.cs           5-hourly raffle that gives power-ups, and using them (`.pu`)
    PropertyService.cs         houses and apartments: buy/sell with cash, letting out, daily market step and rent
    StockService.cs            fake stock market: 5-min price ticks, buy/sell, portfolios (stockmarket.json)
    StockChart.cs              text price graph for `.stocks {symbol}`
    PixelFont.cs               5x7 bitmap font + rectangle fill, shared by the charts and dungeon pictures
    PixelSprites.cs            16x16 pixel-art monsters, bosses and items, drawn at scale
    DungeonSceneRenderer.cs    dashboard picture: current enemy or room, and one panel per player
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
    PowerUp.cs                 raffle power-up catalog and the buffs on accounts
    Property.cs                house/apartment prices, costs, rent and the shared market index
    ConvoService/LLMOptions.cs
    DuckDuckGoService/         ImageResult, ImageSearch
  Installers/OllamaInstaller.cs
  Text/                        NameGenerator.cs, adjectives.txt, names.txt

Common/                        shared netstandard2.1 lib
  Exceptions/                  BadInput, BadSyntax, EnvironmentVariableMissing, NotInGame
  Helpers/                     Discord, Environment, Format, GPT, List, Random, String helpers
  Models/GistSettings.cs
  Services/                    GistService (remote), IFileService, LocalFileService
  _nanogpt/                    unrelated Python nanoGPT; ignore

WebAlerter/                    StrawmanChecker.cs, Models/StrawmanTrade.cs

Tests/                         xunit: BlackjackTests, GPTHelperTests, WebAlerterTests
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
