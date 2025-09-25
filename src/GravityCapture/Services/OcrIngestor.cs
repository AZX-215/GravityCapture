using System;
using System.Collections.Generic;

namespace GravityCapture.Services
{
    // Core utilities only. All ScanAndPostAsync overloads live in OcrIngestor.LegacyCompat.cs
    public partial class OcrIngestor
    {
        private readonly object _gate = new();
        private readonly LinkedList<(string text, DateTime when)> _recent = new();
        private const int MaxRecent = 512;
        private static readonly TimeSpan Window = TimeSpan.FromMinutes(2);

        /// <summary>
        /// Returns true if this line has not been seen in the recent window. Adds it to the cache.
        /// Used to avoid re-posting duplicates from OCR noise.
        /// </summary>
        public bool TryRegisterLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            var now = DateTime.UtcNow;
            lock (_gate)
            {
                // prune old
                while (_recent.Count > 0 && now - _recent.First!.Value.when > Window)
                    _recent.RemoveFirst();

                // duplicate check
                foreach (var (t, _) in _recent)
                    if (string.Equals(t, text, StringComparison.Ordinal))
                        return false;

                _recent.AddLast((text, now));
                if (_recent.Count > MaxRecent)
                    _recent.RemoveFirst();

                return true;
            }
        }

        /// <summary>Clears the recent-line cache.</summary>
        public void ClearRecent() { lock (_gate) _recent.Clear(); }
    }
}
