using Common.Helpers;
using Discord;
using Discord.WebSocket;
using DiscordBot.Exceptions;
using DiscordBot.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DiscordBot.Managers
{
    /// <summary>`.attacks`: a quick recap of the latest attacks on you (power-up hits, raids, the daily roll steal).</summary>
    public partial class CommandManager
    {
        private async Task Attacks(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            int count = AttackHistory.DefaultShown;
            if (args.Count > 1 || (args.Count == 1 && (!int.TryParse(args[0], out count) || count < 1)))
                throw new BadSyntaxException();
            count = Math.Min(count, AttackHistory.MaxKept);

            var account = await _coinService.Get(message.Author.Id, message.Author.Username);
            var now = DateTime.UtcNow;
            if (account.RecentAttacks.Count == 0)
            {
                await message.Channel.SendMessageAsync($"{message.Author.Mention} nobody's laid a finger on you. Yet.");
                return;
            }

            var lines = account.RecentAttacks.Take(count).Select(a => AttackHistory.Describe(a, now)).ToList();
            var (hits, net) = AttackHistory.Last24Hours(account, now);
            string summary = hits == 0
                ? "Nothing in the last 24 hours."
                : net > 0 ? $"Last 24 hours: {hits} attack{(hits == 1 ? "" : "s")}, **{Money.Format(net)}** lost in all."
                : net < 0 ? $"Last 24 hours: {hits} attack{(hits == 1 ? "" : "s")}, you came out **{Money.Format(-net)}** ahead. Good."
                : $"Last 24 hours: {hits} attack{(hits == 1 ? "" : "s")}, nothing lost.";
            lines.Add("");
            lines.Add(summary);
            if (account.RevengeTokens.Count > 0)
                lines.Add("🔥 Revenge raid ready on: " + string.Join(", ", account.RevengeTokens.Select(r => r.Name)) + " (`.raid name`)");
            await SendPagedEmbeds(message.Channel, $"🎯 Last {Math.Min(count, account.RecentAttacks.Count)} attacks on {account.Name}", lines);
        }
    }
}
