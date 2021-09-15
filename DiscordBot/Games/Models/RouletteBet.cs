using System;

namespace DiscordBot.Games.Models
{
    public class RouletteBet
    {
        public ulong UserId { get; }
        public int? BetNumberChoice { get; }
        public double Amount { get; }
        public BetType RoulleteBetType { get; }
        public int PayoutMultiple { get; }
        public RouletteBet(ulong userId, string betChoice, double amount)
        {
            UserId = userId;
            Amount = amount;

            if (int.TryParse(betChoice, out int number))
            {
                RoulleteBetType = BetType.Number;
                BetNumberChoice = betChoice == "00" ? -1 : number;
            }
            else if (Enum.TryParse(betChoice, ignoreCase: true, out BetType betTypeResult))
                RoulleteBetType = betTypeResult;
            else
                RoulleteBetType = BetType.NotValid;

            PayoutMultiple = GetPayoutMultiple(RoulleteBetType);
        }

        private int GetPayoutMultiple(BetType betType)
        {
            if (betType == BetType.Number) return 35;
            if (betType == BetType.Red) return 1;
            if (betType == BetType.Black) return 1;
            if (betType == BetType.FirstColumn) return 2;
            if (betType == BetType.SecondColumn) return 2;
            if (betType == BetType.ThirdColumn) return 2;
            if (betType == BetType.Odd) return 1;
            if (betType == BetType.Even) return 1;
            if (betType == BetType.FirstDozen) return 2;
            if (betType == BetType.SecondDozen) return 2;
            if (betType == BetType.ThirdDozen) return 2;

            return 0;
        }
    }

    public enum BetType
    {
        NotValid,
        Number,
        Red,
        Black,
        FirstColumn,
        SecondColumn,
        ThirdColumn,
        Odd,
        Even,
        FirstDozen,
        SecondDozen,
        ThirdDozen
    }

}
