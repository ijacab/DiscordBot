using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace DiscordBot.Models
{
    /// <summary>
    /// The `.convo` knobs that can be changed while the bot runs (`.convoset`, bot admins), saved to a file next to the bot so they
    /// survive restarts and deploys. The defaults are the constants on <see cref="ConvoModel"/>. A model reads them as it generates,
    /// so a change applies to the very next `.convo`.
    /// </summary>
    public sealed class ConvoSettings
    {
        public double Chaos { get; set; } = ConvoModel.Chaos;
        public double Temperature { get; set; } = ConvoModel.Temperature;
        public double ReplyHookChance { get; set; } = ConvoModel.ReplyHookChance;
        public double OwnStartChance { get; set; } = ConvoModel.OwnStartChance;
        public double TopicLineChance { get; set; } = ConvoModel.TopicLineChance;
        public double TopicPull { get; set; } = ConvoModel.TopicPull;
        public double FavouredShare { get; set; } = ConvoModel.DefaultFavouredShare;
        public int MinLines { get; set; } = ConvoModel.MinLines;
        public int MaxLines { get; set; } = ConvoModel.MaxLines;
        public int SoloMinLines { get; set; } = ConvoModel.SoloMinLines;
        public int SoloMaxLines { get; set; } = ConvoModel.SoloMaxLines;
        public int MaxCopiedWords { get; set; } = ConvoModel.MaxCopiedWords;

        /// <summary>
        /// Users (besides the bot admins) who may run `.convoset`, saved with the settings. Admins add and remove them with
        /// `.convoset allow @user` and `.convoset deny @user`. `reset` leaves it alone.
        /// </summary>
        public List<ulong> AllowedUsers { get; set; } = new List<ulong>();

        public bool IsAllowed(ulong userId)
        {
            lock (AllowedUsers) return AllowedUsers.Contains(userId);
        }

        /// <summary>Lets this user run `.convoset`. False if they already could.</summary>
        public bool Allow(ulong userId)
        {
            lock (AllowedUsers)
            {
                if (AllowedUsers.Contains(userId)) return false;
                AllowedUsers.Add(userId);
                return true;
            }
        }

        /// <summary>Takes `.convoset` away from this user. False if they weren't on the list.</summary>
        public bool Deny(ulong userId)
        {
            lock (AllowedUsers) return AllowedUsers.Remove(userId);
        }

        public List<ulong> AllowedList()
        {
            lock (AllowedUsers) return AllowedUsers.ToList();
        }

        public const string FileName = "convo_settings.json";
        public static string DefaultPath => Path.Join(Directory.GetCurrentDirectory(), FileName);

        /// <summary>One setting: what it's called in `.convoset`, its allowed range and what it does.</summary>
        public sealed class Spec
        {
            public string Name { get; set; }
            public string Property { get; set; }
            public double Min { get; set; }
            public double Max { get; set; }
            public bool Whole { get; set; }
            /// <summary>A 0 to 1 chance that may be typed as a percentage ("85%").</summary>
            public bool Share { get; set; }
            public string About { get; set; }
        }

        public static readonly IReadOnlyList<Spec> Specs = new List<Spec>
        {
            new Spec { Name = "chaos", Property = nameof(Chaos), Min = 0, Max = 1, Share = true, About = "how often a word is picked from one word of context instead of two (higher = crazier)" },
            new Spec { Name = "temperature", Property = nameof(Temperature), Min = 0.5, Max = 5, About = "1 = words as often as they were said, higher = rarer follow-ups come up more" },
            new Spec { Name = "hook", Property = nameof(ReplyHookChance), Min = 0, Max = 1, Share = true, About = "how often a reply picks up a word from the line before" },
            new Spec { Name = "ownstart", Property = nameof(OwnStartChance), Min = 0, Max = 1, Share = true, About = "how often a line opens with a word its speaker really opens with" },
            new Spec { Name = "topicline", Property = nameof(TopicLineChance), Min = 0, Max = 1, Share = true, About = "with a topic: how often a line is built around it" },
            new Spec { Name = "topicpull", Property = nameof(TopicPull), Min = 0, Max = 1, Share = true, About = "with a topic: how hard each word step leans towards it" },
            new Spec { Name = "favoured", Property = nameof(FavouredShare), Min = 0, Max = 1, Share = true, About = "how often the favoured channels are used when they have something" },
            new Spec { Name = "minlines", Property = nameof(MinLines), Min = 1, Max = 20, Whole = true, About = "fewest lines in a convo" },
            new Spec { Name = "maxlines", Property = nameof(MaxLines), Min = 1, Max = 20, Whole = true, About = "most lines in a convo" },
            new Spec { Name = "solomin", Property = nameof(SoloMinLines), Min = 1, Max = 20, Whole = true, About = "fewest lines in a convo of one person (`.convo @someone`)" },
            new Spec { Name = "solomax", Property = nameof(SoloMaxLines), Min = 1, Max = 20, Whole = true, About = "most lines in a convo of one person" },
            new Spec { Name = "maxcopy", Property = nameof(MaxCopiedWords), Min = 0, Max = 40, Whole = true, About = "a line longer than this that repeats a real message is made again" },
        };

        public static Spec Find(string name)
        {
            name = (name ?? "").Trim().ToLowerInvariant();
            return Specs.FirstOrDefault(s => s.Name == name || s.Property.ToLowerInvariant() == name);
        }

        public double Get(Spec spec) => Convert.ToDouble(typeof(ConvoSettings).GetProperty(spec.Property).GetValue(this), CultureInfo.InvariantCulture);

        public static double DefaultOf(Spec spec) => Convert.ToDouble(typeof(ConvoSettings).GetProperty(spec.Property).GetValue(new ConvoSettings()), CultureInfo.InvariantCulture);

        private void Put(Spec spec, double value)
        {
            var property = typeof(ConvoSettings).GetProperty(spec.Property);
            property.SetValue(this, spec.Whole ? (object)(int)Math.Round(value) : value);
        }

        /// <summary>Sets one setting from typed text ("0.9", "90%", "4"). False with a reason when it isn't a number, is out of range, or would put minlines above maxlines.</summary>
        public bool TrySet(string name, string text, out string message)
        {
            var spec = Find(name);
            if (spec == null)
            {
                message = $"I don't know a setting called \"{name}\". `.convoset` lists them.";
                return false;
            }
            text = (text ?? "").Trim();
            bool percent = text.EndsWith("%");
            if (!double.TryParse(percent ? text.TrimEnd('%') : text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || double.IsNaN(value) || double.IsInfinity(value))
            {
                message = $"\"{text}\" isn't a number.";
                return false;
            }
            if (percent) value /= 100;
            if (spec.Whole && Math.Abs(value - Math.Round(value)) > 1e-9)
            {
                message = $"{spec.Name} is a whole number.";
                return false;
            }
            if (value < spec.Min || value > spec.Max)
            {
                message = $"{spec.Name} goes from {Show(spec, spec.Min)} to {Show(spec, spec.Max)}.";
                return false;
            }
            if (spec.Name == "minlines" && value > MaxLines) { message = $"minlines can't be above maxlines ({MaxLines})."; return false; }
            if (spec.Name == "maxlines" && value < MinLines) { message = $"maxlines can't be below minlines ({MinLines})."; return false; }
            if (spec.Name == "solomin" && value > SoloMaxLines) { message = $"solomin can't be above solomax ({SoloMaxLines})."; return false; }
            if (spec.Name == "solomax" && value < SoloMinLines) { message = $"solomax can't be below solomin ({SoloMinLines})."; return false; }

            double before = Get(spec);
            Put(spec, value);
            message = $"{spec.Name}: {Show(spec, before)} → **{Show(spec, Get(spec))}** (default {Show(spec, DefaultOf(spec))})";
            return true;
        }

        /// <summary>Puts one setting back to its default, or all of them when <paramref name="name"/> is empty. Null when there is no such setting.</summary>
        public string Reset(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                foreach (var spec in Specs) Put(spec, DefaultOf(spec));
                return "Every `.convo` setting is back to its default.";
            }
            var one = Find(name);
            if (one == null) return null;
            Put(one, DefaultOf(one));
            return $"{one.Name} is back to its default, {Show(one, DefaultOf(one))}.";
        }

        /// <summary>How a value is written: shares as percentages, the rest as plain numbers.</summary>
        public static string Show(Spec spec, double value) =>
            spec.Share ? $"{value * 100:0.#}%" : spec.Whole ? ((int)Math.Round(value)).ToString(CultureInfo.InvariantCulture) : value.ToString("0.##", CultureInfo.InvariantCulture);

        /// <summary>Every setting with its value, default and meaning, for `.convoset`.</summary>
        public List<string> Describe() => Specs.Select(s =>
        {
            double now = Get(s), def = DefaultOf(s);
            string value = Math.Abs(now - def) < 1e-9 ? Show(s, now) : $"**{Show(s, now)}** (default {Show(s, def)})";
            return $"`{s.Name}` {value}: {s.About}";
        }).ToList();

        public static ConvoSettings Load(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var loaded = JsonConvert.DeserializeObject<ConvoSettings>(File.ReadAllText(path)) ?? new ConvoSettings();
                    return loaded.Sanitised();
                }
            }
            catch (Exception)
            {
                //a damaged file is no reason to stop `.convo`: start from the defaults
            }
            return new ConvoSettings();
        }

        /// <summary>Anything outside its allowed range (a hand-edited file) goes back to the default.</summary>
        private ConvoSettings Sanitised()
        {
            foreach (var spec in Specs)
            {
                double v = Get(spec);
                if (double.IsNaN(v) || v < spec.Min || v > spec.Max) Put(spec, DefaultOf(spec));
            }
            if (MinLines > MaxLines)
            {
                MinLines = ConvoModel.MinLines;
                MaxLines = ConvoModel.MaxLines;
            }
            if (SoloMinLines > SoloMaxLines)
            {
                SoloMinLines = ConvoModel.SoloMinLines;
                SoloMaxLines = ConvoModel.SoloMaxLines;
            }
            return this;
        }

        public void Save(string path)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(this, Formatting.Indented));
            File.Move(tmp, path, true);
        }
    }
}
