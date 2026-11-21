using Common.Helpers;
using Discord;
using Microsoft.Extensions.Logging;
using Discord.WebSocket;
using DiscordBot.Exceptions;
using DiscordBot.Games.Models.Cards;
using DiscordBot.Models;
using DiscordBot.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using static DiscordBot.Models.CoinAccounts;

namespace DiscordBot.Managers
{
    /// <summary>
    /// Battle Person raids and the card's control panel: `.raid`, `.train`, `.stance`, `.guards`, `.card`, and their buttons.
    /// The rules are in <see cref="RaidService"/>, <see cref="RaidRules"/> and <see cref="CardLevels"/>.
    /// </summary>
    public partial class CommandManager
    {
        // ---- typed commands

        private async Task CardView(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            CoinAccount account = await _coinService.Get(message.Author.Id, message.Author.Username);
            if (args.Count >= 1 && args[0].ToLowerInvariant() == "stance")
            {
                if (args.Count < 2 || !RaidRules.TryParseStance(args[1], out RaidStance stance)) throw new BadSyntaxException();
                await message.Channel.SendMessageAsync(await StanceText(account, stance));
                return;
            }

            BattlePerson card = account.BattlePerson;
            if (card == null)
            {
                await message.SendRichEmbedMessage("No card", "You don't have a Battle Person yet. Type `.cardpull` to get one (your first pull each day is free).");
                return;
            }

            var embed = DiscordHelper.GetEmbedBuilder(card.Name, card.GetSummary() + "\n\n" + CardPanel(account, DateTime.UtcNow)).Build();
            if (!string.IsNullOrEmpty(card.FilePath) && File.Exists(card.FilePath))
            {
                using var image = File.OpenRead(card.FilePath);
                await message.Channel.SendFileAsync(stream: image, Path.GetFileName(card.FilePath), embed: embed, components: CardButtons(account));
            }
            else
            {
                await message.Channel.SendMessageAsync(embed: embed, components: CardButtons(account));
            }
        }

        private async Task Raid(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            var now = DateTime.UtcNow;
            CoinAccount attacker = await _coinService.Get(message.Author.Id, message.Author.Username);
            if (args.Count == 0)
            {
                await message.Channel.SendMessageAsync(embed: DiscordHelper.GetEmbedBuilder("⚔️ Raids", RaidOverview(attacker, now)).Build());
                return;
            }

            CoinAccount target = FindRaidTarget(message, args[0]);
            if (args.Count == 1)
            {
                var check = _raidService.Check(attacker, target, now);
                await message.Channel.SendMessageAsync(embed: DiscordHelper.GetEmbedBuilder($"⚔️ Raid {target.Name}?", RaidPreview(attacker, target, check)).Build(),
                    components: check.Refusal == null ? RaidStakeButtons(attacker, target, check) : null);
                return;
            }

            await PostRaid(attacker, target, args[1], message.Channel, now);
        }

        private async Task Train(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            CoinAccount account = await _coinService.Get(message.Author.Id, message.Author.Username);
            bool max = args.Count > 0 && (args[0].ToLowerInvariant() == "max" || args[0].ToLowerInvariant() == "all");
            await message.Channel.SendMessageAsync(await TrainText(account, max), components: CardButtons(account));
        }

        private async Task Stance(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            CoinAccount account = await _coinService.Get(message.Author.Id, message.Author.Username);
            if (args.Count == 0)
            {
                await message.Channel.SendMessageAsync($"Your raid stance is **{RaidRules.Describe(account.RaidStance)}**.\nChange it with `.stance guard`, `.stance counter` or `.stance hoard`.");
                return;
            }
            if (!RaidRules.TryParseStance(args[0], out RaidStance stance)) throw new BadSyntaxException();
            await message.Channel.SendMessageAsync(await StanceText(account, stance));
        }

        private async Task Guards(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            CoinAccount account = await _coinService.Get(message.Author.Id, message.Author.Username);
            await message.Channel.SendMessageAsync(await GuardsText(account, DateTime.UtcNow));
        }

        // ---- shared text and actions

