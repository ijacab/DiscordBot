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
            int position = string.IsNullOrEmpty(key) ? -1 : All.ToList().FindIndex(n => KeyOf(n) == key);
            return position <= 0 ? new List<PatchNote>() : All.Take(position).Take(max).ToList();
        }

        /// <summary>How many notes are newer than this key (for "and N more").</summary>
        public static int CountSince(string key)
        {
            int position = string.IsNullOrEmpty(key) ? -1 : All.ToList().FindIndex(n => KeyOf(n) == key);
            return Math.Max(0, position);
        }

        public static readonly IReadOnlyList<PatchNote> All = new List<PatchNote>
        {
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
