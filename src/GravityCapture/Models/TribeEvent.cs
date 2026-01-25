using System.Text.Json.Serialization;

namespace GravityCapture.Models
{
    public sealed class TribeEvent
    {
        [JsonPropertyName("server")]   public string server   { get; set; }
        [JsonPropertyName("tribe")]    public string tribe    { get; set; }
        [JsonPropertyName("ark_day")]  public int    ark_day  { get; set; }
        [JsonPropertyName("ark_time")] public string ark_time { get; set; }
        [JsonPropertyName("severity")] public string severity { get; set; }
        [JsonPropertyName("category")] public string category { get; set; }
        [JsonPropertyName("actor")]    public string actor    { get; set; }
        [JsonPropertyName("message")]  public string message  { get; set; }
        [JsonPropertyName("raw_line")] public string raw_line { get; set; }

        public TribeEvent(
            string server,
            string tribe,
            int ark_day,
            string ark_time,
            string severity,
            string category,
            string actor,
            string message,
            string raw_line)
        {
            this.server   = server;
            this.tribe    = tribe;
            this.ark_day  = ark_day;
            this.ark_time = ark_time;
            this.severity = severity;
            this.category = category;
            this.actor    = actor;
            this.message  = message;
            this.raw_line = raw_line;
        }
    }
}
