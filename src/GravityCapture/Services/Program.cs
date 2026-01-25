#if SMOKE
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GravityCapture.Models;

namespace GravityCapture.Services
{
    internal static class Program
    {
        private const string DefaultImagePath =
            @"D:\stage-repositories\GravityCapture\test\frame-0000-a_up.png";

        public static async Task<int> Main(string[] args)
        {
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            var settings = AppSettings.Load();
            var remote = new RemoteOcrService(settings);

            if (args.Length >= 2 && args[0].Equals("--file", StringComparison.OrdinalIgnoreCase))
            {
                var path = args[1];
                if (!File.Exists(path)) { Console.WriteLine($"File not found: {path}"); return 2; }
                await using var fs = File.OpenRead(path);
                var res = await remote.ExtractAsync(fs, cts.Token);
                Print(res);
                return 0;
            }

            if (args.Length >= 2 && args[0].Equals("--watch", StringComparison.OrdinalIgnoreCase))
            {
                var dir = args[1];
                if (!Directory.Exists(dir)) { Console.WriteLine($"Directory not found: {dir}"); return 2; }

                Console.WriteLine($"Watching {dir} for *.png, *.jpg, *.jpeg");
                using var watcher = new FileSystemWatcher(dir)
                { IncludeSubdirectories = false, EnableRaisingEvents = true, Filter = "*.*" };

                var tcs = new TaskCompletionSource<object?>();
                watcher.Created += async (_, e) =>
                {
                    var ext = Path.GetExtension(e.FullPath).ToLowerInvariant();
                    if (ext is not ".png" and not ".jpg" and not ".jpeg") return;

                    try
                    {
                        await using var fs = OpenWhenReady(e.FullPath, attempts: 10, delayMs: 100);
                        var res = await remote.ExtractAsync(fs, cts.Token);
                        Console.WriteLine($"\n== {Path.GetFileName(e.FullPath)} ==");
                        Print(res);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error processing {e.FullPath}: {ex.Message}");
                    }
                };

                cts.Token.Register(() => tcs.TrySetResult(null));
                await tcs.Task;
                return 0;
            }

            if (!File.Exists(DefaultImagePath)) { Console.WriteLine($"Default image not found: {DefaultImagePath}"); return 2; }
            await using (var fs = File.OpenRead(DefaultImagePath))
            {
                var res = await remote.ExtractAsync(fs, cts.Token);
                Print(res);
            }
            return 0;
        }

        private static FileStream OpenWhenReady(string path, int attempts, int delayMs)
        {
            for (int i = 0; i < attempts; i++)
            {
                try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
                catch (IOException) { Thread.Sleep(delayMs); }
            }
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        private static void Print(ExtractResponse res)
        {
            Console.WriteLine($"engine={res.Engine} conf={res.Conf:0.###}");
            foreach (var line in res.Lines)
            {
                var bbox = line.Bbox is { Length: > 0 } ? $" [{string.Join(",", line.Bbox)}]" : "";
                Console.WriteLine($"{line.Text} (conf {line.Conf:0.00}){bbox}");
            }
        }
    }
}
#endif
