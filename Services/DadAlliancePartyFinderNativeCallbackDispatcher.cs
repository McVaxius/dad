using System.Runtime.InteropServices;
using System.Text;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace dad.Services;

internal readonly record struct DadAlliancePfNativeCallbackTarget(
    DadAlliancePfJoinCallback Callback,
    nint AddonAddress);

internal readonly record struct DadAlliancePfNativeCallbackDispatchResult(
    bool Sent,
    string Error = "");

internal readonly record struct DadAlliancePfNativeCallbackTrace(
    string Phase,
    DadAlliancePfJoinAction Action,
    string Addon,
    int Ordinal,
    int Total,
    string PayloadTypes,
    bool UpdateState);

internal unsafe interface IDadAlliancePfNativeCallbackSink
{
    void Fire(
        nint addonAddress,
        uint valueCount,
        AtkValue* values,
        bool updateState);
}

internal sealed unsafe class DadAlliancePfAtkUnitBaseCallbackSink :
    IDadAlliancePfNativeCallbackSink
{
    public void Fire(
        nint addonAddress,
        uint valueCount,
        AtkValue* values,
        bool updateState)
        => ((AtkUnitBase*)addonAddress)->FireCallback(
            valueCount,
            values,
            updateState);
}

internal interface IDadAlliancePfNativeMemory
{
    nint AllocHGlobal(int byteCount);
    void FreeHGlobal(nint address);
}

internal sealed class DadAlliancePfNativeMemory :
    IDadAlliancePfNativeMemory
{
    public nint AllocHGlobal(int byteCount)
        => Marshal.AllocHGlobal(byteCount);

    public void FreeHGlobal(nint address)
        => Marshal.FreeHGlobal(address);
}

internal sealed unsafe class DadAlliancePartyFinderNativeCallbackDispatcher
{
    private const int MaximumPayloadValues = 32;
    private readonly IDadAlliancePfNativeCallbackSink sink;
    private readonly IDadAlliancePfNativeMemory memory;
    private readonly Action<DadAlliancePfNativeCallbackTrace>? trace;

    public DadAlliancePartyFinderNativeCallbackDispatcher()
        : this(
            new DadAlliancePfAtkUnitBaseCallbackSink(),
            new DadAlliancePfNativeMemory(),
            null)
    {
    }

    internal DadAlliancePartyFinderNativeCallbackDispatcher(
        Action<DadAlliancePfNativeCallbackTrace> trace)
        : this(
            new DadAlliancePfAtkUnitBaseCallbackSink(),
            new DadAlliancePfNativeMemory(),
            trace)
    {
    }

    internal DadAlliancePartyFinderNativeCallbackDispatcher(
        IDadAlliancePfNativeCallbackSink sink,
        IDadAlliancePfNativeMemory? memory = null,
        Action<DadAlliancePfNativeCallbackTrace>? trace = null)
    {
        this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
        this.memory = memory ?? new DadAlliancePfNativeMemory();
        this.trace = trace;
    }

    public DadAlliancePfNativeCallbackDispatchResult TryDispatch(
        DadAlliancePfJoinAction action,
        IReadOnlyList<DadAlliancePfJoinCallback> callbacks,
        Func<string, nint> resolveAddonAddress)
    {
        ArgumentNullException.ThrowIfNull(callbacks);
        ArgumentNullException.ThrowIfNull(resolveAddonAddress);
        if (callbacks.Count == 0)
        {
            return Failure(
                action,
                string.Empty,
                0,
                0,
                new InvalidOperationException(
                    "At least one native callback is required."));
        }

        for (var index = 0; index < callbacks.Count; index++)
        {
            var callback = callbacks[index];
            try
            {
                Validate(callback);
            }
            catch (Exception exception)
            {
                return Failure(
                    action,
                    callback.Addon,
                    index + 1,
                    callbacks.Count,
                    exception);
            }
        }

        var addonAddresses =
            new Dictionary<string, nint>(StringComparer.Ordinal);
        var targets =
            new DadAlliancePfNativeCallbackTarget[callbacks.Count];
        for (var index = 0; index < callbacks.Count; index++)
        {
            var callback = callbacks[index];
            try
            {
                if (!addonAddresses.TryGetValue(
                        callback.Addon,
                        out var addonAddress))
                {
                    addonAddress =
                        resolveAddonAddress(callback.Addon);
                    if (addonAddress == nint.Zero)
                    {
                        throw new InvalidOperationException(
                            "The callback addon is unavailable or not ready.");
                    }

                    addonAddresses.Add(
                        callback.Addon,
                        addonAddress);
                }

                targets[index] =
                    new DadAlliancePfNativeCallbackTarget(
                        callback,
                        addonAddress);
            }
            catch (Exception exception)
            {
                return Failure(
                    action,
                    callback.Addon,
                    index + 1,
                    callbacks.Count,
                    exception);
            }
        }

        for (var index = 0; index < targets.Length; index++)
        {
            try
            {
                Fire(
                    action,
                    targets[index],
                    index + 1,
                    targets.Length);
            }
            catch (Exception exception)
            {
                return Failure(
                    action,
                    targets[index].Callback.Addon,
                    index + 1,
                    targets.Length,
                    exception);
            }
        }

        return new DadAlliancePfNativeCallbackDispatchResult(true);
    }

