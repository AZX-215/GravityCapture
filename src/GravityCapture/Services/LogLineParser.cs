using System;
using System.Text.RegularExpressions;
using GravityCapture.Models;

namespace GravityCapture.Services
{
    /// <summary>
    /// Parses a single OCR tribe-log line into a TribeEvent (server, tribe, day, time, severity, category, actor, message, raw_line).
    /// Robust to minor OCR noise and line wraps from HDR/SDR captures.
    /// </summary>
    public static class LogLineParser
    {
        // Day header:  Day 6031, 02:12:10: <message>
        private static readonly Regex RxHeader = new(
            @"^\s*Day\s*(?<day>\d+)\s*,\s*(?<time>\d{1,2}:\d{2}:\d{2})\s*:\s*(?<msg>.+?)\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // ---------- Classifiers (tolerant regex) ----------

        // STRUCTURES
        private static readonly Regex RxAutoDecayDestroyed = new(
            @"\bYour\b.*?\b(auto[- ]?decay)\b.*?\bdestroyed\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Generic "was destroyed" (non auto-decay)
        private static readonly Regex RxWasDestroyed = new(
            @"\bYour\b.*?\bwas\b.*?\bdestroyed\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Human/Player demolished a structure
        private static readonly Regex RxDemolished = new(
            @"\b(Human|[A-Za-z0-9_]+)\b\s+\bdemolished\b\s+a\s+['“”]?(?<what>.+?)['“”]?!?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Anti-meshing (coordinates optional)
        private static readonly Regex RxAntiMeshing = new(
            @"\bAnti[- ]?meshing\b.*?\bdestroyed\b.*?(Item\s+Cache)?(?:(?:\s*at\s*X\s*=\s*(?<x>-?\d+(?:\.\d+)?))\s*Y\s*=\s*(?<y>-?\d+(?:\.\d+)?)\s*Z\s*=\s*(?<z>-?\d+(?:\.\d+)?))?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // TELEPORTER privacy (treat as CRITICAL regardless of log color)
        private static readonly Regex RxTeleporterPrivacy = new(
            @"\bset\b.*?\b(Tek\s+Teleporter|Teleporter|TP)\b.*?\bto\b\s*(?<mode>private|public)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // PVP / kills
        private static readonly Regex RxTribeKilled = new(
            @"\b(Your\s+Tribe|Human)\s+killed\b\s+.+?-\s*Lvl\s*\d+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex RxWasKilled = new(
            @"\bwas\b\s*killed\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Tames / care / misc
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

            var m = RxHeader.Match(rawLine);
            if (!m.Success)
                return (false, null, "no_header");

            int arkDay = SafeInt(m.Groups["day"].Value);
            string arkTime = m.Groups["time"].Value.Trim();
            string msg = NormalizeMessage(m.Groups["msg"].Value);

            // Defaults
            string category = "UNKNOWN";
            string severity = "INFO";
            string actor = "";

            // ---- Severity policy (your spec) ----
            // CRITICAL:
            // - player/tame/tribemate deaths (incl. enemy players/tames our tribe kills)
            // - teleporter set public/private
            // - non auto-decay destroyed structures
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
            // WARNING:
            // - auto-decay destroyed
            // - demolished
            // - starved
            // - cryopod death
            // - anti-meshing of anything
            else if (RxAutoDecayDestroyed.IsMatch(msg))
            {
                category = "STRUCTURE_AUTO_DECAY";
                severity = "WARNING";
            }
            else if (RxDemolished.IsMatch(msg))
            {
                category = "STRUCTURE_DEMOLISHED";
                severity = "WARNING";
                var md = RxDemolished.Match(msg);
                if (md.Success) actor = md.Groups["what"].Value.Trim();
            }
            else if (RxStarved.IsMatch(msg))
            {
                category = "TAME_STARVED";
                severity = "WARNING";
            }
            else if (RxCryopodDeath.IsMatch(msg))
            {
                category = "TAME_CRYO_DEATH";
                severity = "WARNING";
            }
            else if (RxAntiMeshing.IsMatch(msg))
            {
                var mm = RxAntiMeshing.Match(msg);
                category = "ANTI_MESHING";
                severity = "WARNING";
                if (mm.Success && mm.Groups["x"].Success)
                {
                    actor = $"X={mm.Groups["x"].Value}, Y={mm.Groups["y"].Value}, Z={mm.Groups["z"].Value}";
                }
            }
            // SUCCESS/INFO (least severe):
            // - tamed/claimed/froze
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
                category = "TAME_FROZEN";
                severity = "INFO";
            }
            else if (RxWasKilled.IsMatch(msg))
            {
                // Generic deaths we didn't bucket above
                category = "DEATH";
                severity = "CRITICAL";
            }

            var ev = new TribeEvent(
                server: server ?? string.Empty,
                tribe: tribe ?? string.Empty,
                ark_day: arkDay,
                ark_time: arkTime,
                severity: severity,
                category: category,
                actor: actor,
                message: msg,
                raw_line: rawLine.Trim());

            return (true, ev, null);
        }

        // ---------- helpers ----------
        private static int SafeInt(string s) => int.TryParse(s, out var i) ? i : 0;

        private static string NormalizeMessage(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;

            var t = s;

            // Normalize quotes/dashes & strip HTML-ish emoji noise we saw in samples
            t = Regex.Replace(t, @"<img[^>]*>", "", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"\bChatImage\b.*?(?=\s|$)", "", RegexOptions.IgnoreCase);

            t = t.Replace('’', '\'')
                 .Replace('‘', '\'')
                 .Replace('“', '"')
                 .Replace('”', '"')
                 .Replace('–', '-')
                 .Replace('—', '-')
                 .Replace('‐', '-');

            // OCR corrections seen in your captures
            t = Regex.Replace(t, @"\bJ\s*urret\b", "Turret", RegexOptions.IgnoreCase); // Jurret -> Turret
            t = Regex.Replace(t, @"\bJurret\b", "Turret", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"\bLvI\b", "Lvl", RegexOptions.IgnoreCase);        // I vs l
            t = Regex.Replace(t, @"\bIvl\b", "Lvl", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"\bIvl\b", "Lvl", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"\bget\s+Trade\s+TP\b", "set Trade TP", RegexOptions.IgnoreCase); // mis-OCR "get" -> "set"

            // Collapse whitespace & fix punctuation artifacts (e.g., 'Your,,Heavy', stray ']')
            t = Regex.Replace(t, @"\s+", " ");
            t = Regex.Replace(t, @",\s*,+", ", ");
            t = Regex.Replace(t, @"\s+,", ",");
            t = Regex.Replace(t, @",\s+", ", ");
            t = Regex.Replace(t, @"\s?]\s?", "]");         // tighten brackets
            t = Regex.Replace(t, @"\s+'", " '");           // spacing before quotes

            // Remove dangling commas inserted by OCR before words
            t = Regex.Replace(t, @"(?<=\b)(,)(?=\w)", " ");

            return t.Trim();
        }
    }
}
