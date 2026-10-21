extern alias DadRuntime;

using System.Buffers.Binary;
using System.Reflection;
using Newtonsoft.Json;
using Xunit;
using RecoveryConfiguration = DadRuntime::dad.Configuration;
using DadConnectionRecovery = DadRuntime::dad.Models.DadConnectionRecovery;
using DadConnectionRecoveryAction = DadRuntime::dad.Models.DadConnectionRecoveryAction;
using DadConnectionRecoveryIdentity = DadRuntime::dad.Models.DadConnectionRecoveryIdentity;
using DadConnectionRecoveryObservation = DadRuntime::dad.Models.DadConnectionRecoveryObservation;
using DadConnectionRecoverySurface = DadRuntime::dad.Models.DadConnectionRecoverySurface;
using RecoveryOwner = DadRuntime::dad.Services.DadTitleMenuReadinessService;

namespace dad.Tests;

public sealed class DadConnectionRecoveryTests
{
    private static readonly DadConnectionRecoveryIdentity Session = new(
        "synthetic-account", "Synthetic Character", "Home", "Visited", 4, 1234);
    private static readonly DateTime Now = new(2026, 10, 20, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ExistingAndNewConfigurationsDefaultOnAndExplicitOffSurvivesRoundTrip()
    {
        Assert.True(new RecoveryConfiguration().ConnectionRecoveryEnabled);
        Assert.True(JsonConvert.DeserializeObject<RecoveryConfiguration>("{\"PluginEnabled\":true}")!.ConnectionRecoveryEnabled);
        var off = new RecoveryConfiguration { ConnectionRecoveryEnabled = false };
        Assert.False(JsonConvert.DeserializeObject<RecoveryConfiguration>(JsonConvert.SerializeObject(off))!.ConnectionRecoveryEnabled);
    }

    [Theory]
    [InlineData(3102)]
    [InlineData(2002)]
    [InlineData(0x332C)]
    [InlineData(0x3390)]
    [InlineData(90001)]
    [InlineData(90002)]
    [InlineData(90006)]
    [InlineData(90007)]
    public void FullTaggedLobbyErrorProtectionPreservesNativeTagAndReturnsOriginalError(int error)
    {
        var native = new byte[24];
        native[0] = 3;
        native[7] = 0x55;
        native[16] = 0x77;
        BinaryPrimitives.WriteUInt32LittleEndian(native.AsSpan(8), (uint)error);
        Assert.True(DadConnectionRecovery.ProtectLobbyError(native, true, out var original));
        Assert.Equal(error, original);
        Assert.Equal(0x3E80, BinaryPrimitives.ReadInt64LittleEndian(native.AsSpan(8)));
        Assert.Equal(3, native[0]);
        Assert.Equal(0x55, native[7]);
        Assert.Equal(0x77, native[16]);
    }

    [Theory]
    [InlineData(false, 3, 3102, 24)]
    [InlineData(true, 0, 3102, 24)]
    [InlineData(true, 0x10, 3102, 24)]
    [InlineData(true, 3, 0, 24)]
    [InlineData(true, 3, 3102, 12)]
    public void DisabledEmptyAndShortNativeValuesAreUntouched(bool enabled, byte tag, int code, int length)
    {
        var value = new byte[length];
        value[0] = tag;
        BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(8), code);
        var before = value.ToArray();
        Assert.False(DadConnectionRecovery.ProtectLobbyError(value, enabled, out _));
        Assert.Equal(before, value);
    }

    [Theory]
    [InlineData(90001)]
    [InlineData(90002)]
    [InlineData(90006)]
    [InlineData(90007)]
    public void DisconnectClearsBothDialogsThenKeepsExactVisitedIdentityThroughLogin(int code)
    {
        var recovery = Ready();
        recovery.ObserveLogout(code);
        Assert.Equal(DadConnectionRecoveryAction.DismissError, Step(recovery, DadConnectionRecoverySurface.Dialogue));
        Assert.Equal(DadConnectionRecoveryAction.None, Step(recovery, DadConnectionRecoverySurface.Dialogue, milliseconds: 499));
        Assert.Equal(DadConnectionRecoveryAction.DismissError, Step(recovery, DadConnectionRecoverySurface.Dialogue, milliseconds: 500));
        Assert.Equal(Session, recovery.Target);
        Assert.Equal(DadConnectionRecoveryAction.OpenDataCenter, Step(recovery, DadConnectionRecoverySurface.Title, milliseconds: 1000));
        Assert.Equal(DadConnectionRecoveryAction.SelectDataCenter, Step(recovery, DadConnectionRecoverySurface.DataCenter, milliseconds: 1150));
        Assert.Equal(DadConnectionRecoveryAction.None, Step(recovery, DadConnectionRecoverySurface.ServiceAccount, milliseconds: 1300));
        Assert.Contains("service-account", recovery.Status);
        Assert.Equal(DadConnectionRecoveryAction.SelectWorld, Step(recovery, DadConnectionRecoverySurface.WorldSelect, milliseconds: 1450));
        Assert.Equal(DadConnectionRecoveryAction.SelectCharacter, Step(recovery, DadConnectionRecoverySurface.CharacterSelect, milliseconds: 1600));
        Assert.Equal(DadConnectionRecoveryAction.ConfirmLogin, Step(recovery, DadConnectionRecoverySurface.LoginConfirmation, milliseconds: 1750));
        Step(recovery, DadConnectionRecoverySurface.World, milliseconds: 2000, session: Session);
        Assert.False(recovery.IsActive);
        Assert.Equal("Connection recovered.", recovery.Status);
    }

