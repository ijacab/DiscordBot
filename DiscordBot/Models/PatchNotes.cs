using System;
using System.Collections.Generic;
using System.Linq;

namespace DiscordBot.Models
{
    public class PatchNote
    {
        public PatchNote(string date, string text)
        {
            Date = date;
            Text = text;
        }

        /// <summary>UTC date of the change, yyyy-MM-dd.</summary>
        public string Date { get; }
        public string Text { get; }
    }

    /// <summary>
    /// What changed, for the .patchnotes command. Newest first. Add a one-line entry for each significant change.
    /// </summary>
    public static class PatchNotes
    {
        /// <summary>Identifies a note across restarts, for remembering which ones were announced.</summary>
        public static string KeyOf(PatchNote note) => $"{note.Date}|{note.Text}";

        /// <summary>
        /// The notes newer than the one with this key, newest first, at most <paramref name="max"/>. Empty when the key is the
        /// newest note, or isn't in the list (a note was reworded), so a reworded note is never announced again.
        /// </summary>
        public static IReadOnlyList<PatchNote> NewSince(string key, int max)
        {
            int position = PositionOf(key);
            return position <= 0 ? new List<PatchNote>() : All.Take(position).Take(max).ToList();
        }

        /// <summary>How many notes are newer than this key (for "and N more").</summary>
        public static int CountSince(string key) => Math.Max(0, PositionOf(key));

        /// <summary>Where a key sits in <see cref="All"/>, or -1. A retired note's key sits where the note that replaces it as the marker does.</summary>
        private static int PositionOf(string key)
        {
            if (string.IsNullOrEmpty(key)) return -1;
            if (Retired.TryGetValue(key, out string anchor)) key = anchor;
            return All.ToList().FindIndex(n => KeyOf(n) == key);
        }

        /// <summary>
        /// Notes that were taken out ON PURPOSE because the changes they announced were undone (the owner wanted the old behaviour
        /// back): the $1,000 handout stays hourly, donations keep their 1-in-12 6x bonus and blackjack and duck race lobbies stay
        /// shared across channels. Never add them back. A server that had already announced one of them as its newest note carries
        /// that key as its marker, so each is mapped to the newest note still listed that is older than it, and only the notes
        /// above that one are announced.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> Retired = new Dictionary<string, string>
        {
            ["2026-11-20|Fixed: the \"daily\" $1,000 was paid every hour. It's now a $10,000 handout once a day, to anyone who played in the last 3 days."] =
                "2026-11-20|Closed exploits: NaN bets, parking cash in a dungeon to fake an all-in, broke players paying out debt they could never back, and the comeback bonus making betting free money.",
            ["2026-11-20|Donating no longer prints money: the 6x bonus is gone. Instead 1 donation in 12 is tax-free."] =
                "2026-11-20|Closed exploits: NaN bets, parking cash in a dungeon to fake an all-in, broke players paying out debt they could never back, and the comeback bonus making betting free money.",
            ["2026-11-20|Blackjack and duck race lobbies are per channel now and start themselves after 30 seconds."] =
                "2026-11-20|Calls and puts are now simple leveraged bets: `.call TECH 1000 5x 1h`. You see exactly what you win, where you break even and where you're wiped out. `.short` is gone (use a put).",
        };

