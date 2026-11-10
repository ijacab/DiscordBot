using System;
using System.Linq;

namespace DiscordBot.Models
{
    /// <summary>
    /// Custom ids for buttons, select menus and modals: "game:action:arg1:arg2". The game prefix routes the click
    /// (see CommandManager_Components). Discord caps custom ids at 100 characters.
    /// </summary>
    public static class ComponentId
    {
        public const int MaxLength = 100;
        private const char Separator = ':';

        public static string Build(string game, string action, params string[] args)
        {
            if (string.IsNullOrWhiteSpace(game) || string.IsNullOrWhiteSpace(action))
                throw new ArgumentException("A component id needs a game and an action.");
            if (new[] { game, action }.Concat(args).Any(part => part == null || part.Contains(Separator)))
                throw new ArgumentException($"Component id parts can't contain '{Separator}'.");

            string id = string.Join(Separator, new[] { game, action }.Concat(args));
            if (id.Length > MaxLength)
                throw new ArgumentException($"Component id '{id}' is longer than {MaxLength} characters.");
            return id;
        }

        public static bool TryParse(string id, out string game, out string action, out string[] args)
        {
            game = null;
            action = null;
            args = Array.Empty<string>();
            if (string.IsNullOrEmpty(id) || id.Length > MaxLength)
                return false;

            string[] parts = id.Split(Separator);
            if (parts.Length < 2 || parts.Take(2).Any(string.IsNullOrWhiteSpace))
                return false;

            game = parts[0];
            action = parts[1];
            args = parts.Skip(2).ToArray();
            return true;
        }
    }
}
