using System.Text.Json;

namespace DevDeck.Web.Services.Portability;

public static class PortabilityJson
{
    public const int CurrentSchemaVersion = 1;

    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
