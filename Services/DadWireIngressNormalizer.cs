using System.Collections;
using System.Reflection;
using System.Text.Json;
using dad.Models;

namespace dad.Services;

internal static class DadWireIngressNormalizer
{
    private const int MaximumDepth = 32;

    public static bool TryValidateRequiredJson(Type type, JsonElement root, out string reason)
    {
        reason = string.Empty;
        var requiresObject = type == typeof(DadWorkerExecutionCommand) ||
                             type == typeof(DadRunPlan) ||
                             type == typeof(DadClaimRequestDto) ||
                             type == typeof(DadAssemblyInstructionDto) ||
                             type == typeof(DadProfileUpdateRequest) ||
                             type == typeof(DadLaunchProfileUpdateRequest) ||
                             type == typeof(DadRosterAssignmentChangeRequest) ||
                             type == typeof(DadAggregateRosterCatalogRequest) ||
                             type == typeof(DadHubHello) ||
                             type == typeof(DadHubHeartbeat);
        if (!requiresObject)
            return true;
        if (root.ValueKind != JsonValueKind.Object)
            return Fail($"{type.Name} must be a JSON object.", out reason);

        if (type == typeof(DadWorkerExecutionCommand))
        {
            if (!TryGetRequiredObject(root, "plan", out var plan))
                return Fail("Worker command is missing required plan.", out reason);
            if (!TryGetRequiredObject(plan, "request", out _))
                return Fail("Worker command plan is missing required request.", out reason);
            if (!TryGetRequiredArray(plan, "modules"))
                return Fail("Worker command plan is missing required modules.", out reason);
        }
        else if (type == typeof(DadRunPlan))
        {
            if (!TryGetRequiredObject(root, "request", out _))
                return Fail("Run plan is missing required request.", out reason);
            if (!TryGetRequiredArray(root, "modules"))
                return Fail("Run plan is missing required modules.", out reason);
        }
        else if (type == typeof(DadClaimRequestDto) && !TryGetRequiredObject(root, "lease", out _))
        {
            return Fail("Claim request is missing required lease.", out reason);
        }
        else if (type == typeof(DadAssemblyInstructionDto) &&
                 !TryGetRequiredObject(root, "frozenInviter", out _))
        {
            return Fail("Assembly instruction is missing required frozen inviter.", out reason);
        }
        else if (type == typeof(DadProfileUpdateRequest) &&
                 !IsTrue(root, "updatePrimaryLaunchProfile") &&
                 !TryGetRequiredObject(root, "profile", out _))
        {
            return Fail("Profile update is missing required profile.", out reason);
        }
        else if (type == typeof(DadLaunchProfileUpdateRequest) &&
                 !TryGetRequiredObject(root, "profile", out _))
        {
            return Fail("Launch profile update is missing required profile.", out reason);
        }
        else if (type == typeof(DadRosterAssignmentChangeRequest) &&
                 !TryGetRequiredObject(root, "characterRef", out _))
        {
            return Fail("Roster assignment change is missing required character reference.", out reason);
        }
        else if (type == typeof(DadAggregateRosterCatalogRequest) &&
                 !TryGetRequiredObject(root, "plan", out _))
        {
            return Fail("Aggregate roster request is missing required refresh plan.", out reason);
        }
        else if ((type == typeof(DadHubHello) || type == typeof(DadHubHeartbeat)) &&
                 !TryGetRequiredObject(root, "participant", out _))
        {
            return Fail($"{type.Name} is missing required participant.", out reason);
        }

        return true;
    }

    public static bool TryNormalize(object? value, out string reason)
    {
        reason = string.Empty;
        if (value == null)
            return false;

        try
        {
            NormalizeObject(value, new HashSet<object>(ReferenceEqualityComparer.Instance), 0);
            return true;
        }
        catch (InvalidOperationException exception)
        {
            reason = exception.Message;
            return false;
        }
    }

