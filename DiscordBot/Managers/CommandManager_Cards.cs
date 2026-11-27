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
            _ = Task.Run(() => OpenPackAndPost(message.Author, message.Channel, pack, null, 0));
            return Task.CompletedTask;
        }

        /// <summary>The old `.cardpull`: a street pack.</summary>
        public Task CardPull(DiscordSocketClient client, SocketMessage message, List<string> args) => OpenPackCommand(client, message, new List<string>());

        /// <summary>Opens and posts a pack. A refusal goes back privately when it came from a button (<paramref name="click"/>), else to the channel.</summary>
        /// <summary>Players with a pack opening now: one at a time each, so two packs can't fetch the same faces side by side.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, bool> _openingPacks = new System.Collections.Concurrent.ConcurrentDictionary<ulong, bool>();

        private async Task OpenPackAndPost(IUser user, IMessageChannel channel, PackType pack, SocketMessageComponent click, double shownPrice)
        {
            bool mine = _openingPacks.TryAdd(user.Id, true);
            try
            {
                if (!mine) throw new BadInputException("You're already opening a pack. Wait for it.");
                using (channel.EnterTypingState())
                    await OpenPack(user, channel, pack, shownPrice);
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
            finally
            {
                if (mine) _openingPacks.TryRemove(user.Id, out _);
            }
        }

        /// <param name="shownPrice">The price on the button that was pressed (0 for a typed command): a pack that now costs more is refused instead of charged, since net worth moves.</param>
        private async Task OpenPack(IUser user, IMessageChannel channel, PackType pack, double shownPrice)
        {
            CoinAccount account = await _coinService.Get(user.Id, user.Username);
            string today = DateTimeOffset.UtcNow.ToString("yyyyMMdd");
            PackRefusal(account, pack, today, PackPrice(account, pack));

            //get the faces before charging, so nobody pays when the image service is down; a card whose face failed gets a stand-in.
            //one at a time and each different, since the site hands the same face to requests made together
            var faces = await _faceService.FetchDistinct(pack.Cards, FaceHashes(account));
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
            double price;
            string xpLine = "";
            try
            {
                lock (_packLock)
                {
                    price = PackPrice(account, pack); //again: net worth and cash may have changed while the faces downloaded
                    PackRefusal(account, pack, today, price);
                    free = IsFreePack(account, pack, today);
                    if (!free && shownPrice > 0 && price > shownPrice)
                        throw new BadInputException($"That button said {Money.Format(shownPrice)}, but your {pack.Name.ToLower()} now costs {Money.Format(price)}. Nothing was charged: `.gang` has fresh buttons, or type `.pack {pack.Kind.ToString().ToLower()}`.");
                    if (free) account.LastFreePullDate = today;
                    else Money.Debit(account, price);
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

            string paid = free ? "Free daily street pack." : $"{Money.Format(price)} spent.";
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
            var components = PackButtons(account, today, cards);
            if (picture != null)
            {
                using (picture)
                    await channel.SendFileAsync(picture, "pack.png", text, embed: embed.Build(), components: components, allowedMentions: AllowedMentions.None);
            }
            else
                await channel.SendMessageAsync(text, embed: embed.Build(), components: components, allowedMentions: AllowedMentions.None);
        }

        /// <summary>What a pack costs this player right now: its share of their total net worth, at least its minimum.</summary>
        private double PackPrice(CoinAccount account, PackType pack) => GangRules.PriceFor(pack, _worth.TotalWorth(account));

        private static bool IsFreePack(CoinAccount account, PackType pack, string today) => pack.Kind == PackKind.Street && account.LastFreePullDate != today;

        /// <summary>Throws when the pack can't be opened: no room in the gang, or not enough cash.</summary>
        public static void PackRefusal(CoinAccount account, PackType pack, string today, double price)
        {
            if (account.Cards.Count + pack.Cards > GangRules.MaxCards)
                throw new BadInputException($"Your gang has {account.Cards.Count}/{GangRules.MaxCards} cards, so a {pack.Cards}-card {pack.Name.ToLower()} won't fit. " +
                    $"`.fuse common` turns {GangRules.FuseCount} into one better card, and `.scrap N` turns one into XP.");
            if (!IsFreePack(account, pack, today) && account.NetWorth < price)
                throw new BadInputException($"A {pack.Name.ToLower()} costs {pack.Share:0%} of your total worth (at least {Money.Format(pack.MinPrice)}): {Money.Format(price)}, and you have {Money.Format(Math.Max(0, account.NetWorth))} in cash." +
                    (pack.Kind == PackKind.Street ? " Your first street pack each day is free, come back tomorrow." : ""));
        }

        /// <summary>
        /// The keys a card's face is known by: a plain card's picture and what it would look like as a foil, or a foil's picture as saved.
        /// Two cards with a key in common have the same face, foil or not (the foil effect always gives the same picture for a face).
        /// </summary>
        private static List<string> FaceKeys(BattlePerson card, bool withFoil = true)
        {
            try
            {
                if (string.IsNullOrEmpty(card.FilePath) || !File.Exists(card.FilePath)) return new List<string>();
                byte[] bytes = File.ReadAllBytes(card.FilePath);
                var keys = new List<string> { FaceService.HashOf(bytes) };
                if (withFoil && !card.Foil) keys.Add(FaceService.HashOf(FoilEffect.Apply(bytes)));
                return keys;
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }

        /// <summary>The hash of every face in a gang as saved, so a new face can be checked against them (a pack compares plain faces; the foil keys are only worth working out for the repair).</summary>
        private static HashSet<string> FaceHashes(CoinAccount account)
        {
            List<BattlePerson> cards;
            lock (_packLock) cards = account.Cards.ToList();
            return new HashSet<string>(cards.SelectMany(c => FaceKeys(c, withFoil: false)));
        }

        private bool _faceRepairStarted;

        /// <summary>
        /// Once after the deploy that fixed packs handing out the same face: in every gang (and every gang waiting from last season), each
        /// card whose face matches another's, foil or not, gets a new face; the first keeps its own. Runs in the background. A new face is
        /// written to a temp file first and only moved into place, under the card lock, if the card is still in the gang. The flag is only
        /// saved once every gang was checked and every new face arrived, so a failure is tried again after the next restart.
        /// </summary>
        private void RepairDuplicateFacesOnce()
        {
            if (_faceRepairStarted || _coinService.GetAll().CardFaceRepairDone) return;
            _faceRepairStarted = true;
            _ = Task.Run(async () =>
            {
                int changed = 0;
                bool complete = true;
                var data = _coinService.GetAll();
                List<(ulong Owner, List<BattlePerson> Cards)> gangs;
                lock (_packLock)
                    gangs = data.Accounts.Select(a => (a.UserId, a.Cards)).Concat(data.CarriedGangs.Select(p => (p.Key, p.Value))).ToList();

                foreach (var (owner, gang) in gangs)
                {
                    try
                    {
                        List<BattlePerson> cards;
                        lock (_packLock) cards = GangRules.Ordered(gang);
                        var seen = new HashSet<string>();
                        var dupes = new List<BattlePerson>();
                        foreach (var card in cards)
                        {
                            var keys = FaceKeys(card);
                            if (keys.Count == 0) continue;
                            if (keys.Any(seen.Contains)) dupes.Add(card);
                            else seen.UnionWith(keys);
                        }
                        if (dupes.Count == 0) continue;

                        var faces = await _faceService.FetchDistinct(dupes.Count, seen);
                        Directory.CreateDirectory(CardDirectory);
                        for (int i = 0; i < dupes.Count; i++)
                        {
                            if (faces[i] == null)
                            {
                                complete = false;
                                continue;
                            }
                            var card = dupes[i];
                            string path = Path.Combine(CardDirectory, $"{owner}_{card.Id}.jpg");
                            string temp = path + ".new";
                            await File.WriteAllBytesAsync(temp, card.Foil ? FoilEffect.Apply(faces[i]) : faces[i]);
                            lock (_packLock)
                            {
                                if (!gang.Contains(card))
                                {
                                    File.Delete(temp); //scrapped or fused while the face downloaded
                                    continue;
                                }
                                File.Move(temp, path, overwrite: true);
                                RaidRenderer.ForgetArt(card.FilePath);
                                RaidRenderer.ForgetArt(path);
                                card.FilePath = path;
                            }
                            changed++;
                        }
                        await _coinService.Update();
                    }
                    catch (Exception ex)
                    {
                        complete = false;
                        _logger.LogError(ex, "Repairing duplicate card faces failed for {Owner}", owner);
                    }
                }

                try
                {
                    if (complete)
                    {
                        data.CardFaceRepairDone = true;
                        await _coinService.Update();
                    }
                    _logger.LogInformation("Gave {Changed} cards that shared a face a new one ({State})", changed, complete ? "done" : "will try again after a restart");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Saving the card face repair failed");
                }
            });
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
            var embed = DiscordHelper.GetEmbedBuilder(own ? "🕶️ Your gang" : $"🕶️ {shown.Name}'s gang", GangText(shown, now, own));
            var components = own ? GangButtons(account, now) : CardPicker(shown);
            List<BattlePerson> ordered;
            lock (_packLock) ordered = GangRules.Ordered(shown.Cards);
            //the whole gang as one picture, numbered as the list is, drawn off the gateway (decoding up to 15 faces takes a moment on the Pi);
            //the text alone if it can't be drawn
            _ = Task.Run(async () =>
            {
                MemoryStream picture = null;
                try
                {
                    if (ordered.Count > 0)
                    {
                        try
                        {
                            picture = RaidRenderer.RenderGrid(ordered, GangColumns, numbered: true);
                            embed.WithImageUrl("attachment://gang.png");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Couldn't draw the gang picture");
                        }
                    }
                    if (picture != null)
                        await message.Channel.SendFileAsync(picture, "gang.png", embed: embed.Build(), components: components);
                    else
                        await message.Channel.SendMessageAsync(embed: embed.Build(), components: components);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Posting the gang failed");
                }
                finally
                {
                    picture?.Dispose();
                }
            });
        }

        /// <summary>Cards per row in the `.gang` picture: 15 cards make 5 by 3.</summary>
        private const int GangColumns = 5;

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
                $"\nScraps for {GangRules.ScrapXp(card)} XP." +
                (card.Rarity == CardRarity.Glitched ? "" : $"\n🌀 Face messed up? Other players can vote it Glitched, the top rarity ({GangRules.VoteCount(card, IsBotAdmin)}).");
            var vote = card.Rarity == CardRarity.Glitched ? null : new ComponentBuilder()
                .WithButton("🌀 Glitched?", Id(MoneyComponents.Card, "glitch", owner.UserId.ToString(), card.Id), ButtonStyle.Secondary).Build();
            var embed = DiscordHelper.GetEmbedBuilder($"#{number} {card.Name} ({owner.Name})", text);
            bool art = !string.IsNullOrEmpty(card.FilePath) && File.Exists(card.FilePath);
            if (art) embed.WithImageUrl("attachment://" + Path.GetFileName(card.FilePath));
            if (component != null)
            {
                if (art)
                {
                    using var image = File.OpenRead(card.FilePath);
                    await component.FollowupWithFileAsync(image, Path.GetFileName(card.FilePath), embed: embed.Build(), components: vote, ephemeral: true);
                }
                else await component.FollowupAsync(embed: embed.Build(), components: vote, ephemeral: true);
                return;
            }
            if (art)
            {
                using var image = File.OpenRead(card.FilePath);
                await channel.SendFileAsync(image, Path.GetFileName(card.FilePath), embed: embed.Build(), components: vote);
            }
            else await channel.SendMessageAsync(embed: embed.Build(), components: vote);
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

        /// <summary>Pack buttons for the opener, and a glitch vote per new card for everyone else.</summary>
        private MessageComponent PackButtons(CoinAccount account, string today, IList<BattlePerson> opened = null)
        {
            string owner = account.UserId.ToString();
            var builder = new ComponentBuilder();
            foreach (var pack in GangRules.Packs)
                builder.WithButton(Trim($"{pack.Name} · {(IsFreePack(account, pack, today) ? "FREE" : RaidService.Money(PackPrice(account, pack)))}", 80),
                    Id(MoneyComponents.Card, "pack", owner, pack.Kind.ToString(), PackPrice(account, pack).ToString("0")), pack.Kind == PackKind.Street ? ButtonStyle.Success : ButtonStyle.Primary, row: 0);
            builder.WithButton("My gang", Id(MoneyComponents.Card, "gang", owner), ButtonStyle.Secondary, row: 0);
            for (int i = 0; opened != null && i < opened.Count && i < 5; i++)
                builder.WithButton(Trim($"🌀 {opened[i].Name.Split(' ')[0]} glitched?", 80), Id(MoneyComponents.Card, "glitch", owner, opened[i].Id), ButtonStyle.Secondary, row: 1);
            return builder.Build();
        }

        private bool IsBotAdmin(ulong userId) => _adminIds.Contains(userId);

        /// <summary>A glitch vote from a button: privately says where the vote stands, and publicly when it turned the card Glitched.</summary>
        private async Task GlitchVote(SocketMessageComponent component, string[] args)
        {
            await component.DeferAsync(ephemeral: true); //answer first: the save below can take longer than Discord waits
            var owner = args.Length > 0 && ulong.TryParse(args[0], out ulong ownerId) ? _coinService.GetAll().Accounts.FirstOrDefault(a => a.UserId == ownerId) : null;
            string cardId = args.Length > 1 ? args[1] : null;
            //looked up, never made: a passer-by who clicks shouldn't get an account (they can't vote without one anyway)
            CoinAccount voter = _coinService.GetAll().Accounts.FirstOrDefault(a => a.UserId == component.User.Id);
            bool glitched;
            BattlePerson card;
            string count;
            try
            {
                lock (_packLock)
                {
                    card = owner?.Cards.FirstOrDefault(c => c.Id == cardId);
                    glitched = GangRules.VoteGlitch(card, owner, voter, IsBotAdmin, DateTime.UtcNow);
                    count = GangRules.VoteCount(card, IsBotAdmin);
                }
            }
            catch (BadInputException ex)
            {
                await ReplyPrivately(component, ex.Message);
                return;
            }
            await _coinService.Update();
            await component.FollowupAsync(glitched ? $"🌀 Your vote made {card.Name} Glitched!" : $"🌀 Vote counted for {card.Name}: {count}.", ephemeral: true);
            if (glitched && component.Channel != null)
                await component.Channel.SendMessageAsync($"🌀 **{card.Name}** is **GLITCHED**! The crowd has spoken: {owner.Name}'s card is now the top rarity, power {card.Power:0}.", allowedMentions: AllowedMentions.None);
        }

        private MessageComponent GangButtons(CoinAccount account, DateTime now)
        {
            string owner = account.UserId.ToString();
            string today = now.ToString("yyyyMMdd");
            var builder = new ComponentBuilder();
            foreach (var pack in GangRules.Packs)
                builder.WithButton(Trim($"{pack.Name} · {(IsFreePack(account, pack, today) ? "FREE" : RaidService.Money(PackPrice(account, pack)))}", 80),
                    Id(MoneyComponents.Card, "pack", owner, pack.Kind.ToString(), PackPrice(account, pack).ToString("0")), pack.Kind == PackKind.Street ? ButtonStyle.Success : ButtonStyle.Primary, row: 0);
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
            if (action == "glitch")
            {
                await GlitchVote(component, args);
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
                    double shown = args.Length > 2 && double.TryParse(args[2], out double parsed) ? parsed : 0; //old buttons carry no price
                    await component.DeferAsync();
                    _ = Task.Run(() => OpenPackAndPost(component.User, component.Channel, GangRules.Pack(kind), component, shown));
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
