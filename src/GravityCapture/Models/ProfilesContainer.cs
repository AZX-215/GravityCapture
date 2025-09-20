using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace GravityCapture.Models
{
    public sealed class ProfilesContainer
    {
        [JsonPropertyName("activeProfile")] public string ActiveProfile { get; set; } = "HDR";
        [JsonPropertyName("profiles")] public Dictionary<string, OcrProfile> Profiles { get; set; } = new();
    }
}
