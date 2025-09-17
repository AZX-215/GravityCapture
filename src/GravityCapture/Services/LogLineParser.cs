using System;
using System.Text.RegularExpressions;
using GravityCapture.Models;

namespace GravityCapture.Services
{
    /// <summary>
    /// Parses raw ASA tribe log lines into a payload we can POST.
    /// Targets red items (tame death, structure destroyed, tribe-mate death),
    /// but also labels other common lines.
    /// </summary>
    public static class LogLineParser
    {
        // Day/time prefix: "Day 6006, 19:43:49: <message>"
        private static readonly Regex DayTimeRx =
            new Regex(@"^\s*Day\s+(?<day>\d+),\s*(?<time>\d{1,2}:\d{2}:\d{2}):\s*(?<msg>.+)\s*$",
                      RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Tame died (generic and "killed by")
        private static readonly Regex YourWasKilledRx =
            new Regex(@"^\s*Your\s+(?<actor>.+?)\s+was killed!\s*$",
                      RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex YourWasKilledByRx =
            new Regex(@"^\s*Your\s+(?<actor>.+?)\s+was killed by\s+(?<killer>.+?)!\s*$",
                      RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        // Tame starved (usually not red)
        private static readonly Regex StarvedToDeathRx =
            new Regex(@"^\s*(?<actor>.+?)\s+starved to death!\s*$",
                      RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        // Tribe killed someone (PvP killfeed line)
        private static readonly Regex TribeKilledRx =
            new Regex(@"^\s*Your\s+Tribe\s+killed\s+(?<actor>.+?)\s*!",
                      RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        // Claimed tame
        private static readonly Regex ClaimedRx =
            new Regex(@"^\s*Human\s+claimed\s+'(?<actor>.+?)'!",
                      RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        // Structure destroyed (with or without "by")
        private static readonly Regex StructureDestroyedRx =
            new Regex(@"^\s*Your\s+(?<actor>.+?)\s+was destroyed(?: by .+?)?!\s*$",
                      RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        // Tribemate death (variants: Tribe Member / Tribemate)
        private static readonly Regex TribeMateKilledRx =
            new Regex(@"^\s*Your\s+Tribe(?:\s*Member|mate)\s+(?<actor>.+?)\s+was killed(?: by .+?)?!\s*$",
                      RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        public static (bool ok, TribeEvent? evt, string? error) TryParse(string rawLine, string server, string tribe)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
                return (false, null, "Empty log line.");

            // Defaults if day/time missing
            int arkDay = 0;
            string arkTime = "";

            string message = rawLine.Trim();

            var m = DayTimeRx.Match(rawLine);
            if (m.Success)
            {
                _ = int.TryParse(m.Groups["day"].Value, out arkDay);
                arkTime = m.Groups["time"].Value;
                message = m.Groups["msg"].Value.Trim();
            }

            string severity = "INFO";
            string category = "GENERAL";
            string actor = "Unknown";

            if (YourWasKilledByRx.IsMatch(message))
            {
                actor = YourWasKilledByRx.Match(message).Groups["actor"].Value;
                severity = "CRITICAL";
                category = "TAME_DEATH";
            }
            else if (YourWasKilledRx.IsMatch(message))
            {
                actor = YourWasKilledRx.Match(message).Groups["actor"].Value;
                severity = "CRITICAL";
                category = "TAME_DEATH";
            }
            else if (StructureDestroyedRx.IsMatch(message) ||
                     message.Contains("was destroyed", StringComparison.OrdinalIgnoreCase) ||
                     message.Contains("destroyed by", StringComparison.OrdinalIgnoreCase))
            {
                // Try to extract the structure name (actor)
                var sm = StructureDestroyedRx.Match(message);
                if (sm.Success) actor = sm.Groups["actor"].Value;
                else if (message.StartsWith("Your ", StringComparison.OrdinalIgnoreCase))
                {
                    var idx = message.IndexOf("was destroyed", StringComparison.OrdinalIgnoreCase);
                    if (idx > 5) actor = message.Substring(5, idx - 5).Trim();
                }

                severity = "CRITICAL";
                category = "STRUCTURE_DESTROYED";
            }
            else if (TribeMateKilledRx.IsMatch(message))
            {
                actor = TribeMateKilledRx.Match(message).Groups["actor"].Value;
                severity = "CRITICAL";
                category = "TRIBE_MATE_DEATH";
            }
            else if (StarvedToDeathRx.IsMatch(message))
            {
                actor = StarvedToDeathRx.Match(message).Groups["actor"].Value;
                severity = "IMPORTANT";
                category = "TAME_STARVED";
            }
            else if (TribeKilledRx.IsMatch(message))
            {
                actor = TribeKilledRx.Match(message).Groups["actor"].Value;
                severity = "IMPORTANT";
                category = "TRIBE_KILL";
            }
            else if (ClaimedRx.IsMatch(message))
            {
                actor = ClaimedRx.Match(message).Groups["actor"].Value;
                severity = "INFO";
                category = "CLAIM";
            }
            else
            {
                category = "UNKNOWN";
            }

            var evt = new TribeEvent(
                server: server,
                tribe: tribe,
                ark_day: arkDay,
                ark_time: arkTime,
                severity: severity,
                category: category,
                actor: actor,
                message: message,
                raw_line: rawLine.Trim()
            );

            return (true, evt, null);
        }
    }
}
