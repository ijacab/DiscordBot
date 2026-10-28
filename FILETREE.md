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
    CommandManager_Games.cs    .roulette .blackjack .faceoff handlers
    CommandManager_Methods.cs  all other command handlers (stats, prestige, donate, stocks/buy/sell/portfolio, img, remindme, convo, ...)
    CommandManager_Bot.cs      bot-only cmds: initiatebet, resolvebet, lbjson
    BetManager.cs              bet init/resolve, bonuses, stats updates
  Games/
    BaseMultiplayerGame.cs     game base class
    Blackjack.cs  Roulette.cs  BattleArena.cs     game rules
    Managers/
      BaseMultiplayerManager.cs   lobby/session base (largest game file)
      BlackjackManager.cs  BattleArenaManager.cs
    Models/
      BasePlayer.cs IPlayer.cs BlackjackPlayer.cs BattleArenaPlayer.cs
      Card.cs CardDeck.cs DiceRoller.cs RouletteBet.cs
      BattleArena/             AttackInfo, BattlePerson, BattleStats, OtherStats
  Services/
    CoinService.cs             accounts, interest, leaderboard, top account
    StockService.cs            fake stock market: 5-min price ticks, buy/sell, portfolios (stockmarket.json)
    ConvoService.cs            LLM conversation (Ollama)
    GPTService.cs              GPT/LLM calls
    DuckDuckGoService.cs       image search
    FaceService.cs             thispersondoesnotexist
    MappingService.cs          custom command storage
    ReminderService.cs         reminders storage
  Models/
    Command.cs                 command definition
    CoinAccounts.cs CoinAccountStats.cs PrestigeLevel.cs Reminder.cs
    Stock.cs StockMarket.cs    stock + risk tiers (max swing per tick), market/portfolios
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
