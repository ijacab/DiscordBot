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
using DiscordBot.Games.Models.Cards;

namespace DiscordBot.Managers
{
    /// <summary>Gangs: `.pack` (and the old `.cardpull`), `.gang` (`.card`), `.train`, `.fuse` and `.scrap`, and their buttons.</summary>
    public partial class CommandManager
    {
        private const string CardDirectory = "card_images";

        /// <summary>One pack at a time is added to an account, so two packs opened together can't pass the card limit or spend the same cash.</summary>
        private static object _packLock => RaidService.Sync; //the same lock raids take, since both change and read the card list

        // ---- typed commands

        /// <summary>`.pack [street|crew|kingpin]`: the faces are slow web calls, so the opening runs off the gateway and reports its own errors.</summary>
        public Task OpenPackCommand(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            var pack = args.Count == 0 ? GangRules.Pack(PackKind.Street) : GangRules.FindPack(args[0])
                ?? throw new BadInputException("Which pack? " + string.Join(", ", GangRules.Packs.Select(p => $"`.pack {p.Kind.ToString().ToLower()}`")) + ".");
            _ = Task.Run(() => OpenPackAndPost(message.Author, message.Channel, pack, null));
            return Task.CompletedTask;
        }

        /// <summary>The old `.cardpull`: a street pack.</summary>
        public Task CardPull(DiscordSocketClient client, SocketMessage message, List<string> args) => OpenPackCommand(client, message, new List<string>());

        /// <summary>Opens and posts a pack. A refusal goes back privately when it came from a button (<paramref name="click"/>), else to the channel.</summary>
        private async Task OpenPackAndPost(IUser user, IMessageChannel channel, PackType pack, SocketMessageComponent click)
        {
            try
            {
                using (channel.EnterTypingState())
                    await OpenPack(user, channel, pack);
            }
            catch (BadInputException ex)
            {
                try
                {
                    if (click != null) await click.FollowupAsync(ex.Message, ephemeral: true);
                    else await channel.SendMessageAsync($"{user.Mention} {ex.Message}", allowedMentions: AllowedMentions.None);
                }
                catch (Exception) { }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Opening a pack failed");
                try { await channel.SendMessageAsync($"{user.Mention} the card printer shat itself. Nothing was charged. Try again in a bit.", allowedMentions: AllowedMentions.None); }
                catch (Exception) { }
            }
        }

        private async Task OpenPack(IUser user, IMessageChannel channel, PackType pack)
        {
            CoinAccount account = await _coinService.Get(user.Id, user.Username);
            string today = DateTimeOffset.UtcNow.ToString("yyyyMMdd");
            PackRefusal(account, pack, today);

            //get the faces before charging, so nobody pays when the image service is down; a card whose face failed gets a stand-in
            var faces = await Task.WhenAll(Enumerable.Range(0, pack.Cards).Select(_ => TryFetchFace()));
            if (faces.All(f => f == null))
                throw new BadInputException("The face printer is down, so nothing was opened or charged. Try again in a bit.");

            var cards = GangRules.Open(pack, RandomHelper.ThisThreadsRandom);
            Directory.CreateDirectory(CardDirectory);
            for (int i = 0; i < cards.Count; i++)
            {
                if (faces[i] == null) continue;
                byte[] bytes = cards[i].Foil ? FoilEffect.Apply(faces[i]) : faces[i]; //foils get the holographic picture
                string path = Path.Combine(CardDirectory, $"{user.Id}_{cards[i].Id}.jpg");
                await File.WriteAllBytesAsync(path, bytes);
                cards[i].FilePath = path;
            }

            bool free;
            string xpLine = "";
            try
            {
                lock (_packLock)
                {
                    PackRefusal(account, pack, today); //again: something may have changed while the faces downloaded
                    free = IsFreePack(account, pack, today);
                    if (free) account.LastFreePullDate = today;
                    else Money.Debit(account, pack.Price);
                    var strongest = GangRules.Ordered(account.Cards).FirstOrDefault();
                    if (strongest != null)
                        xpLine = $" {strongest.Name}: {CardLevels.AddXp(strongest, CardLevels.PullXp)}.";
                    account.Cards.AddRange(cards);
                }
            }
            catch (Exception)
            {
                DeleteCardImages(cards);
                throw;
            }
            await _coinService.Update();

            string paid = free ? "Free daily street pack." : $"{Money.Format(pack.Price)} spent.";
            string text = $"{paid}{xpLine} Your gang has {account.Cards.Count}/{GangRules.MaxCards} cards. `.gang` to see them.";
            var embed = DiscordHelper.GetEmbedBuilder($"{pack.Emoji} {user.Username} opened a {pack.Name}", string.Join("\n", cards.Select(c => c.Line())));
            MemoryStream picture = null;
            try
            {
                picture = RaidRenderer.RenderStrip(cards);
                embed.WithImageUrl("attachment://pack.png");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Couldn't draw the pack picture");
            }
            var components = PackButtons(account, today);
            if (picture != null)
            {
                using (picture)
                    await channel.SendFileAsync(picture, "pack.png", text, embed: embed.Build(), components: components, allowedMentions: AllowedMentions.None);
            }
            else
                await channel.SendMessageAsync(text, embed: embed.Build(), components: components, allowedMentions: AllowedMentions.None);
        }

