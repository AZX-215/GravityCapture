using GravityCapture.Models;
using System.Text.RegularExpressions;

namespace GravityCapture.Services
{
    public static class LogLineParser
    {
        // Header (supports wrapped lines)
        private static readonly Regex RxHeader = new(
            @"^\s*Day\s*(?<day>\d+)\s*,\s*(?<time>\d{1,2}:\d{2}:\d{2})\s*:\s*(?<msg>[\s\S]+?)\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // ---- classifiers ----

        // capture ANY actor text after "was destroyed by" up to "(" or "!" or end
        private static readonly Regex RxDestroyedBy = new(
            @"\bwas\s+destroyed\s+by\s+(?<actor>.+?)(?:\s*\(|!|$)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex RxWasDestroyed = new(
            @"\bYour\b.*?\bwas\b.*?\bdestroyed\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex RxAutoDecayDestroyed = new(
            @"\bYour\b.*?\bauto[- ]?decay\b.*?\bdestroyed\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // e.g. "Human demolished a 'Stone Wall'!"
        private static readonly Regex RxDemolished = new(
            @"\b(?<actor>[\p{L}\p{N}_ ][\p{L}\p{N}_ ]*)\s+demolished\b\s+a\b",
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

        public static (bool ok, TribeEvent? ev, string? error) TryParse(string rawLine, string server, string tribe)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
                return (false, null, "empty");

            // Flatten for DB dedupe
            var rawOneLine = Regex.Replace(rawLine, @"\s*\r?\n\s*", " ").Trim();

            var m = RxHeader.Match(rawLine);
            if (!m.Success) return (false, null, "no_header");

            int arkDay = int.TryParse(m.Groups["day"].Value, out var d) ? d : 0;
            string arkTime = m.Groups["time"].Value.Trim();
            string msg = NormalizeMessage(m.Groups["msg"].Value);

            string category = "UNKNOWN";
            string severity = "INFO";
            string actor = "";

            // ---- mapping ----
            if (RxTeleporterPrivacy.IsMatch(msg))
            {
                var mm = RxTeleporterPrivacy.Match(msg);
                var mode = mm.Groups["mode"].Value.Equals("public", System.StringComparison.OrdinalIgnoreCase) ? "PUBLIC" : "PRIVATE";
                category = $"TELEPORTER_{mode}";
                severity = "CRITICAL";
            }
            else if (RxTribeKilled.IsMatch(msg) || (RxWasKilled.IsMatch(msg) && msg.Contains("Tribemember", System.StringComparison.OrdinalIgnoreCase)))
            {
                category = "KILL";
                severity = "CRITICAL";
            }
            else if (RxGroundUp.IsMatch(msg))
            {
                category = "TAME_DEATH";
                severity = "CRITICAL";
            }
            else if (RxAutoDecayDestroyed.IsMatch(msg))
            {
                category = "STRUCTURE_AUTO_DECAY";
                severity = "WARNING";
                actor = "Auto-decay";
            }
            else if (RxWasDestroyed.IsMatch(msg))
            {
                category = "STRUCTURE_DESTROYED";
                severity = "CRITICAL";
                var md = RxDestroyedBy.Match(msg);
                if (md.Success) actor = CleanActor(md.Groups["actor"].Value);
            }
            else if (RxDemolished.IsMatch(msg))
            {
                category = "STRUCTURE_DESTROYED";
                severity = "WARNING";
                var md = RxDemolished.Match(msg);
                if (md.Success) actor = CleanActor(md.Groups["actor"].Value);
            }
            else if (RxStarved.IsMatch(msg))
            {
                category = "TAME_DEATH";
                severity = "WARNING";
                actor = "Starvation";
            }
            else if (RxCryopodDeath.IsMatch(msg))
            {
                category = "CRYOPOD_DEATH";
                severity = "WARNING";
                actor = "Cryopod";
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

            t = t.Replace('’', '\'').Replace('‘', '\'').Replace('“', '"').Replace('”', '"')
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

        private static string CleanActor(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            // remove trailing "(...)" like "(C4)", "(Tek Rifle)"
            var a = Regex.Replace(s, @"\s*\([^)]*\)\s*$", "");
            // trim trailing punctuation/spaces
            a = Regex.Replace(a, @"[.!:,;\s]+$", "");
            return a.Trim();
        }
    }
}
