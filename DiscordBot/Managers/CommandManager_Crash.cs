using Discord;
using Discord.WebSocket;
using DiscordBot.Exceptions;
using DiscordBot.Games;
using DiscordBot.Games.Managers;
using DiscordBot.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using static DiscordBot.Models.CoinAccounts;

namespace DiscordBot.Managers
{
    /// <summary>`.crash`: the rocket game (<see cref="CrashGame"/>, <see cref="CrashManager"/>) and its buttons.</summary>
    public partial class CommandManager
    {
        private async Task GameCrash(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args.Count == 0)
            {
                await message.Channel.SendMessageAsync("🚀 **Crash**: `.crash 1000` launches a rocket with $1,000 on board. The multiplier climbs from 1.00x; "
                    + "press **Cash out** before it blows and you get your bet times the multiplier. Wait too long and it's gone. "
                    + $"`.crash 1000 2x` cashes out by itself at 2x. It tops out at {CrashGame.MaxMultiplier:0}x. "
                    + $"However you play it, it pays back {1 - CrashGame.HouseEdge:P0} on average, and {CrashGame.HouseEdge:P0} of rockets blow up on the pad.");
                return;
            }
            if (message.Channel is not SocketGuildChannel) throw new BadInputException("Crash is played in a server channel.");
            CoinAccount account = await _coinService.Get(message.Author.Id, message.Author.Username);
            if (!Money.TryParse(args[0], account.NetWorth, out double bet))
                throw new BadInputException("That's not a bet. `.crash 1000`, `.crash 10%` or `.crash max`.");
            double? target = null;
            if (args.Count > 1)
            {
                if (!CrashGame.TryParseTarget(args[1], out double t))
                    throw new BadInputException($"Auto cash-out is a multiplier like `2x`, from 1.01x to {CrashGame.MaxMultiplier:0}x.");
                target = t;
            }
            await _crashManager.Launch(account, bet, target, message.Channel);
        }

        private async Task CrashComponent(SocketMessageComponent component, string action, string[] args)
        {
            if (!await IsOwner(component, args)) return;
            switch (action)
            {
                case "out":
                    await component.DeferAsync();
                    if (!await _crashManager.CashOut(component.User.Id))
                        await ReplyPrivately(component, "That rocket's already gone.");
                    return;
                case "again":
                {
                    if (args.Length < 2 || !double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double bet) || !double.IsFinite(bet) || bet < 1 || component.Channel == null)
                        break;
                    double? target = args.Length > 2 && CrashGame.TryParseTarget(args[2], out double t) ? t : null;
                    await component.DeferAsync();
                    CoinAccount account = await _coinService.Get(component.User.Id, component.User.Username);
                    await _crashManager.Launch(account, Math.Min(bet, Math.Floor(account.NetWorth)), target, component.Channel);
                    MarkActiveFromInteraction(component);
                    return;
                }
            }
            await ReplyPrivately(component, "That button doesn't do anything any more.");
        }
    }
}
