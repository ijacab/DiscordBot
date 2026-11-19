using System;
using System.Collections.Generic;
using System.Text;

namespace DiscordBot.Models
{
    public class CoinAccountStats
    {
        //total/max money lost
        //        total/max money won
        //total/max money bet
        //games played
        //amount of bets won/lost
        //total/max amount donated
        //donation bonuses won
        //donations received
        //donation stats for each person to/from
        //winstreak stat
        public double TotalMoneyBet { get; set; }
        public double TotalMoneyWon { get; set; }
        public double TotalMoneyLost { get; set; }
        public double TotalMoneyDonated { get; set; }
        public double TotalMoneyReceivedFromDonations { get; set; }

        public double MaxMoneyBetAtOnce { get; set; }
        public double MaxMoneyWonAtOnce { get; set; }
        public double MaxMoneyLostAtOnce { get; set; }
        public double MaxMoneyDonatedAtOnce { get; set; }
        public double MaxMoneyReceivedFromDonationAtOnce { get; set; }

        public long MaxWinStreak { get; set; }
        public long MaxLossStreak { get; set; }
        public long CurrentWinStreak { get; set; }
        public long CurrentLossStreak { get; set; }

        public long GamesPlayed { get; set; }
        public long BetsWon { get; set; }
        public long BetsLost { get; set; }

        public double DonationBonusesEncountered { get; set; }
        public Dictionary<ulong, double> DonationAmountsToDict { get; set; } = new Dictionary<ulong, double>();
        public Dictionary<ulong, double> DonationAmountsFromDict { get; set; } = new Dictionary<ulong, double>();

        //---- tracked from here on (older accounts start counting from zero) ----

        /// <summary>Results by game name ("Roulette", "Blackjack", "Duck Race", "Slots", "Face Off").</summary>
        public Dictionary<string, GameRecord> Games { get; set; } = new Dictionary<string, GameRecord>();
        public double TotalBonusWinnings { get; set; }

        //first gamble of the day
        public long DailyRolls { get; set; }
        public long DailyStealsWon { get; set; }
        public double MoneyStolenByDailyRoll { get; set; }
        public long TimesRobbedByDailyRoll { get; set; }
        public double MoneyLostToDailyRoll { get; set; }

        //jackpot
        public long JackpotRolls { get; set; }
        public long JackpotsWon { get; set; }
        public double JackpotMoneyWon { get; set; }
        public long SlotJackpotsHit { get; set; }
        /// <summary>The 75% of lost bets that went into the pool.</summary>
        public double MoneyFedToJackpot { get; set; }

        //income
        public double InterestEarned { get; set; }
        public double DailyIncomeEarned { get; set; }

        //stocks
        public long StockBuys { get; set; }
        public long StockSells { get; set; }
        public double SharesBought { get; set; }
        public double SharesSold { get; set; }
        public double MoneySpentOnStocks { get; set; }
        public double StockSaleProceeds { get; set; }
        public double SellTaxPaid { get; set; }
        public double StockRealizedProfit { get; set; }
        public double BestStockSale { get; set; }
        public double WorstStockSale { get; set; }
        public long ShortsOpened { get; set; }
        public long ShortsCovered { get; set; }
        public double ShortProfit { get; set; }
        public long OptionContractsBought { get; set; }
        public double OptionPremiumsPaid { get; set; }
        public double OptionPayouts { get; set; }
        public long OptionsExpiredInTheMoney { get; set; }

        //property
        public long PropertiesBought { get; set; }
        public long PropertiesSold { get; set; }
        public double PropertySpent { get; set; }
        public double PropertySaleProceeds { get; set; }
        public double RentEarned { get; set; }
        public double HoldingCostsPaid { get; set; }

        //raffle and power-ups
        public long RafflesEntered { get; set; }
        public long RafflesWon { get; set; }
        public long PowerUpsUsed { get; set; }
        public long AttacksLanded { get; set; }
        public long AttacksSuffered { get; set; }
        public double MoneyTakenByPowerUps { get; set; }
        public double MoneyLostToPowerUps { get; set; }

        //debt
        public double PeakDebt { get; set; }
        public double DebtRepaid { get; set; }

        //dungeon
        public long DungeonRuns { get; set; }
        public double DungeonPayouts { get; set; }
        public double DungeonBuyInsLost { get; set; }

        //raids and card training
        public long RaidsWon { get; set; }
        public long RaidsLost { get; set; }
        public long DefencesWon { get; set; }
        public long DefencesLost { get; set; }
        /// <summary>Loot taken in raids won, and stakes and counter-hits won while defending.</summary>
        public double RaidMoneyWon { get; set; }
        /// <summary>Stakes and counter-hits lost on failed raids, and loot taken by raiders.</summary>
        public double RaidMoneyLost { get; set; }
        public double BountiesCollected { get; set; }
        public double CardTrainingSpent { get; set; }
        public double GuardsSpent { get; set; }
    }

    /// <summary>One game's results for one player.</summary>
    public class GameRecord
    {
        public long Played { get; set; }
        public long Won { get; set; }
        public long Lost { get; set; }
        public long Pushed { get; set; }
        public double MoneyBet { get; set; }
        /// <summary>Winnings less bets, bonuses included. Negative when behind.</summary>
        public double Net { get; set; }
        public double BestWin { get; set; }
        public double WorstLoss { get; set; }

        public void Add(double bet, double totalPaid)
        {
            double net = Math.Floor(totalPaid - bet);
            Played++;
            MoneyBet += bet;
            Net += totalPaid - bet;
            if (net > 0) { Won++; BestWin = Math.Max(BestWin, net); }
            else if (net < 0) { Lost++; WorstLoss = Math.Max(WorstLoss, -net); }
            else Pushed++;
        }
    }
}
