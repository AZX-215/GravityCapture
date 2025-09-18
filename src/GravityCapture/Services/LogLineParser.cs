using GravityCapture.Models;
using System.Text.RegularExpressions;

namespace GravityCapture.Services
{
    /// <summary>
    /// Parses a single OCR tribe-log line into a TribeEvent (server, tribe, day, time, severity, category, actor, message, raw_line).
    /// Robust to minor OCR noise and line wraps from captures.
    /// </summary>
    public static class LogLineParser
    {
        // Allow message to span lines (fixes "no_header" on wrapped input)
        private static readonly Regex RxHeader = new(
            @"^\s*Day\s*(?<day>\d+)\s*,\s*(?<time>\d{1,2}:\d{2}:\d{2})\s*:\s*(?<msg>[\s\S]+?)\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // ---------- Classifiers ----------
        private static readonly Regex RxAutoDecayDestroyed = new(
            @"\bYour\b.*?\b(auto[- ]?decay)\b.*?\bdestroyed\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex RxWasDestroyed = new(
            @"\bYour\b.*?\bwas\b.*?\bdestroyed\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex RxDemolished = new(
            @"\b(Human|[A-Za-z0-9_]+)\b\s+\bdemolished\b\s+a\s+['“”]?(?<what>.+?)['“”]?!?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex RxAntiMeshing = new(
            @"\bAnti[- ]?meshing\b.*?\bdestroyed\b.*?(Item\s+Cache)?(?:(?:\s*at\s*X\s*=\s*(?<x>-?\d+(?:\.\d+)?))\s*Y\s*=\s*(?<y>-?\d+(?:\.\d+)?)\s*Z\s*=\s*(?<z>-?\d+(?:\.\d+)?))?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex RxTeleporterPrivacy = new(
            @"\bset\b.*?\b(Tek\s+Teleporter|Teleporter|TP)\b.*?\bto\b\s*(?<mode>private|public)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex RxTribeKilled = new(
            @"\b(Your\s+Tribe|Human)\s+killed\b\s+.+?-\s*Lvl\s*\d+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex RxWasKilled = new(
            @"\bwas\b\s*killed\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex RxYourTribeTamed = new(
            @"\bYour\b\s+Tribe\s+Tamed\s+a\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex RxHumanTamed = new(
            @"\bHuman\b\s+Tamed\s+a\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex RxClaimed = new(
            @"\b(Human|Your\s+Tribe)\s+claimed\b\s+['“”]?(?<name>[^'”]+?)['”]?\s*-\s*Lvl\s*(?<lvl>\d+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex RxStarved = new(
            @"\bstarved\s+to\s+death\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex RxCryopodDeath = new(
            @"\bdied\s+in\s+a\s+Cryopod\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex RxFroze = new(
            @"\b(Human\s+froze|frozen)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex RxGroundUp = new(
            @"\bwas\s+ground\s+up\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// Parse OCR'd line to event. Returns (ok, event, error).
        /// </summary>
        public static (bool ok, TribeEvent? ev, string? error) TryParse(string rawLine, string server, string tribe)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
                return (false, null, "empty");

            // Keep original for message parsing; also make a one-line copy for DB unique key / dedupe.
            var rawOneLine = Regex.Replace(rawLine, @"\s*\r?\n\s*", " ").Trim();

            var m = RxHeader.Match(rawLine);
            if (!m.Success) return (false, null, "no_header");

            int arkDay = int.TryParse(m.Groups["day"].Value, out var d) ? d : 0;
            string arkTime = m.Groups["time"].Value.Trim();
            string msg = NormalizeMessage(m.Groups["msg"].Value);

            string category = "UNKNOWN";
            string severity = "INFO";
            string actor = "";

            // ---- severity/category mapping ----
            if (RxTeleporterPrivacy.IsMatch(msg))
            {
                var mm = RxTeleporterPrivacy.Match(msg);
                var mode = mm.Groups["mode"].Value.Equals("public", StringComparison.OrdinalIgnoreCase) ? "PUBLIC" : "PRIVATE";
                category = $"TELEPORTER_{mode}";
                severity = "CRITICAL";
            }
            else if (RxTribeKilled.IsMatch(msg) || (RxWasKilled.IsMatch(msg) && msg.Contains("Tribemember", StringComparison.OrdinalIgnoreCase)))
            {
                category = "KILL";
                severity = "CRITICAL";
            }
            else if (RxGroundUp.IsMatch(msg))
            {
                category = "TAME_DEATH";
                severity = "CRITICAL";
            }
            else if (RxWasDestroyed.IsMatch(msg) && !RxAutoDecayDestroyed.IsMatch(msg))
            {
                category = "STRUCTURE_DESTROYED";
                severity = "CRITICAL";
            }
            else if (RxAutoDecayDestroyed.IsMatch(msg))
            {
                category = "STRUCTURE_AUTO_DECAY";
                severity = "WARNING";
                actor = "Auto-decay";
            }
            else if (RxDemolished.IsMatch(msg))
            {
                category = "STRUCTURE_DESTROYED";
                severity = "WARNING";
                var md = RxDemolished.Match(msg);
                if (md.Success) actor = md.Groups["what"].Value.Trim();
            }
            else if (RxStarved.IsMatch(msg))
            {
                category = "TAME_DEATH";
                severity = "WARNING";
            }
            else if (RxCryopodDeath.IsMatch(msg))
            {
                category = "CRYOPOD_DEATH";
                severity = "WARNING";
            }
            else if (RxAntiMeshing.IsMatch(msg))
            {
                var mm = RxAntiMeshing.Match(msg);
                category = "ANTI_MESHING";
                severity = "WARNING";
                if (mm.Success && mm.Groups["x"].Success)
                    actor = $"X={mm.Groups["x"].Value}, Y={mm.Groups["y"].Value}, Z={mm.Groups["z"].Value}";
            }
            else if (RxYourTribeTamed.IsMatch(msg) || RxHumanTamed.IsMatch(msg))
            {
                category = "TAME_TAMED";
                severity = "SUCCESS";
            }
            else if (RxClaimed.IsMatch(msg))
            {
                category = "TAME_CLAIMED";
                severity = "INFO";
                var mc = RxClaimed.Match(msg);
                if (mc.Success) actor = $"{mc.Groups["name"].Value.Trim()} - Lvl {mc.Groups["lvl"].Value}";
            }
            else if (RxFroze.IsMatch(msg))
            {
                category = "CREATURE_FROZE";
                severity = "INFO";
            }
            else if (RxWasKilled.IsMatch(msg))
            {
                category = "PLAYER_DEATH";
                severity = "CRITICAL";
            }

            var ev = new TribeEvent(
                server ?? string.Empty,
                tribe ?? string.Empty,
                arkDay,
                arkTime,
                severity,
                category,
                actor,
                msg,
                rawOneLine);

            return (true, ev, null);
        }

        private static string NormalizeMessage(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;

            var t = s;
            t = Regex.Replace(t, @"<img[^>]*>", "", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"\bChatImage\b.*?(?=\s|$)", "", RegexOptions.IgnoreCase);

            t = t.Replace('’', '\'').Replace('‘', '\'')
                 .Replace('“', '"').Replace('”', '"')
                 .Replace('–', '-').Replace('—', '-').Replace('‐', '-');

            // Common OCR fixes
            t = Regex.Replace(t, @"\bJ\s*urret\b", "Turret", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"\bJurret\b", "Turret", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"\bLvI\b", "Lvl", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"\bIvl\b", "Lvl", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"\bget\s+Trade\s+TP\b", "set Trade TP", RegexOptions.IgnoreCase);

            // Whitespace/punctuation cleanup
            t = Regex.Replace(t, @"\s+", " ");
            t = Regex.Replace(t, @",\s*,+", ", ");
            t = Regex.Replace(t, @"\s+,", ",");
            t = Regex.Replace(t, @",\s+", ", ");
            t = Regex.Replace(t, @"\s?]\s?", "]");
            t = Regex.Replace(t, @"\s+'", " '");
            t = Regex.Replace(t, @"(?<=\b)(,)(?=\w)", " ");

            return t.Trim();
        }
    }
}