        private static bool IsFreePack(CoinAccount account, PackType pack, string today) => pack.Kind == PackKind.Street && account.LastFreePullDate != today;

        /// <summary>Throws when the pack can't be opened: no room in the gang, or not enough cash.</summary>
        private static void PackRefusal(CoinAccount account, PackType pack, string today)
        {
            if (account.Cards.Count + pack.Cards > GangRules.MaxCards)
                throw new BadInputException($"Your gang has {account.Cards.Count}/{GangRules.MaxCards} cards, so a {pack.Cards}-card {pack.Name.ToLower()} won't fit. " +
                    $"`.fuse common` turns {GangRules.FuseCount} into one better card, and `.scrap N` turns one into XP.");
            if (!IsFreePack(account, pack, today) && account.NetWorth < pack.Price)
                throw new BadInputException($"A {pack.Name.ToLower()} costs {Money.Format(pack.Price)} and you have {Money.Format(Math.Max(0, account.NetWorth))}." +
                    (pack.Kind == PackKind.Street ? " Your first street pack each day is free, come back tomorrow." : ""));
        }

        private async Task<byte[]> TryFetchFace()
        {
            try
            {
                using var faceStream = await _faceService.Run();
                using var buffer = new MemoryStream();
                await faceStream.CopyToAsync(buffer);
                return buffer.Length > 0 ? buffer.ToArray() : null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Couldn't fetch a card face");
                return null;
            }
        }

        private static void DeleteCardImages(IEnumerable<BattlePerson> cards)
        {
            foreach (var card in cards)
            {
                RaidRenderer.ForgetArt(card.FilePath);
                try
                {
                    if (!string.IsNullOrEmpty(card.FilePath) && File.Exists(card.FilePath)) File.Delete(card.FilePath);
                }
                catch (Exception) { }
            }
        }

        /// <summary>`.gang [@someone]`: the roster, strongest first, numbered for `.train`, `.scrap` and `.gang N`. `.gang N` shows one card.</summary>
        private async Task GangView(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            var now = DateTime.UtcNow;
            CoinAccount account = await _coinService.Get(message.Author.Id, message.Author.Username);
            if (args.Count >= 1 && args[0].Equals("stance", StringComparison.OrdinalIgnoreCase))
            {
                await StanceGone(client, message, args);
                return;
            }
            if (args.Count == 1 && int.TryParse(args[0].TrimStart('#'), out int number))
            {
                await ShowCard(message.Channel, account, GangRules.At(account.Cards, number), number);
                return;
            }
            CoinAccount shown = account;
            if (args.Count >= 1)
                shown = FindRaidTarget(message, args[0]);

            bool own = shown.UserId == account.UserId;
            var embed = DiscordHelper.GetEmbedBuilder(own ? "🕶️ Your gang" : $"🕶️ {shown.Name}'s gang", GangText(shown, now, own)).Build();
            await message.Channel.SendMessageAsync(embed: embed, components: own ? GangButtons(account, now) : CardPicker(shown));
        }

