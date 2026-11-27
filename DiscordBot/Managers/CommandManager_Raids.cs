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
    /// Raids on property: `.raid` and `.guards`, and the raid buttons (pick a property, revenge).
    /// The rules are in <see cref="RaidService"/>, <see cref="RaidRules"/> and <see cref="CardLevels"/>.
    /// </summary>
    public partial class CommandManager
    {
        // ---- typed commands

        /// <summary>`.raid` (revenge and bounties), `.raid @person` (their property and your odds on each), `.raid @person N` (rob property N).</summary>
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
                var (text, menu) = RaidPreview(attacker, target, now);
                await message.Channel.SendMessageAsync(embed: DiscordHelper.GetEmbedBuilder($"⚔️ Rob {target.Name}?", text).Build(), components: menu);
                return;
            }
            if (args.Count != 2 || !int.TryParse(args[1].TrimStart('#'), out int number))
                throw new BadInputException($"Raids rob a property now, so pick one by number: `.raid {target.Name}` lists what they own, then `.raid {target.Name} 1`.");
            if (number < 1 || number > target.Properties.Count)
                throw new BadInputException(target.Properties.Count == 0 ? $"{target.Name} owns no property, so there's nothing to rob." : $"{target.Name} has {target.Properties.Count} propert{(target.Properties.Count == 1 ? "y" : "ies")}. `.raid {target.Name}` lists them.");
            await PostRaid(attacker, target, target.Properties[number - 1].Id, message.Channel, now);
        }

        private async Task Guards(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            CoinAccount account = await _coinService.Get(message.Author.Id, message.Author.Username);
            await message.Channel.SendMessageAsync(await GuardsText(account, DateTime.UtcNow));
        }

        // ---- shared text and actions

        /// <summary>Robs one of the target's properties and posts the result.</summary>
        private async Task PostRaid(CoinAccount attacker, CoinAccount target, string propertyId, IMessageChannel channel, DateTime now)
        {
            if (_betManager.InitiatedBetUserIds.Contains(attacker.UserId)) throw new BadInputException("Finish your bet before raiding.");
            if (_betManager.InitiatedBetUserIds.Contains(target.UserId)) throw new BadInputException($"{target.Name} has a bet open. Try again when it's done.");

            var result = _raidService.Raid(attacker, target, propertyId, now);
            await _coinService.Update();

            var builder = DiscordHelper.GetEmbedBuilder(result.Won ? "⚔️ Robbed" : "🛡️ Raid failed", RaidService.Describe(result));
            var revenge = result.Check.IsRevenge ? null : new ComponentBuilder()
                .WithButton($"🔥 Revenge on {Trim(attacker.Name, 40)}…", Id(MoneyComponents.Raid, "revenge", target.UserId.ToString(), attacker.UserId.ToString()), ButtonStyle.Danger)
                .Build();
            var mentions = new AllowedMentions { UserIds = new List<ulong> { target.UserId } };

            //a picture of the crew against the property, if it can be drawn; the text carries everything either way
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

        /// <summary>The picture for a raid result: the crew's lead card against the property and the owner's lead card, and the dice.</summary>
        public static RaidScene RaidScene(RaidService.RaidResult r)
        {
            var c = r.Check;
            var lead = c.Crew.FirstOrDefault();
            var guard = c.Defenders.FirstOrDefault();
            return new RaidScene
            {
                Attacker = new RaidSide
                {
                    Name = lead?.Name ?? r.Attacker.Name,
                    Kind = $"CREW OF {c.Crew.Count}",
                    Level = lead?.Level ?? 0,
                    Rarity = lead?.Rarity ?? CardRarity.Common,
                    Power = c.AttackPower,
                    ArtPath = lead?.FilePath,
                    Winner = r.Won,
                },
                Defender = new RaidSide
                {
                    Name = guard?.Name ?? r.Target.Name,
                    Kind = PropertyCatalog.DisplayName(c.Property.Kind),
                    Level = guard?.Level ?? 0,
                    Rarity = guard?.Rarity ?? CardRarity.Common,
                    Power = c.DefencePower,
                    ArtPath = guard?.FilePath,
                    Winner = !r.Won,
                },
                Banner = c.IsRevenge ? (r.Won ? "REVENGE IS SWEET" : "REVENGE FAILED") : (r.Won ? "ROBBED" : "RAID FAILED"),
                Won = r.Won,
                Needed = r.Needed,
                Roll = r.Roll,
                Footer = r.Won ? $"{RaidService.Money(r.Loot)} LOOT" : $"{RaidService.Money(r.Stake)} LOST",
            };
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

        /// <summary>How many of the target's properties the preview lists (the menu holds at most 25).</summary>
        private const int MaxRaidTargets = 15;

        /// <summary>The target's properties with your odds, loot and stake on each, and a menu to rob one.</summary>
        private (string Text, MessageComponent Menu) RaidPreview(CoinAccount attacker, CoinAccount target, DateTime now)
        {
            var overall = _raidService.Check(attacker, target, null, now);
            var lines = new List<string>
            {
                $"**You:** {RaidService.GangLabel(attacker, now)}{(overall.IsRevenge ? $" with 🔥 revenge (+{RaidRules.RevengeBonus:0%}, ignores the cops)" : "")}",
                $"**{target.Name}:** gang defence {RaidRules.TotalPower(overall.Defenders):0}{(overall.Guarded ? $", 💂 guards (x{RaidRules.GuardsMultiplier:0.#})" : "")}" +
                    (overall.PunchDown > 1 ? $", {RaidService.Pct(overall.PunchDown - 1)} harder because you're richer" : ""),
            };
            if (target.Bounty > 0) lines.Add($"💰 **Bounty on {target.Name}:** {RaidService.Money(target.Bounty)}, yours if you win.");
            if (overall.Refusal != null)
            {
                lines.Add($"\n❌ {overall.Refusal}");
                return (string.Join("\n", lines), null);
            }

            lines.Add("");
            var menu = new SelectMenuBuilder().WithCustomId(Id(MoneyComponents.Raid, "pick", attacker.UserId.ToString(), target.UserId.ToString())).WithPlaceholder("Rob a property…");
            for (int i = 0; i < target.Properties.Count && i < MaxRaidTargets; i++)
            {
                var property = target.Properties[i];
                var check = _raidService.Check(attacker, target, property, now);
                string label = PropertyCatalog.Label(property.Kind);
                if (check.Refusal != null)
                {
                    lines.Add($"`{i + 1}` {label} · 🔒{check.Security:0} · ❌ {check.Refusal}");
                    continue;
                }
                lines.Add($"`{i + 1}` {label} · 🔒{check.Security:0} · **{RaidService.ChancePercent(check.Chance)}%** · loot {RaidService.Money(check.Loot)} · stake {RaidService.Money(check.Stake)}");
                menu.AddOption(Trim($"{i + 1}. {PropertyCatalog.DisplayName(property.Kind)} · {RaidService.ChancePercent(check.Chance)}% · loot {RaidService.Money(check.Loot)}", 100), property.Id,
                    Trim($"Stake {RaidService.Money(check.Stake)}, kept by {target.Name} if you fail", 100));
            }
            if (target.Properties.Count > MaxRaidTargets) lines.Add($"…and {target.Properties.Count - MaxRaidTargets} more (`.raid {target.Name} N`).");
            lines.Add($"\nLoot is {RaidRules.LootHours:0} hours of the place's profit; you stake half of it. A robbed place is watched for {RaidRules.PropertyProtection.TotalHours:0} hours. " +
                $"Your crew is your best {GangRules.CrewSize} rested cards, and they lie low for {GangRules.RestTime.TotalHours:0} hour after a job. Pick below or type `.raid {target.Name} N`.");
            return (string.Join("\n", lines), menu.Options.Count == 0 ? null : new ComponentBuilder().WithSelectMenu(menu).Build());
        }

        private string RaidOverview(CoinAccount account, DateTime now)
        {
            var lines = new List<string>();
            _raidService.Check(account, account, null, now); //drops expired revenge and cooldowns
            lines.Add($"**Raids left this hour:** {Math.Max(0, RaidRules.RaidsPerHour - account.RecentRaidUtcTicks.Count)} of {RaidRules.RaidsPerHour} · **your crew:** {RaidService.GangLabel(account, now)}");
            if (account.RevengeTokens.Count > 0)
                lines.Add("🔥 **Revenge ready on:** " + string.Join(", ", account.RevengeTokens.Select(r => $"{r.Name} ({RaidService.Wait(new DateTime(r.ExpiresUtcTicks) - now)} left)")));
            var bounties = _coinService.GetAll().Accounts.Where(a => a.Bounty > 0).OrderByDescending(a => a.Bounty).Take(5).ToList();
            if (bounties.Count > 0)
                lines.Add("💰 **Bounties:** " + string.Join(", ", bounties.Select(a => $"{a.Name} {RaidService.Money(a.Bounty)}")));
            var watched = account.Properties.Where(p => p.RaidProtectedUntilUtcTicks > now.Ticks).ToList();
            if (watched.Count > 0)
                lines.Add($"🚓 {watched.Count} of your properties {(watched.Count == 1 ? "is" : "are")} watched by the cops after a robbery.");
            lines.Add("\nRaids rob a property: `.raid @person` lists theirs with your odds, then pick one. Stocks and cash can't be robbed. `.gang` for your cards.");
            return string.Join("\n", lines);
        }

        private async Task<string> GuardsText(CoinAccount account, DateTime now)
        {
            double cost = _raidService.HireGuards(account, now);
            await _coinService.Update();
            return $"💂 {account.Name} hired guards for {RaidService.Money(cost)}, into the jackpot. On duty for {RaidService.Wait(new DateTime(account.GuardsUntilUtcTicks) - now)}: your property defends {RaidRules.GuardsMultiplier - 1:0%} harder against raids.";
        }

        // ---- clicks

        private async Task RaidComponent(SocketMessageComponent component, string action, string[] args)
        {
            if (action == "go" || action == "stake")
            {
                await ReplyPrivately(component, "Raids rob a property now. `.raid @person` lists what they own.");
                return;
            }
            if (!await IsOwner(component, args)) return;
            if (args.Length < 2 || !ulong.TryParse(args[1], out ulong targetId) || component.Channel == null)
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
            CoinAccount attacker = await _coinService.Get(component.User.Id, component.User.Username);
            switch (action)
            {
                case "pick":
                {
                    string propertyId = component.Data.Values?.FirstOrDefault();
                    if (string.IsNullOrEmpty(propertyId)) break;
                    await component.DeferAsync();
                    await PostRaid(attacker, target, propertyId, component.Channel, DateTime.UtcNow);
                    MarkActiveFromInteraction(component);
                    return;
                }
                case "revenge":
                {
                    var (text, menu) = RaidPreview(attacker, target, DateTime.UtcNow);
                    await component.RespondAsync(embed: DiscordHelper.GetEmbedBuilder($"🔥 Revenge on {target.Name}", text).Build(), components: menu, ephemeral: true);
                    return;
                }
            }
            await ReplyPrivately(component, "That button doesn't do anything any more.");
        }

        private Task RaidModal(SocketModal modal, string action, string[] args) =>
            ReplyPrivately(modal, "Raids rob a property now. `.raid @person` lists what they own.");
    }
}
