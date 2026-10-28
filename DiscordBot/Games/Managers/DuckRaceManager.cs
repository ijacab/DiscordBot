using Discord;
using Discord.WebSocket;
using DiscordBot.Games.Models;
using DiscordBot.Managers;
using DiscordBot.Services;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DiscordBot.Games.Managers
{
    public class DuckRaceManager : BaseMultiplayerManager<DuckRace, DuckRacePlayer> //should be singleton
    {
        private const int MillisecondsBetweenTicks = 1500;

        public override string GameName => "Duck Race";
        public override string BaseCommand => "duck";
        public override string[] PlayCommands => new string[] { "start" };

        public DuckRaceManager(DiscordSocketClient client, BetManager betManager, CoinService coinService)
            : base(client, betManager, coinService)
        {
        }

        protected override void PostStartActions(DuckRace game, IEnumerable<DuckRacePlayer> players)
        {
            //players don't take any actions, so the race is decided as soon as it starts and the game ends right after
            game.RunRace();
            foreach (var player in players)
            {
                player.IsFinishedPlaying = true;
            }
        }

        /// <summary>
        /// Replays the race by editing one message per channel on every tick.
        /// </summary>
        protected override async Task AnimateGame(DuckRace game, IEnumerable<DuckRacePlayer> players)
        {
            var messages = new List<IUserMessage>();
            foreach (ulong channelId in players.Select(p => p.ChannelId).Distinct())
            {
                if (_client.GetChannel(channelId) is IMessageChannel channel)
                    messages.Add(await channel.SendMessageAsync(embed: BuildRaceEmbed(game, 0)));
            }

            for (int frame = 1; frame < game.Frames.Count; frame++)
            {
                await Task.Delay(MillisecondsBetweenTicks);
                var embed = BuildRaceEmbed(game, frame);
                foreach (var message in messages)
                    await message.ModifyAsync(m => m.Embed = embed);
            }
        }

        protected override string GetStartMessage(DuckRacePlayer player)
        {
            return string.Empty;
        }

        protected override string GetEndMessage(DuckRacePlayer player, string networthMessage)
        {
            string result = player.BaseWinnings > 0 ? "🏆" : "💀";
            return $"\n{result} {player.Username} (Duck #{player.DuckNumber})\n\t{networthMessage}";
        }

        private static Embed BuildRaceEmbed(DuckRace game, int frame)
        {
            bool isLastFrame = frame == game.Frames.Count - 1;
            int[] positions = game.Frames[frame];

            var lanes = Enumerable.Range(0, game.DuckCount).Select(i =>
            {
                bool isBot = game.IsBotDuck(i + 1);
                string name = isBot ? "Bot" : game.Players[i].Username;
                string duck = isBot ? "🤖" : "🦆";
                string label = i < 9 ? $"{i + 1}️⃣" : $"`{i + 1}`";
                string winner = isLastFrame && game.WinningDuck == i + 1 ? " 🏆" : string.Empty;
                return $"{label} {new string('~', positions[i])}{duck}{new string('~', DuckRace.TrackLength - positions[i])}🏁 **{name}**{winner}";
            });

            string status;
            if (isLastFrame)
            {
                string winnerName = game.IsBotDuck(game.WinningDuck) ? "The bot" : game.Players[game.WinningDuck - 1].Username;
                status = $"🏆 **{winnerName}** wins the race!";
            }
            else if (frame == 0)
                status = "🚦 Ready... set... GO!";
            else
                status = "🏁 They're off!";

            double payout = game.DuckCount * DuckRace.PayoutPerDuck;
            return new EmbedBuilder()
                .WithTitle("🦆 Duck Race")
                .WithDescription($"{string.Join("\n", lanes)}\n\n{status}")
                .WithFooter($"{game.DuckCount} ducks racing - winner's payout is {payout:0.##}x their bet")
                .WithColor(Color.Gold)
                .Build();
        }
    }
}