        /// <summary>Runs a raid with a stake typed as an amount, a percentage of cash, min or max, and posts the result.</summary>
        private async Task PostRaid(CoinAccount attacker, CoinAccount target, string amountText, IMessageChannel channel, DateTime now)
        {
            if (_betManager.InitiatedBetUserIds.Contains(attacker.UserId)) throw new BadInputException("Finish your bet before raiding.");
            if (_betManager.InitiatedBetUserIds.Contains(target.UserId)) throw new BadInputException($"{target.Name} has a bet open. Try again when it's done.");

            var check = _raidService.Check(attacker, target, now);
            if (check.Refusal != null) throw new BadInputException(check.Refusal);
            double stake = ParseStake(amountText, attacker, check);

            var result = _raidService.Raid(attacker, target, stake, now);
            await _coinService.Update();

            var builder = DiscordHelper.GetEmbedBuilder(result.Won ? "⚔️ Raid succeeded" : "🛡️ Raid failed", RaidService.Describe(result));
            var revenge = result.Check.IsRevenge ? null : new ComponentBuilder()
                .WithButton($"🔥 Revenge on {Trim(attacker.Name, 40)}…", Id(MoneyComponents.Raid, "revenge", target.UserId.ToString(), attacker.UserId.ToString()), ButtonStyle.Danger)
                .Build();
            var mentions = new AllowedMentions { UserIds = new List<ulong> { target.UserId } };

            //a picture of the two cards, if it can be drawn; the text carries everything either way
            MemoryStream picture = null;
            try
            {
                picture = RaidRenderer.Render(RaidScene(result));
                builder.WithImageUrl("attachment://raid.png");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Couldn't draw the raid picture");
            }
            if (picture != null)
            {
                using (picture)
                    await channel.SendFileAsync(picture, "raid.png", $"<@{target.UserId}>", embed: builder.Build(), components: revenge, allowedMentions: mentions);
            }
            else
            {
                await channel.SendMessageAsync($"<@{target.UserId}>", embed: builder.Build(), components: revenge, allowedMentions: mentions);
            }
        }

        /// <summary>The picture for a raid result: both cards, the dice and the money.</summary>
        public static RaidScene RaidScene(RaidService.RaidResult r)
        {
            RaidSide Side(CoinAccount account, bool winner) => new RaidSide
            {
                Name = account.BattlePerson?.Name ?? account.Name,
                Kind = account.BattlePerson == null ? "NO CARD" : Archetypes.Name(account.BattlePerson.Archetype),
                Level = account.BattlePerson?.Level ?? 0,
                Rarity = account.BattlePerson?.Rarity ?? CardRarity.Common,
                Power = RaidRules.Power(account.BattlePerson),
                ArtPath = account.BattlePerson?.FilePath,
                Winner = winner,
            };
            return new RaidScene
            {
                Attacker = Side(r.Attacker, r.Won),
                Defender = Side(r.Target, !r.Won),
                Banner = r.Check.IsRevenge ? (r.Won ? "REVENGE IS SWEET" : "REVENGE FAILED") : (r.Won ? "RAID SUCCEEDED" : "RAID FAILED"),
                Won = r.Won,
                Needed = r.Needed,
                Roll = r.Roll,
                Footer = r.Won ? $"{RaidService.Money(r.Loot)} LOOT" : $"{RaidService.Money(r.Stake + r.ExtraLoss)} LOST",
            };
        }

        private static double ParseStake(string text, CoinAccount attacker, RaidService.RaidCheck check)
        {
            text = (text ?? "").Trim().ToLowerInvariant();
            double cash = Math.Max(0, attacker.NetWorth);
            if (text == "max" || text == "all") return Math.Floor(Math.Min(cash, check.MaxStake));
            if (text == "min") return check.MinStake;
            if (Money.TryParse(text, cash, out double amount)) return Math.Floor(amount);
            throw new BadInputException("That stake is gibberish. Use an amount like 5000, a share of your cash like 10%, min or max.");
        }

