using AngleSharp;
using Common.Helpers;
using Common.Services;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using WebAlerter.Models;

namespace WebAlerter
{
    public class StrawmanChecker
    {
        private readonly ILogger<StrawmanChecker> _logger;
        private readonly HttpClient _client;
        private readonly GistService _gistService;
        private List<StrawmanTradesCollection> _savedTradesCollectionList;
        private const string _fileName = "strawman_trades.json";
        private const string _baseUrl = "https://strawman.com";

        private ulong _serverIdToSendTo = 887293150903828582;
        private ulong _channelIdToSendTo = 887293150903828585;
        private string[] _strawmanMembersToCheck = { "ArrowTrades", "Slats", "Wini", "CanadianAussie" };

        public StrawmanChecker(ILogger<StrawmanChecker> logger, HttpClient client, GistService gistService)
        {
            _logger = logger;
            _client = client;
            _gistService = gistService;
            _savedTradesCollectionList = GetAll().Result;
        }

        public async Task Run(DiscordSocketClient client)
        {
            foreach (string member in _strawmanMembersToCheck)
            {
                await NotifyTrades(member, client);
            }
        }

        public async Task NotifyTrades(string memberName, DiscordSocketClient client)
        {
            var newTradesCollection = await GetMemberTradesCollection(memberName);
            var newTrades = newTradesCollection.Trades;

            IEnumerable<StrawmanTrade> savedTrades = new Queue<StrawmanTrade>();
            IEnumerable<StrawmanTrade> notifyTrades = newTrades;

            if (_savedTradesCollectionList.Exists(c => c.MemberName == memberName)) //if there is already stored info for that member, we only need to notify on new trades
            {
                savedTrades = _savedTradesCollectionList.First(c => c.MemberName == memberName).Trades;
                notifyTrades = newTrades.Where(nt => savedTrades.Any(st => nt.Id == st.Id) == false //where this id doesn't exist in save trades, i.e. is new
                || (nt.Pending == false && savedTrades.FirstOrDefault(st => st.Id == nt.Id && st.Pending) != null) //or the trade is not pending but it was previously saved as pending
                );
            }

            var guilds = client.Guilds.ToList();
            await client.DownloadUsersAsync(guilds);

            try
            {
                var messageChannel = client.GetGuild(_serverIdToSendTo).GetTextChannel(_channelIdToSendTo);

                foreach (var notifyTrade in notifyTrades)
                {
                    string tradeType = notifyTrade.TradeType.ToString();
                    decimal tradeValue = Convert.ToInt32(notifyTrade.Value) == 0
                        ? notifyTrade.Volume * notifyTrade.Price
                        : notifyTrade.Value;

                    string title = $"Trade executed by {memberName}:";
                    string output =
                        $"Date: {notifyTrade.Date.ToString("dd/MM/yyyy")}\n" +
                        $"Company: {notifyTrade.Company.Code}:{notifyTrade.Company.StockExchangeCode} ({notifyTrade.Company.Name})\n" +
                        $"Trade: {tradeType} ${tradeValue.ToString("0.##")}\n" +
                        $"Volume: {notifyTrade.Volume}\n" +
                        $"Price: ${notifyTrade.Price}";

                    if (notifyTrade.Pending)
                        title = "**PENDING** " + title;

                    if (notifyTrade.TradeType == TradeType.Dividend)
                        title = "`DIVIDEND`\n" + title;

                    await messageChannel.SendRichEmbedMessage(title, output);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error while notifying about new trades: {ex.Message}");
            }


            //updating our local copy here
            var memberTrades = _savedTradesCollectionList.FirstOrDefault(l => l.MemberName == memberName);

            if (memberTrades == null)
            {
                _savedTradesCollectionList.Add(newTradesCollection);
            }
            else
            {
                int index = _savedTradesCollectionList.IndexOf(memberTrades);

                if (index != -1)
                    _savedTradesCollectionList[index] = newTradesCollection;
            }
            //ensure db copy is the same as local copy
            await UpdateContent(_savedTradesCollectionList);
        }

        private async Task<StrawmanTradesCollection> GetMemberTradesCollection(string memberName)
        {
            string su = Decode(EnvironmentHelper.GetEnvironmentVariable("S_U"));
            string sp = Decode(EnvironmentHelper.GetEnvironmentVariable("S_P"));

            var collection = new StrawmanTradesCollection()
            {
                MemberName = memberName,
                Trades = new Queue<StrawmanTrade>()
            };


            string cookieUrl = $"{_baseUrl}/users/login";
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, cookieUrl);
            var cookieDict = AddCookiesToRequest(request, cookieUrl);
            string body = $"csrf_test_name={cookieDict["csrf_cookie_name"]}&username={su}&password={sp}";

            request.Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");

            request.Method = HttpMethod.Post;

            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
            request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
            request.Headers.Add("User-Agent", "Pepsi-Dog-Bot");
            request.Headers.Add("Accept-Encoding", "gzip, deflate, br");
            request.Headers.Add("Accept-Language", "en-US,en;q=0.5");

            var response = await _client.SendAsync(request);
            var html = await _client.GetStringAsync($"{_baseUrl}/{memberName}/trades");
            Console.WriteLine(html);

            var config = Configuration.Default.WithDefaultLoader();
            var document = await BrowsingContext.New(config).OpenAsync(m => m.Content(html));

            var tradeBoxes = document.All.Where(m =>
                m.HasAttribute("class") &&
                m.GetAttribute("class").Contains("card rounded-lg shadow-center border-0 p-3 mb-3")
            );

            int id = tradeBoxes.Count();
            foreach (var tradeBox in tradeBoxes)
            {
                var dataBox = tradeBox.Children.Where(b => b.GetAttribute("class").Contains("row align-items-center")).First();

                var trade = new StrawmanTrade
                {
                    Id = id
                };

                foreach (var column in dataBox.Children)
                {
                    var regex = new Regex(@"([^\s]).*([^\s])");
                    var matches = regex.Matches(column.TextContent);

                    if (column.TextContent.Contains("Pending"))
                        trade.Pending = true;


                    if (matches.Count == 2)
                    {
                        var key = matches[0].Value;
                        var value = matches[1].Value;
                        trade = UpdateTradeDetails(trade, key, value);
                    }
                }

                collection.Trades.Enqueue(trade);
                id--;
            }

            return collection;
        }

