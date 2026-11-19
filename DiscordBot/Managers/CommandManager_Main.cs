using Discord;
using Discord.WebSocket;
using DiscordBot.Exceptions;
using DiscordBot.Games.Managers;
using DiscordBot.Models;
using DiscordBot.Services;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DiscordBot.Managers
{
    public partial class CommandManager
    {
        private List<Command> _commands;
        private readonly ILogger<CommandManager> _logger;
        private readonly AppSettings _appSettings;
        private readonly MappingService _mappingService;
        private readonly ReminderService _reminderService;
        private readonly CoinService _coinService;
        private readonly StockService _stockService;
        private readonly RaffleService _raffleService;
        private readonly PropertyService _propertyService;
        private readonly DebtService _debtService;
        private readonly RaidService _raidService;
        private readonly WorthService _worth;
        private readonly StockThreadService _stockThreadService;
        private readonly DuckDuckGoService _duckDuckGoService;
        private readonly FaceService _faceService;
        private readonly BlackjackManager _blackjackManager;
        private readonly BetManager _betManager;
        private readonly BattleArenaManager _battleArenaManager;
        private readonly DuckRaceManager _duckRaceManager;
        private readonly DungeonManager _dungeonManager;
        private readonly SlotMachineManager _slotMachineManager;
        private readonly CrashManager _crashManager;
        private readonly GPTService _gptService;
        private readonly ConvoService _convoService;
        private readonly ConvoCorpusService _convoCorpus;
        private readonly TranslationService _translationService;
        private readonly BanService _banService;
        private HashSet<ulong> _bannedUserIds;
        private Dictionary<string, string> _customMappings;
        private ulong _botId = 205386259026280448;
        private ulong[] _adminIds = new ulong[] { 166477511469957120, 195207667902316544 };
        private int _argCharLimit = 950;
        private int _messageCharLimit = 2000;
        private double _startingAmount = 10000;

        private static readonly HashSet<string> _moneyCommands = new HashSet<string>
        {
            "roulette", "r", "blackjack", "bj", "faceoff", "fo", "duckrace", "duck", "dungeon", "dng", "slots", "slot", "crash",
            "buy", "sell", "short", "cover", "call", "put", "donate", "cardpull", "buyproperty", "sellproperty", "raid", "train", "guards"
        };

        private bool _patchNotesChecked;
        private bool _stopped;
        private bool _imageSearchStopped = true;
        public CommandManager(ILogger<CommandManager> logger, AppSettings appSettings,
            MappingService mappingService, ReminderService reminderService, CoinService coinService, StockService stockService, RaffleService raffleService, PropertyService propertyService, DebtService debtService, StockThreadService stockThreadService, DuckDuckGoService duckDuckGoService, FaceService faceService,
            BlackjackManager blackjackManager, BetManager betManager, BattleArenaManager battleArenaManager, DuckRaceManager duckRaceManager, DungeonManager dungeonManager, SlotMachineManager slotMachineManager, GPTService gptService, ConvoService convoService, ConvoCorpusService convoCorpus,
            TranslationService translationService, BanService banService, RaidService raidService, CrashManager crashManager)
        {
            _logger = logger;
            _appSettings = appSettings;
            _mappingService = mappingService;
            _reminderService = reminderService;
            _coinService = coinService;
            _stockService = stockService;
            _raffleService = raffleService;
            _propertyService = propertyService;
            _debtService = debtService;
            _raidService = raidService;
            _worth = new WorthService(coinService, stockService, appSettings);
            _stockThreadService = stockThreadService;
            _duckDuckGoService = duckDuckGoService;
            _faceService = faceService;
            _blackjackManager = blackjackManager;
            _betManager = betManager;
            _battleArenaManager = battleArenaManager;
            _duckRaceManager = duckRaceManager;
            _dungeonManager = dungeonManager;
            _slotMachineManager = slotMachineManager;
            _crashManager = crashManager;
            _gptService = gptService;
            _convoService = convoService;
            _convoCorpus = convoCorpus;
            _translationService = translationService;
            _banService = banService;
            _bannedUserIds = new HashSet<ulong>(banService.GetAll().GetAwaiter().GetResult());
            _customMappings =mappingService.GetAll().GetAwaiter().GetResult();//new Dictionary<string, string>(mappingService.GetAll().GetAwaiter().GetResult(), StringComparer.InvariantCultureIgnoreCase);

            //need to add new commands in here as they are created
            _commands = new List<Command>
            {
                new Command("help", Help),
                new Command("patchnotes", ShowPatchNotes) { Description = "Shows a short summary of recent changes. Add `all` to see every change.", Syntax = "`.patchnotes` or `.patchnotes all`" },
                new Command("roulette", GameRoulette) { Description = "Plays a roulette game using virtual money. Type '.leaderboard' to see how much money you have", Syntax = "`.roulette 00-100,0-300,1-10,36-100,Red-100,Black-200,FirstColumn-50,SecondColumn-300,ThirdColumn-20,Odd-100,Even-50,FirstDozen-10,SecondDozen-10,ThirdDozen-100`" },
                new Command("r", GameRoulette) { Hidden = true, Syntax = "`.roulette 00-100,0-300,1-10,36-100,Red-100,Black-200,FirstColumn-50,SecondColumn-300,ThirdColumn-20,Odd-100,Even-50,FirstDozen-10,SecondDozen-10,ThirdDozen-100`" },
                new Command("blackjack", GameBlackjack) { Description = "Plays a multiplayer blackjack game using virtual money. Type '.leaderboard' to see how much money you have", Syntax = "`.bj betAmount` to start where betAmount is the amount you want to bet. For example `.bj 1000`" },
                new Command("bj", GameBlackjack) { Hidden = true, Syntax = "`.bj betAmount` to start where betAmount is the amount you want to bet. For example `.bj 1000`" },
                new Command("faceoff", GameFaceOff) { Description = "A multiplayer fight using your Battle Person card and virtual money, on one live dashboard with buttons. Join the lobby (it starts 60 seconds after the second fighter joins, up to 6 fighters). Each round everyone privately picks Attack, Defend, Focus or Gamble and rolls a d20; picks stay hidden until the round resolves. Attack and Gamble hit one opponent you choose. Picks counter each other: Attack beats Focus, Focus beats Gamble, Gamble beats Defend, Defend beats Attack. A natural 20 is always the best roll. Last one standing takes the whole pot; after 12 rounds the highest HP wins, and ties split it. Spectators can back a fighter until round 1 resolves, and backers of the winner share everything backed. Type '.leaderboard' to see how much money you have", Syntax = "`.fo betAmount` to open or join the lobby (for example `.fo 1000`, `.fo 25%` or `.fo max`), then use the buttons. Typed: `.fo start`, `.fo attack|defend|focus|gamble [name]`, `.fo ability`, `.fo leave`, `.fo back name amount`, `.fo status`" },
                new Command("fo", GameFaceOff) { Hidden = true, Syntax = "`.fo betAmount` to open or join the lobby, then use the buttons. For example `.fo 1000`" },
                new Command("duckrace", GameDuckRace) { Description = "Plays a multiplayer duck race using virtual money. You race against the other players and one bot, so more players means a bigger payout. Type '.leaderboard' to see how much money you have", Syntax = "`.duckrace betAmount` to start where betAmount is the amount you want to bet. For example `.duckrace 1000`" },
                new Command("duck", GameDuckRace) { Hidden = true, Syntax = "`.duckrace betAmount` to start where betAmount is the amount you want to bet. For example `.duckrace 1000`" },
                new Command("dungeon", GameDungeon) { Description = "Only one dungeon session can be open at a time, across all channels. Open a lobby with a buy-in from your cash, and anyone presses Join on the dashboard within 60 seconds. Then each player presses Go to crawl their own run, floor by floor, in blocks of 10 floors. At each boss door (floors 5 and 10) you choose: fight it, sneak past (half the time it works, otherwise the boss strikes first), or drink a potion and fight. Every block is about a 50% chance, so 20 floors is about 25% and 30 floors about 12.5%. After each block you're at a checkpoint: cash out to bank your haul, or enter the PvP pool. Once every run has decided, the pool fights, the winner takes the pot of the hauls and crawls on, and the losers are out. Stalled runs are cashed out after 15 minutes. Loot and potions drop automatically. Dying loses the haul and the buy-in. Everything is on the dashboard's buttons; typed commands work too. Shorthand: `.dng`", Syntax = "`.dungeon 1000` to open a lobby with a 1000 buy-in each, then use the buttons. Typed: `.dng join`, `.dng leave`, `.dng start`, `.dng go`, `.dng boss`, `.dng sneak`, `.dng potion`, `.dng fight`, `.dng cash`, or `.dng` to bring the dashboard to the bottom" },
                new Command("dng", GameDungeon) { Hidden = true, Syntax = "`.dng 1000` to open a lobby with a 1000 buy-in each, then use the buttons. Typed: `.dng join`, `.dng leave`, `.dng start`, `.dng go`, `.dng boss`, `.dng sneak`, `.dng potion`, `.dng fight`, `.dng cash`, or `.dng` to bring the dashboard to the bottom" },
                new Command("slots", GameSlots) { Description = "A slot machine with buttons. Pull the lever with Spin, change the bet with Bet ½, Bet x2, Max or Bet…, and cash out when you're done. Match symbols on the middle line: three 7️⃣ pay x250, and three 💎 win the jackpot pool (at least x100 your bet). 75% of every lost bet goes into the jackpot. Shorthand: `.slot`", Syntax = "`.slots` (bets 1% of your cash), `.slots 1000`, `.slots 10%` or `.slots max`" },
                new Command("crash", GameCrash) { Description = $"🚀 A rocket climbs from 1.00x and blows up at a random point. Hit Cash out before it does and you get your bet times the multiplier; too slow and it's gone. Add a target like `2x` to cash out by itself. Tops out at {Games.CrashGame.MaxMultiplier:0}x and pays back {1 - Games.CrashGame.HouseEdge:P0} on average whatever you do.", Syntax = "`.crash 1000`, `.crash 10% 2x`, `.crash max 1.5x`" },
                new Command("slot", GameSlots) { Hidden = true, Syntax = "`.slots 1000`" },
                new Command("leaderboard", LeaderboardWithHints) { Description = "Shows the money leaderboard. Shorthand: `.lb`", Syntax = "`.leaderboard`" },
                new Command("lb", LeaderboardWithoutHints) { Description = "Shows the money leaderboard.", Syntax = "`.lb`", Hidden = true },
                new Command("stats", Stats) { Description = "Shows your stats (or someone else's) on one page with a menu to switch: overview, betting, per-game results, stocks, property and debt, jackpot and income, power-ups, dungeon and Face Off, donations.", Syntax = "`.stats`, `.stats me`, `.stats @someone`, or add a page: `.stats me stocks`" },
                new Command("prestige", Prestige) { Description = "Levels up your account and resets your money to the starting amount.", Syntax = "`.prestige`", Hidden = appSettings.PrestigeDisabled },
                new Command("stocks", Stocks) { Description = "Shows the stock market, or a price graph for one stock over a time window. Prices change every 5 minutes. Higher risk tiers (3-6) swing more, 6 is crypto. Graphs are green above the launch price and red below.", Syntax = "`.stocks`, `.stocks SYMBOL` or `.stocks SYMBOL 48h`" },
                new Command("stocksthread", StocksThread) { Description = "Bot admins only. Opens a thread here with one live message: a line per stock and one image with every stock's chart, all time and the last 3 hours. Updates every 5 minutes. One per server; running it again moves it and deletes the old thread.", Syntax = "`.stocksthread` or `.stocksthread stop`", RequiresAdmin = true },
                new Command("buy", BuyStock) { Description = "Buys whole shares of a stock.", Syntax = "`.buy SYMBOL 10`, `.buy SYMBOL 50%` (half your cash) or `.buy SYMBOL max`" },
                new Command("sell", SellStock) { Description = "Sells shares of a stock you own.", Syntax = "`.sell SYMBOL 10`, `.sell SYMBOL 50%` (half your shares), `.sell SYMBOL all` or `.sell SYMBOL max`" },
                new Command("short", ShortStock) { Hidden = true },
                new Command("cover", CoverStock) { Description = "Buys back shares you shorted and releases your margin, plus profit or minus loss.", Syntax = "`.cover SYMBOL 10`, `.cover SYMBOL 50%`, `.cover SYMBOL all` or `.cover SYMBOL max`" },
                new Command("call", CallOption) { Description = $"Bet a stock goes UP, with leverage: at 5x every 1% it rises makes you 5%. Pick 2x, 5x or 10x and 15m to 24h (default 5x for 1h). Paid out on the price when it closes: you can't lose more than you put in, you're wiped out if it drops 1/leverage (20% at 5x), and it pays at most {LeverageRules.MaxMultiple:0}x what rides. The fee depends on how likely it is to pay. Type it with no amount to see the payout table first.", Syntax = "`.call SYMBOL` (payout table), `.call SYMBOL 1000`, `.call SYMBOL 25% 10x 6h` or `.call SYMBOL max 2x 15m`" },
                new Command("put", PutOption) { Description = "Bet a stock goes DOWN, with leverage: at 5x every 1% it falls makes you 5%. Same rules as `.call`. A 2x put is a short that can't lose more than you put in.", Syntax = "`.put SYMBOL` (payout table), `.put SYMBOL 1000`, `.put SYMBOL 25% 10x 6h`" },
                new Command("options", Options) { Description = "Shows your open calls and puts, what each would pay right now, and the last few that closed.", Syntax = "`.options`" },
                new Command("portfolio", Portfolio) { Description = "Shows the stocks you own.", Syntax = "`.portfolio`" },
                new Command("pf", Portfolio) { Description = "Shorthand for `.portfolio`.", Syntax = "`.pf`", Hidden = true },
                new Command("networth", NetWorthGraph) { Description = "Shows a graph of net worth (cash + stocks + property) over the last 30 days, sampled hourly. Add a time like `48h` or `7d` to show just that window, as on `.stocks`. Pass a user to see theirs, or `all` to put everyone on the same graph. Shorthand: `.nw`", Syntax = "`.networth`, `.networth 48h`, `.networth @person 7d` or `.networth all 24h`" },
                new Command("nw", NetWorthGraph) { Description = "Shorthand for `.networth`.", Syntax = "`.nw`, `.nw 48h`, `.nw @person 7d` or `.nw all 24h`", Hidden = true },
                new Command("donate", Donate) { Description = "Donate some of your money to someone else.", Syntax = "`.donate @person 1000`" },
                new Command("property", Property) { Description = "Shows the house price, the market (and today's crash risk), and the houses you own. Rent is paid hourly after the agency's cut.", Syntax = "`.property`" },
                new Command("buyproperty", BuyProperty) { Description = "Buys a house at the current market price, with cash only. Stamp duty and conveyancing are 4.5% on top.", Syntax = "`.buyproperty`" },
                new Command("sellproperty", SellProperty) { Description = "Sells a property you own at market value, less 3% for agent commission and marketing.", Syntax = "`.sellproperty 1`" },
                new Command("letout", RentingIsAutomatic) { Hidden = true },
                new Command("unlet", RentingIsAutomatic) { Hidden = true },
                new Command("powerup", PowerUp) { Description = "Shows your power-ups. One is drawn every hour among everyone who used money in the 6 hours before, and it goes off the moment it's won (lower on the leaderboard you draw more attacks and catch-up items). One that couldn't go off yet is kept: use it by its number. Shorthand: `.pu`", Syntax = "`.powerup`, `.pu use 2` or `.pu list`" },
                new Command("autosell", AutoSell) { Description = "When on, your assets are sold automatically to pay off any debt (debt grows 10% every hour): stocks first, then shorts are covered, options cashed out, and then property.", Syntax = "`.autosell`, `.autosell on` or `.autosell off`" },
                new Command("pu", PowerUp) { Description = "Shorthand for `.powerup`.", Syntax = "`.pu`, `.pu use 2` or `.pu list`", Hidden = true },
                new Command("jackpot", Jackpot) { Description = "Shows the jackpot. 75% of every lost bet, sell tax, donation tax and debt repayments go in. A bet of all your cash (up to $10,000) or half your cash or more (above $10,000), with no stocks or property, can win the lot, with 3 rolls an hour each. Three 💎 on `.slots` win it too.",Syntax = "`.jackpot`" },
                new Command("face", FaceGenerate) { Syntax = "`.face`", Description = "Gets an AI generated face from thispersondoesnotexist.com and displays it." },
                new Command("time", Time) { Syntax = "`.time utc`" },
                new Command("img", ImageSearch) { Description = "Searches DuckDuckGo for a random image related to a given search query", Hidden = true },
                new Command("dbz", ImageSearchDbz) { Description = "Gets a DBZ image" },
                new Command("cardpull", CardPull) { Description = "Pulls a new Battle Person card. Costs $1,000, but your first pull each day is free. Keep it by typing the `.cardpull keep` line that follows.", Syntax = "`.cardpull`" },
                new Command("card", CardView) { Description = "Shows your Battle Person card and its control panel: level and XP, training, focus, raid stance, guards, bounty and revenge. Your level is yours: it carries over to a new card you keep, and resets each season. XP comes from raids and Face Off.", Syntax = "`.card`, `.card focus attack|defense|crit|balanced`" },
                new Command("raid", Raid) { Description = "Raid another player with your Battle Person card to take their money. Stake some cash (at least 1% of your worth, at most 10% of theirs): win and you get it back plus twice it from them (cash first, then their debt); lose and they keep it. The odds come from both cards' power, their stance and guards, and how much richer they are than you. 3 raids an hour, the same person once every 6 hours, and someone just raided is safe for an hour. Whoever you raid can take revenge for 24 hours, and 10% of your loot becomes a bounty for whoever beats you.", Syntax = "`.raid` (revenge and bounties), `.raid @person` (odds), `.raid @person 5000`, `.raid @person 10%` or `.raid name max`" },
                new Command("train", Train) { Description = "Trains your Battle Person card a level for cash (into the jackpot). Each level costs 30% more than the last, less the XP you already have.", Syntax = "`.train` or `.train max`" },
                new Command("stance", Stance) { Description = "Sets how your card defends against raids while you're away. Guard: 30% stronger. Counter: normal, but a beaten raider pays you their stake twice over. Hoard: weaker, but a raider who gets through takes half as much.", Syntax = "`.stance guard`, `.stance counter` or `.stance hoard`" },
                new Command("guards", Guards) { Description = "Hires guards for 6 hours: your card defends 50% harder against raids. Costs 2% of your worth, into the jackpot.", Syntax = "`.guards`" },
                new Command("add", Add) { Description = "Adds (or overwrites if exists) a custom command. When 'key' is typed, 'value' will be displayed.", Syntax = "`.add \"key\" \"value\"`" },
                new Command("remove", Remove) { Description = "Removes a custom command that has been added using .add command.", Syntax = "`.remove \"key\"`" },
                new Command("clear", Clear, hidden: true, requiresAdmin: true),
                new Command("age", Age) { Description = "Displays age of your discord account", Syntax = "`.age`" },
                new Command("remindme", AddReminder) { Syntax = "`.remindme \"reminder for something\" 6 hours`" },
                new Command("roll", Roll) { Syntax = "`.roll 1 100`" },
                new Command("archiveleaderboard", ArchiveLeaderboard) { Description = "Archives the leaderboard and resets the current one.", Syntax = ".archiveleaderboard", RequiresAdmin = true, Hidden = true },
                new Command("start", Start, hidden: true, requiresAdmin: true),
                new Command("stop", Stop, hidden: true, requiresAdmin: true),
                new Command("freespace", GetFreeSpace, hidden: true, requiresAdmin: true),
                new Command("convo", Convo) { Description = "Makes up a 3 to 10 line conversation between people from this server, learned from the server's chat history. Add some words to set the topic, and the chat keeps coming back to them.", Syntax = "`.convo` or `.convo pizza time`" },
                new Command("scrapeconvo", ScrapeConvo) { Description = "Bot admins only. Downloads the server's chat history (in the background, onto the Pi) for `.convo`; later runs only fetch new messages.", Syntax = "`.scrapeconvo`, `.scrapeconvo status` or `.scrapeconvo stop`", Hidden = true, RequiresAdmin = true },
                new Command("lang", Lang) { Description = "Mangles your text by bouncing it through 8 random far-flung languages (Yoruba, Khmer, Latin, Hmong and friends) and back to English.", Syntax = "`.lang some text to mangle`" },
                new Command("ban", Ban) { Syntax = "`.ban @someone`", Hidden = true, RequiresAdmin = true },
                new Command("unban", Unban) { Syntax = "`.unban @someone`", Hidden = true, RequiresAdmin = true },

                //bot commands
                new Command("initiatebet", InitiateBet, hidden: true),
                new Command("resolvebet", ResolveBet, hidden: true),
                new Command("lbjson", GetLeaderboardJson, hidden: true)
            };

        }

        public async Task RunCommand(DiscordSocketClient client, SocketMessage message)
        {
            if (_stopped)
            {
                if (message.Content != ".start")
                {
                    _logger.LogDebug("Bot is stopped at the moment. Run .start to start it up again.");
                    return;
                }
            }

            if (_bannedUserIds.Contains(message.Author.Id))
            {
                if (message.Content.StartsWith('.'))
                    await message.Channel.SendMessageAsync("you are BANNED YOU CANNOT INTERACT WITH BOT");
                return;
            }

            if (message.Author.IsBot &&
                !(message.Content.StartsWith(".initiatebet") || message.Content.StartsWith(".resolvebet"))) //This ignores all non bot commands from bots
                return;

            //custom commands
            string messageCommand;

            if (_customMappings.ContainsKey(message.Content))
            {
                await message.Channel.SendMessageAsync(_customMappings[message.Content]);
            }

            //filtering messages begin here
            if (!message.Content.StartsWith('.')
                || message.Content == "."
                || message.Content == ".."
                || message.Content == "..."
                || message.Content == "....") //this is your prefix
                return;

            try
            {
                EnsureInputContainsOnlyValidCharacters(message.Content);
            }
            catch (BadInputException ex)
            {
                await message.Channel.SendMessageAsync($"{message.Author.Mention} {ex.Message}");
                return;
            }

            int lengthOfCommand;
            if (message.Content.Contains(' '))
                lengthOfCommand = message.Content.IndexOf(' ');
            else
                lengthOfCommand = message.Content.Length;

            messageCommand = message.Content.Substring(1, lengthOfCommand - 1).ToLower();


            //normal commands begin here
            Command commandToExecute = null;
            foreach (var command in _commands)
            {
                if (messageCommand.Equals(command.Name))
                {
                    commandToExecute = command;
                }
            }

            if (commandToExecute != null)
            {
                if (commandToExecute.RequiresAdmin && _adminIds.Contains(message.Author.Id) == false)
                {
                    await message.Channel.SendMessageAsync($"You not admin. FUCK OFF");
                    return;
                }

                try
                {
                    var args = ExtractArguments(message.Content.Substring(lengthOfCommand));
                    foreach (string arg in args)
                    {
                        if (arg.Length > _argCharLimit)
                        {
                            await message.Channel.SendMessageAsync($"{message.Author.Mention} u CAN NOTA RUN COMMAN D THAT LONGER THAN {_argCharLimit} CHARACTER OKAY???? U TRY BREAK ME??? HUH??? try again neck time... nothin pesonnel kiddo");
                            return;
                        }
                    }

                    //execute command
                    await commandToExecute.ExecuteAsync(client, message, args);

                    //only commands that move money count towards the raffle
                    if (_moneyCommands.Contains(commandToExecute.Name) && message.Channel is SocketGuildChannel)
                        _coinService.MarkActive(message.Author.Id, message.Channel.Id);
                }
                catch (Exception ex)
                {
                    string errorMsg = ex.Message.StartsWith("Exception of type") ? "Something went wrong." : ex.Message;

                    if (ex is BadInputException)
                    {
                        await message.Channel.SendMessageAsync($"{message.Author.Mention} {errorMsg}");
                        return;
                    }

                    if (ex is BadSyntaxException)
                    {
                        await message.Channel.SendMessageAsync($"{message.Author.Mention} {errorMsg}\nSyntax is {commandToExecute.Syntax}");
                        return;
                    }

                    throw;
                }
            }
            else
            {
                await message.Channel.SendMessageAsync($"I don't FUCKING know what this command does!");
            }

        }

        /// <summary>
        /// The minute loop. Each step is caught on its own, so one failing (a reminder for a deleted channel, a save that
        /// throws) never stops the rest of them, every minute, forever.
        /// </summary>
        public async Task RunBackgroundTasks(DiscordSocketClient client)
        {
            await Task.WhenAll(
                Safely("reminders", () => SendReminders(client)),
                Safely("interest", () => _coinService.AddInterest()),
                Safely("stock tick", () => _stockService.TickIfDue()),
                Safely("property tick", () => _propertyService.TickIfDue(_stockService.RiskyAssetShare)));

            //the .convo model loads in the background once after a start, and the first scrape of the server's history starts by itself
            await Safely("convo", () =>
            {
                _convoCorpus.EnsureLoadStarted();
                _convoCorpus.EnsureScrapeStarted(client);
                return Task.CompletedTask;
            });

            //redraw the live ticker threads after a new stock tick, in the background so slow uploads never hold up this loop
            _ = Safely("stock threads", () => _stockThreadService.RefreshIfDue(client));

            await Safely("settled bets", () => AnnounceSettledBets(client));

            //market headlines when a crash starts, bottoms out, recovers or ends, in the last few channels money was used in
            await Safely("market news", async () =>
            {
                foreach (var headline in _propertyService.TakeNews())
                    await PostToRecentChannels(client, headline);
            });

            await Safely("patch notes", () => AnnouncePatchNotesIfNew(client));

            //any spare cash goes straight to debt, then players with auto-sell on sell assets for what's left
            await Safely("debt", async () =>
            {
                await _coinService.RepayDebtsFromCash(_betManager.InitiatedBetUserIds);
                foreach (var line in await _debtService.AutoSellForDebts(_betManager.InitiatedBetUserIds))
                {
                    if (client.GetChannel(_coinService.GetAll().LastActiveChannelId) is IMessageChannel debtChannel)
                        await debtChannel.SendMessageAsync(line);
                }
            });

            //after interest and stock ticks, so the hourly sample reflects both
            await Safely("net worth history", () => _coinService.RecordNetWorthHistory(_worth.TotalWorth));
            await Safely("raffle", () => _raffleService.RunIfDue(client, _betManager.InitiatedBetUserIds));
            //kept power-ups go off by themselves the moment they can
            await Safely("kept power-ups", () => _raffleService.ActivateKeptAndAnnounce(client, _betManager.InitiatedBetUserIds));
        }

        private async Task Safely(string step, Func<Task> work)
        {
            try
            {
                await work();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background step {Step} failed: {Message}", step, ex.Message);
            }
        }

        /// <summary>Sends reminders that are due. One that can't be delivered (deleted channel, DM closed) is dropped, not retried forever.</summary>
        private async Task SendReminders(DiscordSocketClient client)
        {
            var reminders = await _reminderService.GetAll();
            foreach (var reminder in reminders.Where(r => DateTime.Now >= r.TimeToRemind).ToList())
            {
                try
                {
                    string reminderMessage = $"⏰ Oi {reminder.AuthorMention}, you told me to remind you: {reminder.Message}";
                    IMessageChannel channel = client.GetChannel(reminder.ChannelId) as IMessageChannel
                        ?? await client.GetDMChannelAsync(reminder.ChannelId) as IMessageChannel;
                    if (channel != null)
                        await channel.SendMessageAsync(reminderMessage);
                    else
                        _logger.LogWarning("Reminder {Id} dropped: channel {Channel} is gone", reminder.Id, reminder.ChannelId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Reminder {Id} couldn't be sent and was dropped", reminder.Id);
                }
                await _reminderService.Remove(reminder.Id);
            }
        }

        private void EnsureInputContainsOnlyValidCharacters(string input)
        {
            foreach (char c in input)
            {
                if ((Char.IsLetterOrDigit(c) 
                    || !Char.IsWhiteSpace(c)
                    || c != '.') == false)
                {
                    throw new BadInputException();
                }
            }
        }

        private List<string> ExtractArguments(string commandArgumentString)
        {
            //https://stackoverflow.com/a/14655199
            var result = commandArgumentString.Split('"')
                                 .Select((element, index) => index % 2 == 0  // If even index
                                                       ? element.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)  // Split the item
                                                       : new string[] { element })  // Keep the entire item
                                 .SelectMany(element => element).ToList();
            return result;
        }
    }
}
