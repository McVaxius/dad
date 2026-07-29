using System.Runtime.InteropServices;
using System.Text;
using dad.Models;
using dad.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Xunit;

namespace dad.Tests;

public sealed class DadAlliancePartyFinderNativeCallbackDispatcherTests
{
    [Fact]
    public void MixedPayloadUsesHGlobalUtf8AndRedactedBeginReturnedTrace()
    {
        var sink = new CapturingSink();
        var memory = new CapturingHGlobalMemory();
        var traces = new List<DadAlliancePfNativeCallbackTrace>();
        var dispatcher =
            new DadAlliancePartyFinderNativeCallbackDispatcher(
                sink,
                memory,
                traces.Add);
        var callback = new DadAlliancePfJoinCallback(
            "LookingForGroup",
            true,
            [-2, "Allianc\u00e9"]);

        var result = dispatcher.TryDispatch(
            DadAlliancePfJoinAction.SelectAlliance,
            [callback],
            _ => (nint)0x1234);

        Assert.True(result.Sent, result.Error);
        var call = Assert.Single(sink.Calls);
        AssertCall(call, (nint)0x1234, [-2, "Allianc\u00e9"]);
        Assert.Equal(2, memory.Allocations.Count);
        Assert.Contains(
            Encoding.UTF8.GetByteCount("Allianc\u00e9") + 1,
            memory.Allocations.Select(static allocation => allocation.Bytes));
        Assert.Equal(
            memory.Allocations.Select(static allocation => allocation.Address)
                .Order(),
            memory.Freed.Order());
        Assert.Collection(
            traces,
            trace => AssertTrace(trace, "dispatch-begin"),
            trace => AssertTrace(trace, "dispatch-returned"));
        Assert.DoesNotContain(
            "Allianc\u00e9",
            string.Join("|", traces),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            typeof(DadAlliancePartyFinderNativeCallbackDispatcher)
                .Assembly
                .GetReferencedAssemblies(),
            static reference =>
                string.Equals(
                    reference.Name,
                    "ECommons",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void ExistingIntegerOnlyPrivateSearchAndListCallbacksStayStackOnly()
    {
        var sink = new CapturingSink();
        var memory = new CapturingHGlobalMemory();
        var dispatcher =
            new DadAlliancePartyFinderNativeCallbackDispatcher(
                sink,
                memory);
        var requests = new[]
        {
            new DadAlliancePfJoinActionRequest(
                DadAlliancePfJoinAction.SelectPrivate),
            new DadAlliancePfJoinActionRequest(
                DadAlliancePfJoinAction.SelectRaids),
            new DadAlliancePfJoinActionRequest(
                DadAlliancePfJoinAction.Refresh),
            new DadAlliancePfJoinActionRequest(
                DadAlliancePfJoinAction.OpenListing,
                ListingIndex: 4),
        };

        foreach (var request in requests)
        {
            var result = dispatcher.TryDispatch(
                request.Action,
                DadAlliancePartyFinderJoinCallbacks.Build(request),
                _ => (nint)1);
            Assert.True(result.Sent, result.Error);
        }

        Assert.Empty(memory.Allocations);
        Assert.Collection(
            sink.Calls,
            call => AssertCall(call, 1, [20, 2]),
            call => AssertCall(call, 1, [21, 5]),
            call => AssertCall(call, 1, [17]),
            call => AssertCall(call, 1, [13, 4]),
            call => AssertCall(call, 1, [11, 4]));
    }

    [Fact]
    public void ExactCallbackPlanReachesSinkInOrderWithSuppliedUpdateState()
    {
        var sink = new CapturingSink();
        var dispatcher =
            new DadAlliancePartyFinderNativeCallbackDispatcher(sink);
        var addresses = new Dictionary<string, nint>(StringComparer.Ordinal)
        {
            ["LookingForGroup"] = (nint)1,
            ["LookingForGroupDetail"] = (nint)2,
            ["SelectYesno"] = (nint)3,
            ["LookingForGroupPrivate"] = (nint)4,
        };
        var requests = new[]
        {
            new DadAlliancePfJoinActionRequest(
                DadAlliancePfJoinAction.SelectPrivate),
            new DadAlliancePfJoinActionRequest(
                DadAlliancePfJoinAction.SelectRaids),
            new DadAlliancePfJoinActionRequest(
                DadAlliancePfJoinAction.Refresh),
            new DadAlliancePfJoinActionRequest(
                DadAlliancePfJoinAction.OpenListing,
                ListingIndex: 4),
            new DadAlliancePfJoinActionRequest(
                DadAlliancePfJoinAction.CloseDetail),
            new DadAlliancePfJoinActionRequest(
                DadAlliancePfJoinAction.SelectAlliance,
                Alliance: DadAllianceAssignment.C),
            new DadAlliancePfJoinActionRequest(
                DadAlliancePfJoinAction.ConfirmYes),
            new DadAlliancePfJoinActionRequest(
                DadAlliancePfJoinAction.SubmitPasscodeAndCloseDetail,
                Passcode: 9752),
        };

        foreach (var request in requests)
        {
            var result = dispatcher.TryDispatch(
                request.Action,
                DadAlliancePartyFinderJoinCallbacks.Build(request),
                addon => addresses[addon]);
            Assert.True(result.Sent, result.Error);
        }

        Assert.Collection(
            sink.Calls,
            call => AssertCall(call, 1, [20, 2]),
            call => AssertCall(call, 1, [21, 5]),
            call => AssertCall(call, 1, [17]),
            call => AssertCall(call, 1, [13, 4]),
            call => AssertCall(call, 1, [11, 4]),
            call => AssertCall(call, 2, [-2], updateState: false),
            call => AssertCall(call, 1, [14, "AllianceC"]),
            call => AssertCall(call, 3, [0]),
            call => AssertCall(call, 4, [0, 9752]),
            call => AssertCall(call, 2, [-2], updateState: false));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ListingGroupResolvesOnceAndFiresExactPairWithoutInterleaving(
        int listingIndex)
    {
        var events = new List<string>();
        var sink = new CapturingSink
        {
            OnCall = call =>
                events.Add($"sink:{call.Values[0]}"),
        };
        var dispatcher =
            new DadAlliancePartyFinderNativeCallbackDispatcher(
                sink,
                trace: trace =>
                    events.Add(
                        $"trace:{trace.Phase}:{trace.Ordinal}"));
        var callbacks = DadAlliancePartyFinderJoinCallbacks.Build(
            new DadAlliancePfJoinActionRequest(
                DadAlliancePfJoinAction.OpenListing,
                ListingIndex: listingIndex));
        var lookups = 0;

        var result = dispatcher.TryDispatch(
            DadAlliancePfJoinAction.OpenListing,
            callbacks,
            addon =>
            {
                events.Add($"resolve:{addon}");
                lookups++;
                return (nint)0x1234;
            });

        Assert.True(result.Sent, result.Error);
        Assert.Equal(1, lookups);
        Assert.Collection(
            sink.Calls,
            call => AssertCall(
                call,
                (nint)0x1234,
                [13, listingIndex]),
            call => AssertCall(
                call,
                (nint)0x1234,
                [11, listingIndex]));
        Assert.Equal(
            [
                "resolve:LookingForGroup",
                "trace:dispatch-begin:1",
                "sink:13",
                "trace:dispatch-returned:1",
                "trace:dispatch-begin:2",
                "sink:11",
                "trace:dispatch-returned:2",
            ],
            events);
    }

    [Fact]
    public void FailedGroupAddonPreflightSendsNoCallbacks()
    {
        var sink = new CapturingSink();
        var dispatcher =
            new DadAlliancePartyFinderNativeCallbackDispatcher(sink);
        var callbacks = DadAlliancePartyFinderJoinCallbacks.Build(
            new DadAlliancePfJoinActionRequest(
                DadAlliancePfJoinAction.SubmitPasscodeAndCloseDetail,
                Passcode: 9752));
        var lookups = 0;

        var result = dispatcher.TryDispatch(
            DadAlliancePfJoinAction.SubmitPasscodeAndCloseDetail,
            callbacks,
            _ => ++lookups == 1 ? (nint)1 : nint.Zero);

        Assert.False(result.Sent);
        Assert.Empty(sink.Calls);
        Assert.Equal(2, lookups);
        Assert.Contains(
            "SubmitPasscodeAndCloseDetail",
            result.Error,
            StringComparison.Ordinal);
        Assert.Contains("2/2", result.Error, StringComparison.Ordinal);
        Assert.Contains(
            "LookingForGroupDetail",
            result.Error,
            StringComparison.Ordinal);
        Assert.Contains(
            nameof(InvalidOperationException),
            result.Error,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TraceFailureCannotSeparateOrderedGroup()
    {
        var sink = new CapturingSink();
        var dispatcher =
            new DadAlliancePartyFinderNativeCallbackDispatcher(
                sink,
                trace: _ =>
                    throw new InvalidOperationException(
                        "Injected trace failure."));
        var request = new DadAlliancePfJoinActionRequest(
            DadAlliancePfJoinAction.OpenListing,
            ListingIndex: 2);

        var result = dispatcher.TryDispatch(
            request.Action,
            DadAlliancePartyFinderJoinCallbacks.Build(request),
            _ => (nint)0x1234);

        Assert.True(result.Sent, result.Error);
        Assert.Collection(
            sink.Calls,
            call => AssertCall(call, (nint)0x1234, [13, 2]),
            call => AssertCall(call, (nint)0x1234, [11, 2]));
    }

    [Fact]
    public void PasscodeAndDetailClosePreflightBothThenFireAsOneGroup()
    {
        var events = new List<string>();
        var sink = new CapturingSink
        {
            OnCall = call =>
                events.Add($"sink:{call.AddonAddress}"),
        };
        var dispatcher =
            new DadAlliancePartyFinderNativeCallbackDispatcher(sink);
        var resolvedAddons = new List<string>();
        nint Resolve(string addon)
        {
            resolvedAddons.Add(addon);
            events.Add($"resolve:{addon}");
            return (nint)resolvedAddons.Count;
        }

        var request = new DadAlliancePfJoinActionRequest(
            DadAlliancePfJoinAction.SubmitPasscodeAndCloseDetail,
            Passcode: 9752);

        var result = dispatcher.TryDispatch(
            request.Action,
            DadAlliancePartyFinderJoinCallbacks.Build(request),
            Resolve);

        Assert.True(result.Sent, result.Error);
        Assert.Equal(
            ["LookingForGroupPrivate", "LookingForGroupDetail"],
            resolvedAddons);
        Assert.Equal(
            [
                "resolve:LookingForGroupPrivate",
                "resolve:LookingForGroupDetail",
                "sink:1",
                "sink:2",
            ],
            events);
        Assert.Collection(
            sink.Calls,
            call => AssertCall(call, 1, [0, 9752]),
            call => AssertCall(call, 2, [-2], updateState: false));
    }

    [Theory]
    [InlineData(DadAllianceAssignment.A, 12, "AllianceA")]
    [InlineData(DadAllianceAssignment.B, 13, "AllianceB")]
    [InlineData(DadAllianceAssignment.C, 14, "AllianceC")]
    public void AlliancePayloadsRetainExactHGlobalUtf8Storage(
        DadAllianceAssignment alliance,
        int callbackId,
        string callbackText)
    {
        var sink = new CapturingSink();
        var memory = new CapturingHGlobalMemory();
        var dispatcher =
            new DadAlliancePartyFinderNativeCallbackDispatcher(
                sink,
                memory);
        var request = new DadAlliancePfJoinActionRequest(
            DadAlliancePfJoinAction.SelectAlliance,
            Alliance: alliance);

        var result = dispatcher.TryDispatch(
            request.Action,
            DadAlliancePartyFinderJoinCallbacks.Build(request),
            _ => (nint)0x1234);

        Assert.True(result.Sent, result.Error);
        AssertCall(
            Assert.Single(sink.Calls),
            (nint)0x1234,
            [callbackId, callbackText]);
        Assert.Equal(2, memory.Allocations.Count);
        Assert.Contains(
            Encoding.UTF8.GetByteCount(callbackText) + 1,
            memory.Allocations.Select(
                static allocation => allocation.Bytes));
        Assert.Equal(
            memory.Allocations.Select(
                    static allocation => allocation.Address)
                .Order(),
            memory.Freed.Order());
    }

    [Fact]
    public void UnsupportedPayloadIsRejectedBeforeFirstDispatchOrLookup()
    {
        var sink = new CapturingSink();
        var dispatcher =
            new DadAlliancePartyFinderNativeCallbackDispatcher(sink);
        var callbacks = new[]
        {
            new DadAlliancePfJoinCallback(
                "LookingForGroup",
                true,
                [17]),
            new DadAlliancePfJoinCallback(
                "LookingForGroupDetail",
                true,
                [1.5]),
        };
        var lookups = 0;

        var result = dispatcher.TryDispatch(
            DadAlliancePfJoinAction.Refresh,
            callbacks,
            _ =>
            {
                lookups++;
                return (nint)1;
            });

        Assert.False(result.Sent);
        Assert.Empty(sink.Calls);
        Assert.Equal(0, lookups);
        Assert.Contains("Refresh", result.Error, StringComparison.Ordinal);
        Assert.Contains("2/2", result.Error, StringComparison.Ordinal);
        Assert.Contains(
            "LookingForGroupDetail",
            result.Error,
            StringComparison.Ordinal);
        Assert.Contains(
            nameof(NotSupportedException),
            result.Error,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MixedAllocationsAreReleasedWhenNativeSinkThrows()
    {
        var sink = new CapturingSink
        {
            ThrowOnCall = 1,
        };
        var memory = new CapturingHGlobalMemory();
        var dispatcher =
            new DadAlliancePartyFinderNativeCallbackDispatcher(
                sink,
                memory);
        var callback = new DadAlliancePfJoinCallback(
            "LookingForGroup",
            true,
            [14, "AllianceC"]);

        var result = dispatcher.TryDispatch(
            DadAlliancePfJoinAction.SelectAlliance,
            [callback],
            _ => (nint)1);

        Assert.False(result.Sent);
        Assert.Equal(
            memory.Allocations.Select(static allocation => allocation.Address)
                .Order(),
            memory.Freed.Order());
    }

    [Fact]
    public void NativeFailureNamesActionAddonOrdinalAndExceptionType()
    {
        var sink = new CapturingSink
        {
            ThrowOnCall = 2,
        };
        var dispatcher =
            new DadAlliancePartyFinderNativeCallbackDispatcher(sink);
        var callbacks = DadAlliancePartyFinderJoinCallbacks.Build(
            new DadAlliancePfJoinActionRequest(
                DadAlliancePfJoinAction.OpenListing,
                ListingIndex: 4));

        var result = dispatcher.TryDispatch(
            DadAlliancePfJoinAction.OpenListing,
            callbacks,
            _ => (nint)1);

        Assert.False(result.Sent);
        Assert.Single(sink.Calls);
        Assert.Contains("OpenListing", result.Error, StringComparison.Ordinal);
        Assert.Contains(
            "LookingForGroup",
            result.Error,
            StringComparison.Ordinal);
        Assert.Contains("2/2", result.Error, StringComparison.Ordinal);
        Assert.Contains(
            nameof(NullReferenceException),
            result.Error,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NativeFailureContextReachesAuditedJoinRetrySummary()
    {
        const string failure =
            "SelectPrivate native callback 1/1 for LookingForGroup failed " +
            "with NullReferenceException: injected.";
        var ui = new FailingJoinUi(failure);
        var flow = new DadAlliancePartyFinderJoinFlow(ui);
        var target = new DadAlliancePfJoinTarget
        {
            LeaderName = "Expected Leader",
            LeaderWorld = "Expected World",
            TargetContentId = 123,
            AssignedAlliance = DadAllianceAssignment.B,
            Passcode = 9752,
        };

        var acknowledged = flow.Advance(target);
        var retry = flow.Advance(target);

        Assert.Equal(
            "window-acknowledged",
            acknowledged.Event);
        Assert.Equal(DadAlliancePfJoinResultKind.Retry, retry.Kind);
        Assert.True(retry.ShouldAudit);
        Assert.Contains(
            DadAlliancePfJoinAction.SelectPrivate.ToString(),
            retry.Summary,
            StringComparison.Ordinal);
        Assert.Contains(
            "LookingForGroup",
            retry.Summary,
            StringComparison.Ordinal);
        Assert.Contains("1/1", retry.Summary, StringComparison.Ordinal);
        Assert.Contains(
            nameof(NullReferenceException),
            retry.Summary,
            StringComparison.Ordinal);
    }

    private static void AssertTrace(
        DadAlliancePfNativeCallbackTrace trace,
        string phase)
    {
        Assert.Equal(phase, trace.Phase);
        Assert.Equal(
            DadAlliancePfJoinAction.SelectAlliance,
            trace.Action);
        Assert.Equal("LookingForGroup", trace.Addon);
        Assert.Equal(1, trace.Ordinal);
        Assert.Equal(1, trace.Total);
        Assert.Equal("Int,String", trace.PayloadTypes);
        Assert.True(trace.UpdateState);
    }

    private static void AssertCall(
        CapturedCall call,
        nint addonAddress,
        object[] values,
        bool updateState = true)
    {
        Assert.Equal(addonAddress, call.AddonAddress);
        Assert.Equal(updateState, call.UpdateState);
        Assert.Equal(values, call.Values);
        Assert.True(call.AllStringsNullTerminated);
    }

    private sealed unsafe class CapturingSink :
        IDadAlliancePfNativeCallbackSink
    {
        public List<CapturedCall> Calls { get; } = [];
        public int ThrowOnCall { get; init; }
        public Action<CapturedCall>? OnCall { get; init; }

        public void Fire(
            nint addonAddress,
            uint valueCount,
            AtkValue* values,
            bool updateState)
        {
            if (ThrowOnCall == Calls.Count + 1)
            {
                throw new NullReferenceException(
                    "Injected native sink failure.");
            }

            var captured = new object[valueCount];
            var allStringsNullTerminated = true;
            for (var index = 0; index < valueCount; index++)
            {
                captured[index] = values[index].Type switch
                {
                    AtkValueType.Int => values[index].Int,
                    AtkValueType.String => ReadString(
                        values[index],
                        ref allStringsNullTerminated),
                    _ => throw new InvalidOperationException(
                        $"Unexpected AtkValue type {values[index].Type}."),
                };
            }

            var call = new CapturedCall(
                addonAddress,
                updateState,
                captured,
                allStringsNullTerminated);
            Calls.Add(call);
            OnCall?.Invoke(call);
        }

        private static string ReadString(
            AtkValue value,
            ref bool nullTerminated)
        {
            var bytes = value.String.AsSpan();
            nullTerminated &=
                value.String.Value != null &&
                value.String.Value[bytes.Length] == 0;
            return Encoding.UTF8.GetString(bytes);
        }
    }

    private sealed class CapturingHGlobalMemory :
        IDadAlliancePfNativeMemory
    {
        public List<Allocation> Allocations { get; } = [];
        public List<nint> Freed { get; } = [];

        public nint AllocHGlobal(int byteCount)
        {
            var address = Marshal.AllocHGlobal(byteCount);
            Allocations.Add(new Allocation(address, byteCount));
            return address;
        }

        public void FreeHGlobal(nint address)
        {
            Freed.Add(address);
            Marshal.FreeHGlobal(address);
        }
    }

    private sealed record Allocation(nint Address, int Bytes);

    private sealed record CapturedCall(
        nint AddonAddress,
        bool UpdateState,
        object[] Values,
        bool AllStringsNullTerminated);

    private sealed class FailingJoinUi(string failure) :
        IDadAlliancePartyFinderJoinUi
    {
        public DadAlliancePfJoinSnapshot Read(DadAlliancePfJoinTarget target)
            => new()
            {
                MainVisible = true,
                MainReady = true,
            };

        public DadAlliancePfJoinActionResult Perform(
            DadAlliancePfJoinActionRequest request)
            => new(
                false,
                $"{request.Action} callback sequence failed.",
                failure);
    }
}
