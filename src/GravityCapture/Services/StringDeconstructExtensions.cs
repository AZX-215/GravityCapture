using System;

namespace GravityCapture.Services
{
    // Lets code do: var (key, url) = someString;
    public static class StringDeconstructExtensions
    {
        public static void Deconstruct(this string? s, out string first, out string second)
        {
            first = string.Empty;
            second = s?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(s)) return;

            var parts = s.Split(new[] { '|', ',', ';' }, 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2) { first = parts[0]; second = parts[1]; return; }

            var i = s.IndexOf(' ');
            if (i > 0) { first = s[..i].Trim(); second = s[(i + 1)..].Trim(); }
        }
    }
}
