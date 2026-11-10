using DiscordBot.Games.Models.BattleArena;
using DiscordBot.Services;
using SixLabors.ImageSharp;
using Xunit;

namespace Tests
{
    public class FaceOffRendererTests
    {
        private static FaceOffScene SceneWith(int fighters)
        {
            var scene = new FaceOffScene { Title = "ROUND 3 OF 12", Footer = "ATTACK BEATS FOCUS" };
            for (int i = 0; i < fighters; i++)
            {
                scene.Fighters.Add(new FaceOffFighterView
                {
                    Name = $"Fighter {i}",
                    Level = i * 3,
                    Rarity = (CardRarity)(i % 4),
                    Hp = 100 - i * 15,
                    MaxHp = 100,
                    ArtPath = i % 2 == 0 ? null : "/no/such/card.jpg",
                    Status = i == 0 ? "PICKED" : "THINKING",
                    Out = i == 5,
                    Winner = i == 1,
                    RevealAction = i == 2 ? "GAMBLE" : null,
                    Roll = i == 2 ? 17 : (int?)null,
                    TargetName = i == 2 ? "Fighter 0" : null,
                    DamageTaken = i == 3 ? 23 : (int?)null
                });
            }
            return scene;
        }

        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(6)]
        public void Arena_RendersAtItsSize(int fighters)
        {
            byte[] png = FaceOffRenderer.Render(SceneWith(fighters)).ToArray();

            using Image image = Image.Load(png);
            Assert.Equal(FaceOffRenderer.Width, image.Width);
            Assert.Equal(FaceOffRenderer.HeightFor(fighters), image.Height);
        }

        [Fact]
        public void SixFighters_UseTwoRows()
        {
            Assert.True(FaceOffRenderer.HeightFor(6) > FaceOffRenderer.HeightFor(3));
            Assert.Equal(FaceOffRenderer.HeightFor(2), FaceOffRenderer.HeightFor(3));
        }
    }
}
