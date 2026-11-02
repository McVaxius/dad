extern alias DadRuntime;

using System.Reflection;
using System.Text.Json;
using DutyIpc = DadRuntime::dad.Services.DadDutyIpcService;
using Xunit;

namespace dad.Tests;

[CollectionDefinition("Wiggly duty runtime", DisableParallelization = true)]
public sealed class WigglyDutyRuntimeCollection;

[Collection("Wiggly duty runtime")]
public sealed class DadWigglyQuestSessionRuntimeTests
{
    [Fact]
    public void ExactWigglySequencesCompleteAThroughDWithoutReloadOrStickyOverrides()
    {
        using var fixture = new Fixture();
        fixture.AssertContract();
        var ids = new HashSet<string>();
        foreach (var run in new[] { "A", "B", "C", "D" })
        {
            var forceLoot = run is "A" or "C";
            var leveling = run is "B" or "C";
            var loot = forceLoot ? fixture.ForceLoot() : null;
            var saved = new List<(string Key, string Value)>();
            if (!leveling) fixture.Set("leveling", "None");
            fixture.CheckedWrite("Meta.AutoDutyModeEnum", "Looping", saved);
            if (leveling)
            {
                fixture.Set("leveling", "Support");
                fixture.Start();
            }
            else
            {
                fixture.CheckedWrite("Meta.Unsynced", "False", saved);
                fixture.CheckedWrite("Meta.DutyModeEnum", "Support", saved);
                fixture.Run();
            }

            fixture.Until(() => fixture.Duty.GetStatus().SessionState == "Running");
            var id = fixture.Duty.GetStatus().LastRunId;
            Assert.True(ids.Add(id));
            using var policy = JsonDocument.Parse(fixture.Policy());
            Assert.Equal(forceLoot, policy.RootElement.GetProperty("active").GetBoolean());
            Assert.Equal(forceLoot ? id : null, fixture.ExitOwner);
            if (forceLoot)
            {
                Assert.True(policy.RootElement.GetProperty("lootTreasure").GetBoolean());
                Assert.False(policy.RootElement.GetProperty("lootBossTreasureOnly").GetBoolean());
                Assert.Equal("AutoDuty", policy.RootElement.GetProperty("lootMethodEnum").GetString());
                fixture.AssertExitSettings();
                // A read while running and a writeback cannot arm the next run
                // or change the frozen policy published for this one.
                Assert.Equal("True", fixture.Get("DutyConfig.LootTreasure"));
                fixture.Set("DutyConfig.LootTreasure", "False");
                Assert.Equal(policy.RootElement.GetRawText(), fixture.Policy());
            }
            fixture.CompleteDungeon();
            Assert.Empty(fixture.Failure());
            Assert.True(fixture.Stopped());
            Assert.Null(fixture.ExitOwner);
            using var stoppedPolicy = JsonDocument.Parse(fixture.Policy());
            Assert.False(stoppedPolicy.RootElement.GetProperty("active").GetBoolean());

            foreach (var (key, value) in saved.AsEnumerable().Reverse()) fixture.Set(key, value);
            if (loot != null)
                foreach (var (key, value) in loot) fixture.Set(key, value);
        }
        Assert.Equal(2, fixture.Applies.Count);
        Assert.Equal(fixture.Applies, fixture.Releases);
        Assert.Equal(2, fixture.Validations.Count);
        Assert.Equal(4, fixture.Events.Count(line => line.StartsWith("native:npc-register:DutySupport")));
        Assert.Equal(4, fixture.Events.Count(line => line.Contains("[dad][DutyIpc] Starting bridge session")));
        Assert.Empty(fixture.State.GetProperty("unexpected").EnumerateArray());
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("failure")]
    [InlineData("replacement")]
    [InlineData("dispose")]
    public void EveryTerminalPathReleasesTheMatchingTemporaryExitSettings(string terminal)
    {
        using var fixture = new Fixture();
        fixture.ForceLoot();
        fixture.Run();
        var owner = fixture.ExitOwner;
        Assert.False(string.IsNullOrEmpty(owner));
        switch (terminal)
        {
            case "stop": fixture.Stop(); break;
            case "failure":
                typeof(DutyIpc).GetMethod("FailSession", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(fixture.Duty, ["Synthetic executor failure."]);
                Assert.Contains("Synthetic executor failure", fixture.Failure());
                break;
            case "replacement": fixture.Run(); break;
            case "dispose": fixture.Duty.Dispose(); break;
        }
        Assert.Null(fixture.ExitOwner);
        Assert.Equal(new[] { owner }, fixture.Releases);
        if (terminal != "dispose")
        {
            using var policy = JsonDocument.Parse(fixture.Policy());
            Assert.False(policy.RootElement.GetProperty("active").GetBoolean());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectedOrUncertainExitSettingsFailBeforeQueueAndReleaseAnyUncertainOwnership(bool uncertain)
    {
        using var fixture = new Fixture { RejectExit = !uncertain, ThrowAfterExitAcceptance = uncertain };
        fixture.ForceLoot();
        fixture.Run();
        Assert.True(fixture.Stopped());
        Assert.Contains("FrenRider", fixture.Failure());
        Assert.Null(fixture.ExitOwner);
        Assert.Equal(uncertain ? 1 : 0, fixture.Releases.Count);
        Assert.DoesNotContain(fixture.Events, line => line.StartsWith("native:npc-register:"));
        using var policy = JsonDocument.Parse(fixture.Policy());
        Assert.False(policy.RootElement.GetProperty("active").GetBoolean());
    }

    [Fact]
    public void FailedReleaseRemainsVisibleAndBlocksAnotherRunUntilTheOwnerIsReleased()
    {
        using var fixture = new Fixture();
        fixture.ForceLoot();
        fixture.Run();
        fixture.RejectRelease = true;
        fixture.CompleteDungeon();
        var owner = fixture.ExitOwner;
        Assert.NotNull(owner);
        Assert.Contains("Cannot release temporary FrenRider", fixture.Failure());
        fixture.Run();
        Assert.True(fixture.Stopped());
        Assert.Equal(owner, fixture.ExitOwner);
        Assert.Contains("Cannot release temporary FrenRider", fixture.Failure());
        Assert.Single(fixture.Applies);
        fixture.RejectRelease = false;
        fixture.Run();
        Assert.Null(fixture.ExitOwner);
        Assert.Equal(new[] { owner }, fixture.Releases);
        Assert.Empty(fixture.Failure());
        using var policy = JsonDocument.Parse(fixture.Policy());
        Assert.False(policy.RootElement.GetProperty("active").GetBoolean());
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly object node;
        private readonly Type nodeType = typeof(DutyIpc).Assembly.GetType("dad.Headless.RuntimeNode")!;
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("dad-wiggly-test-");
        private readonly Dictionary<string, Delegate> handlers;
        private readonly Dictionary<PropertyInfo, object?> previousStatics;
        public readonly List<string> Events = [];
        public readonly List<string> Applies = [];
        public readonly List<string> Releases = [];
        public readonly List<string> Validations = [];
        public string? ExitOwner;
        public string? ExitSettings;
        public bool RejectExit;
        public bool ThrowAfterExitAcceptance;
        public bool RejectRelease;
        public JsonElement State;
        public DutyIpc Duty { get; }

        public Fixture()
        {
            previousStatics = new[] { "dad.Plugin", "dad.DadClock" }.SelectMany(name =>
                typeof(DutyIpc).Assembly.GetType(name)!.GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                .Where(property => property.CanWrite).ToDictionary(property => property, property => property.GetValue(null));
            node = Activator.CreateInstance(nodeType, JsonSerializer.SerializeToElement(new
            {
                index = 1, port = 0, directory = directory.FullName, secret = "synthetic-wiggly-test",
                combatMode = 0,
            }))!;
            handlers = (Dictionary<string, Delegate>)nodeType.GetField("ipcProviders", Private)!.GetValue(node)!;
            Duty = (DutyIpc)nodeType.GetField("dutyIpc", Private)!.GetValue(node)!;
            handlers["ADS.Duty.ValidateLootPolicy"] = (Func<string, string>)(policy =>
            {
                Validations.Add(policy);
                return "{\"schemaVersion\":1,\"success\":true}";
            });
            handlers["FrenRider.Dad.ApplyQuestionableDutySettings"] = (Func<string, string, bool>)((owner, settings) =>
            {
                if (RejectExit) return false;
                Assert.Null(ExitOwner);
                Applies.Add(owner);
                ExitOwner = owner;
                ExitSettings = settings;
                if (ThrowAfterExitAcceptance) throw new IOException("Synthetic lost acceptance response.");
                return true;
            });
            handlers["FrenRider.Dad.ReleaseQuestionableDutySettings"] = (Func<string, bool>)(owner =>
            {
                Assert.Equal(ExitOwner, owner);
                if (RejectRelease) return false;
                Releases.Add(owner);
                ExitOwner = null;
                return true;
            });
            Observe(new { op = "observe", jobId = 1, level = 15, itemLevel = 10 });
        }

        public void AssertContract()
        {
            Assert.IsType<Func<uint, bool>>(handlers["dad.Duty.ContentHasPath"]);
            Assert.IsType<Func<string, string>>(handlers["dad.Duty.GetConfig"]);
            Assert.IsType<Action<string, string>>(handlers["dad.Duty.SetConfig"]);
            Assert.IsType<Action<uint, int, bool>>(handlers["dad.Duty.Run"]);
            Assert.IsType<Action<bool>>(handlers["dad.Duty.Start"]);
            Assert.IsType<Func<bool>>(handlers["dad.Duty.IsStopped"]);
            Assert.IsType<Action>(handlers["dad.Duty.Stop"]);
            Assert.Equal((1, 0), ((Func<(int, int)>)handlers["dad.Duty.ApiVersion"])());
        }

        public string Get(string key) => ((Func<string, string>)handlers["dad.Duty.GetConfig"])(key);
        public void Set(string key, string value) => ((Action<string, string>)handlers["dad.Duty.SetConfig"])(key, value);
        public string Policy() => ((Func<string>)handlers["dad.Duty.GetLootPolicyJson"])();
        public string Failure() => ((Func<string>)handlers["dad.Duty.GetLastFailure"])();
        public bool Stopped() => ((Func<bool>)handlers["dad.Duty.IsStopped"])();
        public void Start() => ((Action<bool>)handlers["dad.Duty.Start"])(true);
        public void Run() => ((Action<uint, int, bool>)handlers["dad.Duty.Run"])(1036, 1, true);
        public void Stop() => ((Action)handlers["dad.Duty.Stop"])();

        public List<(string Key, string Value)> ForceLoot()
        {
            var saved = new[] { "LootTreasure", "LootBossTreasureOnly", "LootMethodEnum" }
                .Select(key => (Key: "DutyConfig." + key, Value: Get("DutyConfig." + key))).ToList();
            Assert.All(saved, pair => Assert.False(string.IsNullOrEmpty(pair.Value)));
            Set("DutyConfig.LootTreasure", "True");
            Set("DutyConfig.LootBossTreasureOnly", "False");
            if (saved[2].Value is not ("AutoDuty" or "All")) Set("DutyConfig.LootMethodEnum", "AutoDuty");
            return saved;
        }

        public void CheckedWrite(string key, string value, List<(string Key, string Value)> saved)
        {
            saved.Add((key, Get(key)));
            Set(key, value);
            Assert.Equal(value, Get(key), ignoreCase: true);
        }

        public void AssertExitSettings()
        {
            using var document = JsonDocument.Parse(ExitSettings!);
            var settings = document.RootElement;
            Assert.Equal(10, settings.EnumerateObject().Count());
            Assert.True(settings.GetProperty("UseAdsLeaveAfterAdsDuty").GetBoolean());
            Assert.False(settings.GetProperty("ExitAfterDutyEnds").GetBoolean());
            Assert.False(settings.GetProperty("LeaveWhenAllLeft").GetBoolean());
            Assert.Equal(20, settings.GetProperty("ExitAfterDutySeconds").GetInt32());
        }

        public void CompleteDungeon()
        {
            Until(() => State.GetProperty("dutyFinder").GetProperty("stage").GetString() == "queued");
            Observe(new { op = "observe", stage = "duty" });
            for (var i = 0; i < 4; i++) Tick();
            Observe(new { op = "observe", stage = "completed" });
            Tick();
            Assert.False(Stopped());
            Observe(new { op = "observe", stage = "outside" });
            Until(Stopped);
        }

        public void Until(Func<bool> done)
        {
            for (var i = 0; i < 100 && !done(); i++) Tick();
            Assert.True(done(), $"DAD did not reach the expected state: {Duty.GetStatus().SessionState}; {Failure()}");
        }
        private void Tick() => Observe(new { op = "tick", milliseconds = 1000 });
        private void Observe(object command)
        {
            var result = nodeType.GetMethod("Execute")!.Invoke(node, [JsonSerializer.SerializeToElement(command, Json)]);
            State = JsonSerializer.SerializeToElement(result, Json);
            Events.AddRange(State.GetProperty("events").EnumerateArray().Select(value => value.GetString()!));
        }

        public void Dispose()
        {
            try { ((IDisposable)node).Dispose(); }
            finally
            {
                foreach (var (property, value) in previousStatics) property.SetValue(null, value);
                directory.Delete(true);
            }
        }
    }
}