        /// <summary>A player to raid: a mention, an id, or the name on their account.</summary>
        private CoinAccount FindRaidTarget(SocketMessage message, string text)
        {
            var accounts = _coinService.GetAll().Accounts;
            ulong id = 0;
            var mentioned = message.MentionedUsers.FirstOrDefault();
            if (mentioned != null)
            {
                if (mentioned.IsBot) throw new BadInputException("Bots don't have anything to raid.");
                id = mentioned.Id;
            }
            else
            {
                var match = Regex.Match(text ?? "", @"^<@!?(\d+)>$");
                if (match.Success) ulong.TryParse(match.Groups[1].Value, out id);
                else ulong.TryParse(text, out id);
            }

            CoinAccount found = id != 0 ? accounts.FirstOrDefault(a => a.UserId == id) : null;
            if (found == null && id == 0)
            {
                string name = (text ?? "").Trim().TrimStart('@');
                found = accounts.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
                if (found == null)
                {
                    var starts = accounts.Where(a => a.Name != null && a.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (starts.Count == 1) found = starts[0];
                    else if (starts.Count > 1) throw new BadInputException($"More than one player starts with \"{name}\": {string.Join(", ", starts.Take(5).Select(a => a.Name))}. Use more of the name or @mention them.");
                }
            }
            if (found == null || found.UserId == _botId)
                throw new BadInputException("I can't find that player. @mention them, or use the name on the leaderboard.");
            return found;
        }

        private string RaidPreview(CoinAccount attacker, CoinAccount target, RaidService.RaidCheck check)
        {
            var card = target.BattlePerson;
            var lines = new List<string>
            {
                $"**You:** {RaidService.CardLabel(attacker)}, power {check.AttackPower:0}{(check.IsRevenge ? " with 🔥 revenge (+25%)" : "")}",
                $"**{target.Name}:** {RaidService.CardLabel(target)}, defends at {check.DefencePower:0}: {target.RaidStance}{(check.Guarded ? ", with guards" : "")}{(check.Underdog > 1 ? $", {RaidService.Pct(check.Underdog - 1)} harder for being richer than you" : "")}",
            };
            if (check.Matchup.Length > 0) lines.Add($"🔺 {check.Matchup}.");
            if (Archetypes.Normalise(attacker.BattlePerson?.Archetype ?? Archetype.None) == Archetype.Gambler)
                lines.Add($"🎰 Your Gambler's power swings {Archetypes.GamblerSwing * 100:0}% up or down on a coin flip when you raid, so the chance below is the middle of it.");
            lines.Add($"**Chance to win:** {RaidService.ChancePercent(check.Chance)}% (you need {RaidService.NeededRoll(check.Chance)}+ on a d100)");
            if (check.MaxStake >= check.MinStake)
            {
                double best = RaidRules.Loot(check.MaxStake, target.RaidStance, check.TargetWorth, Archetypes.LootMultiple(attacker.BattlePerson?.Archetype ?? Archetype.None, RaidRules.LootMultiple));
                lines.Add($"**Stake:** {RaidService.Money(check.MinStake)} to {RaidService.Money(check.MaxStake)}. At the most, a win takes {RaidService.Money(best)} from them (you keep 90%, 10% becomes a bounty on you).");
            }
            if (target.Bounty > 0) lines.Add($"💰 **Bounty on {target.Name}:** {RaidService.Money(target.Bounty)}, yours if you win.");
            if (target.RaidStance == RaidStance.Counter) lines.Add("⚠️ They're on Counter: if you lose, they also take your stake again from you.");
            if (check.Refusal != null) lines.Add($"\n❌ {check.Refusal}");
            else lines.Add($"\nPress a stake below, or type `.raid {target.Name} 5000` (or 10%, min, max).");
            return string.Join("\n", lines);
        }

        private MessageComponent RaidStakeButtons(CoinAccount attacker, CoinAccount target, RaidService.RaidCheck check)
        {
            string owner = attacker.UserId.ToString(), t = target.UserId.ToString();
            return new ComponentBuilder()
                .WithButton($"Min · {RaidService.Money(check.MinStake)}", Id(MoneyComponents.Raid, "go", owner, t, "min"), ButtonStyle.Secondary)
                .WithButton($"Max · {RaidService.Money(Math.Min(check.MaxStake, Math.Floor(Math.Max(0, attacker.NetWorth))))}", Id(MoneyComponents.Raid, "go", owner, t, "max"), ButtonStyle.Danger)
                .WithButton("Stake…", Id(MoneyComponents.Raid, "stake", owner, t), ButtonStyle.Primary)
                .Build();
        }

        private string RaidOverview(CoinAccount account, DateTime now)
        {
            var lines = new List<string>();
            _raidService.Check(account, account, now); //drops expired revenge and cooldowns
            int used = account.RecentRaidUtcTicks.Count;
            lines.Add($"**Raids left this hour:** {Math.Max(0, RaidRules.RaidsPerHour - used)} of {RaidRules.RaidsPerHour}");
            if (account.RevengeTokens.Count > 0)
                lines.Add("🔥 **Revenge ready on:** " + string.Join(", ", account.RevengeTokens.Select(r => $"{r.Name} ({RaidService.Wait(new DateTime(r.ExpiresUtcTicks) - now)} left)")));
            var bounties = _coinService.GetAll().Accounts.Where(a => a.Bounty > 0).OrderByDescending(a => a.Bounty).Take(5).ToList();
            if (bounties.Count > 0)
                lines.Add("💰 **Bounties:** " + string.Join(", ", bounties.Select(a => $"{a.Name} {RaidService.Money(a.Bounty)}")));
            if (account.RaidProtectedUntilUtcTicks > now.Ticks)
                lines.Add($"🛡️ You're protected from raids for {RaidService.Wait(new DateTime(account.RaidProtectedUntilUtcTicks) - now)}.");
            lines.Add($"Your stance: **{account.RaidStance}**. `.card` for your card's control panel.");
            lines.Add("\n`.raid @person` shows your odds, then `.raid @person 5000` (or 10%, min, max) raids them.");
            return string.Join("\n", lines);
        }

        /// <summary>The raid and levelling part of `.card`.</summary>
        private string CardPanel(CoinAccount account, DateTime now)
        {
            var card = account.BattlePerson;
            _raidService.Check(account, account, now); //drops expired revenge and cooldowns
            var lines = new List<string>();
            lines.Add(card.Level >= CardLevels.MaxLevel
                ? "**Training:** max level."
                : $"**Training:** {RaidService.Money(CardLevels.TrainCost(card))} for level {card.Level + 1} (`.train`, or `.train max`).");
            lines.Add($"**Raid power:** {RaidRules.AttackPower(card, null, false):0} attacking, {RaidRules.DefencePower(card, null, account.RaidStance, account.GuardsUntilUtcTicks > now.Ticks, 1):0} defending");
            lines.Add($"**Stance:** {RaidRules.Describe(account.RaidStance)}");
            lines.Add(account.GuardsUntilUtcTicks > now.Ticks
                ? $"**Guards:** on duty for {RaidService.Wait(new DateTime(account.GuardsUntilUtcTicks) - now)}"
                : $"**Guards:** none (`.guards` hires them for 6 hours: {RaidService.Money(_raidService.GuardsCost(account))})");
            if (account.RaidProtectedUntilUtcTicks > now.Ticks)
                lines.Add($"**Protected** from raids for {RaidService.Wait(new DateTime(account.RaidProtectedUntilUtcTicks) - now)}");
            if (account.Bounty > 0) lines.Add($"💰 **Bounty on you:** {RaidService.Money(account.Bounty)}");
            if (account.RevengeTokens.Count > 0)
                lines.Add("🔥 **Revenge ready on:** " + string.Join(", ", account.RevengeTokens.Select(r => r.Name)));
            var s = account.Stats;
            lines.Add($"**Raids:** won {s.RaidsWon} / lost {s.RaidsLost} · **defences:** won {s.DefencesWon} / lost {s.DefencesLost}" + (account.RaidStreak >= 2 ? $" · 🔥 {account.RaidStreak} raid wins in a row" : ""));
            return string.Join("\n", lines);
        }

        private MessageComponent CardButtons(CoinAccount account)
        {
            var card = account.BattlePerson;
            if (card == null) return null;
            string owner = account.UserId.ToString();
            var builder = new ComponentBuilder();
            bool max = card.Level >= CardLevels.MaxLevel;
            builder.WithButton(max ? "Max level" : $"Train · {RaidService.Money(CardLevels.TrainCost(card))}", Id(MoneyComponents.Card, "train", owner), ButtonStyle.Success, disabled: max, row: 0);
            builder.WithButton("Train max", Id(MoneyComponents.Card, "trainmax", owner), ButtonStyle.Success, disabled: max, row: 0);
            builder.WithButton($"Hire guards · {RaidService.Money(_raidService.GuardsCost(account))}", Id(MoneyComponents.Card, "guards", owner), ButtonStyle.Primary, row: 0);

            var stance = new SelectMenuBuilder().WithCustomId(Id(MoneyComponents.Card, "stance", owner)).WithPlaceholder($"Stance: {account.RaidStance}");
            foreach (RaidStance option in Enum.GetValues(typeof(RaidStance)))
                stance.AddOption(option.ToString(), option.ToString(), Trim(RaidRules.Describe(option), 100), isDefault: option == account.RaidStance);
            builder.WithSelectMenu(stance, 1);

            return builder.Build();
        }

        private async Task<string> TrainText(CoinAccount account, bool max)
        {
            int before = account.BattlePerson?.Level ?? 0;
            var (levels, cost) = _raidService.Train(account, max ? CardLevels.MaxLevel : 1);
            await _coinService.Update();
            var card = account.BattlePerson;
            string next = card.Level >= CardLevels.MaxLevel ? "Max level!" : $"Next level costs {RaidService.Money(CardLevels.TrainCost(card))}.";
            return $"🏋️ {account.Name} trained **{card.Name}** {before} → **{card.Level}** ({levels} level{(levels == 1 ? "" : "s")}) for {RaidService.Money(cost)}, into the jackpot. {next}";
        }

        private async Task<string> GuardsText(CoinAccount account, DateTime now)
        {
            double cost = _raidService.HireGuards(account, now);
            await _coinService.Update();
            return $"💂 {account.Name} hired guards for {RaidService.Money(cost)}, into the jackpot. On duty for {RaidService.Wait(new DateTime(account.GuardsUntilUtcTicks) - now)}: your card defends 50% harder against raids.";
        }

        private async Task<string> StanceText(CoinAccount account, RaidStance stance)
        {
            account.RaidStance = stance;
            await _coinService.Update();
            return $"🛡️ {account.Name}'s raid stance is now **{RaidRules.Describe(stance)}**.";
        }

        // ---- clicks

        private async Task CardComponent(SocketMessageComponent component, string action, string[] args)
        {
            if (!await IsOwner(component, args)) return;
            CoinAccount account = await _coinService.Get(component.User.Id, component.User.Username);
            switch (action)
            {
                case "keep":
                    if (args.Length < 2) break;
                    await component.DeferAsync();
                    await component.FollowupAsync(await KeepFromButton(account, args[1]));
                    return;
                case "train":
                case "trainmax":
                    await component.DeferAsync();
                    await component.FollowupAsync(await TrainText(account, action == "trainmax"));
                    MarkActiveFromInteraction(component);
                    return;
                case "guards":
                    await component.DeferAsync();
                    await component.FollowupAsync(await GuardsText(account, DateTime.UtcNow));
                    MarkActiveFromInteraction(component);
                    return;
                case "stance":
                {
                    string value = component.Data.Values?.FirstOrDefault();
                    if (!Enum.TryParse(value, out RaidStance stance)) break;
                    await component.DeferAsync(ephemeral: true);
                    await component.FollowupAsync(await StanceText(account, stance), ephemeral: true);
                    return;
                }
            }
            await ReplyPrivately(component, "That button doesn't do anything any more.");
        }

        private async Task RaidComponent(SocketMessageComponent component, string action, string[] args)
        {
            if (!await IsOwner(component, args)) return;
            if (args.Length < 2 || !ulong.TryParse(args[1], out ulong targetId))
            {
                await ReplyPrivately(component, "That button doesn't do anything any more.");
                return;
            }
            CoinAccount target = _coinService.GetAll().Accounts.FirstOrDefault(a => a.UserId == targetId);
            if (target == null)
            {
                await ReplyPrivately(component, "That player isn't playing any more.");
                return;
            }

            switch (action)
            {
                case "go":
                    if (args.Length < 3) break;
                    if (component.Channel == null) break;
                    await component.DeferAsync();
                    await PostRaid(await _coinService.Get(component.User.Id, component.User.Username), target, args[2], component.Channel, DateTime.UtcNow);
                    MarkActiveFromInteraction(component);
                    return;
                case "stake":
                case "revenge":
                    await component.RespondWithModalAsync(AmountModal(Id(MoneyComponents.Raid, "amt", args[0], targetId.ToString()),
                        Trim(action == "revenge" ? $"Revenge on {target.Name}" : $"Raid {target.Name}", 45), "Your stake", "5000, 10%, min or max"));
                    return;
            }
            await ReplyPrivately(component, "That button doesn't do anything any more.");
        }

        private async Task RaidModal(SocketModal modal, string action, string[] args)
        {
            if (action != "amt" || args.Length < 2 || !ulong.TryParse(args[0], out ulong ownerId) || !ulong.TryParse(args[1], out ulong targetId)
                || !ButtonOwner.Allows(ownerId, modal.User.Id) || modal.Channel == null)
            {
                await ReplyPrivately(modal, "That form doesn't do anything any more.");
                return;
            }
            CoinAccount target = _coinService.GetAll().Accounts.FirstOrDefault(a => a.UserId == targetId);
            if (target == null)
            {
                await ReplyPrivately(modal, "That player isn't playing any more.");
                return;
            }
            await modal.DeferAsync();
            string amount = modal.Data.Components.FirstOrDefault(c => c.CustomId == "amount")?.Value ?? "";
            await PostRaid(await _coinService.Get(modal.User.Id, modal.User.Username), target, amount, modal.Channel, DateTime.UtcNow);
            MarkActiveFromInteraction(modal);
        }
    }
}
