﻿using Common.Helpers;
using Discord;
using Discord.WebSocket;
using DiscordBot.Exceptions;
using DiscordBot.Games.Models;
using DiscordBot.Games.Models.BattleArena;
using DiscordBot.Managers;
using DiscordBot.Models;
using DiscordBot.Services;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static DiscordBot.Models.CoinAccounts;

namespace DiscordBot.Games.Managers
{
    /// <summary>
    /// Face Off on one live dashboard per fight, with buttons. Fighters join a lobby (it starts 60 seconds after the second
    /// fighter joins), then every round each picks Attack, Defend, Focus or Gamble privately. Picks stay hidden until
    /// everyone has picked or the round's 30 seconds run out, then the dashboard reveals them and applies the damage.
    /// Spectators can back a fighter until round 1 resolves. The rules are in <see cref="BattleArena"/> and
    /// <see cref="SpectatorPool"/>; the base class holds the games, takes bets and pays out the results.
    /// Actions return (Ok, Message): a refusal, or a confirmation to show the player privately.
    /// </summary>
    public class BattleArenaManager : BaseMultiplayerManager<BattleArena, BattleArenaPlayer> //should be singleton
    {
        public const string ComponentGame = "fo";
        public override string GameName => "Face Off";
        public override string BaseCommand => "fo";
        public override string[] PlayCommands => new string[] { "attack", "defend", "focus", "gamble", "ability" };

        public static readonly TimeSpan RoundTime = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan LobbyStartDelay = TimeSpan.FromSeconds(60);
        public static readonly TimeSpan LoneLobbyTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan RevealPause = TimeSpan.FromSeconds(1.2);
        private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
        private static readonly BattleAction DefaultAction = BattleAction.Attack;

        /// <summary>A fight's dashboard and everything shown on it that isn't game state.</summary>
        private sealed class Table
        {
            public BattleArena Game;
            public LiveMessage Dashboard;
            public readonly SpectatorPool Pool = new SpectatorPool();
            public DateTime CreatedUtc;
            public DateTime? StartsAt;
            public DateTime RoundEndsAt;
            public List<RoundOutcome> LastOutcomes = new List<RoundOutcome>();
            public int LastRound;
            public readonly List<string> Notes = new List<string>();
            public string ResultText;
            public bool Changed = true;
        }

        private readonly ILogger<BattleArenaManager> _logger;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        //read without the lock by the button lookups, so it has to be safe to read while an action changes it
        private readonly ConcurrentDictionary<Guid, Table> _tables = new ConcurrentDictionary<Guid, Table>();
        private CancellationTokenSource _loop;

        public BattleArenaManager(DiscordSocketClient client, BetManager betManager, CoinService coinService, ILogger<BattleArenaManager> logger)
            : base(client, betManager, coinService)
        {
            _logger = logger;
        }

        protected override void PreEndActions(BattleArena game)
        {
            base.PreEndActions(game);
            int playerCount = game.Players.Count();
            var winners = game.Winners;
            var ranking = game.PlayerRanking;

            foreach (var player in game.Players)
            {
                CoinAccount account = _coinService.Get(player.UserId, player.Username).Result;
                if (account.BattlePerson == null)
                    continue;

                int place = ranking.IndexOf(player);
                if (winners.Contains(player))
                {
                    account.BattlePerson.Wins++;
                    account.BattlePerson.Level += playerCount > 2 ? 2 : 1;
                }
                else if (place == winners.Count && playerCount > 2)
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

        protected override string GetStartMessage(BattleArenaPlayer player) => null;

        protected override string GetEndMessage(BattleArenaPlayer player, string networthMessage)
        {
            return $"\n{player.Username}: {player.GetFormattedStanding()} HP\n\t{networthMessage}";
        }

        // ---------- lookups for the buttons (no lock: they only read, and every action re-checks under the lock) ----------

        public bool IsDashboard(Guid gameId, ulong messageId) => _tables.TryGetValue(gameId, out Table table) && table.Dashboard.Owns(messageId);
        public bool IsLive(Guid gameId) => _tables.ContainsKey(gameId);

        public Guid? GameOf(ulong userId)
        {
            return Snapshot(() => _tables.Values.FirstOrDefault(t => t.Game.Players.Any(p => p.UserId == userId))?.Game.GameGuid);
        }

        /// <summary>Living opponents with their HP, for the target picker.</summary>
        public List<(ulong Id, string Name, int Hp)> OpponentsOf(ulong userId)
        {
            return Snapshot(() =>
            {
                Table table = _tables.Values.FirstOrDefault(t => t.Game.Players.Any(p => p.UserId == userId));
                if (table == null)
                    return new List<(ulong, string, int)>();
                return table.Game.LivingPlayers.Where(p => p.UserId != userId).Select(p => (p.UserId, p.Username, Hp(p))).ToList();
            }) ?? new List<(ulong, string, int)>();
        }

        /// <summary>The fighters of a game, with the payout multiple a new backer would see, for the backing picker.</summary>
        public List<(ulong Id, string Name, double? Multiple)> FightersOf(Guid gameId)
        {
            return Snapshot(() =>
            {
                if (!_tables.TryGetValue(gameId, out Table table))
                    return new List<(ulong, string, double?)>();
                return table.Game.Players.Select(p => (p.UserId, p.Username, table.Pool.PayoutMultiple(p.UserId))).ToList();
            }) ?? new List<(ulong, string, double?)>();
        }

        /// <summary>
        /// Reads game state without the lock, for building a picker within Discord's 3 seconds. If an action changes the
        /// lists mid-read, it reads again; the action itself re-checks everything under the lock.
        /// </summary>
        private static T Snapshot<T>(Func<T> read)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    return read();
                }
                catch (InvalidOperationException)
                {
                    //a list changed while it was being read
                }
            }
            return default;
        }