        private string GangText(CoinAccount account, DateTime now, bool own)
        {
            var lines = new List<string>();
            var ordered = GangRules.Ordered(account.Cards);
            if (ordered.Count == 0)
                return own ? "No gang yet. `.pack` opens a street pack, and the first one each day is free." : $"{account.Name} has no gang.";

            var crew = GangRules.Crew(account.Cards, now);
            lines.Add($"**{ordered.Count}/{GangRules.MaxCards} cards** · raiding crew power **{RaidRules.TotalPower(crew):0}** ({crew.Count} ready) · defence **{RaidRules.TotalPower(GangRules.Defenders(account.Cards)):0}** plus your property's security");
            if (own)
            {
                _raidService.Check(account, account, null, now); //drops expired revenge and cooldowns
                lines.Add($"Raids left this hour: {Math.Max(0, RaidRules.RaidsPerHour - account.RecentRaidUtcTicks.Count)} of {RaidRules.RaidsPerHour}" +
                    (account.GuardsUntilUtcTicks > now.Ticks ? $" · 💂 guards for {RaidService.Wait(new DateTime(account.GuardsUntilUtcTicks) - now)}" : "") +
                    (account.Bounty > 0 ? $" · 💰 bounty on you {RaidService.Money(account.Bounty)}" : "") +
                    (account.RevengeTokens.Count > 0 ? " · 🔥 revenge on " + string.Join(", ", account.RevengeTokens.Select(r => r.Name)) : ""));
            }
            lines.Add("");
            for (int i = 0; i < ordered.Count; i++)
            {
                var card = ordered[i];
                string rest = card.IsResting(now) ? $" · 😴 {RaidService.Wait(new DateTime(card.RestedUntilUtcTicks) - now)}" : "";
                lines.Add($"`{i + 1,2}` {card.Line()}{rest}");
            }
            if (own)
                lines.Add($"\n`.gang N` shows a card · `.train N` / `.train N max` · `.fuse common` ({GangRules.FuseCount} into one better) · `.scrap N into M` (XP) · `.raid @someone`");
            var s = account.Stats;
            lines.Add($"Raids won {s.RaidsWon} / lost {s.RaidsLost} · defences won {s.DefencesWon} / lost {s.DefencesLost}" + (account.RaidStreak >= 2 ? $" · 🔥 {account.RaidStreak} jobs in a row" : ""));
            return string.Join("\n", lines);
        }

        private async Task ShowCard(IMessageChannel channel, CoinAccount owner, BattlePerson card, int number, SocketMessageComponent component = null)
        {
            string text = card.GetSummary() + (card.Level < CardLevels.MaxLevel ? $"\nTraining to level {card.Level + 1}: {RaidService.Money(CardLevels.TrainCost(card))} (`.train {number}`)" : "") +
                $"\nScraps for {GangRules.ScrapXp(card)} XP.";
            var embed = DiscordHelper.GetEmbedBuilder($"#{number} {card.Name} ({owner.Name})", text);
            bool art = !string.IsNullOrEmpty(card.FilePath) && File.Exists(card.FilePath);
            if (art) embed.WithImageUrl("attachment://" + Path.GetFileName(card.FilePath));
            if (component != null)
            {
                if (art)
                {
                    using var image = File.OpenRead(card.FilePath);
                    await component.FollowupWithFileAsync(image, Path.GetFileName(card.FilePath), embed: embed.Build(), ephemeral: true);
                }
                else await component.FollowupAsync(embed: embed.Build(), ephemeral: true);
                return;
            }
            if (art)
            {
                using var image = File.OpenRead(card.FilePath);
                await channel.SendFileAsync(image, Path.GetFileName(card.FilePath), embed: embed.Build());
            }
            else await channel.SendMessageAsync(embed: embed.Build());
        }

        /// <summary>`.train [N] [max]`: trains card N (your strongest when there's no number) a level, or as many as your cash pays for.</summary>
        private async Task Train(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            CoinAccount account = await _coinService.Get(message.Author.Id, message.Author.Username);
            bool max = args.Any(a => a.Equals("max", StringComparison.OrdinalIgnoreCase) || a.Equals("all", StringComparison.OrdinalIgnoreCase));
            var numbers = args.Where(a => int.TryParse(a.TrimStart('#'), out _)).ToList();
            if (args.Count > numbers.Count + (max ? 1 : 0)) throw new BadSyntaxException();
            var card = numbers.Count == 0 ? GangRules.Ordered(account.Cards).FirstOrDefault() : GangRules.At(account.Cards, int.Parse(numbers[0].TrimStart('#')));
            await message.Channel.SendMessageAsync(await TrainText(account, card, max), components: GangButtons(account, DateTime.UtcNow));
        }

        private async Task<string> TrainText(CoinAccount account, BattlePerson card, bool max)
        {
            if (card == null) throw new BadInputException("You don't have a gang to train. `.pack` opens a street pack (the first each day is free).");
            int before = card.Level;
            var (levels, cost) = _raidService.Train(account, card, max ? CardLevels.MaxLevel : 1);
            await _coinService.Update();
            string next = card.Level >= CardLevels.MaxLevel ? "Max level!" : $"Next level costs {RaidService.Money(CardLevels.TrainCost(card))}.";
            return $"🏋️ {account.Name} trained **{card.Name}** {before} → **{card.Level}** ({levels} level{(levels == 1 ? "" : "s")}) for {RaidService.Money(cost)}, into the jackpot. {next}";
        }

