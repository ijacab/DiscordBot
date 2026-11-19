using Microsoft.Extensions.Logging;
using Discord;
using Discord.WebSocket;
using DiscordBot.Exceptions;
using DiscordBot.Games;
using DiscordBot.Games.Models;
using Common.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using static DiscordBot.Models.CoinAccounts;
using System.IO;
using Newtonsoft.Json;
using Common.Services;
using System.Reflection;
using DiscordBot.Models;
using DiscordBot.Services;
using DiscordBot.Text;
using DiscordBot.Games.Models.BattleArena;

namespace DiscordBot.Managers
{
    /// <summary>The raffle's power-ups: `.pu`, the catalog and using one.</summary>
    public partial class CommandManager
    {

        private async Task PowerUp(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            var now = DateTime.UtcNow;
            CoinAccount account = await _coinService.Get(message.Author.Id, message.Author.Username);
            account.Prune(now);

            if (args.Count >= 1 && args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                await SendPagedEmbeds(message.Channel, PowerUpCatalogTitle, PowerUpCatalogLines());
                return;
            }

            if (args.Count >= 1 && args[0].Equals("use", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Count != 2 || !int.TryParse(args[1], out int number))
                    throw new BadSyntaxException();
                await message.Channel.SendMessageAsync(await UsePowerUpText(account, message.Author.Mention, number, now));
                return;
            }

            if (args.Count != 0) throw new BadSyntaxException();

            //ten held power-ups with their descriptions can pass Discord's 2000 characters, so long lists go in parts
            var parts = SplitForMessages(PowerUpView(account, now));
            for (int i = 0; i < parts.Count; i++)
                await message.Channel.SendMessageAsync(parts[i], components: i == parts.Count - 1 ? PowerUpButtons(account) : null);
        }

        private const string PowerUpCatalogTitle = "Power-ups the raffle can give";

        private static List<string> PowerUpCatalogLines()
        {
            var lines = new List<string>();
            foreach (var (category, heading) in new[] { (PowerUpCategory.Boost, "✨ **Boosts** (anyone can draw these)"),
                (PowerUpCategory.Attack, "⚔️ **Attacks** (not drawn by the top of the leaderboard; Shield, Mirror and Golden Parachute work against them)"),
                (PowerUpCategory.CatchUp, "🧾 **Catch-up** (only for players below the top, much more common when 1st runs away; nothing blocks them)") })
            {
                lines.Add(heading);
                lines.AddRange(PowerUpCatalog.All.Where(p => p.Category == category)
                    .Select(p => $"**{p.Name}** ({PowerUpCatalog.RarityOf(p)}): {p.Description}"));
            }
            return lines;
        }

        /// <summary>Uses the <paramref name="number"/>th kept power-up and returns the public line, for `.pu use N` and the Use buttons.</summary>
        private async Task<string> UsePowerUpText(CoinAccount account, string mention, int number, DateTime now)
        {
            EnsureNotBetting(account.UserId, "using a power-up");
            string result = await _raffleService.Use(account, number, now, new Random().NextDouble);
            return $"{mention} {result}";
        }

        /// <summary>What `.pu` shows: raffle entry, kept power-ups, debuffs and what's running.</summary>
        private static string PowerUpView(CoinAccount account, DateTime now)
        {
            string output = "";
            string untilDraw = FormatRemaining(RaffleService.NextRaffleUtc(now) - now);
            int draws = RaffleService.EnteredDrawCount(account, now);
            output += draws > 0
                ? $"🎟️ **You're entered** in the raffle in {untilDraw}" + (draws > 1 ? $" and the {draws - 1} after it" : "") +
                    $" (until {FormatRemaining(RaffleService.EntryEndsUtc(account) - now)} from now). Any money action enters you for the next 6 hours.\n"
                : $"🎟️ **You're not entered** in the raffle in {untilDraw}. Do something with money (bets, trades, donations) to be entered in every draw for the next 6 hours.\n";
            if (account.HeldPowerUps.Count == 0)
                output += "You have no power-ups.\n";
            else
            {
                output += "**Kept power-ups** (these couldn't go off when won; each goes off by itself as soon as it can, or press Use / `.pu use <number>` to try now):\n";
                for (int i = 0; i < account.HeldPowerUps.Count; i++)
                {
                    var held = account.HeldPowerUps[i];
                    var power = PowerUpCatalog.Find(held.Id);
                    output += $"{i + 1}. **{power?.Name ?? held.Id}**: {power?.Description} (expires in {FormatRemaining(new DateTime(held.ExpiresAtTicks) - now)})\n";
                }
            }

            var debuffs = account.ActivePowerUps.Where(a => PowerUpCatalog.Find(a.Id)?.Category == PowerUpCategory.Debuff).ToList();
            if (debuffs.Count > 0)
            {
                output += "**Debuffs on you:**\n";
                foreach (var debuff in debuffs)
                {
                    var power = PowerUpCatalog.Find(debuff.Id);
                    output += $"- {power.Name}: {power.Description} ({FormatRemaining(new DateTime(debuff.ExpiresAtTicks) - now)} left)\n";
                }
            }

            var running = account.ActivePowerUps.Except(debuffs).ToList();
            if (running.Count > 0)
            {
                output += "**Running now:**\n";
                foreach (var active in running)
                {
                    var power = PowerUpCatalog.Find(active.Id);
                    string left = active.ChargesLeft == ActivePowerUp.Unlimited
                        ? FormatRemaining(new DateTime(active.ExpiresAtTicks) - now) + " left"
                        : $"{active.ChargesLeft} use(s) left";
                    output += $"- {power?.Name ?? active.Id} ({left})\n";
                }
            }

            output += $"Next raffle in {untilDraw}.";
            return output;
        }

        /// <summary>Splits text into messages under Discord's 2000 characters, on line breaks.</summary>
        private List<string> SplitForMessages(string text)
        {
            var parts = new List<string>();
            var part = new System.Text.StringBuilder();
            foreach (string line in text.Split('\n'))
            {
                if (part.Length + line.Length + 1 > _messageCharLimit - 50)
                {
                    parts.Add(part.ToString());
                    part.Clear();
                }
                part.Append(line).Append('\n');
            }
            if (part.Length > 0)
                parts.Add(part.ToString());
            return parts;
        }
    }
}
