using Xunit;

//Many tests build a CoinService or StockService on the real save files (coinaccounts.json, stockmarket.json) in the working
//directory. Running them in parallel made one test read a file another was half way through writing, so they run one at a time.
//The whole suite takes a couple of seconds.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