        /// <summary>`.fuse common|rare|epic`: three of your weakest of that rarity become one card of the next.</summary>
        private async Task Fuse(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args.Count != 1 || !GangRules.TryParseRarity(args[0], out CardRarity rarity)) throw new BadSyntaxException();
            CoinAccount account = await _coinService.Get(message.Author.Id, message.Author.Username);
            List<BattlePerson> used;
            BattlePerson made;
            lock (_packLock)
                (used, made) = GangRules.Fuse(account.Cards, rarity, RandomHelper.ThisThreadsRandom);
            //the new card takes the best face of the three, so it keeps a picture without another download
            var face = used.OrderByDescending(c => c.Level).FirstOrDefault(c => !string.IsNullOrEmpty(c.FilePath) && File.Exists(c.FilePath));
            if (face != null)
            {
                string path = Path.Combine(CardDirectory, $"{message.Author.Id}_{made.Id}.jpg");
                File.Copy(face.FilePath, path, true);
                made.FilePath = path;
            }
            DeleteCardImages(used);
            await _coinService.Update();
            await message.Channel.SendMessageAsync($"🔥 {account.Name} fused {string.Join(", ", used.Select(c => c.Name))} into {made.Line()}.", allowedMentions: AllowedMentions.None);
        }

        /// <summary>`.scrap N [into M]`: card N is gone and card M (your strongest other card) gets its XP.</summary>
        private async Task Scrap(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            var words = args.Where(a => !a.Equals("into", StringComparison.OrdinalIgnoreCase) && !a.Equals("to", StringComparison.OrdinalIgnoreCase)).ToList();
            if (words.Count < 1 || words.Count > 2 || !words.All(w => int.TryParse(w.TrimStart('#'), out _))) throw new BadSyntaxException();
            CoinAccount account = await _coinService.Get(message.Author.Id, message.Author.Username);
            string line;
            BattlePerson scrapped;
            lock (_packLock)
            {
                scrapped = GangRules.At(account.Cards, int.Parse(words[0].TrimStart('#')));
                var into = words.Count == 2 ? GangRules.At(account.Cards, int.Parse(words[1].TrimStart('#')))
                    : GangRules.Ordered(account.Cards).FirstOrDefault(c => c != scrapped);
                if (into == null) throw new BadInputException("That's your only card, so there's nothing to give its XP to.");
                if (into == scrapped) throw new BadInputException("A card can't be scrapped into itself.");
                int xp = GangRules.ScrapXp(scrapped);
                account.Cards.Remove(scrapped);
                line = $"♻️ {account.Name} scrapped {scrapped.Name}. **{into.Name}**: {CardLevels.AddXp(into, xp)}.";
            }
            DeleteCardImages(new[] { scrapped });
            await _coinService.Update();
            await message.Channel.SendMessageAsync(line, allowedMentions: AllowedMentions.None);
        }

        /// <summary>The old `.stance`: stances are gone.</summary>
        private Task StanceGone(DiscordSocketClient client, SocketMessage message, List<string> args) =>
            message.Channel.SendMessageAsync($"Stances are gone. Raids hit property now: your property's security and your best cards defend it, and `.guards` makes them {RaidRules.GuardsMultiplier - 1:0%} tougher.");

        // ---- buttons

        private MessageComponent PackButtons(CoinAccount account, string today)
        {
            string owner = account.UserId.ToString();
            var builder = new ComponentBuilder();
            foreach (var pack in GangRules.Packs)
                builder.WithButton(Trim($"{pack.Name} · {(IsFreePack(account, pack, today) ? "FREE" : RaidService.Money(pack.Price))}", 80),
                    Id(MoneyComponents.Card, "pack", owner, pack.Kind.ToString()), pack.Kind == PackKind.Street ? ButtonStyle.Success : ButtonStyle.Primary, row: 0);
            builder.WithButton("My gang", Id(MoneyComponents.Card, "gang", owner), ButtonStyle.Secondary, row: 0);
            return builder.Build();
        }

