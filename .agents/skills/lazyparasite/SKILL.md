---
name: lazyparasite
description: Implement or use opt-in Dalamud plugin reload testing in normal or full autonomous development mode, reusing manual actions with resumable progress, optional independent diagnostics, and verified reload attempts. Use when Lazyparasite or continuous plugin reload testing is selected.
---

# Lazyparasite

Repeat selected plugin features or scenarios after reload while keeping their
manual actions, prerequisites, scheduling, cleanup, and stop controls intact.
This skill does not itself authorize client control, deployment, remote access,
publication, or additional persistent machinery.

The canonical source is `.agents/skills/lazyparasite/SKILL.md` in the
[DAD repository](https://github.com/McVaxius/dad); `plan.skill/lazyparasite.zip`
contains `lazyparasite/SKILL.md`. Extract that folder into the user's chosen skill
directory. All guidance and examples are in this file; no installed private skill,
helper script, or DAD runtime feature is required. DAD hosts the package, but the
workflow applies to other Dalamud plugins.

## Choose the mode and scope once

- **Normal mode:** add or reuse a debug panel that saves selected features or
  scenarios for subsequent reloads. Selection does not run anything immediately.
  Keep ordinary manual buttons and stop controls usable.
- **Full autonomous mode:** continue implementing, building, observing results,
  and correcting problems until the agreed end state has fresh verification.
  Pause affected work for an unresolved decision, access problem, or missing
  required runtime evidence. Continue independent authorized work where useful;
  do not substitute a build for a runtime result or loop on a blocked attempt.

Establish the repository, selected plan, scope, client instance, end state, and
permitted operations from the live task and carried-forward decisions. Ask only
for missing decisions. Carry authorization forward without repeatedly asking;
autonomous mode does not expand it. Resolve the checkout from the user's supplied
path or current workspace and verify its Git root and project files. Have the user
supply any missing client root, development DLL destination, configuration root,
log locations, and stable client-instance key. Verify these against the project's
existing build/configuration and recorded client mapping before access. Never infer
another account from a drive letter, profile, or this skill's install directory.
Paths may be local, mounted, or remote; access alone does not authorize mutation or
host control. Named plugin/client examples below are illustrative, not defaults.
Keep release versions unchanged unless version work was separately selected.

### Machinery choice for future tasks

Reuse an already recorded choice or approved plan; never repeat the interview.
For additions not yet approved, give one concrete choice covering only the needed
items, then wait for the answer. For example:

> Reload testing needs a saved selection; long runs can lose progress or outlive
> the main log. The lean option is existing manual actions, conversation/Git
> state, and available log excerpts. A saved hook, one checkpoint, and an
> independent logger add configuration/code maintenance, checkpoint updates,
> logging I/O, and up to about 500 MiB per client logger to clean up. Skipping
> them means manual restarts and possibly repeating investigation or pausing
> when evidence disappears. Choose `include` (name the additions),
> `lean alternative`, or `skip`.

Journaling and independent logging are separate selections. Use the lean workflow
for declined additions; do not silently introduce scripts, dependencies, services,
watchers, backups, hashes, extra reports, or a separate documentation system.

## Development loop and checkpoint

Let the task determine test granularity. Batch related small edits before a build;
isolate larger or uncertain changes. Reuse existing scenario sequencing when
several checks belong together. For VERMAXION, `/vmx debug` selects ordinary manual
actions, including their existing scheduling behavior.

When journaling is selected, maintain **one** task checkpoint: update the selected
plan/status document; otherwise use repository-root `LAZYPARASITE.md`. Record:

- scope, mode, client, permitted operations, decisions, and end state;
- current changes and Git state, verified results, and still-unverified claims;
- selected scenario; pending/consumed/dispatched/accepted/completed/cancelled or
  unknown attempt state; expected startup marker and relevant build/test markers;
- resolved artifact and log locations, evidence windows, blockers, and next action.

Update at meaningful transitions and **before** potentially interrupting runtime
actions: build/copy/reload, cleanup, stop, or dispatch as applicable. Record intent
before the action and observed result afterward. Use runtime markers to reconcile
the gap; the agent-maintained checkpoint is not an execution ledger.

Use occasional bounded log snapshots tied to the current attempt and a specific
question. For longer tests, take periodic progress snapshots at meaningful scenario
milestones or an agreed coarse interval. Do not read every arriving line, tail logs,
create a watcher, or repeatedly poll while waiting for one event. Read the relevant
window, including numbered files if it rotated. Windows readers must share
read/write/delete access and close promptly so they do not block the active writer
or rotation. For .NET snapshots, open a read-only `FileStream` with
`FileShare.ReadWrite | FileShare.Delete`; convenience readers can deny writes.

After a fork or interruption, read the checkpoint, inspect Git/current files and
artifact identity, and obtain fresh runtime evidence. Reconcile differences before
continuing. If an attempt may have dispatched, classify it as unknown until logs
or current state resolve it; never blindly redispatch or trigger a build/reload
that would rearm it. Use existing stop/cleanup
only within recorded authorization. Missing evidence is not proof of failure or
success. Record a justified new attempt explicitly before restarting it.

A forked conversation or copied checkpoint carries context, not proof of working
tool, client, network, or runtime connections; check required access afresh.

## One attempt per plugin load

1. Save optional stable action/scenario IDs in existing configuration, defaulting
   to no selection. For multiple selections, snapshot the set at load and dispatch
   the existing sequence once; do not invent another scheduler. Configuration-only
   stubs remain visible and unavailable.
2. Share manual descriptors and availability checks through a non-rendering path.
   Startup must work with all windows closed. Preserve prerequisites without
   turning informational dependencies or scheduled eligibility into new gates.
3. Capture selection at load. Wait for existing character registration or equivalent
   readiness. **Consume before cleanup**, run existing full cleanup, then refresh
   readiness/availability and use the normal manual-action wrapper once. Recheck
   cancellation after cleanup.
4. Unavailability or an exception ends this load's attempt with its reason. Add
   no automatic retry; preserve scheduling/retries owned by the selected action.
   Report dispatch, acceptance, progress, and completion as distinct outcomes.
5. Keep the saved selection. Unchecking or replacing it cancels pending startup;
   replacements arm only the next reload. Cancellation during cleanup or the
   final availability check must also prevent dispatch. FULL STOP cancels pending
   startup and invokes existing stop without clearing the saved choice. UI redraws,
   selection toggles, and login events must not rearm a consumed attempt.
6. Unsubscribe on unload, cancel pending work, and use existing shutdown cleanup.
   An accepted action remains owned by its normal stop/lifecycle path.

### C# reload-hook example

Adapt these contracts to existing descriptors, readiness, cleanup, and manual
wrappers. `selectedAtLoad` can identify an existing multi-scenario sequence.
Construct once per plugin instance; call `Tick` from framework updates and
unsubscribe before disposing. Serialize all hook calls on the framework thread;
marshal off-thread selection/stop events there. Do not construct from Draw/Login.
Save selection in the existing UI handler, then call `SelectionChanged` only when
the value actually changes. That method does not run or rearm anything.

```csharp
using System;
using System.Threading;

public interface IReloadActions
{
    bool Ready { get; }
    bool CanRun(string id, out string reason);
    void Cleanup();
    bool DispatchManual(string id, CancellationToken cancellation);
    void Stop();
    void Log(string message, Exception? error = null);
}

public sealed class ReloadAttempt : IDisposable
{
    // Change this executable value for every settings-only attempt.
    public const string BuildMarker = "lp-debug-0001";
    private readonly IReloadActions actions;
    private readonly CancellationTokenSource cancellation = new();
    private string? pending;
    private bool dispatchStarted;
    private bool disposed;
    public string LoadMarker { get; } =
        $"{BuildMarker}/pid={Environment.ProcessId}/utc={DateTimeOffset.UtcNow:O}";

    public ReloadAttempt(IReloadActions actions, string? selectedAtLoad)
    {
        this.actions = actions;
        pending = selectedAtLoad;
        actions.Log($"startup {LoadMarker}; selection={pending ?? "none"}");
    }

    public void Tick()
    {
        if (disposed || pending is null || cancellation.IsCancellationRequested)
            return;
        var id = pending;
        try
        {
            if (!actions.Ready) return;
            pending = null; // Consume BEFORE cleanup or any reentrant callback.
            actions.Log($"consumed {LoadMarker}; scenario={id}");
            if (disposed || cancellation.IsCancellationRequested) return;
            actions.Cleanup();
            if (disposed || cancellation.IsCancellationRequested) return;
            if (!actions.Ready)
            {
                actions.Log($"blocked {LoadMarker}; readiness lost after cleanup");
                return;
            }
            if (!actions.CanRun(id, out var reason))
            {
                actions.Log($"blocked {LoadMarker}; {reason}");
                return;
            }
            if (disposed || cancellation.IsCancellationRequested) return;
            actions.Log($"dispatch {LoadMarker}; scenario={id}");
            if (disposed || cancellation.IsCancellationRequested) return;
            dispatchStarted = true; // Existing action owns cancellation from here.
            var accepted = actions.DispatchManual(id, cancellation.Token);
            actions.Log($"{(accepted ? "accepted" : "rejected")} {LoadMarker}");
            // Existing action reports progress/completion using this marker.
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            pending = null;
            actions.Log($"cancelled {LoadMarker}");
        }
        catch (Exception ex)
        {
            pending = null; // Readiness/cleanup/dispatch failures never rearm.
            actions.Log($"failed {LoadMarker}", ex);
        }
    }

    public void SelectionChanged()
    {
        if (disposed || dispatchStarted || cancellation.IsCancellationRequested) return;
        pending = null;
        cancellation.Cancel(); // Also cancel a consumed attempt still in cleanup.
        actions.Log($"pending selection cancelled {LoadMarker}; change applies next load");
    }

    public void FullStop()
    {
        if (disposed) return;
        pending = null;
        try { cancellation.Cancel(); }
        finally { actions.Stop(); } // Stop even if a cancellation callback fails.
        actions.Log($"stopped {LoadMarker}; saved selection retained");
    }

    public void Dispose()
    {
        if (disposed) return;
        try { FullStop(); }
        finally { disposed = true; cancellation.Dispose(); }
    }
}
```

The adapter's `Log` must be non-throwing and route to the chosen diagnostics path.
Pass the load/attempt marker into existing task diagnostics; dispatch alone does
not verify acceptance or completion. Keep cancellation and shutdown semantics
consistent with the host action, including asynchronous work it already owns.

## Reload verification, including settings-only changes

A successful incremental build does not prove the client loaded new code. For a
settings-only test, change a small debug attempt marker **compiled into the DLL
and emitted at startup** (the example's `BuildMarker`), then build the configured
development output. Comments and touching files are insufficient. Do not bump
assembly, manifest, or release versions just to trigger reload.

Record the expected marker, selected settings/scenario, build result, actual
output DLL path, and authorized client's dev-plugin path. Perform only agreed
copy/reload actions. Confirm a fresh startup record with that exact build marker,
client identity, and load timestamp **before** evaluating the scenario. An old or
missing marker means the reload is unverified; inspect output routing/reload errors
within scope and pause affected tests if the cause cannot be resolved. Changing
the marker creates executable build input; it does not guarantee a watcher loaded
the DLL. Separate built, loaded, dispatched, and passed.

## Optional independent diagnostics

Some Dalamud builds have a **100 MiB cap** on the main file sink. Check the target
version's logging configuration/source before relying on that limit; a plugin
reload does not reset a host-owned capped log. A plugin's logging toggle may only
filter events: trace its actual call path before calling it an independent sink.

When selected, capture **all target-plugin diagnostics**, including all levels,
exceptions, existing action messages, startup/reload/test markers, and shutdown,
while preserving normal Dalamud output. Audit every plugin logging path (direct
`IPluginLog`, wrappers, static loggers, embedded components) and route it through
the shared fan-out before level/category filters. A second logger alone does not
capture existing calls. Do not replace Dalamud's global logger or capture unrelated
plugins. Identify any unobservable external diagnostics.

Use a plugin-owned directory scoped to the resolved client instance. Keep one
writer per directory; if clients share a config root, use distinct stable instance
keys. Retain `plugin.log` and `plugin.1.log` through `plugin.4.log`,
**100 MiB (104,857,600 bytes) per file**, about **500 MiB maximum** per instance.
Rotate before a write would exceed the limit; retire only this logger's oldest
file. Never delete broad directory contents. Account in encoded bytes, avoid a BOM,
and handle oversized events without silently dropping/truncating them.

Initialize before startup diagnostics. Flush for bounded snapshots; stop producers
and flush/dispose on unload. Surface initialization, write, rotation, flush, and
disposal failures through a latched UI error and the normal logger. A stalled main
sink cannot be the only failure notification. Lost required evidence prevents an
affected runtime-verification claim.

If the main log stalls, continue with independent evidence where its markers,
coverage, and freshness suffice. If required evidence is missing from both, pause
affected runtime verification and state what is needed. Source/build work can
continue within scope. At completion, open the **resolved log folder on the user's
machine** using the existing local folder-opening path. If that requires an
unapproved remote operation, provide the path and state the limitation; do not
start remote control. Leave retained logs for the user's cleanup decision, apart
from agreed rotation.

### C# rolling-log example

The examples use the target project's existing Dalamud/Serilog references and a
compatible .NET SDK; add no dependency. Compile the three blocks as separate
compilation units in one temporary project, using the target plugin's framework
and nullable reference types. The first block's
`IReloadActions` is an instructional contract, not a Dalamud API. Adapt it to the
plugin's existing actions. `PluginDiagnostics` below depends on `RollingPluginSink`.
The sink is thread-safe and fails visibly without retrying. It flushes each event
for visibility (an I/O cost), splits oversized UTF-8 events at character boundaries,
and touches only its own filenames. Reconstruct split events oldest-to-newest.
An event larger than the entire retention window necessarily ages out its own
oldest fragments.

```csharp
using System;
using System.Globalization;
using System.IO;
using System.Text;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

public sealed class RollingPluginSink : ILogEventSink, IDisposable
{
    public const long FileLimit = 100L * 1024 * 1024;
    private readonly object gate = new();
    private readonly string folder;
    private readonly Action<Exception> reportFailure;
    private readonly MessageTemplateTextFormatter formatter = new(
        "{Timestamp:O} [{Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}",
        CultureInfo.InvariantCulture);
    private FileStream? stream;
    private bool disposed;
    private string? failure;
    public string? Failure { get { lock (gate) return failure; } }

    public RollingPluginSink(string folder, Action<Exception> reportFailure)
    {
        this.folder = Path.GetFullPath(folder);
        this.reportFailure = reportFailure;
        try { Directory.CreateDirectory(this.folder); Open(); }
        catch (Exception ex) { Fail(ex); }
    }

    private string FileName(int index) => Path.Combine(folder,
        index == 0 ? "plugin.log" : $"plugin.{index}.log");

    private void Open()
    {
        stream = new FileStream(FileName(0), FileMode.Append, FileAccess.Write,
            FileShare.Read); // Reject another writer; allow bounded readers.
        if (stream.Length > FileLimit)
            throw new IOException("Existing active file exceeds this logger's limit.");
    }

    private void Rotate()
    {
        stream!.Flush();
        stream.Dispose();
        stream = null;
        File.Delete(FileName(4)); // Only this logger's oldest retained file.
        for (var i = 3; i >= 1; --i)
            if (File.Exists(FileName(i))) File.Move(FileName(i), FileName(i + 1));
        File.Move(FileName(0), FileName(1));
        Open();
    }

    public void Emit(LogEvent logEvent)
    {
        lock (gate)
        {
            if (disposed || failure is not null) return;
            try
            {
                using var text = new StringWriter(CultureInfo.InvariantCulture);
                formatter.Format(logEvent, text);
                var bytes = Encoding.UTF8.GetBytes(text.ToString());
                if (stream!.Length > 0 && bytes.LongLength > FileLimit - stream.Length)
                    Rotate(); // Keep ordinary events together.
                for (var offset = 0; offset < bytes.Length;)
                {
                    var count = (int)Math.Min(bytes.Length - offset, FileLimit - stream!.Length);
                    // Do not split a UTF-8 character across files.
                    while (count > 0 && offset + count < bytes.Length &&
                           (bytes[offset + count] & 0xC0) == 0x80) --count;
                    if (count == 0) { Rotate(); continue; }
                    stream.Write(bytes, offset, count);
                    offset += count;
                    if (offset < bytes.Length) Rotate();
                }
                stream!.Flush();
            }
            catch (Exception ex) { Fail(ex); }
        }
    }

    private void Fail(Exception ex)
    {
        failure ??= ex.ToString(); // Latched independently of the main log.
        try { stream?.Dispose(); }
        catch (Exception closeError) { failure += "\nClose failure: " + closeError; }
        stream = null;
        try { reportFailure(ex); } catch { /* UI still reads Failure. */ }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            try { stream?.Flush(); stream?.Dispose(); stream = null; }
            catch (Exception ex) { Fail(ex); }
        }
    }
}
```

### C# logging-adapter example

The adapter below forwards the same event to the existing plugin logger and file
sink. Calls enter `PluginDiagnostics.Log`, which sends each event to the file sink
and `IPluginLog.Logger`; that host logger continues through normal Dalamud sinks.
Use its `Log` at **every** target-plugin call site, or wire existing wrappers to it;
calls directly to the old logger bypass the file. Keep structured templates,
properties, and exception objects intact. Set Verbose before filtering; normal
Dalamud filtering still applies to its own output. Do not dispose or change
Dalamud's shared logger.

```csharp
using System;
using System.IO;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Serilog;
using Serilog.Core;

public sealed class PluginDiagnostics : IDisposable
{
    private readonly Logger logger;
    private bool disposed;
    public RollingPluginSink FileSink { get; }
    public string Folder { get; }
    public ILogger Log => logger;

    public PluginDiagnostics(IDalamudPluginInterface pi, IPluginLog normal,
        string instanceKey)
    {
        // Supply the verified stable client key; never infer another account.
        if (string.IsNullOrWhiteSpace(instanceKey) || instanceKey is "." or ".." ||
            instanceKey.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("A single client-instance folder name is required.");
        Folder = Path.Combine(pi.GetPluginConfigDirectory(), "lazyparasite", instanceKey);
        FileSink = new RollingPluginSink(Folder,
            ex => normal.Error(ex, "Independent diagnostics failed; runtime evidence is incomplete"));
        logger = new LoggerConfiguration().MinimumLevel.Verbose()
            .WriteTo.Sink(FileSink)
            .WriteTo.Logger(normal.Logger, attemptDispose: false)
            .CreateLogger();
        Log.Information("Diagnostics started; client={Client}; folder={Folder}; pid={Pid}",
            instanceKey, Folder, Environment.ProcessId);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { Log.Information("Diagnostics stopping"); }
        finally
        {
            try { logger.Dispose(); }
            finally { FileSink.Dispose(); } // Idempotent if Serilog already closed it.
        }
    }
}
```

Initialize diagnostics before the reload hook. Route the hook's `Log(message, error)`
to `diagnostics.Log.Information("{Message}", message)` or
`diagnostics.Log.Error(error, "{Message}", message)`. Route all other plugin
diagnostics too. Render `FileSink.Failure` prominently in the debug panel and use
the project's existing visible notification path when that panel is closed.
The callback must not recurse into the sink; marshal UI notifications to the
framework thread. Read the latched state immediately after initialization too.
Have the host's initialization error path visibly report constructor failures
(for example, an invalid client key) that occur before a sink is available.

At unload, unsubscribe hook updates, stop/cancel through existing cleanup, quiesce
diagnostic producers, then dispose diagnostics last in a `finally` path. Inspect
and surface final `Failure` through the existing host notification path before
losing the object. A shutdown marker alone does not prove flush/disposal succeeded.
Reuse this policy with existing logging where possible; these are examples, not a
required new abstraction.

## Prompt examples

**Normal mode**

> Use $lazyparasite in normal mode for VERMAXION. Reuse `/vmx debug` to select
> existing manual scenarios for the next reload, preserving prerequisites, full
> cleanup, and FULL STOP. Follow recorded machinery choices and existing build
> authorization. Keep unapproved runtime actions pending. Verify one dispatch per
> load, including with the debug window closed.

**Full autonomous development**

> Use $lazyparasite in full autonomous mode for the agreed repository plan, scope,
> client, and acceptance criteria. Carry forward our recorded machinery choices
> and permitted operations. Implement, build, inspect bounded runtime snapshots,
> and fix until the end state is verified. Batch related small edits; isolate
> uncertain changes. Pause affected work when a decision, access, or required
> runtime evidence is missing, and state the exact blocker.

**Resume after a fork**

> Resume $lazyparasite from the selected plan/checkpoint and this conversation.
> Reconcile Git, actual build output, expected startup marker, and fresh client
> evidence. Preserve scope and authorization. Resolve pending or uncertain attempts
> before another dispatch; do not assume `/fork` repaired connections.

**Force a settings-only reload**

> Apply the agreed debug settings and change the compiled startup attempt marker
> to the next distinct value. Build without changing release versions and use only
> our authorized copy/reload path. Confirm that exact marker in fresh startup
> diagnostics for the selected client before judging the scenario. Diagnose an
> absent marker as an unverified reload, not a passed test.

**DDuck autonomous example (illustrative authorization, not an instruction now)**

> Use $lazyparasite in full autonomous mode on my supplied DDuck checkout, with
> `TODO.md` as the repository plan and sole task checkpoint. Limit this work to
> single-player Deep
> Dungeon behavior: supported safe entry, next-set continuation, recovery, Stop,
> and confirmed Leave in the agreed solo scenario matrix. Leave DAD, remote worker,
> party, and publication items pending. My client alias "Free to Play 1" is the sole
> runtime target; use the client paths and instance key I supplied and inspect
> occasional bounded logs, including periodic progress snapshots for long tests.
> I authorize source edits, local builds,
> copying the DDuck development DLL to that client's configured dev-plugin path,
> DDuck reloads through the existing supported control path, and invoking its
> existing debug/manual actions for those solo scenarios, cleanup, and Stop.
> Do not restart/close the game, control other clients, publish, or change versions.
> Our completed machinery choice is `include`: a saved reload selection, journaling
> in `TODO.md`, and independent diagnostics. We accepted configuration/code
> maintenance, checkpoint updates, logging I/O and roughly 500 MiB per-instance
> retention cost over manual repeats and conversation/main-log-only evidence.
> Continue until the selected solo matrix passes with the expected startup marker
> and fresh completion/stop evidence. If that matrix or a required control path is
> not established, resolve it before runtime work. If the main log stalls, use the
> independent log when sufficient; pause affected runtime verification if required
> markers, progress, or results cannot be observed. Open the resolved independent
> log folder on my machine when finished; leave retained logs for my cleanup choice.

Example prompts do not authorize operating DDuck or VERMAXION during a
skill-edit task. Establish missing runtime targets or acceptance decisions in the
actual development task; do not invent them from these examples.

## Validate an implementation

Use existing targeted checks and one focused regression test where useful. For a
skill-only edit, use an available skill validator on the source and extracted ZIP.
If no validator is installed, check the YAML frontmatter (`name: lazyparasite` and
a nonempty description), complete Markdown fences, and self-contained paths
directly. Compile the C# examples with available Dalamud/Serilog references in a
disposable local workspace, and walk through the cases below. Keep examples and guidance in
this file; add no helper scripts, dependencies, or separate documentation system.
Compilation and simulated checks are not live-client verification.
Inspect the ZIP for exactly `lazyparasite/SKILL.md` and compare its Markdown bytes
directly with the canonical source; do not generate hashes.

| Case | Required outcome |
| --- | --- |
| Settings-only build | Executable marker changes, release version stays fixed; evaluate only after matching fresh startup evidence. |
| Failed/stale reload | Old/missing marker blocks the runtime verdict; verify DLL routing and reload errors before another attempt. |
| Readiness, UI, login | No pre-readiness run; one cleanup/dispatch after readiness; redraw/login never rearm. |
| Interrupted attempt | Compare checkpoint intent with runtime markers/current state; uncertain dispatch is not repeated blindly. |
| Cancellation/replacement | Cancel pending selection; replacement waits for next load; FULL STOP uses existing stop; cancellation during cleanup prevents dispatch. |
| Unavailable/exception | Consume and record the reason; existing action scheduling remains intact. |
| Main logging stops | Continue only with sufficient independent evidence; plugin reload does not reset the main cap. |
| File rotation | Exact byte limit, active plus four older files, UTF-8 boundaries, oldest-only retirement; unrelated files survive. |
| Logging failure | Latched visible error for open/write/rotation/flush/dispose failure; normal output preserved; lost evidence prevents affected runtime claims. |
| Unload | Unsubscribe, cancel/stop and quiesce producers before flushing/disposal; retained logs remain. |
| Resume after fork | Reconcile checkpoint, Git, build identity, access and fresh evidence; carry existing authorization without expanding it. |
