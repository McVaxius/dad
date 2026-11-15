extern alias DadRuntime;

using System.Reflection;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Xunit;

namespace dad.Tests;

public sealed unsafe class DadLoginRecoveryNativeLayoutTests
{
    private delegate bool ButtonUsable(AtkComponentButton* button);

    [Fact]
    public void ConsumedLobbyAndReturnButtonFieldsMatchBothReviewedApi15Baselines()
    {
        Assert.Equal(0x23D8, Marshal.SizeOf<AgentLobby>());
        Assert.Equal(0xA40, Marshal.SizeOf<LobbyData>());
        Assert.Equal(0x40, Marshal.OffsetOf<AgentLobby>(nameof(AgentLobby.LobbyData)).ToInt32());
        Assert.Equal(0x13D8, Marshal.OffsetOf<AgentLobby>(nameof(AgentLobby.TemporaryLocked)).ToInt32());
        Assert.Equal(0x12C8, Marshal.OffsetOf<AgentLobby>(nameof(AgentLobby.QueuePosition)).ToInt32());
        Assert.Equal(0x8D8, Marshal.OffsetOf<LobbyData>(nameof(LobbyData.CharaSelectEntries)).ToInt32());
        Assert.Equal(0xF0, Marshal.SizeOf<AtkComponentButton>());
        Assert.Equal(0xE8, Marshal.OffsetOf<AtkComponentButton>(nameof(AtkComponentButton.Flags)).ToInt32());
    }

    [Fact]
    public void RealNativeButtonGuardRequiresOwnerEnabledStateAndRegisteredEvent()
    {
        var method = typeof(DadRuntime::dad.Services.DadTitleMenuReadinessService)
            .GetMethod("IsRecoveryButtonUsable", BindingFlags.Static | BindingFlags.NonPublic)!;
        var probe = method.CreateDelegate<ButtonUsable>();
        Assert.False(probe(null));
        AtkComponentButton button = default;
        Assert.False(probe(&button));
        AtkComponentNode owner = default;
        button.AtkComponentBase.OwnerNode = &owner;
        Assert.False(probe(&button));
        owner.AtkResNode.NodeFlags |= NodeFlags.Enabled;
        Assert.False(probe(&button));
        AtkEvent eventData = default;
        owner.AtkResNode.AtkEventManager.Event = &eventData;
        Assert.True(probe(&button));
    }
}
