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
CI/CD: `azure-pipelines.yml` publishes linux-arm64 to a Pi and restarts the `DiscordBot` systemd service (`DiscordBot/DiscordBot.service`). Pushing to `master` deploys.

## Env vars (see `Common/Helpers/EnvironmentHelper.cs`)
`DiscordBotToken`, `GITHUB_PAT_TOKEN`, `GITHUB_USERNAME`, `GITHUB_GIST_ID`, `S_U`, `S_P`, `PORT` (default 5000), `IS_HEROKU`. Never commit secrets.

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
- First gamble of the day (`BetManager.ResolveBet`, tracked by `CoinAccount.LastGambleDate`): d10 roll, a 10 steals 10% of the top (cash) player's net worth; result is read via `BetManager.TakeDailyRoll`.
- Stocks: `Services/StockService` (singleton) ticks every 5-min UTC slot from the `ChatWorker` minute loop via `CommandManager.RunBackgroundTasks`; risk tiers/swings in `Models/Stock.cs`; `.stocks .buy .sell .portfolio`. Leaderboard ranks cash + stock value.
- Money: `Services/CoinService` (accounts, interest, leaderboard) + `Managers/BetManager` (initiate/resolve bets, bonuses, stats). Persistence via `Common/Services` (`GistService` remote, `LocalFileService` local).

## Adding things
- **New command**: write handler in the matching `CommandManager_*.cs`, then register it in the `_commands` list in `CommandManager_Main.cs` (comment there says so).
- **New service**: create in `DiscordBot/Services/`, register in `Startup.ConfigureServices`, inject into `CommandManager` ctor if commands need it.
- **New game**: Game + Manager + Player model, register Manager singleton in `Startup`, add handler in `CommandManager_Games.cs`.

## Conventions / gotchas
- Files use UTF-8 BOM and namespaces matching folders (note: `DiscordBot.Exceptions` namespace is used for exceptions that live in `Common/Exceptions`).
- Hardcoded admin/bot IDs live in `CommandManager_Main.cs` (`_adminIds`, `_botId`); blacklists in `appsettings.json` (`AppSettings.cs`).
- Discord message limit 2000 chars (`_messageCharLimit`); keep replies under it.
- `mappings.json` / `reminders.json` are runtime data copied to output.
- Branch `gambling-rework` is active; `master` is the deploy branch.
