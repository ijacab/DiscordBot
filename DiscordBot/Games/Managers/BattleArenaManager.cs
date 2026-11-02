 using Common.Helpers;
using Discord.WebSocket;
using DiscordBot.Exceptions;
using DiscordBot.Games.Models;
using DiscordBot.Managers;
using DiscordBot.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static DiscordBot.Models.CoinAccounts;

namespace DiscordBot.Games.Managers
{
    public class BattleArenaManager : BaseMultiplayerManager<BattleArena, BattleArenaPlayer> //should be singleton
    {
        public override string GameName => "Face Off";
        public override string BaseCommand => "fo";
        public override string[] PlayCommands => new string[] { "roll numberOfDice", "ability" };

        private const int SecondsToForceAttackAfter = 30;
        private const int DefaultDice = 3;

        //one round timer per game, so a timer from an earlier round can't roll for the next one
        private readonly object _roundTimerLock = new object();
        private readonly Dictionary<Guid, CancellationTokenSource> _roundTimers = new Dictionary<Guid, CancellationTokenSource>();

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
            int playerCount = game.Players.Count();
            var ranking = game.PlayerRanking;

            foreach (var player in game.Players)
            {
                CoinAccount account = _coinService.Get(player.UserId, player.Username).Result;
                if (account.BattlePerson == null)
                    continue;

                int place = ranking.IndexOf(player);
                if (place == 0)
                {
                    account.BattlePerson.Wins++;
                    account.BattlePerson.Level += playerCount > 2 ? 2 : 1;
                }
                else if (place == 1 && playerCount > 2)
                {
                    account.BattlePerson.Level += 1; //runner up in a bigger fight still levels up
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
            return FormattedPlayerStanding(GetExisitingGame(player.UserId));
        }

        protected override string GetEndMessage(BattleArenaPlayer player, string networthMessage)
        {
            return $"\n{player.Username}: {player.GetFormattedStanding()}\n\t{networthMessage}";
        }

        private string FormattedPlayerStanding(BattleArena game)
        {
            string output = "";

            foreach (var baPlayer in game.PlayerRanking)
            {
                output += $"{baPlayer.Username}: {baPlayer.GetFormattedStanding()}{(baPlayer.IsDead ? " (out)" : "")}\n";
            }

            return output;
        }

        public async Task Roll(BattleArenaPlayer player, int numberOfDice, SocketMessage message, DiscordSocketClient client)
        {
            if (player.HasAttacked || player.IsDead)
                return;

            var game = GetExisitingGame(player.UserId);
            if (!game.Started)
            {
                await message.SendRichEmbedMessage("Not started", "Type `.fo start` to start the game first.");
                return;
            }

            game.RollDice(numberOfDice, player);
            if (!game.IsReadyToResolve)
            {
                await message.SendRichEmbedMessage($"{player.Username} has rolled", "Waiting on the other players to roll before resolving.");

                if (game.CurrentPlayerAttacks.Count == 1) //first roll of the round starts the clock
                    StartRoundTimer(game, message, client);
            }
            else
            {
                await ResolveRound(game, message);
            }
        }

        private async Task ResolveRound(BattleArena game, SocketMessage message)
        {
            CancelRoundTimer(game.GameGuid);

            int round = game.Round;
            string output = "";
            foreach (var attackInfo in game.ResolveRolls())
            {
                output += $"{attackInfo.PlayerAttacking.Username} rolled a {attackInfo.DiceRoll}\t (**{attackInfo.AttackType.ToString().SplitCamelCaseWithSpace()}**)\n";
                foreach (var attack in attackInfo.Attacks)
                {
                    output += $"\t{attack.PlayerAttacked.Username} took {attack.AttackDamage:0.0} dmg\n";
                }
                output += '\n';
            }

            await message.SendRichEmbedMessage($"Round {round} attacks", output);
            await message.SendRichEmbedMessage($"Player standings", FormattedPlayerStanding(game));

            if (!game.IsOver)
                await message.SendRichEmbedMessage($"Round {game.Round}", $"Type `.fo roll` to roll again. Use `.fo ability` to use your card's ability once per game.");

            await EndGameIfAllPlayersFinished(game.Players.First().UserId, message);
        }

        public async Task UseAbility(BattleArenaPlayer player, SocketMessage message)
        {
            var game = GetExisitingGame(player.UserId);
            string description = game.UseAbility(player);
            await message.SendRichEmbedMessage($"{player.Username} used their ability", description);
        }

        private void StartRoundTimer(BattleArena game, SocketMessage message, DiscordSocketClient client)
        {
            var cancellation = new CancellationTokenSource();
            lock (_roundTimerLock)
            {
                CancelRoundTimerLocked(game.GameGuid);
                _roundTimers[game.GameGuid] = cancellation;
            }

            _ = Task.Delay(TimeSpan.FromSeconds(SecondsToForceAttackAfter), cancellation.Token).ContinueWith(t =>
            {
                if (t.IsCanceled)
                    return;

                _ = ForceRoundRolls(game.GameGuid, message, client);
            });
        }

        /// <summary>Rolls the default number of dice for every living player who has not rolled yet this round.</summary>
        private async Task ForceRoundRolls(Guid gameGuid, SocketMessage message, DiscordSocketClient client)
        {
            try
            {
                if (!TryGetExisitingGame(gameGuid, out var game) || !game.Started)
                    return;

                foreach (var player in game.LivingPlayers.Where(p => !p.HasAttacked).ToList())
                {
                    if (!TryGetExisitingGame(gameGuid, out _)) //the game ended while rolling
                        return;

                    await message.SendRichEmbedMessage($"{player.Username} ran out of time", $"Rolling {DefaultDice} dice for them.");
                    await Roll(player, DefaultDice, message, client);
                }
            }
            catch (BadInputException)
            {
                //the game ended while the forced rolls were running
            }
        }

        private void CancelRoundTimer(Guid gameGuid)
        {
            lock (_roundTimerLock)
            {
                CancelRoundTimerLocked(gameGuid);
            }
        }

        private void CancelRoundTimerLocked(Guid gameGuid)
        {
            if (_roundTimers.TryGetValue(gameGuid, out var existing))
            {
                existing.Cancel();
                existing.Dispose();
                _roundTimers.Remove(gameGuid);
            }
        }
    }
}