        private MessageComponent GangButtons(CoinAccount account, DateTime now)
        {
            string owner = account.UserId.ToString();
            string today = now.ToString("yyyyMMdd");
            var builder = new ComponentBuilder();
            foreach (var pack in GangRules.Packs)
                builder.WithButton(Trim($"{pack.Name} · {(IsFreePack(account, pack, today) ? "FREE" : RaidService.Money(pack.Price))}", 80),
                    Id(MoneyComponents.Card, "pack", owner, pack.Kind.ToString()), pack.Kind == PackKind.Street ? ButtonStyle.Success : ButtonStyle.Primary, row: 0);
            var strongest = GangRules.Ordered(account.Cards).FirstOrDefault();
            if (strongest != null)
            {
                bool max = strongest.Level >= CardLevels.MaxLevel;
                builder.WithButton(Trim(max ? "#1 is max level" : $"Train #1 · {RaidService.Money(CardLevels.TrainCost(strongest))}", 80), Id(MoneyComponents.Card, "train", owner), ButtonStyle.Success, disabled: max, row: 1);
                builder.WithButton("Train #1 max", Id(MoneyComponents.Card, "trainmax", owner), ButtonStyle.Success, disabled: max, row: 1);
            }
            builder.WithButton(Trim($"Hire guards · {RaidService.Money(_raidService.GuardsCost(account))}", 80), Id(MoneyComponents.Card, "guards", owner), ButtonStyle.Primary, row: 1);
            var picker = CardPickerMenu(account);
            if (picker != null) builder.WithSelectMenu(picker, 2);
            return builder.Build();
        }

        /// <summary>A menu to look at one of a player's cards (privately, for whoever picks).</summary>
        private static MessageComponent CardPicker(CoinAccount account)
        {
            var menu = CardPickerMenu(account);
            return menu == null ? null : new ComponentBuilder().WithSelectMenu(menu).Build();
        }

        private static SelectMenuBuilder CardPickerMenu(CoinAccount account)
        {
            var ordered = GangRules.Ordered(account.Cards);
            if (ordered.Count == 0) return null;
            var menu = new SelectMenuBuilder().WithCustomId(Id(MoneyComponents.Card, "view", account.UserId.ToString())).WithPlaceholder("Look at a card…");
            for (int i = 0; i < ordered.Count && i < 25; i++)
                menu.AddOption(Trim($"{i + 1}. {ordered[i].Name}", 100), ordered[i].Id, Trim($"{(ordered[i].Foil ? "Foil " : "")}{ordered[i].Rarity} · Lv {ordered[i].Level} · power {ordered[i].Power:0}", 100));
            return menu;
        }

        private async Task CardComponent(SocketMessageComponent component, string action, string[] args)
        {
            if (action == "view")
            {
                //anyone can look at anyone's cards
                var owner = args.Length > 0 && ulong.TryParse(args[0], out ulong ownerId) ? _coinService.GetAll().Accounts.FirstOrDefault(a => a.UserId == ownerId) : null;
                string cardId = component.Data.Values?.FirstOrDefault();
                var ordered = GangRules.Ordered(owner?.Cards);
                int index = ordered.FindIndex(c => c.Id == cardId);
                if (index < 0)
                {
                    await ReplyPrivately(component, "That card isn't in the gang any more.");
                    return;
                }
                await component.DeferAsync(ephemeral: true);
                await ShowCard(component.Channel, owner, ordered[index], index + 1, component);
                return;
            }
            if (action == "keep" || action == "stance")
            {
                await ReplyPrivately(component, action == "keep" ? "Cards go straight into your gang now. `.gang` to see them." : "Stances are gone. Your property's security and best cards defend it now.");
                return;
            }
            if (!await IsOwner(component, args)) return;
            CoinAccount account = await _coinService.Get(component.User.Id, component.User.Username);
            switch (action)
            {
                case "pack":
                {
                    if (args.Length < 2 || !Enum.TryParse(args[1], out PackKind kind) || component.Channel == null) break;
                    await component.DeferAsync();
                    _ = Task.Run(() => OpenPackAndPost(component.User, component.Channel, GangRules.Pack(kind), component));
                    MarkActiveFromInteraction(component);
                    return;
                }
                case "gang":
                    await component.DeferAsync(ephemeral: true);
                    await component.FollowupAsync(embed: DiscordHelper.GetEmbedBuilder("🕶️ Your gang", GangText(account, DateTime.UtcNow, true)).Build(), ephemeral: true);
                    return;
                case "train":
                case "trainmax":
                    await component.DeferAsync();
                    await component.FollowupAsync(await TrainText(account, GangRules.Ordered(account.Cards).FirstOrDefault(), action == "trainmax"), allowedMentions: AllowedMentions.None);
                    MarkActiveFromInteraction(component);
                    return;
                case "guards":
                    await component.DeferAsync();
                    await component.FollowupAsync(await GuardsText(account, DateTime.UtcNow), allowedMentions: AllowedMentions.None);
                    MarkActiveFromInteraction(component);
                    return;
            }
            await ReplyPrivately(component, "That button doesn't do anything any more.");
        }
    }
}