        private StrawmanTrade UpdateTradeDetails(StrawmanTrade trade, string key, string value)
        {
            var companyRegex = new Regex(@"(\w+):(\w+)");

            if (key == "Date")
            {
                trade.Date = DateTimeOffset.Parse(value);
            }
            else if (companyRegex.IsMatch(value))
            {
                trade.Company = new Company()
                {
                    Code = companyRegex.Match(value).Groups[1].Value,
                    StockExchangeCode = companyRegex.Match(value).Groups[2].Value,
                    Name = key
                };
            }
            else if (key == "Volume")
            {
                trade.Volume = Convert.ToUInt64(value);
            }
            else if (key == "Trade")
            {
                if (value.Contains("sell"))
                    trade.TradeType = TradeType.Sell;
                else if (value.Contains("dividend"))
                    trade.TradeType = TradeType.Dividend;
                else
                    trade.TradeType = TradeType.Buy;

                trade.Trade = value;
            }
            else if (key == "Price")
            {
                trade.Price = Convert.ToDecimal(value.TrimStart('$'));
            }
            else if (key == "Value")
            {
                trade.Value = Convert.ToDecimal(value.TrimStart('-').TrimStart('$'));
            }

            return trade;
        }

        private Dictionary<string, string> AddCookiesToRequest(HttpRequestMessage request, string urlToGetCookiesFrom)
        {
            CookieContainer cookieJar = new CookieContainer();
            HttpWebRequest request1 = (HttpWebRequest)HttpWebRequest.Create(urlToGetCookiesFrom);
            request1.CookieContainer = cookieJar;
            HttpWebResponse response1 = (HttpWebResponse)request1.GetResponse();
            int cookieCount = cookieJar.Count;

            var cookies = cookieJar.GetCookies(new Uri(urlToGetCookiesFrom));
            string cookieStr = "";
            var cookieDict = new Dictionary<string, string>();
            foreach (Cookie cookie in cookies)
            {
                cookieDict.Add(cookie.Name, cookie.Value);
                cookieStr += $"{cookie.Name}={cookie.Value};";
            }
            cookieStr.TrimEnd(';');

            request.Headers.Add("Cookie", cookieStr);
            return cookieDict;
        }

        private async Task<List<StrawmanTradesCollection>> GetAll()
        {
            return await _gistService.GetContent<List<StrawmanTradesCollection>>(_fileName);
        }

        private async Task UpdateContent(List<StrawmanTradesCollection> tradeCollectionList)
        {
            string content = JsonConvert.SerializeObject(tradeCollectionList);
            await _gistService.UpdateContent(_fileName, content);
        }

        public static string Decode(string base64EncodedData)
        {
            var base64EncodedBytes = Convert.FromBase64String(base64EncodedData);
            return Encoding.UTF8.GetString(base64EncodedBytes);
        }
    }
}
