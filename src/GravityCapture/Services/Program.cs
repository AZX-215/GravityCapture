using System;
using System.Threading.Tasks;

namespace GravityCapture.Services;

internal class Program
{
    static async Task<int> Main(string[] args)
    {
        var imagePath = args.Length > 0
            ? args[0]
            : @"D:\stage-repositories\GravityCapture\test\frame-0000-a_up.png";

        // You can override with env var OCR_BASE_URL, e.g. https://screenshots-api-stage-production.up.railway.app
        using var client = new OcrClient();

        Console.WriteLine($"Sending image: {imagePath}");
        try
        {
            var result = await client.ExtractAsync(imagePath);
            Console.WriteLine($"engine: {result.Engine} | conf: {result.Confidence:F3}");
            foreach (var line in result.Lines)
            {
                Console.WriteLine($"{line.Text}  (conf {line.Confidence:F2})");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }
}
