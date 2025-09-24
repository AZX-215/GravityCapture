// src/GravityCapture/Services/OcrSmoke/Program.cs
// Minimal smoke tester for the OCR API.
//
// Usage:
//   OcrSmoke.exe [path] [engine] [baseUrl]
// Examples:
//   OcrSmoke.exe
//   OcrSmoke.exe "D:\stage-repositories\GravityCapture\test\frame-0000-a_up.png" tess
//   OcrSmoke.exe "D:\stage-repositories\GravityCapture\test\frame-0000-a_up.png" auto "https://screenshots-api-stage-production.up.railway.app"

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GravityCapture.Services; // OcrClient

namespace GravityCapture.Services.OcrSmoke
{
    internal class Program
    {
        static async Task<int> Main(string[] args)
        {
            // Defaults per your repo/layout
            var defaultPath = @"D:\stage-repositories\GravityCapture\test\frame-0000-a_up.png";

            var path    = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]) ? args[0] : defaultPath;
            var engine  = args.Length > 1 && !string.IsNullOrWhiteSpace(args[1]) ? args[1] : Environment.GetEnvironmentVariable("OCR_ENGINE"); // "tess" | "auto"
            var baseUrl = args.Length > 2 && !string.IsNullOrWhiteSpace(args[2]) ? args[2] : Environment.GetEnvironmentVariable("OCR_API_BASE");

            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"File not found: {path}");
                Console.Error.WriteLine(@"Usage: OcrSmoke.exe [path] [engine] [baseUrl]");
                return 2;
            }

            try
            {
                using var client = string.IsNullOrWhiteSpace(baseUrl) ? new OcrClient() : new OcrClient(baseUrl);

                var res = await client.ExtractFromFileAsync(path, engine);

                Console.WriteLine($"engine={res.Engine} conf={res.Conf:F3} lines={res.Lines.Count}");
                foreach (var line in res.Lines.Take(20))
                {
                    var bb = line.Bbox is { Length: > 0 } ? $"[{string.Join(",", line.Bbox)}]" : "[]";
                    Console.WriteLine($"{line.Conf:F2}\t{line.Text}\t{bb}");
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
}
