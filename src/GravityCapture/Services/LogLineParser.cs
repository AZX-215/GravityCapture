using System;
using System.Text.RegularExpressions;
using GravityCapture.Models;

namespace GravityCapture.Services
{
    /// <summary>
    /// Parses OCR'ed tribe log lines into a TribeEvent suitable for posting.
    /// Emits string severities ("CRITICAL" | "WARNING" | "INFO") and category strings.
    /// </summary>
    public static class LogLineParser
    {
        // "Day 6022, 03:59:40: <message>"
        private static readonly Regex RxHeader = new(
            @"^\s*Day\s*(?<day>\d+)\s*,\s*(?<time>\d{1,2}:\d{2}:\d{2})\s*:\s*(?<body>.+?)\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        // Tribemate killed:
        //  "Tribemember AZX - Lvl 195 was killed!"
        //  "Your Tribemate Bob - Lvl 100 was killed!"
        private static readonly Regex RxTribeMateKilled = new(
            @"^(?:(?:Your\s+)?Tribe(?:mate|member)\s+)(?<name>.+?)\s*-\s*Lvl\s*(?<lvl>\d+)\s*was\s+killed!?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        // Tame killed:
        //  "Your Pegomastax - Lvl 264 (Pegomastax) was killed!"
        private static readonly Regex RxTameKilled = new(
            @"^(?:Your\s+)?(?<actor>[^-]+?)\s*-\s*Lvl\s*(?<lvl>\d+).*?was\s+killed!?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        // Structure destroyed:
        //  "Your Auto Turret was destroyed!"
        private static readonly Regex RxStructureDestroyed = new(
            @"^(?:Your\s+)?(?<thing>.+?)\s+was\s+destroyed!?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        // Structure damaged:
        //  "Your Stone Wall took damage from Bob - Lvl 205 (Rifle)"
        //  "Your Stone Wall was damaged by Bob - Lvl 205"
        private static readonly Regex RxStructureDamaged = new(
            @"^(?:Your\s+)?(?<thing>.+?)\s+(?:took\s+damage|was\s+damaged)(?:\s+(?:from|by)\s+(?<by>.+?))?[.!]?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>
        /// Try to parse one OCR line (must start with "Day ...").
        /// Returns (ok, evt, error). On success, evt is ready to post.
        /// </summary>
        public static (bool ok, TribeEvent? evt, string? error) TryParse(string rawLine, string server, string tribe)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
                return (false, null, "empty");

            var m = RxHeader.Match(rawLine);
            if (!m.Success)
                return (false, null, "no-day-header");

            int arkDay = SafeInt(m.Groups["day"].Value);
            string arkTime = m.Groups["time"].Value.Trim();
            string body = m.Groups["body"].Value.Trim();

            // Defaults
            string category = "UNKNOWN";
            string severity = "INFO";
            string actor = string.Empty;

            // 1) Tribemate death
            var tm = RxTribeMateKilled.Match(body);
            if (tm.Success)
            {
                category = "TRIBE_MATE_DEATH";
                severity = "CRITICAL";
                actor = $"Tribemate {tm.Groups["name"].Value.Trim()} - Lvl {tm.Groups["lvl"].Value}";
                return (true, NewEvent(server, tribe, arkDay, arkTime, severity, category, actor, body, rawLine), null);
            }

            // 2) Tame death
            var tk = RxTameKilled.Match(body);
            if (tk.Success)
            {
                category = "TAME_DEATH";
                severity = "CRITICAL";
                actor = $"{tk.Groups["actor"].Value.Trim()} - Lvl {tk.Groups["lvl"].Value}";
                return (true, NewEvent(server, tribe, arkDay, arkTime, severity, category, actor, body, rawLine), null);
            }

            // 3) Structure destroyed
            var sd = RxStructureDestroyed.Match(body);
            if (sd.Success)
            {
                category = "STRUCTURE_DESTROYED";
                severity = "CRITICAL";
                actor = sd.Groups["thing"].Value.Trim();
                return (true, NewEvent(server, tribe, arkDay, arkTime, severity, category, actor, body, rawLine), null);
            }

            // 4) Structure damaged
            var dmg = RxStructureDamaged.Match(body);
            if (dmg.Success)
            {
                category = "STRUCTURE_DAMAGE";
                severity = "WARNING"; // downgraded vs destroyed
                var thing = dmg.Groups["thing"].Value.Trim();
                var by = dmg.Groups["by"].Success ? dmg.Groups["by"].Value.Trim() : "";
                actor = string.IsNullOrEmpty(by) ? thing : $"{thing} ← {by}";
                return (true, NewEvent(server, tribe, arkDay, arkTime, severity, category, actor, body, rawLine), null);
            }

            // Fallback (unclassified)
            return (true, NewEvent(server, tribe, arkDay, arkTime, "INFO", "UNKNOWN", "", body, rawLine), null);
        }

        private static TribeEvent NewEvent(
            string server, string tribe, int arkDay, string arkTime,
            string severity, string category, string actor, string message, string rawLine)
            => new TribeEvent(server, tribe, arkDay, arkTime, severity, category, actor, message, rawLine);

        private static int SafeInt(string s) => int.TryParse(s, out var i) ? i : 0;
    }
}
