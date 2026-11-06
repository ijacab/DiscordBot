namespace DiscordBot.Games.Models.Dungeon
{
    /// <summary>One player in a dungeon party. Holds their own HP, gear and potions. The haul is shared.</summary>
    public class DungeonMember
    {
        public const int StartingHp = 30;

        public DungeonMember(ulong userId, string name)
        {
            UserId = userId;
            Name = name;
            Hp = StartingHp;
            MaxHp = StartingHp;
        }

        public ulong UserId { get; }
        public string Name { get; }
        public int Hp { get; set; }
        public int MaxHp { get; set; }
        public int Attack { get; set; } = 4;
        public int Defense { get; set; } = 1;
        public int Potions { get; set; }
        public int Kills { get; set; }
        public bool Alive { get; set; } = true;
    }
}
