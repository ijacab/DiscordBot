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
using DiscordBot.Games.Models.BattleArena;

namespace DiscordBot.Managers
{
    /// <summary>Battle Person card pulls: `.cardpull` and keeping a card.</summary>
    public partial class CommandManager
    {

        private const string CardDirectory = "card_images";

        public async Task CardPull(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args.Count > 1 && args[0].StartsWith("keep"))
            {
                string keepPrefix = args[1];
                if (!ulong.TryParse(keepPrefix.Split('_')[0], out ulong userId)
                    || userId != message.Author.Id)
                {
                    await message.SendRichEmbedMessage($"You are not the owner of this card. FUCK OFF");
                    return;
                }

                CoinAccount ownerAccount = await _coinService.Get(message.Author.Id, message.Author.Username);
                await SaveCard(keepPrefix, ownerAccount, message);
                return;
            }

            CoinAccount coinAccount = await _coinService.Get(message.Author.Id, message.Author.Username);
            string today = DateTimeOffset.UtcNow.ToString("yyyyMMdd");
            bool isFreePull = coinAccount.LastFreePullDate != today;

            if (!isFreePull && coinAccount.NetWorth < BattlePerson.PullCost)
            {
                await message.SendRichEmbedMessage("Not enough money", $"A card pull costs ${FormatHelper.GetCommaNumber(BattlePerson.PullCost)}. Your first pull each day is free.");
                return;
            }

            //get the face before charging, so nobody pays when the image service is down
            byte[] faceBytes;
            using (var faceStream = await _faceService.Run())
            using (var buffer = new MemoryStream())
            {
                await faceStream.CopyToAsync(buffer);
                faceBytes = buffer.ToArray();
            }

            var battlePerson = new BattlePerson(NameGenerator.GetGeneratedName(2));
            CardLevels.CarryOver(coinAccount.BattlePerson, battlePerson); //shown at the player's own level, which it keeps
            if (battlePerson.Foil)
                faceBytes = FoilEffect.Apply(faceBytes); //foil cards get the holographic image, saved with the card
            string prefix = $"{message.Author.Id}_{battlePerson.Name.Replace(" ", "")}_{DateTimeOffset.Now.Ticks}";

            //the card is saved next to the image so 'keep' saves the card the player actually saw
            Directory.CreateDirectory(CardDirectory);
            string unsavedBase = Path.Combine(CardDirectory, $"{prefix}_unsaved");
            await File.WriteAllBytesAsync($"{unsavedBase}.jpg", faceBytes);
            await File.WriteAllTextAsync($"{unsavedBase}.json", JsonConvert.SerializeObject(battlePerson));

            if (isFreePull)
                coinAccount.LastFreePullDate = today;
            else
                Money.Debit(coinAccount, BattlePerson.PullCost);
            await _coinService.Update();

            _ = Task.Delay(TimeSpan.FromMinutes(5)).ContinueWith(t => DeleteUnsavedCard(prefix));

            var embed = DiscordHelper.GetEmbedBuilder(battlePerson.Name, battlePerson.GetSummary()).Build();
            using (var image = new MemoryStream(faceBytes))
            {
                await message.Channel.SendFileAsync(stream: image, $"{prefix}_unsaved.jpg", embed: embed);
            }

            if (coinAccount.BattlePerson == null)
            {
                await SaveCard(prefix, coinAccount, message); //auto save the card if they don't have any card
            }
            else
            {
                string paidLine = isFreePull ? "Free daily pull used." : $"${FormatHelper.GetCommaNumber(BattlePerson.PullCost)} paid.";
                await message.Channel.SendMessageAsync($"{paidLine} Type the following if you want to replace your current card with the new one (you have 5 minutes before it is gone):");
                await message.Channel.SendMessageAsync($".cardpull keep {prefix}");
            }
        }

        private async Task SaveCard(string prefix, CoinAccount coinAccount, SocketMessage message)
        {
            //the prefix comes from the player's message, so it must be a plain file name
            if (string.IsNullOrWhiteSpace(prefix) || prefix != Path.GetFileName(prefix))
            {
                await message.SendRichEmbedMessage($"This Battle Person ID does not exist. It may have been deleted.");
                return;
            }

            string unsavedBase = Path.Combine(CardDirectory, $"{prefix}_unsaved");
            string unsavedJsonPath = $"{unsavedBase}.json";
            string unsavedImagePath = $"{unsavedBase}.jpg";
            if (!File.Exists(unsavedJsonPath) || !File.Exists(unsavedImagePath))
            {
                await message.SendRichEmbedMessage($"This Battle Person ID does not exist. It may have been deleted.");
                return;
            }

            BattlePerson battlePerson = JsonConvert.DeserializeObject<BattlePerson>(await File.ReadAllTextAsync(unsavedJsonPath));

            if (coinAccount.BattlePerson?.FilePath != null && File.Exists(coinAccount.BattlePerson.FilePath))
                File.Delete(coinAccount.BattlePerson.FilePath); //replacing the old card removes its image
            FaceOffRenderer.ForgetArt(coinAccount.BattlePerson?.FilePath);

            string savedImagePath = Path.Combine(CardDirectory, $"{prefix}.jpg");
            FaceOffRenderer.ForgetArt(savedImagePath);
            File.Move(unsavedImagePath, savedImagePath);
            File.Delete(unsavedJsonPath);

            battlePerson.FilePath = savedImagePath;
            CardLevels.CarryOver(coinAccount.BattlePerson, battlePerson); //the level belongs to the player, not the card
            coinAccount.BattlePerson = battlePerson;
            await _coinService.Update();
            await message.SendRichEmbedMessage($"New Battle Person saved: {battlePerson.Name}");
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