    [Theory]
    [InlineData(DadConnectionRecoverySurface.LobbyError)]
    [InlineData(DadConnectionRecoverySurface.Connecting)]
    [InlineData(DadConnectionRecoverySurface.OtherDialog)]
    [InlineData(DadConnectionRecoverySurface.None)]
    public void UnsupportedAndUnreadySurfacesNeverReceiveActions(DadConnectionRecoverySurface surface)
    {
        var recovery = Ready(); recovery.ObserveError(3102);
        Assert.Equal(DadConnectionRecoveryAction.None, Step(recovery, surface));
        Assert.Equal(DadConnectionRecoveryAction.None, Step(recovery, DadConnectionRecoverySurface.Dialogue, ready: false));
        Assert.True(recovery.IsActive);
    }

    [Fact]
    public void NoObservedSessionOrNormalLogoutCannotStartRecovery()
    {
        var neverLoggedIn = new DadConnectionRecovery();
        neverLoggedIn.ObserveError(3102);
        Assert.Equal(DadConnectionRecoveryAction.None, Step(neverLoggedIn, DadConnectionRecoverySurface.Title));
        var normal = Ready(); normal.ObserveLogout(0);
        Assert.Equal(DadConnectionRecoveryAction.None, Step(normal, DadConnectionRecoverySurface.Title));
    }

    [Theory]
    [InlineData("off")]
    [InlineData("shift")]
    [InlineData("account")]
    [InlineData("owner")]
    [InlineData("stop")]
    public void StopAndOwnershipChangesDiscardPendingWorkWithoutReplay(string stop)
    {
        var recovery = Ready(); recovery.ObserveError(3102);
        Assert.Equal(DadConnectionRecoveryAction.DismissError, Step(recovery, DadConnectionRecoverySurface.Dialogue));
        if (stop == "stop") recovery.Cancel("Stop All", forgetSession: true);
        else recovery.Update(stop != "off", stop == "account" ? "other" : Session.AccountId, null,
            stop == "shift", stop == "owner", new(DadConnectionRecoverySurface.Title, true), Now.AddSeconds(1));
        Assert.False(recovery.IsActive);
        Assert.Equal(DadConnectionRecoveryAction.None, Step(recovery, DadConnectionRecoverySurface.Title, milliseconds: 5000));
    }

    [Fact]
    public void RepeatedLobbyFailuresStayOnTheSameTargetAndSuccessDoesNotReplayLogin()
    {
        var recovery = Ready(); recovery.ObserveError(2002);
        Step(recovery, DadConnectionRecoverySurface.Title);
        for (var i = 1; i <= 8; i++)
        {
            recovery.ObserveError(3102);
            Assert.Equal(DadConnectionRecoveryAction.DismissError, Step(recovery, DadConnectionRecoverySurface.Dialogue, i * 1000));
            Assert.Equal(Session, recovery.Target);
        }
        Step(recovery, DadConnectionRecoverySurface.World, 9000, session: Session);
        Assert.Equal(DadConnectionRecoveryAction.None, Step(recovery, DadConnectionRecoverySurface.Title, 10000));
    }

    [Theory]
    [InlineData(3102)]
    [InlineData(2002)]
    [InlineData(90002)]
    public void NativeErrorSurvivesFinalLoggedInFrameAndRewrittenLogoutCode(int code)
    {
        var recovery = Ready();
        recovery.ObserveError(code);
        Assert.Equal(DadConnectionRecoveryAction.None,
            Step(recovery, DadConnectionRecoverySurface.World, session: Session));
        recovery.ObserveLogout(0x3E80);
        Assert.Equal(DadConnectionRecoveryAction.DismissError, Step(recovery, DadConnectionRecoverySurface.Dialogue, 1000));
        Assert.Equal(Session, recovery.Target);
    }

    [Fact]
    public void NamedLobbyErrorWaitsForNativeDismissalAndThenContinuesWithTheSameTarget()
    {
        var recovery = Ready(); recovery.ObserveError(2002);
        Assert.Equal(DadConnectionRecoveryAction.None, Step(recovery, DadConnectionRecoverySurface.LobbyError, ready: false));
        Assert.Contains("Close the visible lobby error", recovery.Status);
        Assert.Equal(DadConnectionRecoveryAction.OpenDataCenter, Step(recovery, DadConnectionRecoverySurface.Title, 1000));
        Assert.Equal(Session, recovery.Target);
    }

