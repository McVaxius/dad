---
name: dad-autoparty-testing
description: Test DAD and AutoParty features, fixes, refactors, integrations, and regressions using relevant project tests and the standalone integrated lifecycle lab, including its browser GUI. Apply during ordinary development in either repository or explicit lab/replay work; virtual results do not establish live game or Discord acceptance.
---

# DAD / AutoParty development testing

The maintained skill lives in [DAD](https://github.com/McVaxius/dad) at
`.agents/skills/dad-autoparty-testing`. It also works as a user-level copy when the starting workspace is
[AutoParty](https://github.com/McVaxius/Autoparty). Do not depend on the skill's installation directory to locate source.

The lab in AutoParty's `src/AutoParty.LifecycleLab` drives DAD's linked-production `Headless` executable in child
processes. It creates synthetic participants and temporary runtime storage and routes Discord HTTP adapters to a
loopback provider. Production lifecycle and central services remain in use.

## Resolve both checkouts

1. Inspect the current workspace and its Git root. Recognize DAD by `dad.csproj`, `Headless/Dad.Headless.csproj` and
   `Tests/dad.Tests.csproj`; recognize AutoParty by `src/AutoParty.LifecycleLab/AutoParty.LifecycleLab.csproj` and
   `tests/AutoParty.Tests/AutoParty.Tests.csproj`. Either repository may be the starting workspace, including a subfolder.
2. Use a companion path supplied in the task or inspect a named sibling checkout. Verify its project files and Git
   identity before use. Do not assume fixed drive letters, a particular parent layout, or that a user-installed skill
   sits inside DAD. If the companion cannot be found, ask for its checkout location before integrated verification.
3. Bind absolute, verified paths to `$dadRoot` and `$autoPartyRoot` in PowerShell. Use `Resolve-Path -LiteralPath` and
   `Join-Path` so paths with spaces work. Preserve unrelated changes in both repositories.
4. Source builds require Windows x64, compatible .NET 10 SDKs and API 15 Dalamud development-reference assemblies.
   AutoParty's `global.json` controls SDK selection. DAD uses `$env:DALAMUD_HOME` when set, otherwise the standard
   `$env:APPDATA\XIVLauncher\addon\Hooks\dev` reference directory. Request missing prerequisites; do not install or
   change machine-wide configuration as part of testing. No running game or Discord bot is required.

## Own testing alongside implementation

Apply this workflow during ordinary development in either repository without requiring an explicit skill invocation
or separate testing request. Own selection, current-source builds, failure reproduction, task-specific coverage and
verification; do not require the collaborator to choose scenarios or coordinate testing.

- **DAD changes:** run relevant `Tests/dad.Tests.csproj` checks and select lifecycle scenarios for affected scheduling,
  coordination, worker execution, queues, cancellation and recovery. Respect DAD's required complete Release suite.
- **AutoParty changes:** run relevant `tests/AutoParty.Tests/AutoParty.Tests.csproj` checks and select lifecycle scenarios
  for affected registration, pairing, directory sharing, mailbox delivery, security, persistence and restart recovery.
- **Integration changes:** build current DAD headless and AutoParty lab sources, then use `list --json` to select by
  `components` and `coverage`. The current catalog is authoritative; examples below are not an exhaustive scenario list.

Identify the expected outcome, topology, account/slot identity, lifecycle phase and cancellation or restart conditions.
For failures, reproduce with a matching scenario before changing behavior; add focused coverage first when needed.
For features or refactors, use relevant existing checks as a baseline and define new observable outcomes. Require the
expected transition, correlated identity, action count, terminal result and cleanup. A quiet queue or green isolated
unit test does not establish lifecycle success.

Maintain relevant existing tests and lab scenarios within the implementation task. Adapters supply observations,
native actions, IPC responses, clock and isolated storage. Retain production coordinator, scheduler, worker,
admission, registration, directory, security and relay behavior; share production sequencing and handler registration
instead of copying that behavior into a scenario.

After changes, rebuild affected projects and rerun selected scenarios and tests against current code. Use focused
scenarios for narrow work. Require `run all` for full integrated verification when shared lifecycle behavior changes,
including dispatch, cancellation, registration, pairing, mailbox handling or restart recovery. Investigate failures
within scope and rerun after corrections; do not weaken assertions or change production versions to get a pass.

## Build and test current sources

With verified roots set, restore from DAD's vendored protocol source and public NuGet. Enter each checkout so its SDK
rules apply. Stop and inspect any failed command; do not use stale binaries after a failed build.

```powershell
$packageSource = Join-Path $dadRoot '.github\nuget'
$nugetSource = 'https://api.nuget.org/v3/index.json'
Push-Location $dadRoot
try {
    dotnet restore '.\Headless\Dad.Headless.csproj' --source $packageSource --source $nugetSource
    if ($LASTEXITCODE -ne 0) { throw 'DAD headless restore failed.' }
    dotnet build '.\Headless\Dad.Headless.csproj' -c Debug --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'DAD headless build failed.' }
    dotnet restore '.\Tests\dad.Tests.csproj' --source $packageSource --source $nugetSource
    if ($LASTEXITCODE -ne 0) { throw 'DAD test restore failed.' }
    dotnet test '.\Tests\dad.Tests.csproj' -c Release -p:Platform=x64 --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'DAD tests failed; inspect the failures.' }
} finally { Pop-Location }

Push-Location $autoPartyRoot
try {
    dotnet restore '.\src\AutoParty.LifecycleLab\AutoParty.LifecycleLab.csproj' --locked-mode --source $nugetSource
    if ($LASTEXITCODE -ne 0) { throw 'AutoParty lab restore failed.' }
    dotnet build '.\src\AutoParty.LifecycleLab\AutoParty.LifecycleLab.csproj' -c Debug --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'AutoParty lab build failed.' }
    dotnet restore '.\tests\AutoParty.Tests\AutoParty.Tests.csproj' --locked-mode --source $nugetSource
    if ($LASTEXITCODE -ne 0) { throw 'AutoParty test restore failed.' }
    dotnet test '.\tests\AutoParty.Tests\AutoParty.Tests.csproj' -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'AutoParty tests failed; inspect the failures.' }
} finally { Pop-Location }

$lab = Join-Path $autoPartyRoot 'src\AutoParty.LifecycleLab\bin\Debug\net10.0-windows10.0.17763.0\AutoParty.LifecycleLab.exe'
$dad = Join-Path $dadRoot 'Headless\bin\Debug\net10.0-windows\Dad.Headless.exe'
& $lab list --json
& $lab run i305-queue-ready --seed 1 --json --dad $dad
& $lab run autoparty-pairing-directory --seed 1 --json --dad $dad
& $lab run all --seed 1 --json --dad $dad
```

Choose relevant test filters and scenarios for the task; the block shows complete suites and integrated examples.
Use the actual target frameworks/output paths from the project files if they change. JSON includes replay `definition`,
aggregate `passed`, individual `results` and build identities. Exit 0 means every requested scenario passed; 1 means
failed verification; 2 means invalid invocation or an application failure. Inspect errors and expected versus observed
assertions, especially on nonzero exit.

## Browser GUI and replay

Run `& $lab ui --dad $dad` to open the browser. For a server without automatic browser launch, use
`& $lab ui --no-browser --dad $dad` and read the URL in startup JSON. The server is loopback-only; `/` serves the GUI
and `/api/scenarios` serves the catalog. Stop a foreground server with Ctrl+C; stop only an exact owned process when
automating a GUI check.

- Select `i305-queue-ready` and **Run** for a DAD example, or `autoparty-pairing-directory` and **Run** for AutoParty.
  **Run All** exercises the catalog.
- **Scenario inputs / replay** accepts synthetic participant inputs; `dad-direct-solo` also accepts a custom DAD
  request in `preset`. Empty participants use scenario defaults.
- **Pause** stops at a virtual tick boundary. **Step** advances one tick and remains paused. **Advance Time** advances
  one tick by the entered milliseconds; **Resume** continues. **Reset** cancels the run and clears session state.
- Inspect **Node states and transitions**, **Assertions and outcomes**, and each node's `unexecutedSteps`.
- **Export scenario + result** downloads inputs and results. Paste an export into **Scenario inputs / replay**, click
  **Apply inputs**, then **Run** to replay it. For CLI replay, resolve the export to `$replayFile` and run:

```powershell
& $lab run --scenario-file $replayFile --json --dad $dad
```

Replay accepts either a definition or a full result containing `definition`. Fresh synthetic cryptographic identities
are generated per run; compare role bindings and observable behavior, not random bytes. Format 2 exports replay
`definition.clockAdvances` at their named scenario/phase/occurrence and local tick. The separate `clock` field is
observed polling evidence: asynchronous I/O can vary those counts. An unreached required advance must fail; never drop
or move inputs to make replay pass. Legacy `tickMilliseconds` files remain strict and may fail under different polling
timing; use a fresh export for the current format.

For verifier changes, run `i305-queue-ready` with each `--verifier-fault`: `missing-event`, `unexpected-callback`,
`broken-correlation` and `stalled-polling`, retaining `--json --dad $dad`. Each must produce a failed result and nonzero
exit. `broken-correlation` probes the child control channel; it does not by itself prove LAN or AutoParty rejection.

## Supplied portable package

If a package is supplied, extract `AutoParty.LifecycleLab-win-x64.zip` and keep all files, including its adjacent `dad`
folder. Run `AutoParty.LifecycleLab.exe` for the GUI, or use the same CLI commands without `--dad`; it automatically
locates `dad/Dad.Headless.exe`. The self-contained Windows x64 package needs no SDK or separately installed Dalamud
references, game client or Discord setup. It verifies its bundled revisions. Always rebuild both source projects and
pass the current headless path explicitly when verifying development changes.

## Interpret evidence precisely

- Inspect expected versus observed assertions, exercised components, substitutions and each node's `unexecutedSteps`.
  Report uncovered requested behavior explicitly.
- An installed-library identity assertion can fail independently of passing layout and virtual lifecycle checks.
  Do not update a pinned baseline merely to make it pass.
- The four focused I305 scenarios enter at the routing boundary. They exercise dispatch, polling, worker execution
  and authenticated LAN exchange without proving scheduler or formation coverage. `i305-scheduled-leveling` starts a
  saved Leveling roulette preset through the production scheduler with a participating Slot1 coordinator and three LAN
  workers, covering formation, the readiness barrier, duty completion, cleanup and schedule completion.
- I302 retains internal-name compatibility: display-name changes must remain recognized; an unknown replacement
  internal name must retain the existing missing-dependency result until an authoritative replacement is supplied.
- Report builds, selected tests/scenarios, pass/fail results, reproduced failures, coverage changes and unresolved or
  unverified behavior. Include base revisions, package versions from build/node metadata and uncommitted source changes.
  Do not compute file hashes or add separate reports, backups or helper scripts.
- GUI and CLI success establish virtual lifecycle verification. They do not establish in-game acceptance, real Discord
  connectivity or production acceptance. Do not read production credentials, access live game clients, deploy
  applications or change external workflow statuses as part of this skill.
