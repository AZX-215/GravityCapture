#nullable enable
using System;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace GravityCapture
{
    /// <summary>
    /// Persists window geometry and safely forwards saving of the settings object
    /// without requiring a strong reference to its concrete type.
    /// </summary>
    internal static class MainWindowPersistence
    {
        private const string FileName = "window.json";

        private static string DirPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GravityCapture");

        private static string FilePath => Path.Combine(DirPath, FileName);

        public static void SaveWindowState(double left, double top, double width, double height, bool isMaximized)
        {
            Directory.CreateDirectory(DirPath);
            var dto = new WindowStateDto
            {
                Left = left,
                Top = top,
                Width = width,
                Height = height,
                Maximized = isMaximized
            };
            var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }

        public static bool TryLoadWindowState(out double left, out double top, out double width, out double height, out bool isMaximized)
        {
            left = top = width = height = 0;
            isMaximized = false;

            try
            {
                if (!File.Exists(FilePath)) return false;
                var dto = JsonSerializer.Deserialize<WindowStateDto>(File.ReadAllText(FilePath));
                if (dto is null) return false;

                left = dto.Left;
                top = dto.Top;
                width = dto.Width;
                height = dto.Height;
                isMaximized = dto.Maximized;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Calls a public instance Save() on the provided settings object if it exists.
        /// Avoids CS1061 when the compile-time type is object.
        /// </summary>
        public static void SaveSettingsObject(object? settings)
        {
            if (settings is null) return;
            try
            {
                var mi = settings.GetType().GetMethod("Save", BindingFlags.Instance | BindingFlags.Public);
                mi?.Invoke(settings, null);
            }
            catch
            {
                // swallow – persistence isn't critical for app flow
            }
        }

        private sealed class WindowStateDto
        {
            public double Left { get; set; }
            public double Top { get; set; }
            public double Width { get; set; }
            public double Height { get; set; }
            public bool Maximized { get; set; }
        }
    }
}
