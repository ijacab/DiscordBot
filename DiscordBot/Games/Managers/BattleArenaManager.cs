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


        protected override string GetStartMessage(BattleArenaPlayer player)
        {
            return FormattedPlayerStanding(player);
        }

        protected override string GetEndMessage(BattleArenaPlayer player, string networthMessage)
        {
            return $"\n{player.Username}: {player.HitPoints}\n\t{networthMessage}";
        }

        private string FormattedPlayerStanding(BattleArenaPlayer player)
        {
            string output = "";
            var game = GetExisitingGame(player.UserId);

            foreach (var baPlayer in game.Players.Where(p => !p.IsDealer))
            {
                output += $"{baPlayer.Username}: {baPlayer.HitPoints}\n";
            }

            return output;
        }

        public async Task Roll(BattleArenaPlayer player, int numberOfDice, SocketMessage message, DiscordSocketClient client)
        {
            if (player.HasAttacked == true)
                return;

            var game = GetExisitingGame(player.UserId);

            game.RollDice(numberOfDice, player);
            if (!game.IsReadyToResolve)
            {
                await message.SendRichEmbedMessage($"{player.Username} has rolled", "Waiting on the other players to roll before resolving.");

                if (game.CurrentPlayerAttacks.Count == game.Players.Count - 1)
                {
                    //timer on force rolling
                    _ = Task.Delay(TimeSpan.FromSeconds(SecondsToForceAttackAfter)).ContinueWith(async t =>
                    {
                        var playersThatHaveAttacked = game.CurrentPlayerAttacks.Keys.ToList();
                        var playersToForceAttack = game.Players.Except(playersThatHaveAttacked);
                        if (playersToForceAttack.Count() != 1)
                            throw new Exception($"{nameof(Roll)}: Something has gone wrong here. {nameof(playersToForceAttack)} should one have 1 item but has {playersToForceAttack.Count()} items.");

                        await Roll(playersToForceAttack.First(), 3, message, client);
                    });
                }
            }
            else
            {
                
                string output = "";
                var attackInfos = game.ResolveRolls();
                foreach (var attackInfo in attackInfos)
                {
                    output += $"{attackInfo.PlayerAttacking.Username} rolled a {attackInfo.DiceRoll}\t (**{attackInfo.AttackType.ToString().SplitCamelCaseWithSpace()}**)\n";
                    foreach (var attack in attackInfo.Attacks) 
                    {
                        output += $"\t{attack.PlayerAttacked} took {attack.AttackDamage} dmg\n";
                    }
                    output += '\n';
                }
                await message.SendRichEmbedMessage($"Attacks", output);
                await message.SendRichEmbedMessage($"Player standings", FormattedPlayerStanding(player));
                await EndGameIfAllPlayersFinished(player.UserId, message);
            }
        }

    }
}
