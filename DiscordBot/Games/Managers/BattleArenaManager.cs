 using Common.Helpers;
using Discord.WebSocket;
using DiscordBot.Exceptions;
using DiscordBot.Games.Models;
using DiscordBot.Managers;
using DiscordBot.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static DiscordBot.Models.CoinAccounts;

namespace DiscordBot.Games.Managers
{
    public class BattleArenaManager : BaseMultiplayerManager<BattleArena, BattleArenaPlayer> //should be singleton
    {
        public override string GameName => "Face Off";
        public override string BaseCommand => "fo";
        public override string[] PlayCommands => new string[] { "roll numberOfDice" };

        private const int SecondsToForceAttackAfter = 30;

        public BattleArenaManager(DiscordSocketClient client, BetManager betManager, CoinService coinService)
            : base(client, betManager, coinService)
        {
            SecondsToForceEndAfter = 200;
        }

        protected override void PostStartActions(BattleArena game, IEnumerable<BattleArenaPlayer> players)
        {
            base.PostStartActions(game, players);
        }

        protected override void PreEndActions(BattleArena game)
        {
            base.PreEndActions(game);
            foreach (var player in game.Players)
            {
                CoinAccount account = _coinService.Get(player.UserId, player.Username).Result;

                if (game.PlayerRanking.IndexOf(player) == 0)
                {
                    account.BattlePerson.Wins++;

                    if (game.Players.Count() > 2)
                        account.BattlePerson.Level += 2;
                    else
                        account.BattlePerson.Level += 1;
                }
                else if (game.PlayerRanking.IndexOf(player) == 1 || game.DeathOrderList.IndexOf(player) == 0)
                {
                    if (game.Players.Count() > 2)
                        account.BattlePerson.Level += 1;
                    else
                        account.BattlePerson.Losses++;
                }
                else
                {
                    account.BattlePerson.Losses++;
                }

            }

            _coinService.Update().Wait();
        }


        protected override string GetStartMessage(ulong playerId, DiscordSocketClient client)
        {
            return FormattedPlayerStanding(playerId, _client);
        }

        protected override string GetEndMessage(BattleArenaPlayer player, string networthMessage)
        {
            return $"\n{player.Username}: {player.HitPoints}\n\t{networthMessage}";
        }

        private string FormattedPlayerStanding(ulong playerId, DiscordSocketClient client)
        {
            string output = "";
            var game = GetExisitingGame(playerId);

            foreach (var player in game.Players.Where(p => !p.IsDealer))
            {
                output += $"{client.GetUser(player.UserId).Username}: {player.HitPoints}\n";
            }

            return output;
        }

        public async Task Roll(ulong playerId, int numberOfDice, SocketMessage message, DiscordSocketClient client)
        {
            var game = GetExisitingGame(playerId);

            TryGetPlayer(playerId, out var player);

            var diceResults = game.RollDice(numberOfDice, player);
            string output = "";
            foreach (var diceResult in diceResults)
            {
                output += $"Rolled a {diceResult.DiceRoll}. *{diceResult.AttackType.ToString().SplitCamelCaseWithSpace()}*\n";
            }
            await EndGameIfAllPlayersFinished(playerId, client, message);
            await message.SendRichEmbedMessage($"{player.Username}'s dice rolls", output);

            game.PlayersWaitingToAttack.Remove(player);
            if(game.PlayersWaitingToAttack.Count == 0)
            {
                game.PlayersWaitingToAttack = new List<BattleArenaPlayer>(game.Players);
            }
            else if(game.PlayersWaitingToAttack.Count == 1)
            {
                //timer on ending the game
                _ = Task.Delay(TimeSpan.FromSeconds(SecondsToForceAttackAfter)).ContinueWith(async t =>
                {
                    var playerToForceAttack = game.PlayersWaitingToAttack.First();
                    await Roll(playerToForceAttack.UserId, 3, message, client);
                });
            }
        }

    }
}