        // ---------- actions ----------

        /// <summary>Joins the open lobby, or opens one with its dashboard in this channel.</summary>
        public async Task<(bool Ok, string Message)> Join(CoinAccount account, double bet, IMessageChannel channel, ulong guildId)
        {
            await _gate.WaitAsync();
            try
            {
                if (TryGetPlayer(account.UserId, out BattleArenaPlayer existing))
                    return (false, GetExisitingGame(account.UserId).Started ? "You're already in a fight." : "You're already in the lobby.");
                if (account.BattlePerson == null)
                    return (false, "You can't fight without a card. Type `.cardpull` to get one.");

                Table table = _tables.Values.FirstOrDefault(t => !t.Game.Started);
                if (table != null && table.Game.Players.Count >= BattleArena.MaxPlayers)
                    return (false, $"The lobby is full at {BattleArena.MaxPlayers} fighters. Wait for the next one.");

                try
                {
                    await _betManager.InitiateBet(account, bet, 10);
                }
                catch (BadInputException ex)
                {
                    return (false, ex.Message);
                }

                var player = new BattleArenaPlayer
                {
                    UserId = account.UserId,
                    ChannelId = channel.Id,
                    ServerId = guildId,
                    BetAmount = bet,
                    Username = account.Name,
                    CoinAccount = account
                };

                DateTime now = DateTime.UtcNow;
                if (table == null)
                {
                    var game = new BattleArena();
                    Games.Add(game);
                    table = new Table { Game = game, Dashboard = new LiveMessage(channel), CreatedUtc = now };
                    _tables[game.GameGuid] = table;
                }
                table.Game.Join(player);
                if (table.Game.Players.Count == 2)
                    table.StartsAt = now + LobbyStartDelay;
                table.Notes.Add($"{account.Name} joins with ${FormatHelper.GetCommaNumber(bet)}.");
                table.Changed = true;

                await Publish(table);
                StartLoop();
                string where = table.Dashboard.Channel.Id == channel.Id ? "" : $" The fight is in <#{table.Dashboard.Channel.Id}>.";
                return (true, $"You're in with ${FormatHelper.GetCommaNumber(bet)}.{where}");
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Leaves the lobby and gets the bet back.</summary>
        public Task<(bool Ok, string Message)> Leave(ulong userId)
        {
            return OnTableOf(userId, async table =>
            {
                if (table.Game.Started)
                    return (false, "The fight has started, so you can't leave.");

                BattleArenaPlayer player = table.Game.GetPlayer(userId);
                table.Game.Leave(userId);
                await _betManager.CancelBet(userId, player.Username, player.BetAmount);
                await Refund(table.Pool.RemoveFighter(userId).ToDictionary(b => b.BackerId, b => b.Amount));
                table.Notes.Add($"{player.Username} leaves the lobby.");
                if (table.Game.Players.Count < 2)
                    table.StartsAt = null;
                table.Changed = true;

                if (table.Game.Players.Count == 0)
                    await Close(table, "The lobby closed. Everyone got their money back.");
                return (true, "You left and got your bet back.");
            });
        }

        /// <summary>Any fighter in the lobby can start once there are two.</summary>
        public Task<(bool Ok, string Message)> Start(ulong userId)
        {
            return OnTableOf(userId, async table =>
            {
                if (table.Game.Started)
                    return (false, "The fight has already started.");
                if (table.Game.Players.Count < table.Game.MinimumRequiredPlayers)
                    return (false, "You need at least 2 fighters to start.");
                await BeginFight(table, DateTime.UtcNow);
                return (true, "The fight begins.");
            });
        }

        /// <summary>Locks in this round's pick. Attack and Gamble take an optional target; without one it's a random opponent.</summary>
        public Task<(bool Ok, string Message)> Pick(ulong userId, BattleAction action, ulong? targetId)
        {
            return OnTableOf(userId, async table =>
            {
                BattleArena game = table.Game;
                if (!game.Started)
                    return (false, "The fight hasn't started yet.");
                BattleArenaPlayer player = game.GetPlayer(userId);
                BattleArenaPlayer target = targetId.HasValue ? game.Players.FirstOrDefault(p => p.UserId == targetId.Value) : null;
                if (targetId.HasValue && target == null)
                    return (false, "That opponent isn't in this fight.");

                game.SubmitAction(player, action, RollD20, target);
                BattleArenaPlayer aimed = game.CurrentActions[player].Target;
                string on = aimed != null ? $" on {aimed.Username}" : "";
                table.Changed = true;

                if (game.IsReadyToResolve)
                    await Resolve(table);
                else
                    await Publish(table);
                return (true, $"You picked **{action}**{on}. It stays hidden until the round resolves.");
            });
        }

        public Task<(bool Ok, string Message)> UseAbility(ulong userId)
        {
            return OnTableOf(userId, async table =>
            {
                BattleArenaPlayer player = table.Game.GetPlayer(userId);
                string description = table.Game.UseAbility(player);
                CardAbility ability = player.CoinAccount?.BattlePerson?.Ability ?? CardAbility.None;
                table.Notes.Add($"✨ {player.Username} uses {ability}.");
                table.Changed = true;
                await Publish(table);
                return (true, $"You used {ability}: {description}");
            });
        }

        /// <summary>Backs a fighter with a stake, taken from cash now and paid back when the fight ends.</summary>
        public async Task<(bool Ok, string Message)> Back(CoinAccount backer, Guid gameId, ulong fighterId, double amount)
        {
            await _gate.WaitAsync();
            try
            {
                if (!_tables.TryGetValue(gameId, out Table table))
                    return (false, "That fight has ended.");
                if (amount > backer.NetWorth)
                    return (false, $"You only have ${FormatHelper.GetCommaNumber(backer.NetWorth)}.");

                var fighterIds = table.Game.Players.Select(p => p.UserId).ToList();
                string refusal = table.Pool.Back(backer.UserId, backer.Name, fighterId, amount, fighterIds);
                if (refusal != null)
                    return (false, refusal);

                backer.NetWorth -= Math.Round(amount, 2);
                await _coinService.Update(backer.UserId, backer.NetWorth, backer.Name);

                string fighter = table.Game.Players.First(p => p.UserId == fighterId).Username;
                table.Notes.Add($"🎟️ {backer.Name} backs {fighter} with ${FormatHelper.GetCommaNumber(amount)}.");
                table.Changed = true;
                await Publish(table);
                return (true, $"You backed {fighter} with ${FormatHelper.GetCommaNumber(amount)}. If they win, backers of the winner share everything backed on this fight.");
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Moves the dashboard of the player's fight to the bottom of its channel.</summary>
        public Task<(bool Ok, string Message)> Bump(ulong userId, IMessageChannel channel)
        {
            return OnTableOf(userId, async table =>
            {
                if (table.Dashboard.Channel.Id != channel.Id)
                    return (false, $"Your fight is in <#{table.Dashboard.Channel.Id}>.");
                await Publish(table, bump: true);
                return (true, null);
            });
        }

        /// <summary>Runs an action on the table of the player's game, under the lock.</summary>
        private async Task<(bool Ok, string Message)> OnTableOf(ulong userId, Func<Table, Task<(bool, string)>> action)
        {
            await _gate.WaitAsync();
            try
            {
                Table table = _tables.Values.FirstOrDefault(t => t.Game.Players.Any(p => p.UserId == userId));
                if (table == null)
                    return (false, "You're not in a Face Off. Join one with `.fo 1000` or the Join buttons.");
                try
                {
                    return await action(table);
                }
                catch (BadInputException ex)
                {
                    return (false, ex.Message);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        // ---------- the fight ----------

        private async Task BeginFight(Table table, DateTime now)
        {
            table.Game.Start();
            table.StartsAt = null;
            table.RoundEndsAt = now + RoundTime;
            table.Notes.Clear();
            table.Notes.Add("The fight begins. Pick your move.");
            table.Changed = true;
            await Publish(table);
        }

        /// <summary>Reveals the picks, waits a moment, then shows the damage. Ends the fight if it's over.</summary>
        private async Task Resolve(Table table)
        {
            BattleArena game = table.Game;
            var hpBefore = game.Players.ToDictionary(p => p, p => Hp(p));
            var picks = game.CurrentActions.ToDictionary(kv => kv.Key, kv => (kv.Value.Action, kv.Value.DiceRoll, kv.Value.Target));

            //frame 1: everyone's pick and roll, before the damage lands
            table.Changed = true;
            await Publish(table, reveal: (picks, hpBefore, null));
            await Task.Delay(RevealPause);

            int round = game.Round;
            table.LastOutcomes = game.ResolveRound();
            table.LastRound = round;
            if (round == 1)
                table.Pool.Close();
            var damage = game.Players.ToDictionary(p => p, p => Math.Max(0, hpBefore[p] - Hp(p)));

            if (game.IsOver)
            {
                await Finish(table, picks, damage);
                return;
            }

            //frame 2: the damage, then the next round's buttons
            table.Notes.Clear();
            table.RoundEndsAt = DateTime.UtcNow + RoundTime;
            table.Changed = true;
            await Publish(table, reveal: (picks, null, damage));
        }

        /// <summary>Pays the fighters (through the base results) and the backers, then shows the final dashboard.</summary>
        private async Task Finish(Table table, Dictionary<BattleArenaPlayer, (BattleAction, int, BattleArenaPlayer)> picks, Dictionary<BattleArenaPlayer, int> damage)
        {
            BattleArena game = table.Game;
            var winners = game.Winners;
            double pot = game.Players.Sum(p => p.BetAmount);

            var result = new StringBuilder();
            result.AppendLine(winners.Count == 1
                ? $"🏆 **{winners[0].Username}** wins ${FormatHelper.GetCommaNumber(pot)}."
                : $"🏆 {string.Join(" and ", winners.Select(w => $"**{w.Username}**"))} tie and split ${FormatHelper.GetCommaNumber(pot)}.");
            if (game.Round > BattleArena.MaxRounds && game.LivingPlayers.Count() > 1)
                result.AppendLine($"The fight went the full {BattleArena.MaxRounds} rounds, so the highest HP wins.");

            if (table.Pool.Backings.Count > 0)
            {
                var payouts = table.Pool.Settle(winners.Select(w => w.UserId).ToList());
                bool refunded = !table.Pool.Backings.Any(b => winners.Any(w => w.UserId == b.FighterId));
                await Refund(payouts);
                result.AppendLine(refunded ? "Nobody backed a winner, so every backer gets their stake back." : "**Backers paid:**");
                if (!refunded)
                    foreach (var payout in payouts.OrderByDescending(p => p.Value))
                        result.AppendLine($"{table.Pool.BackingOf(payout.Key).BackerName}: ${FormatHelper.GetCommaNumber(payout.Value)}");
            }
            table.ResultText = result.ToString();

            _tables.TryRemove(game.GameGuid, out _);
            try
            {
                await EndGameIfAllPlayersFinished(game.Players.First().UserId, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Face Off results failed: {Message}", ex.Message);
            }

            table.Changed = true;
            await Publish(table, reveal: (picks, null, damage), ended: true);
        }

        /// <summary>Ends a lobby that never fought: refunds everyone and shows why.</summary>
        private async Task Close(Table table, string why)
        {
            foreach (BattleArenaPlayer player in table.Game.Players.ToList())
                await _betManager.CancelBet(player.UserId, player.Username, player.BetAmount);
            await Refund(table.Pool.RefundAll());
            Games.Remove(table.Game);
            _tables.TryRemove(table.Game.GameGuid, out _);
            table.ResultText = why;
            table.Changed = true;
            await Publish(table, ended: true);
        }

        private async Task Refund(Dictionary<ulong, double> amounts)
        {
            foreach (var pay in amounts)
            {
                if (pay.Value <= 0)
                    continue;
                CoinAccount account = await _coinService.Get(pay.Key, null);
                account.NetWorth += pay.Value;
                await _coinService.Update(account.UserId, account.NetWorth, account.Name);
            }
        }

        private static int RollD20() => DiceRoller.RollDice(1, 20)[0];
        private static int Hp(BattleArenaPlayer player) => (int)Math.Max(0, Math.Round(player.HitPoints));

        // ---------- timers ----------

        private void StartLoop()
        {
            if (_loop != null)
                return;
            var cts = new CancellationTokenSource();
            _loop = cts;
            _ = Task.Run(() => RunLoop(cts.Token));
        }

        private async Task RunLoop(CancellationToken token)
        {
            using var timer = new PeriodicTimer(TickInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(token))
                {
                    await _gate.WaitAsync(token);
                    try
                    {
                        foreach (Table table in _tables.Values.ToList())
                            await Tick(table, DateTime.UtcNow);
                        if (_tables.Count == 0)
                        {
                            _loop = null;
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Face Off tick failed: {Message}", ex.Message);
                    }
                    finally
                    {
                        _gate.Release();
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>Starts lobbies whose timer ran out, closes lonely ones, and picks Attack for anyone who ran out of time.</summary>
        private async Task Tick(Table table, DateTime now)
        {
            BattleArena game = table.Game;
            if (!game.Started)
            {
                if (table.StartsAt.HasValue && now >= table.StartsAt.Value && game.Players.Count >= game.MinimumRequiredPlayers)
                    await BeginFight(table, now);
                else if (game.Players.Count < game.MinimumRequiredPlayers && now - table.CreatedUtc >= LoneLobbyTimeout)
                    await Close(table, $"Nobody else joined within {LoneLobbyTimeout.TotalMinutes} minutes, so the lobby closed and the bet was returned.");
                return;
            }

            if (now < table.RoundEndsAt)
                return;

            foreach (BattleArenaPlayer player in game.LivingPlayers.Where(p => !game.HasPicked(p)).ToList())
            {
                game.SubmitAction(player, DefaultAction, RollD20);
                table.Notes.Add($"⏰ {player.Username} ran out of time and attacks.");
            }
            await Resolve(table);
        }

        // ---------- the dashboard ----------

        private async Task Publish(Table table, bool bump = false,
            (Dictionary<BattleArenaPlayer, (BattleAction Action, int Roll, BattleArenaPlayer Target)> Picks,
             Dictionary<BattleArenaPlayer, int> HpOverride,
             Dictionary<BattleArenaPlayer, int> Damage)? reveal = null,
            bool ended = false)
        {
            if (!table.Changed && !bump)
                return;

            LiveMessage.Frame frame = table.Dashboard.NewFrame();
            string arenaUrl = frame.Add("faceoff", RenderArena(table, reveal, ended));
            Embed embed = BuildEmbed(table, reveal, ended).WithImageUrl(arenaUrl).Build();
            bool revealing = reveal.HasValue && reveal.Value.HpOverride != null;
            MessageComponent components = ended ? new ComponentBuilder().Build() : BuildButtons(table, disabled: revealing);

            bool published = bump
                ? await table.Dashboard.BumpAsync(frame, new[] { embed }, components)
                : await table.Dashboard.PublishAsync(frame, new[] { embed }, components);
            if (published)
                table.Changed = false;
        }

        private static byte[] RenderArena(Table table,
            (Dictionary<BattleArenaPlayer, (BattleAction Action, int Roll, BattleArenaPlayer Target)> Picks,
             Dictionary<BattleArenaPlayer, int> HpOverride,
             Dictionary<BattleArenaPlayer, int> Damage)? reveal, bool ended)
        {
            BattleArena game = table.Game;
            var winners = ended ? game.Winners : new List<BattleArenaPlayer>();
            string title = ended ? (winners.Count == 1 ? $"{winners[0].Username} WINS" : winners.Count > 1 ? "A TIE" : "FACE OFF OVER")
                : !game.Started ? $"FACE OFF LOBBY  {game.Players.Count} OF {BattleArena.MaxPlayers}"
                : reveal.HasValue && reveal.Value.HpOverride != null ? $"ROUND {game.Round}  REVEAL"
                : $"ROUND {game.Round} OF {BattleArena.MaxRounds}";

            var scene = new FaceOffScene
            {
                Title = title,
                Footer = ended ? "THANKS FOR PLAYING"
                    : !game.Started ? $"POT ${game.Players.Sum(p => p.BetAmount):N0}"
                    : "ATTACK BEATS FOCUS BEATS GAMBLE BEATS DEFEND BEATS ATTACK"
            };

            foreach (BattleArenaPlayer player in game.Players)
            {
                BattlePerson card = player.CoinAccount?.BattlePerson;
                int hp = reveal?.HpOverride != null && reveal.Value.HpOverride.TryGetValue(player, out int before) ? before : Hp(player);
                var view = new FaceOffFighterView
                {
                    Name = player.Username,
                    Level = card?.Level ?? 0,
                    Rarity = card?.Rarity ?? CardRarity.Common,
                    ArtPath = card?.FilePath,
                    Hp = hp,
                    MaxHp = (int)BattleArenaPlayer.MaxHitPoints,
                    Out = hp <= 0,
                    Winner = winners.Contains(player),
                    Status = winners.Contains(player) ? "WINNER"
                        : hp <= 0 ? "OUT"
                        : !game.Started ? $"${player.BetAmount:N0}"
                        : game.HasPicked(player) ? "PICKED"
                        : "THINKING"
                };
                if (reveal.HasValue && reveal.Value.Picks.TryGetValue(player, out var pick))
                {
                    view.RevealAction = pick.Action.ToString().ToUpperInvariant();
                    view.Roll = pick.Roll;
                    view.TargetName = pick.Target?.Username;
                }
                if (reveal?.Damage != null && reveal.Value.Damage.TryGetValue(player, out int taken))
                    view.DamageTaken = taken;
                scene.Fighters.Add(view);
            }
            return FaceOffRenderer.Render(scene).ToArray();
        }

        private static EmbedBuilder BuildEmbed(Table table,
            (Dictionary<BattleArenaPlayer, (BattleAction Action, int Roll, BattleArenaPlayer Target)> Picks,
             Dictionary<BattleArenaPlayer, int> HpOverride,
             Dictionary<BattleArenaPlayer, int> Damage)? reveal, bool ended)
        {
            BattleArena game = table.Game;
            double pot = game.Players.Sum(p => p.BetAmount);
            var embed = new EmbedBuilder().WithColor(ended ? Color.Gold : Color.DarkRed);

            if (ended)
            {
                embed.WithTitle("Face Off over");
            }
            else if (!game.Started)
            {
                embed.WithTitle($"Face Off lobby · pot ${FormatHelper.GetCommaNumber(pot)}");
                string when = table.StartsAt.HasValue
                    ? $"The fight starts <t:{new DateTimeOffset(table.StartsAt.Value).ToUnixTimeSeconds()}:R>, or when a fighter presses **Start**."
                    : "Waiting for a second fighter.";
                embed.WithDescription($"Join with your Battle Person card. Bets start at 10% of your net worth. Winner takes the pot. {when}");
            }
            else
            {
                embed.WithTitle($"Face Off · round {game.Round} of {BattleArena.MaxRounds} · pot ${FormatHelper.GetCommaNumber(pot)}");
                bool revealing = reveal.HasValue && reveal.Value.HpOverride != null;
                embed.WithDescription(revealing
                    ? "Picks revealed…"
                    : $"Pick your move. Picks stay hidden until everyone has picked, or <t:{new DateTimeOffset(table.RoundEndsAt).ToUnixTimeSeconds()}:R>. {BattleActionInfo.CounterRules}");
            }

            if (table.ResultText != null)
                embed.AddField("Result", table.ResultText);

            var fighters = new StringBuilder();
            foreach (BattleArenaPlayer player in game.Players)
            {
                string state = player.IsDead ? "💀 out"
                    : !game.Started ? $"${FormatHelper.GetCommaNumber(player.BetAmount)}"
                    : game.HasPicked(player) ? "✅ picked" : "⏳ thinking";
                double backed = table.Pool.TotalOn(player.UserId);
                string backing = backed > 0 ? $" · backed ${FormatHelper.GetCommaNumber(backed)} (x{table.Pool.PayoutMultiple(player.UserId):0.##})" : "";
                fighters.AppendLine($"**{player.Username}** · {Hp(player)} HP · {state}{backing}");
            }
            embed.AddField("Fighters", Trim(fighters.ToString(), 1000));

            if (table.LastOutcomes.Count > 0 && !(reveal.HasValue && reveal.Value.HpOverride != null))
            {
                var lines = new StringBuilder();
                foreach (RoundOutcome outcome in table.LastOutcomes)
                {
                    string target = outcome.Target != null ? $" → {outcome.Target.Username}" : "";
                    string roll = outcome.DiceRoll > 0 ? $" ({outcome.DiceRoll})" : "";
                    lines.Append($"**{outcome.Player.Username}** {Emoji(outcome.Action)} {outcome.Action}{target}{roll}: {outcome.Result}");
                    foreach (Hit hit in outcome.Hits)
                        lines.Append($" · {hit.Target.Username} -{hit.Damage:0}{(hit.Note != null ? $" ({hit.Note})" : "")}");
                    lines.AppendLine();
                }
                embed.AddField($"Round {table.LastRound}", Trim(lines.ToString(), 1000));
            }

            if (table.Notes.Count > 0)
                embed.AddField("Notes", Trim(string.Join("\n", table.Notes.TakeLast(6)), 1000));

            if (!ended && table.Pool.IsOpen)
                embed.WithFooter($"Watching? Back a fighter before round 1 resolves. Backed so far: ${FormatHelper.GetCommaNumber(table.Pool.Total)}");
            else if (table.Pool.Total > 0)
                embed.WithFooter($"Backed on this fight: ${FormatHelper.GetCommaNumber(table.Pool.Total)}");
            return embed;
        }

        private static MessageComponent BuildButtons(Table table, bool disabled)
        {
            string game = table.Game.GameGuid.ToString("N");
            var builder = new ComponentBuilder();
            if (!table.Game.Started)
            {
                builder.WithButton("Join 10%", Id("join", game, "10"), ButtonStyle.Success, new Emoji("🥊"), row: 0);
                builder.WithButton("Join 25%", Id("join", game, "25"), ButtonStyle.Success, row: 0);
                builder.WithButton("Join max", Id("join", game, "100"), ButtonStyle.Danger, row: 0);
                builder.WithButton("Custom bet", Id("joincustom", game), ButtonStyle.Secondary, new Emoji("✏️"), row: 0);
                builder.WithButton("Start", Id("start", game), ButtonStyle.Primary, new Emoji("▶️"), disabled: table.Game.Players.Count < 2, row: 1);
                builder.WithButton("Leave", Id("leave", game), ButtonStyle.Secondary, new Emoji("↩️"), row: 1);
                builder.WithButton("Back a fighter", Id("back", game), ButtonStyle.Secondary, new Emoji("🎟️"), row: 1);
                return builder.Build();
            }

            builder.WithButton("Attack", Id("pick", game, "attack"), ButtonStyle.Danger, new Emoji("⚔️"), disabled: disabled, row: 0);
            builder.WithButton("Defend", Id("pick", game, "defend"), ButtonStyle.Primary, new Emoji("🛡️"), disabled: disabled, row: 0);
            builder.WithButton("Focus", Id("pick", game, "focus"), ButtonStyle.Secondary, new Emoji("🧘"), disabled: disabled, row: 0);
            builder.WithButton("Gamble", Id("pick", game, "gamble"), ButtonStyle.Success, new Emoji("🎲"), disabled: disabled, row: 0);
            builder.WithButton("Ability", Id("ability", game), ButtonStyle.Secondary, new Emoji("✨"), disabled: disabled, row: 1);
            builder.WithButton("My card", Id("card", game), ButtonStyle.Secondary, new Emoji("🃏"), row: 1);
            if (table.Pool.IsOpen)
                builder.WithButton("Back a fighter", Id("back", game), ButtonStyle.Secondary, new Emoji("🎟️"), disabled: disabled, row: 1);
            return builder.Build();
        }

        private static string Id(string action, params string[] args) => ComponentId.Build(ComponentGame, action, args);

        private static string Emoji(BattleAction action)
        {
            switch (action)
            {
                case BattleAction.Attack: return "⚔️";
                case BattleAction.Defend: return "🛡️";
                case BattleAction.Focus: return "🧘";
                default: return "🎲";
            }
        }

        private static string Trim(string text, int max)
        {
            return text.Length <= max ? text : text.Substring(0, max - 1) + "…";
        }
    }
}