    [Fact]
    public void LateRewrittenLogoutKeepsAnAlreadyOwnedRecoveryAndRepeatedAttempt()
    {
        var recovery = Ready(); recovery.ObserveError(3102);
        Assert.Equal(DadConnectionRecoveryAction.DismissError, Step(recovery, DadConnectionRecoverySurface.Dialogue));
        recovery.ObserveLogout(0x3E80);
        Assert.Equal(DadConnectionRecoveryAction.OpenDataCenter, Step(recovery, DadConnectionRecoverySurface.Title, 1000));
        recovery.ObserveError(2002);
        Assert.Equal(DadConnectionRecoveryAction.DismissError, Step(recovery, DadConnectionRecoverySurface.Dialogue, 2000));
        recovery.ObserveLogout(0x3E80);
        Assert.Equal(DadConnectionRecoveryAction.OpenDataCenter, Step(recovery, DadConnectionRecoverySurface.Title, 3000));
        Assert.Equal(Session, recovery.Target);
        recovery.ObserveLogout(0);
        Assert.False(recovery.IsActive);
    }

    [Fact]
    public void NormalLogoutBeforeThePendingErrorIsConsumedStillCancelsRecovery()
    {
        var recovery = Ready(); recovery.ObserveError(2002);
        recovery.ObserveLogout(0);
        Assert.Equal(DadConnectionRecoveryAction.None, Step(recovery, DadConnectionRecoverySurface.Title, 1000));
        Assert.False(recovery.IsActive);
    }

    [Fact]
    public async Task ActualOwnerSerializesStopAcknowledgementAfterTheCurrentNativeCallback()
    {
        object Proxy(Type type, Func<MethodInfo, object?[]?, object?> handler)
        {
            var value = DispatchProxy.Create(type, typeof(DadQuestionableTestProxy));
            ((DadQuestionableTestProxy)value).Handler = handler;
            return value;
        }
        var constructor = typeof(RecoveryOwner).GetConstructors().Single();
        var parameters = constructor.GetParameters();
        var framework = Proxy(parameters[0].ParameterType, (_, _) => null);
        var keys = Proxy(parameters[3].ParameterType, (method, _) => method.Name == "get_Item" ? false : null);
        using var owner = (RecoveryOwner)constructor.Invoke([framework, null, null, keys]);
        var configuration = new RecoveryConfiguration { PluginEnabled = true, ClientAccountId = Session.AccountId };
        DadConnectionRecoveryIdentity? identity = Session;
        using var nativeEntered = new ManualResetEventSlim();
        using var releaseNative = new ManualResetEventSlim();
        using var cancelEntered = new ManualResetEventSlim();
        var callbacks = 0;
        var configure = typeof(RecoveryOwner).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == "ConfigureConnectionRecovery" && method.GetParameters().Length == 5);
        configure.Invoke(owner, [configuration, (Func<DadConnectionRecoveryIdentity?>)(() => identity),
            (Func<bool>)(() => false),
            (Func<DadConnectionRecoveryObservation>)(() => new(DadConnectionRecoverySurface.Dialogue, true, 123)),
            (Action<DadConnectionRecoveryAction, DadConnectionRecoveryIdentity, DadConnectionRecoveryObservation>)((_, _, _) =>
            {
                Interlocked.Increment(ref callbacks);
                nativeEntered.Set();
                Assert.True(releaseNative.Wait(TimeSpan.FromSeconds(5)));
            })]);
        var update = typeof(RecoveryOwner).GetMethod("UpdateConnectionRecovery", BindingFlags.Instance | BindingFlags.NonPublic)!;
        update.Invoke(owner, null);
        typeof(RecoveryOwner).GetMethod("OnRecoveryLogout", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(owner, [0, 90002]);
        identity = null;
        var dispatch = Task.Run(() => update.Invoke(owner, null));
        try
        {
            Assert.True(nativeEntered.Wait(TimeSpan.FromSeconds(5)));
            var cancel = Task.Run(() =>
            {
                cancelEntered.Set();
                owner.CancelConnectionRecovery("Synthetic Stop All");
            });
            Assert.True(cancelEntered.Wait(TimeSpan.FromSeconds(5)));
            Assert.NotSame(cancel, await Task.WhenAny(cancel, Task.Delay(100)));
            releaseNative.Set();
            await Task.WhenAll(dispatch, cancel).WaitAsync(TimeSpan.FromSeconds(5));
            update.Invoke(owner, null);
            Assert.Equal(1, callbacks);
        }
        finally { releaseNative.Set(); await dispatch.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private static DadConnectionRecovery Ready()
    {
        var result = new DadConnectionRecovery();
        Step(result, DadConnectionRecoverySurface.World, session: Session);
        return result;
    }

    private static DadConnectionRecoveryAction Step(DadConnectionRecovery recovery,
        DadConnectionRecoverySurface surface, int milliseconds = 0, bool ready = true,
        DadConnectionRecoveryIdentity? session = null)
        => recovery.Update(true, Session.AccountId, session, false, false,
            new(surface, ready), Now.AddMilliseconds(milliseconds));
}
