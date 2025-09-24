using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace GravityCapture.Services;

internal static class Program
{
    // Defaults for stage
    private const string DefaultBaseUrl =
        "https://screenshots-api-stage-production.up.railway.app";

    private const string DefaultImagePath =
        @"D:\stage-repositories\GravityCapture\test\frame-0000-a_up.png";

    private static string BaseUrl =>
        Environment.GetEnvironmentVariable("OCR_BASE_URL")?.TrimEnd('/')
        ?? DefaultBaseUrl;

    public static async Task Main(string[] args)
    {
        if (args.Length >= 1 && args[0].Equals("--watch", StringComparison.OrdinalIgnoreCase))
        {
            var dir = args.Length >= 2
                ? args[1]
                : Path.GetDirectoryName(DefaultImagePath) ?? ".";

            await WatchDirAsync(dir);
            return;
        }

        var file = args.Length >= 2 && args[0].Equals("--file", StringComparison.OrdinalIgnoreCase)
            ? args[1]
            : DefaultImagePath;

        await SendOnceAsync(file);
    }

    private static async Task SendOnceAsync(string path)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine($"File not found: {path}");
            return;
        }

        Console.WriteLine($"Sending image: {path}");
        using var client = new OcrClient(BaseUrl);

        var result = await client.ExtractAsync(path, CancellationToken.None);

        Console.WriteLine($"engine: {result.Engine} | conf: {result.Conf:0.###}");
        foreach (var line in result.Lines)
            Console.WriteLine($"{line.Text}  (conf {line.Conf:0.00})");
    }

    private static async Task WatchDirAsync(string dir)
    {
        Console.WriteLine($"Watching: {dir}");
        using var client = new OcrClient(BaseUrl);

        using var watcher = new FileSystemWatcher(dir)
        {
            IncludeSubdirectories = false,
            Filter = "*.*",
            EnableRaisingEvents = true
        };

        TaskCompletionSource tcs = new();

        watcher.Created += async (_, e) =>
        {
            var ext = Path.GetExtension(e.FullPath).ToLowerInvariant();
            if (ext is ".png" or ".jpg" or ".jpeg" or ".webp")
            {
                // wait a moment so the file is fully written
                await Task.Delay(250);
                try
                {
                    Console.WriteLine($"Detected: {e.FullPath}");
                    var res = await client.ExtractAsync(e.FullPath, CancellationToken.None);
                    Console.WriteLine($"engine: {res.Engine} | conf: {res.Conf:0.###}");
                    foreach (var line in res.Lines)
                        Console.WriteLine($"{line.Text}  (conf {line.Conf:0.00})");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error processing {e.FullPath}: {ex.Message}");
                }
            }
        };

        Console.CancelKeyPress += (_, __) => tcs.TrySetResult();

        await tcs.Task;
    }
}
