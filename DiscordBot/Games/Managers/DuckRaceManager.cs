using Discord.WebSocket;
using DiscordBot.Games.Models;
using DiscordBot.Managers;
using DiscordBot.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DiscordBot.Games.Managers
{
    public class DuckRaceManager : BaseMultiplayerManager<DuckRace, DuckRacePlayer> //should be singleton
    {
        public override string GameName => "Duck Race";
        public override string BaseCommand => "duck";
        public override string[] PlayCommands => new string[] { "start" };

        public DuckRaceManager(DiscordSocketClient client, BetManager betManager, CoinService coinService)
            : base(client, betManager, coinService)
        {
        }

        protected override void PostStartActions(DuckRace game, IEnumerable<DuckRacePlayer> players)
        {
            //players don't take any actions, so the race runs as soon as it starts and the game ends right after
            game.RunRace();
            foreach (var player in players)
            {
                player.IsFinishedPlaying = true;
            }
        }

        protected override string GetStartMessage(DuckRacePlayer player)
        {
            return string.Empty;
        }

        protected override string GetGameEndMessage(DuckRace game)
        {
            string lanes = string.Join("\n", Enumerable.Range(0, DuckRace.DuckCount).Select(i =>
            {
                int position = Math.Min(game.FinalPositions[i], DuckRace.TrackLength);
                return $"`{i + 1}` {new string('-', position)}🦆{new string('-', DuckRace.TrackLength - position)}🏁";
            }));

            return $"{lanes}\n\n🏆 **Duck #{game.WinningDuck}** wins the race!\n";
        }

        protected override string GetEndMessage(DuckRacePlayer player, string networthMessage)
        {
            return $"\n{player.Username} (Duck #{player.DuckNumber})\n\t{networthMessage}";
        }
    }
}