    private static void NormalizeObject(object value, HashSet<object> visited, int depth)
    {
        if (depth > MaximumDepth)
            throw new InvalidOperationException($"Wire payload exceeds the {MaximumDepth}-level normalization limit.");

        var type = value.GetType();
        if (IsTerminal(type))
            return;

        if (!type.IsValueType && !visited.Add(value))
            return;

        if (value is IDictionary dictionary)
        {
            List<object>? nullStringValueKeys = null;
            var dictionaryType = FindGenericInterface(type, typeof(IDictionary<,>));
            var normalizesStringValues = dictionaryType?.GetGenericArguments()[1] == typeof(string);
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Value == null)
                {
                    if (normalizesStringValues && entry.Key != null)
                    {
                        nullStringValueKeys ??= [];
                        nullStringValueKeys.Add(entry.Key);
                    }
                    continue;
                }

                NormalizeObject(entry.Value, visited, depth + 1);
            }

            if (nullStringValueKeys != null)
            {
                foreach (var key in nullStringValueKeys)
                    dictionary[key] = string.Empty;
            }

            return;
        }

        if (value is IList list)
        {
            if (IsStringList(type))
            {
                for (var index = list.Count - 1; index >= 0; index--)
                {
                    if (list[index] == null)
                        list.RemoveAt(index);
                }
            }

            foreach (var item in list)
            {
                if (item != null)
                    NormalizeObject(item, visited, depth + 1);
            }
            return;
        }

        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanRead || property.GetIndexParameters().Length != 0)
                continue;

            var propertyValue = property.GetValue(value);
            if (propertyValue == null && property.CanWrite)
            {
                if (property.PropertyType == typeof(string))
                {
                    property.SetValue(value, string.Empty);
                    continue;
                }

                var emptyCollection = CreateEmptyCollection(property.PropertyType);
                if (emptyCollection != null)
                {
                    property.SetValue(value, emptyCollection);
                    propertyValue = emptyCollection;
                }
            }

            if (propertyValue != null)
                NormalizeObject(propertyValue, visited, depth + 1);
        }
    }

    private static object? CreateEmptyCollection(Type type)
    {
        if (type.IsArray)
            return Array.CreateInstance(type.GetElementType()!, 0);

        if (!type.IsAbstract && type.GetConstructor(Type.EmptyTypes) != null &&
            (typeof(IList).IsAssignableFrom(type) || typeof(IDictionary).IsAssignableFrom(type)))
        {
            return Activator.CreateInstance(type);
        }

        var listInterface = FindGenericInterface(type, typeof(IList<>)) ?? FindGenericInterface(type, typeof(ICollection<>));
        if (listInterface != null)
            return Activator.CreateInstance(typeof(List<>).MakeGenericType(listInterface.GetGenericArguments()[0]));

        var dictionaryInterface = FindGenericInterface(type, typeof(IDictionary<,>));
        return dictionaryInterface == null
            ? null
            : Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(dictionaryInterface.GetGenericArguments()));
    }

    private static Type? FindGenericInterface(Type type, Type definition)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == definition)
            return type;
        return type.GetInterfaces().FirstOrDefault(candidate =>
            candidate.IsGenericType && candidate.GetGenericTypeDefinition() == definition);
    }

    private static bool IsStringList(Type type)
    {
        var list = FindGenericInterface(type, typeof(IList<>)) ?? FindGenericInterface(type, typeof(ICollection<>));
        return list?.GetGenericArguments()[0] == typeof(string);
    }

    private static bool IsTerminal(Type type)
        => type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) ||
           type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan) ||
           type == typeof(Guid);

    private static bool TryGetRequiredObject(JsonElement parent, string name, out JsonElement value)
        => TryGetProperty(parent, name, out value) && value.ValueKind == JsonValueKind.Object;

    private static bool TryGetRequiredArray(JsonElement parent, string name)
        => TryGetProperty(parent, name, out var value) && value.ValueKind == JsonValueKind.Array;

    private static bool IsTrue(JsonElement parent, string name)
        => TryGetProperty(parent, name, out var value) &&
           value.ValueKind == JsonValueKind.True;

    private static bool TryGetProperty(JsonElement parent, string name, out JsonElement value)
    {
        foreach (var property in parent.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool Fail(string message, out string reason)
    {
        reason = message;
        return false;
    }
}
