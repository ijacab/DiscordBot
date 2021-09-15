using System;
using System.Collections.Generic;
using System.Text;

namespace WebAlerter.Models
{
    public class StrawmanTradesCollection
    {
        public string MemberName { get; set; }
        public Queue<StrawmanTrade> Trades;
    }

    public class StrawmanTrade
    {
        public int Id { get; set; }
        public DateTimeOffset Date { get; set; }
        public Company Company { get; set; }
        public string Trade { get; set; }
        public ulong Volume { get; set; }
        public decimal Price { get; set; }
        public TradeType TradeType { get; set; }
        public decimal Value { get; set; }
        public bool Pending { get; set; } = false;
    }

    public class Company
    {
        public string Name { get; set; }
        public string Code { get; set; }
        public string StockExchangeCode { get; set; }
    }

    public enum TradeType
    {
        Buy,
        Sell,
        Dividend
    }

}
