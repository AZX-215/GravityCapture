using System;
using System.Windows;
using Microsoft.Extensions.Configuration;

namespace GravityCapture
{
    // Simple place to read settings from anywhere in the app
    public static class AppConfig
    {
        public static string ApiBaseUrl { get; internal set; } = "";
        public static string SharedKey  { get; internal set; } = "";
    }

    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Which environment config to load (e.g., GC_ENV=Stage -> appsettings.Stage.json)
            var env = Environment.GetEnvironmentVariable("GC_ENV") ?? "Production";

            // Load JSON + environment variables; json files are optional
            var config = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)                 // folder where the EXE + json live
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddJsonFile($"appsettings.{env}.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();

            // Bind the few keys we need (leave empty strings if missing)
            AppConfig.ApiBaseUrl = config["ApiBaseUrl"] ?? "";
            AppConfig.SharedKey  = config["Auth:SharedKey"] ?? "";

            // Optional fallbacks from env vars if JSON is absent
            if (string.IsNullOrWhiteSpace(AppConfig.ApiBaseUrl))
                AppConfig.ApiBaseUrl = Environment.GetEnvironmentVariable("API_BASE_URL") ?? "";
            if (string.IsNullOrWhiteSpace(AppConfig.SharedKey))
                AppConfig.SharedKey = Environment.GetEnvironmentVariable("GC_GL_SHARED_SECRET") ?? "";

            // (Optional) quick sanity log in Debug output
            System.Diagnostics.Debug.WriteLine(
                $"[GC] ENV={env}, ApiBaseUrl={(string.IsNullOrEmpty(AppConfig.ApiBaseUrl) ? "<empty>" : AppConfig.ApiBaseUrl)}, KeyLen={AppConfig.SharedKey?.Length ?? 0}");
        }
    }
}