        public static readonly IReadOnlyList<PatchNote> All = new List<PatchNote>
        {
            new PatchNote("2026-11-24", "New `.sug your idea` adds a suggestion to the master list. `.sug` on its own shows the whole list, and a bot admin can empty it with `.sug clear`."),
            new PatchNote("2026-11-23", "Raid results are shorter: the picture shows the cards and the dice, the text just has the roll, the money and one battle cry."),
            new PatchNote("2026-11-22", "`.convo` history scraping now resumes by itself after a restart, update or error instead of stopping half way, so the whole server history (and random samples of it) gets read."),
            new PatchNote("2026-11-22", "`.scrapeconvo status` now shows how many messages `.convo` really learned from and how old they are (date range, share from the last 30 days and year)."),
            new PatchNote("2026-11-22", "`.convo` is wilder: it now picks rarer follow-up words more often (a temperature setting) and goes off-script more, so the chats are less predictable."),
            new PatchNote("2026-11-22", "Revenge raids always work now, however rich either of you is, but you can only stake up to what they raided you with. Raid results show the d100 you needed and what you rolled."),
            new PatchNote("2026-11-21", "Face Off is gone: raids are the main fight now. Cards got simpler: one Power number, five archetypes (Brute, Guardian, Thief, Trickster, Gambler) with matchups, and funny names."),
            new PatchNote("2026-11-21", "Raids now come with a picture of both cards, battle cries, win streaks and archetype matchups: Brute beats Thief beats Guardian beats Brute. Every card pull also gives your card XP."),
            new PatchNote("2026-11-21", "`.convo` no longer leans on the newest messages while the history is still being read: it learns from a random sample across all of it."),
            new PatchNote("2026-11-20", "Houses earn far less: rent is 0.7% of the price an hour (was 3.5%) and upkeep 0.3% (was 1%). About 0.32% an hour after fees instead of 2.1%, so they're a slow hold, not a money printer."),
            new PatchNote("2026-11-20", "`.dbz` is fixed: it finds a working picture and posts it as an attachment, with more variety each time. `.img` does the same."),
            new PatchNote("2026-11-20", "Houses are bought and sold like stocks now: `.buy property` and `.sell property 1`. `.buyproperty` and `.sellproperty` still work."),
            new PatchNote("2026-11-20", "New `.attacks`: a recap of the latest attacks on you (power-up hits, raids, the daily roll steal), who did it and what it cost."),
            new PatchNote("2026-11-20", "`.pf` is simpler: one line per stock, and your houses as one summary with what they really earn an hour after fees and upkeep."),
            new PatchNote("2026-11-20", "`.cardpull` has a Keep button now. No more typing a long id."),
            new PatchNote("2026-11-20", "New game: `.crash 1000`. The rocket climbs, hit Cash out before it blows. Add `2x` to bail automatically. Pays back 97% however you play it."),
            new PatchNote("2026-11-20", "Closed exploits: NaN bets, parking cash in a dungeon to fake an all-in, broke players paying out debt they could never back, and the comeback bonus making betting free money."),
            new PatchNote("2026-11-20", "Calls and puts are now simple leveraged bets: `.call TECH 1000 5x 1h`. You see exactly what you win, where you break even and where you're wiped out. `.short` is gone (use a put)."),
            new PatchNote("2026-11-19", "Debt auto-sell now sells anything: after stocks it covers shorts and cashes out options worth something, then sells houses."),
            new PatchNote("2026-11-19", "Card levels: XP from raids and Face Off, `.train` buys levels, `.card focus` picks the stat that grows. Your level stays with a new card and resets each season."),
            new PatchNote("2026-11-19", "Raids! `.raid @person 5000`: win twice your stake from them, or lose it to them. Defend with `.stance` and `.guards`, take revenge, chase bounties. `.card` is your control panel."),
            new PatchNote("2026-11-19", ".property now shows today's crash risk: the more of everyone's wealth in stocks and houses, the likelier a crash."),
            new PatchNote("2026-11-19", "Jackpot: 3 rolls an hour each, down from 10."),
            new PatchNote("2026-11-19", "Words after .convo now set the topic: the chat starts with it and keeps coming back to it, e.g. `.convo pizza`."),
            new PatchNote("2026-11-18", ".convo is even more random now."),
            new PatchNote("2026-11-18", ".convo is wilder: more scrambling, and it no longer just repeats things people actually said word for word."),
            new PatchNote("2026-11-18", ".convo now leans on the two main chat channels: about 95% of its people and phrases come from them."),
            new PatchNote("2026-11-18", ".convo now gives everyone their own voice: each line is made only from that person's own messages, so it sounds like them."),
            new PatchNote("2026-11-18", ".convo is a bit more unhinged, and words after it (`.convo pizza`) now steer the chat without being repeated in it."),
            new PatchNote("2026-11-18", ".convo fix: no more robot essays. It now only posts \"user: words\" chats and starts reading the server's history by itself, so it works a few minutes after a restart."),
            new PatchNote("2026-11-18", ".convo now makes up 3 to 10 line chats between server members, learned from the server's own history, and it's deliberately unhinged. Add words to start it: `.convo pizza`."),
            new PatchNote("2026-11-18", "Jackpot: bets over $10,000 now qualify with half your cash or more (up to $10,000 it's still all your cash), and you get 10 rolls an hour, up from 2."),
            new PatchNote("2026-11-18", "Blackjack and duck race lobbies (and blackjack results) have a Join max button: one click bets all your cash, no form."),
            new PatchNote("2026-11-18", "Every power-up now goes off by itself: when won, or as soon as it can (a target appears, a freeze ends, a bet closes). No more `.pu use` needed."),
            new PatchNote("2026-11-18", "Auto-sell now keeps selling until your debt is fully cleared, instead of stopping a little short when its own sales pushed the price down."),
            new PatchNote("2026-11-18", "House prices have no limit now: they can rise or fall as far as they go, and the odd day moves them 10% to 30% or more either way."),
            new PatchNote("2026-11-18", "A property with squatters in can't be sold until they leave (12 hours), so selling and rebuying no longer shakes them off. Auto-sell skips it too."),
            new PatchNote("2026-11-18", "Market crashes now take stocks down by the stated amount. Before, they fell far further (a mild one could wipe out a high-risk stock) and some stocks even rose."),
            new PatchNote("2026-11-18", "Fixed the duck race freezing the bot while it ran: other commands now work during a race."),
            new PatchNote("2026-11-17", "You can now trade by percentage: `.buy MOONX 50%` uses half your cash, `.sell MOONX 50%` sells half your shares. Works for .short, .cover, .call and .put too."),
            new PatchNote("2026-11-16", "Calls can now pay up to 20x their strike (was 10x). Higher would make crypto calls dearer than the coin itself, so buying would always beat them."),
            new PatchNote("2026-11-16", "Crypto can now mega boom or crash 10x to 100,000x about every 2 days each way (min price 1 cent). Big trades move it much more. Calls are capped at 10x their strike."),
            new PatchNote("2026-11-16", "Sped up for a 2-4 week season: a crash now lasts a week, property booms/busts 4-8 days, bonds pay 0.1% an hour and house upkeep is 1% an hour with the rent."),
            new PatchNote("2026-11-16", "Crypto moons and crashes are now equally likely and equally big, so holding crypto is no longer a money machine and calls on it can beat buying."),
            new PatchNote("2026-11-16", "Calls and puts are priced by simulating how the stock really moves: much cheaper on tiers 3-5, dearer below the launch price, and a call pays at most 10x its strike."),
            new PatchNote("2026-11-16", "The bot posts the new patch notes in the last few active channels after an update."),
            new PatchNote("2026-11-16", "Market crashes (GFC) come about every 2 months, more often the more stocks and houses players hold (gold/bonds don't count), and can wipe stocks out up to 99%."),
            new PatchNote("2026-11-16", "The live stock thread is now one message with one image (all-time and 3-hour charts for every stock, like `.stocks`) instead of a message per stock."),
            new PatchNote("2026-11-15", "`.stats` now has nine pages (betting, per-game, stocks, property, jackpot, power-ups, dungeon, donations...) with a menu to switch. New counters start from today."),
            new PatchNote("2026-11-15", "Slot spins show their result instantly now, with no spinning animation."),
            new PatchNote("2026-11-15", "Blackjack's Play again button now deals a new game straight away with your last bet (no form). `.stocks` no longer graphs gold and bonds."),
            new PatchNote("2026-11-15", "New game: `.slots`, a slot machine with Spin / Bet / Max / Cash out buttons. Three 💎 win the jackpot (x100 to x2000 your bet)."),
            new PatchNote("2026-11-15", "75% of every lost bet (and dungeon buy-ins) now goes into the jackpot, but each player only gets 2 jackpot rolls an hour. Face Off is excluded."),
            new PatchNote("2026-11-15", "Blackjack is one table message now, updated as you play: the lobby, the hands with Hit/Stay, then the results. Typed `.bj` commands are tidied away."),
            new PatchNote("2026-11-15", "Buttons: Buy/Sell on `.stocks` and trades, Sell menus and auto-sell on `.portfolio`, Use on `.pu` and raffles, Buy a house on `.property`, Join/Start/Hit/Stay in blackjack and duck race."),
            new PatchNote("2026-11-15", "Nerfs: Safety Net now covers your next losing bet only, and Hot Streak boosts your next 5 winning bets instead of lasting 3 hours."),
            new PatchNote("2026-11-15", "Fixed `.buy max` / `.short max` freezing the whole bot for players with billions. Holdings top out at about 2.1 billion shares per stock, and the bot now restarts itself if it ever hangs."),
            new PatchNote("2026-11-14", "One money action now enters you in every power-up draw for the next 6 hours, not just the next one."),
            new PatchNote("2026-11-14", "`.stocksthread` is bot admins only now, and the existing ticker threads were deleted."),
            new PatchNote("2026-11-14", "`.stocksthread` recent tickers now cover the last 3 hours instead of 1."),
            new PatchNote("2026-11-14", "`.stocksthread` opens a thread with live tickers for every stock: each over all time, then each over the last hour, updated every 5 minutes. `.stocksthread stop` ends it."),
            new PatchNote("2026-11-13", "Debt now grows 10% every hour. Pay it off fast, or turn on `.autosell`."),
            new PatchNote("2026-11-13", "Only houses now: apartments are gone, and any you owned were bought back at full market price. `.buyproperty` buys a house."),
            new PatchNote("2026-11-13", "`.networth` takes a time window like `.stocks`: `.nw 48h`, `.nw @person 7d`, `.nw all 24h`."),
            new PatchNote("2026-11-13", "Debt you pay off goes into the jackpot."),
            new PatchNote("2026-11-12", "Debt now grows 1% every hour. Pay it off or turn on `.autosell`."),
            new PatchNote("2026-11-12", "`.portfolio` shows the rent you earn per hour."),
            new PatchNote("2026-11-12", "`.portfolio` groups your houses and apartments into one line each, with the average you paid."),
            new PatchNote("2026-11-12", "The raffle draws every hour among everyone who used money in the hour before, and power-ups go off the moment they're won."),
            new PatchNote("2026-11-12", "Squatters take one property (1st place's best rental), not all their rent."),
            new PatchNote("2026-11-12", "`.autosell on` sells your stocks and then property automatically to pay off debt."),
            new PatchNote("2026-11-12", "The raffle draws every 30 minutes, and lower on the leaderboard you draw more attacks. Every power-up is about twice as strong."),
            new PatchNote("2026-11-12", "Catch-up power-ups that nothing blocks: Tax the Rich, Audit, Bailout, Squatters and Leech. The rich pay in debt if they're short on cash."),
            new PatchNote("2026-11-12", "New power-ups: Mirror, Golden Parachute, Toll Booth, Time Machine, Echo, Copycat, Scratchie, Rigged Raffle, Heist, Musical Chairs, Sticky Fingers and Freeze Ray."),
            new PatchNote("2026-11-11", "Every property is rented out automatically, including ones you already own. `.letout` and `.unlet` are gone."),
            new PatchNote("2026-11-10", "`.lang` mangles harder: 8 hops through far-flung languages like Yoruba, Khmer and Latin."),
            new PatchNote("2026-11-10", "Face Off is reworked: buttons, a live arena with card art, hidden picks, pick your target, a 20 is always best, and spectators can back a fighter."),
            new PatchNote("2026-11-10", "Dungeon has buttons and a live dashboard. You crawl floor by floor, and at each boss door you fight, sneak past or drink a potion. Bosses are on floors 5 and 10 again."),
            new PatchNote("2026-11-10", "The bot runs on .NET 10 and the latest Discord.Net. Nothing should change in play."),
            new PatchNote("2026-11-09", "`max` works for calls, puts, shorts, sells and covers: it buys or sells as much as you can afford, or everything you hold."),
            new PatchNote("2026-11-09", "Rent is paid hourly: 3.5% of a house's market price per hour, 4% for an apartment, before the agency and vacancy."),
            new PatchNote("2026-11-09", "Tiers 1 and 2 are safe havens only (bonds and gold). Tiers 3-6 hold the ordinary stocks. Holders of the removed stocks are paid out at current value."),
            new PatchNote("2026-11-09", "`.stocks` lists gold (🥇) and bonds (🏦) in their own Safe havens section, and their graphs say what they are."),
            new PatchNote("2026-11-09", "Rare market crash (about once every 2 years): stocks and property fall for weeks, then slowly recover. Gold rises in a crash, and bonds pay a steady yield."),
            new PatchNote("2026-11-09", "Red Shell, Lightning Bolt, Robin Hood and Hostile Takeover take from players above you. `.pu` shows whether you're in the next raffle."),
            new PatchNote("2026-11-09", "`.portfolio` lists all your assets: cash, stocks, property, debt and total."),
            new PatchNote("2026-11-09", "Property says \"rented out\" instead of \"let out\"."),
            new PatchNote("2026-11-09", "`.help` is split across several messages, so it no longer fails on Discord's size limit."),
            new PatchNote("2026-11-09", "Debt is paid off automatically from any spare cash every minute. `.repay` is removed."),
            new PatchNote("2026-11-09", "Stock and property sales pay debt first. Blue Shell and Pickpocket take cash first, then put any shortfall on debt."),
            new PatchNote("2026-11-09", "Houses and apartments: buy with cash, rent out for daily rent after an agency cut. Prices follow one shared market index."),
            new PatchNote("2026-11-08", "Power-ups that take net worth are no longer capped."),
            new PatchNote("2026-11-08", "5-hourly raffle: anyone who used money in the last 24h is entered and wins a power-up. `.pu` lists and uses them."),
            new PatchNote("2026-11-08", "Crypto stock bursts into moons and crashes, and rebounds after a crash."),
            new PatchNote("2026-11-08", "Shorts, call and put options, one stock per risk tier, and graph time windows."),
            new PatchNote("2026-11-08", "Jackpot: an all-in `max` bet with all your money in cash and no stocks can win the pool."),
            new PatchNote("2026-11-07", "Stock trades move the price, with a 1% spread and a 2% sell tax that goes to the jackpot."),
            new PatchNote("2026-11-06", "Dungeon: blocks of 10 floors with checkpoints, solo runs and PvP.")
        };
    }
}
