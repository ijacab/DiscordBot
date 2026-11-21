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
    /// <summary>Battle Person card pulls: `.cardpull` and keeping a card.</summary>
    public partial class CommandManager
    {

        private const string CardDirectory = "card_images";

        /// <summary>How long a pulled card waits to be kept before it's binned.</summary>
        public static readonly TimeSpan KeepWindow = TimeSpan.FromMinutes(5);

        /// <summary>Short keys for Keep buttons (a card's file prefix is too long for a button id), by key.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _pendingCards = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();

        /// <summary>`.cardpull`: fetching the face is a slow web call, so the pull runs off the gateway and reports its own errors.</summary>
        public Task CardPull(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args.Count > 1 && args[0].StartsWith("keep"))
                return KeepTyped(message, args[1]); //old `.cardpull keep <id>` lines still work
            _ = Task.Run(async () =>
            {
                try
                {
                    await PullCard(message);
                }
                catch (BadInputException ex)
                {
                    await message.Channel.SendMessageAsync($"{message.Author.Mention} {ex.Message}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Card pull failed");
                    await message.Channel.SendMessageAsync($"{message.Author.Mention} the card printer shat itself. Nothing was charged. Try again in a bit.");
                }
            });
            return Task.CompletedTask;
        }

        private async Task KeepTyped(SocketMessage message, string prefix)
        {
            if (!ulong.TryParse(prefix.Split('_')[0], out ulong userId) || userId != message.Author.Id)
            {
                await message.SendRichEmbedMessage("You are not the owner of this card. FUCK OFF");
                return;
            }
            CoinAccount owner = await _coinService.Get(message.Author.Id, message.Author.Username);
            await message.Channel.SendMessageAsync(await SaveCard(prefix, owner));
        }

        private async Task PullCard(SocketMessage message)
        {
            CoinAccount coinAccount = await _coinService.Get(message.Author.Id, message.Author.Username);
            string today = DateTimeOffset.UtcNow.ToString("yyyyMMdd");
            bool isFreePull = coinAccount.LastFreePullDate != today;
            if (!isFreePull && coinAccount.NetWorth < BattlePerson.PullCost)
                throw new BadInputException($"A pull costs {Money.Format(BattlePerson.PullCost)} and you're too broke. Your first pull each day is free, come back tomorrow.");

            //get the face before charging, so nobody pays when the image service is down
            byte[] faceBytes;
            using (var faceStream = await _faceService.Run())
            using (var buffer = new MemoryStream())
            {
                await faceStream.CopyToAsync(buffer);
                faceBytes = buffer.ToArray();
            }

            var battlePerson = BattlePerson.Pull();
            CardLevels.CarryOver(coinAccount.BattlePerson, battlePerson); //shown at the player's own level, which it keeps
            if (battlePerson.Foil)
                faceBytes = FoilEffect.Apply(faceBytes); //foil cards get the holographic image, saved with the card
            //a plain file name: letters and digits only, since the nickname has quotes in it
            string prefix = $"{message.Author.Id}_{System.Text.RegularExpressions.Regex.Replace(battlePerson.Name, "[^A-Za-z0-9]", "")}_{DateTimeOffset.Now.Ticks}";

            //the card is saved next to the image so Keep saves the card the player actually saw
            Directory.CreateDirectory(CardDirectory);
            string unsavedBase = Path.Combine(CardDirectory, $"{prefix}_unsaved");
            await File.WriteAllBytesAsync($"{unsavedBase}.jpg", faceBytes);
            await File.WriteAllTextAsync($"{unsavedBase}.json", JsonConvert.SerializeObject(battlePerson));

            if (isFreePull)
                coinAccount.LastFreePullDate = today;
            else
                Money.Debit(coinAccount, BattlePerson.PullCost);
            await _coinService.Update();

            string key = Guid.NewGuid().ToString("N").Substring(0, 8);
            _pendingCards[key] = prefix;
            _ = Task.Delay(KeepWindow).ContinueWith(t =>
            {
                _pendingCards.TryRemove(key, out _);
                DeleteUnsavedCard(prefix);
            });

            string paidLine = isFreePull ? "Free daily pull." : $"{Money.Format(BattlePerson.PullCost)} down the drain.";
            //every pull trains your current card a little, kept or not
            string xpLine = coinAccount.BattlePerson == null ? "" : " " + CardLevels.AddXp(coinAccount.BattlePerson, CardLevels.PullXp) + " for " + coinAccount.BattlePerson.Name + ".";
            await _coinService.Update();
            var embed = DiscordHelper.GetEmbedBuilder(battlePerson.Name, battlePerson.GetSummary()).Build();
            MessageComponent keep = coinAccount.BattlePerson == null ? null : new ComponentBuilder()
                .WithButton($"Keep {Trim(battlePerson.Name, 60)}", Id(MoneyComponents.Card, "keep", message.Author.Id.ToString(), key), ButtonStyle.Success)
                .Build();
            string text = coinAccount.BattlePerson == null
                ? $"{paidLine} Your first card, so it's yours."
                : $"{paidLine}{xpLine}\n{Compare(coinAccount.BattlePerson, battlePerson)}\nPress **Keep** within {KeepWindow.TotalMinutes:0} minutes to swap it in (you keep your level and XP). Otherwise it's binned.";
            using (var image = new MemoryStream(faceBytes))
            {
                await message.Channel.SendFileAsync(stream: image, $"{prefix}_unsaved.jpg", text, embed: embed, components: keep);
            }

            if (coinAccount.BattlePerson == null)
                await message.Channel.SendMessageAsync(await SaveCard(prefix, coinAccount)); //auto save the card if they don't have any card
        }

        /// <summary>How a new pull stacks up against the card you have, at your level: power, and what changes about how it raids.</summary>
        private static string Compare(BattlePerson current, BattlePerson pulled)
        {
            double diff = pulled.Power - current.Power;
            string power = Math.Abs(diff) < 0.05 ? "the same power" : diff > 0 ? $"**+{diff:0} power** 📈" : $"**{diff:0} power** 📉";
            string kind = Archetypes.Normalise(current.Archetype) == Archetypes.Normalise(pulled.Archetype)
                ? "the same archetype"
                : $"{Archetypes.Label(current.Archetype)} → {Archetypes.Label(pulled.Archetype)}";
            return $"Versus your {Archetypes.Emoji(current.Archetype)} {current.Name}: {power}, {kind}.";
        }

        /// <summary>The Keep button under a pull.</summary>
        private async Task<string> KeepFromButton(CoinAccount owner, string key)
        {
            if (!_pendingCards.TryRemove(key, out string prefix))
                return "Too slow, that card's already in the bin. Pull another.";
            return await SaveCard(prefix, owner);
        }

        /// <summary>Makes the pulled card the player's card. Returns what to say.</summary>
        private async Task<string> SaveCard(string prefix, CoinAccount coinAccount)
        {
            //the prefix can come from the player's message, so it must be a plain file name
            if (string.IsNullOrWhiteSpace(prefix) || prefix != Path.GetFileName(prefix))
                return "That card doesn't exist. It may have been binned already.";

            string unsavedBase = Path.Combine(CardDirectory, $"{prefix}_unsaved");
            string unsavedJsonPath = $"{unsavedBase}.json";
            string unsavedImagePath = $"{unsavedBase}.jpg";
            if (!File.Exists(unsavedJsonPath) || !File.Exists(unsavedImagePath))
                return "That card doesn't exist. It may have been binned already.";

            BattlePerson battlePerson = JsonConvert.DeserializeObject<BattlePerson>(await File.ReadAllTextAsync(unsavedJsonPath));

            if (coinAccount.BattlePerson?.FilePath != null && File.Exists(coinAccount.BattlePerson.FilePath))
                File.Delete(coinAccount.BattlePerson.FilePath); //replacing the old card removes its image
            RaidRenderer.ForgetArt(coinAccount.BattlePerson?.FilePath);

            string savedImagePath = Path.Combine(CardDirectory, $"{prefix}.jpg");
            RaidRenderer.ForgetArt(savedImagePath);
            File.Move(unsavedImagePath, savedImagePath, overwrite: true);
            File.Delete(unsavedJsonPath);

            battlePerson.FilePath = savedImagePath;
            CardLevels.CarryOver(coinAccount.BattlePerson, battlePerson); //the level belongs to the player, not the card
            coinAccount.BattlePerson = battlePerson;
            await _coinService.Update();
            return $"🃏 **{battlePerson.Name}** is your Battle Person now: {Archetypes.Label(battlePerson.Archetype)}, {battlePerson.Rarity}, power {battlePerson.Power:0}. `.card` for the control panel, `.raid @someone` to put it to work.";
        }

        private static void DeleteUnsavedCard(string prefix)
        {
            string unsavedBase = Path.Combine(CardDirectory, $"{prefix}_unsaved");
            foreach (string path in new[] { $"{unsavedBase}.jpg", $"{unsavedBase}.json" })
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }
    }
}
