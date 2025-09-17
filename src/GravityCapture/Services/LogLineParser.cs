using System;
using System.Text.RegularExpressions;
using GravityCapture.Models;

namespace GravityCapture.Services
{
    /// <summary>
    /// Turns OCR'd tribe-log text lines into structured TribeEvent objects.
    /// This version recognizes "Tribemember/Tribemate was killed" variants and sets severity from category.
    /// </summary>
    public static class LogLineParser
    {
        // Day header: Day 6022, 03:59:40: <message>
        private static readonly Regex RxHeader = new(
            @"^\s*Day\s*(\d+)\s*,\s*(\d{1,2}:\d{2}:\d{2})\s*:\s*(.+?)\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        // Tame death:
        //   Your Pegomastax - Lvl 264 (Pegomastax) was killed!
        private static readonly Regex RxTameKilled = new(
            @"^(?:Your\s+)?(?<actor>[^-]+?)\s*-\s*Lvl\s*(?<lvl>\d+).*?was\s+killed!?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        // Tribe-mate death (multiple wordings seen across ASA/servers):
        //   Tribemember AZX - Lvl 195 was killed!
        //   Your Tribemate Bob - Lvl 100 was killed!
        private static readonly Regex RxTribeMateKilled = new(
            @"^(?:(?:Your\s+)?Tribe(?:mate|member)\s+)(?<name>.+?)\s*-\s*Lvl\s*(?<lvl>\d+)\s*was\s+killed!?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        // Structure destroyed:
        //   Your Auto Turret was destroyed!
        private static readonly Regex RxStructureDestroyed = new(
            @"^(?:Your\s+)?(?<thing>.+?)\s+was\s+destroyed!?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>
        /// Try to parse a single OCR line that begins with "Day ...".
        /// </summary>
        public static bool TryParse(string rawLine, out TribeEvent ev)
        {
            ev = null!;
            if (string.IsNullOrWhiteSpace(rawLine)) return false;

            var m = RxHeader.Match(rawLine);
            if (!m.Success) return false;

            var arkDay   = SafeInt(m.Groups[1].Value);
            var arkTime  = m.Groups[2].Value.Trim();
            var message  = m.Groups[3].Value.Trim();

            // --- classify message body
            TribeEventCategory category = TribeEventCategory.Unknown;
            string actor = string.Empty;

            // 1) Tribe-mate killed
            var tm = RxTribeMateKilled.Match(message);
            if (tm.Success)
            {
                category = TribeEventCategory.TribemateDeath;
                actor    = $"Tribemate {tm.Groups["name"].Value.Trim()} - Lvl {tm.Groups["lvl"].Value}";
                ev = Make(arkDay, arkTime, category, actor, message, Severity.Critical);
                return true;
            }

            // 2) Tame killed
            var tk = RxTameKilled.Match(message);
            if (tk.Success)
            {
                // Heuristic: if it looks like a living thing (has Lvl … was killed) but not a Tribemate,
                // treat as tame death.
                category = TribeEventCategory.TameDeath;
                actor    = $"{tk.Groups["actor"].Value.Trim()} - Lvl {tk.Groups["lvl"].Value}";
                ev = Make(arkDay, arkTime, category, actor, message, Severity.Critical);
                return true;
            }

            // 3) Structure destroyed
            var sd = RxStructureDestroyed.Match(message);
            if (sd.Success)
            {
                category = TribeEventCategory.StructureDestroyed;
                actor    = sd.Groups["thing"].Value.Trim();
                ev = Make(arkDay, arkTime, category, actor, message, Severity.Critical);
                return true;
            }

            // Fallback: keep the text, mark as Info
            ev = Make(arkDay, arkTime, TribeEventCategory.Unknown, string.Empty, message, Severity.Info);
            return true;
        }

        private static TribeEvent Make(
            int arkDay,
            string arkTime,
            TribeEventCategory category,
            string actor,
            string message,
            Severity severity)
        {
            return new TribeEvent
            {
                ArkDay   = arkDay,
                ArkTime  = arkTime,
                Category = category.ToString().ToUpperInvariant(),
                Actor    = actor,
                Message  = message,
                Severity = severity
            };
        }

        private static int SafeInt(string s) => int.TryParse(s, out var i) ? i : 0;
    }

    /// <summary>
    /// Categories we emit (align with what your API expects).
    /// </summary>
    public enum TribeEventCategory
    {
        Unknown = 0,
        TameDeath,
        TribemateDeath,
        StructureDestroyed
    }
}
