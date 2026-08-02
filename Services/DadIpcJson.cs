using System.Text.Json;

namespace dad.Services;

internal static class DadIpcJson
{
    // Review M2: cap nesting depth so a malicious/garbled peer payload can't exhaust the stack/CPU.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    public static T? Deserialize<T>(string json)
        => TryDeserialize(json, out T? value, out _) ? value : default;

    public static bool TryDeserialize<T>(string json, out T? value, out string rejectionReason)
    {
        value = default;
        rejectionReason = string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            rejectionReason = "Wire payload is empty.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = JsonOptions.MaxDepth });
            if (!DadWireIngressNormalizer.TryValidateRequiredJson(typeof(T), document.RootElement, out rejectionReason))
                return false;

            value = JsonSerializer.Deserialize<T>(json, JsonOptions);
            if (value == null)
            {
                rejectionReason = $"Wire payload did not contain a {typeof(T).Name}.";
                return false;
            }

            if (!DadWireIngressNormalizer.TryNormalize(value, out rejectionReason))
            {
                value = default;
                return false;
            }

            return true;
        }
        catch (JsonException exception)
        {
            rejectionReason = exception.Message;
            return false;
        }
        catch (NotSupportedException exception)
        {
            rejectionReason = exception.Message;
            return false;
        }
    }

    public static T? DeserializeRaw<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return default;

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
        catch (NotSupportedException)
        {
            return default;
        }
    }

    public static T? DeepClone<T>(T? value)
        => value == null ? default : Deserialize<T>(Serialize(value));
}
