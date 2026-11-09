using System.Text.Json;
using System.Text.Json.Serialization;

namespace DownloadDock.Integration;

/// <summary>Serialization settings shared by both sides: camelCase, no nulls, strict depth.</summary>
public static class IntegrationJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        MaxDepth = 16,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static JsonElement? ToElement(object? value)
        => value is null ? null : JsonSerializer.SerializeToElement(value, value.GetType(), Options);

    public static T? FromElement<T>(JsonElement? element)
    {
        if (element is not { } e || e.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return default;
        return e.Deserialize<T>(Options);
    }

    public static byte[] Serialize(IntegrationMessage message) => JsonSerializer.SerializeToUtf8Bytes(message, Options);

    /// <summary>Parses a message; throws <see cref="IntegrationException"/> (invalidMessage) on malformed input.</summary>
    public static IntegrationMessage Deserialize(ReadOnlySpan<byte> utf8)
    {
        IntegrationMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<IntegrationMessage>(utf8, Options);
        }
        catch (JsonException ex)
        {
            throw new IntegrationException(IntegrationErrorCodes.InvalidMessage, "Malformed JSON: " + ex.Message, ex);
        }
        if (message is null || string.IsNullOrEmpty(message.Type))
            throw new IntegrationException(IntegrationErrorCodes.InvalidMessage, "Message has no type.");
        return message;
    }
}
