// test/Program.cs
// Minimal smoke tester for the OCR API.
using GravityCapture.Services;

class Program
{
    static async Task<int> Main(string[] args)
    {
        var defaultPath = @"D:\stage-repositories\GravityCapture\test\frame-0000-a_up.png";
        var path = args.Length > 0 ? args[0] : defaultPath;
        var engine = Environment.GetEnvironmentVariable("OCR_ENGINE"); // optional: "tess" or "auto"

        try
        {
            using var client = new OcrClient(); // reads OCR_API_BASE if set
            var res = await client.ExtractFromFileAsync(path, engine);

            Console.WriteLine($"engine={res.Engine} conf={res.Conf:F3} lines={res.Lines.Count}");
            foreach (var line in res.Lines.Take(10))
            {
                Console.WriteLine($"{line.Conf:F2}\t{line.Text}\t[{string.Join(",", line.Bbox)}]");
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