    private static void Validate(DadAlliancePfJoinCallback callback)
    {
        if (string.IsNullOrWhiteSpace(callback.Addon))
            throw new ArgumentException("The callback addon name is required.");
        if (callback.Values == null ||
            callback.Values.Length == 0)
        {
            throw new ArgumentException("The callback payload is required.");
        }
        if (callback.Values.Length > MaximumPayloadValues)
        {
            throw new ArgumentOutOfRangeException(
                nameof(callback),
                $"Callback payloads are limited to {MaximumPayloadValues} values.");
        }

        foreach (var value in callback.Values)
        {
            if (value is int)
                continue;
            if (value is string text && !text.Contains('\0'))
                continue;
            throw new NotSupportedException(
                value is string
                    ? "Callback strings cannot contain embedded null characters."
                    : $"Callback value type {value?.GetType().Name ?? "null"} is unsupported.");
        }
    }

    private void Fire(
        DadAlliancePfJoinAction action,
        DadAlliancePfNativeCallbackTarget target,
        int ordinal,
        int total)
    {
        if (target.Callback.Values.All(static value => value is int))
        {
            FireIntegerOnly(
                action,
                target,
                ordinal,
                total);
            return;
        }

        FireMixed(
            action,
            target,
            ordinal,
            total);
    }

    private void FireIntegerOnly(
        DadAlliancePfJoinAction action,
        DadAlliancePfNativeCallbackTarget target,
        int ordinal,
        int total)
    {
        var payload = target.Callback.Values;
        var values = stackalloc AtkValue[payload.Length];
        for (var index = 0; index < payload.Length; index++)
        {
            values[index] = default;
            values[index].Type = AtkValueType.Int;
            values[index].Int = (int)payload[index];
        }

        RecordTrace(
            "dispatch-begin",
            action,
            target.Callback,
            ordinal,
            total);
        sink.Fire(
            target.AddonAddress,
            (uint)payload.Length,
            values,
            target.Callback.UpdateState);
        RecordTrace(
            "dispatch-returned",
            action,
            target.Callback,
            ordinal,
            total);
    }

    private void FireMixed(
        DadAlliancePfJoinAction action,
        DadAlliancePfNativeCallbackTarget target,
        int ordinal,
        int total)
    {
        var payload = target.Callback.Values;
        var valueArraySize = checked(sizeof(AtkValue) * payload.Length);
        var valueArrayAddress = memory.AllocHGlobal(valueArraySize);
        if (valueArrayAddress == nint.Zero)
        {
            throw new OutOfMemoryException(
                "The callback AtkValue array could not be allocated.");
        }

        var stringAllocations = new nint[payload.Length];
        try
        {
            new Span<byte>((void*)valueArrayAddress, valueArraySize).Clear();
            var values = (AtkValue*)valueArrayAddress;
            for (var index = 0; index < payload.Length; index++)
            {
                switch (payload[index])
                {
                    case int integer:
                        values[index].Type = AtkValueType.Int;
                        values[index].Int = integer;
                        break;
                    case string text:
                        var utf8 = Encoding.UTF8.GetBytes(text);
                        var allocation =
                            memory.AllocHGlobal(checked(utf8.Length + 1));
                        if (allocation == nint.Zero)
                        {
                            throw new OutOfMemoryException(
                                "The callback UTF-8 string could not be allocated.");
                        }

                        stringAllocations[index] = allocation;
                        Marshal.Copy(utf8, 0, allocation, utf8.Length);
                        Marshal.WriteByte(allocation, utf8.Length, 0);
                        values[index].Type = AtkValueType.String;
                        values[index].String = (byte*)allocation;
                        break;
                }
            }

            RecordTrace(
                "dispatch-begin",
                action,
                target.Callback,
                ordinal,
                total);
            sink.Fire(
                target.AddonAddress,
                (uint)payload.Length,
                values,
                target.Callback.UpdateState);
            RecordTrace(
                "dispatch-returned",
                action,
                target.Callback,
                ordinal,
                total);
        }
        finally
        {
            for (var index = stringAllocations.Length - 1;
                 index >= 0;
                 index--)
            {
                if (stringAllocations[index] != nint.Zero)
                    memory.FreeHGlobal(stringAllocations[index]);
            }

            memory.FreeHGlobal(valueArrayAddress);
        }
    }

    private void RecordTrace(
        string phase,
        DadAlliancePfJoinAction action,
        DadAlliancePfJoinCallback callback,
        int ordinal,
        int total)
    {
        if (trace == null)
            return;

        try
        {
            trace(new DadAlliancePfNativeCallbackTrace(
                phase,
                action,
                callback.Addon,
                ordinal,
                total,
                string.Join(
                    ",",
                    callback.Values.Select(
                        static value =>
                            value is int ? "Int" : "String")),
                callback.UpdateState));
        }
        catch
        {
            // Redacted diagnostics must never separate an ordered group.
        }
    }

    private static DadAlliancePfNativeCallbackDispatchResult Failure(
        DadAlliancePfJoinAction action,
        string addon,
        int ordinal,
        int total,
        Exception exception)
    {
        var addonName = string.IsNullOrWhiteSpace(addon)
            ? "<unknown>"
            : addon;
        return new DadAlliancePfNativeCallbackDispatchResult(
            false,
            $"{action} native callback {ordinal}/{total} for {addonName} failed " +
            $"with {exception.GetType().Name}: {exception.Message}");
    }
}
