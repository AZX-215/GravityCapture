using System;
using System.Text.RegularExpressions;

namespace GravityCapture.Services
{
    /// <summary>
    /// Parses raw ASA tribe log lines into a payload we can POST.
    /// Heuristics are simple on purpose; we can expand as you share more samples.
    /// </summary>
    public static class LogLineParser
    {
        // Day/time prefix: "Day 6006, 19:43:49: <message>"
        private static readonly Regex DayTimeRx =
            new Regex(@"^\s*Day\s+(?<day>\d+),\s*(?<time>\d{1,2}:\d{2}:\d{2}):\s*(?<msg>.+)\s*$",
                      RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Common message patterns
        private static readonly Regex YourWasKilledRx =
            new Regex(@"^\s*Your\s+(?<actor>.+?)\s+was killed!\s*$",
                      RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex StarvedToDeathRx =
            new Regex(@"^\s*(?<actor>.+?)\s+starved to death!\s*$",
                      RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex TribeKilledRx =
            new Regex(@"^\s*Your\s+Tribe\s+killed\s+(?<actor>.+?)\s*!",
                      RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex ClaimedRx =
            new Regex(@"^\s*Human\s+claimed\s+'(?<actor>.+?)'!",
                      RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        public static (bool ok, LogIngestClient.TribeEvent? evt, string? error) TryParse(string rawLine, string server, string tribe)
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

            if (YourWasKilledRx.IsMatch(message))
            {
                actor = YourWasKilledRx.Match(message).Groups["actor"].Value;
                severity = "CRITICAL";
                category = "TAME_DEATH";
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
            else if (message.Contains("was destroyed", StringComparison.OrdinalIgnoreCase) ||
                     message.Contains("destroyed by", StringComparison.OrdinalIgnoreCase))
            {
                severity = "CRITICAL";
                category = "STRUCTURE_DESTROYED";
                // try to pull "Your <actor> was destroyed"
                var idx = message.IndexOf("was destroyed", StringComparison.OrdinalIgnoreCase);
                if (idx > 5 && message.StartsWith("Your ", StringComparison.OrdinalIgnoreCase))
                    actor = message.Substring(5, idx - 5).Trim();
            }
            else
            {
                // Fallback – unknown category, keep INFO
                category = "UNKNOWN";
            }

            var evt = new LogIngestClient.TribeEvent(
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
