# DAD

DAD is a duty planner and multibox coordinator for FFXIV. Build reusable crews, ready the right characters,
assemble and verify parties, queue supported duties and roulettes, and chain proven presets into schedules.

[Read the player-friendly Aethertek guide](DAD_AETHERTEK.md) for supported activities, setup, safety,
integrations, and everyday commands.

## What DAD does

- Plans repeatable solo or multibox duty runs from saved presets and role-based templates.
- Shows live readiness, blockers, queue state, reconnect progress, and scoped cancellation controls.
- Coordinates launch or relog preparation, requested jobs, verified party formation, and supported queues.
- Runs presets immediately or in ordered manual and daily-reset schedules.
- Keeps future planner lanes visibly separate from activities that have guarded live execution today.

## Build

```powershell
$packageSource = (Resolve-Path -LiteralPath '.\.github\nuget').Path
$nugetSource = 'https://api.nuget.org/v3/index.json'
$package = Join-Path $packageSource 'Dad.AutoParty.Protocol.0.1.0-preview.2.nupkg'
$expectedHash = '475964fad1a400125b0a80a3ac4ab28e45150d5390d97e992fcf6dfb8dd09ac5'

if (-not (Test-Path -LiteralPath $package -PathType Leaf)) {
    throw 'The vendored Dad.AutoParty.Protocol package is missing.'
}

if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedHash) {
    throw 'The vendored Dad.AutoParty.Protocol package hash does not match this source revision.'
}

dotnet restore .\dad.csproj -r win --locked-mode --source $packageSource --source $nugetSource
dotnet restore .\Tests\dad.Tests.csproj --source $packageSource --source $nugetSource
dotnet build .\dad.csproj -c Debug -p:Platform=x64 --no-restore
dotnet build .\dad.csproj -c Release -p:Platform=x64 --no-restore
```

DAD pins [ECommons 3.2.1.15](https://www.nuget.org/packages/ECommons/3.2.1.15) for its stateless Party Finder
addon-readiness wrappers and UI-input helpers. DAD does not initialize the ECommons global service layer or use its
signature-dependent paths. The separate DAD-owned Party Finder preset loader resolves one fail-closed recruitment-editor
refresh function through Dalamud's game-interop service. ECommons is maintained by NightmareXIV and its contributors.

The build requires the current API 15 Dalamud development assemblies. Set `DALAMUD_HOME` to that directory when they are
not installed at the normal XIVLauncher development-hook location. Restore uses the checked-in AutoParty package and the
public NuGet feed explicitly; it does not require a machine-global AutoParty source. The committed lock file contains both
the base framework graph and the `win` runtime target required by the locked `-r win` restore.

## Test

```powershell
$packageSource = (Resolve-Path -LiteralPath '.\.github\nuget').Path
$nugetSource = 'https://api.nuget.org/v3/index.json'
dotnet restore .\Tests\dad.Tests.csproj --source $packageSource --source $nugetSource
dotnet test .\Tests\dad.Tests.csproj -c Release -p:Platform=x64 --no-restore
```

The complete unfiltered Release suite is required. CI runs it without `continue-on-error` before any build artifact can be
uploaded or promoted to a release.

## Crew and persistence

- The top of Plans now has **Crew Tools** for deliberate party-only work from the selected saved preset. It shows the
  frozen/effective preset, resolved regular-versus-alliance mode, live state, and first blocker. Once the selected roster
  is structurally valid, **Create group** submits one in-memory formation request immediately. Offline, busy, post-AR,
  and relog readiness then wait in the ordinary scheduler instead of disabling the button. The request is single-active,
  is not persisted across restart, and does not change the saved preset. Leveling Mode uses only its compiled first
  effective child.
- Formation uses the exact number of selected primary crew slots even when the preset's duty or roulette normally expects
  a larger queue party. Missing or ambiguous identities, duplicate accounts/characters, invalid requested jobs,
  incompatible wake policy, exact Slot1 authority, dependency, transport, scheduler-conflict, and cancellation guards
  remain fail-closed. The Coordinator is the orchestration control plane and does not need its account or character in
  the crew. Ordinary Plan and Schedule validation still require their normal duty, roulette, queue, and stop policy fields.
- A regular crew starts the existing coordinator with only the runtime request's formation-only flag enabled and holds
  the verified party at `GroupReady`; it never queues. An effective preset with at least one resolved character in each
  of Alliance A, B, and C uses the existing private alliance PF path: Create once, wait for the exact owned listing,
  Grab once, then finish only after exact subgroup verification and recruitment-only cleanup. It never queues or automatically disbands
  the alliance.
- The exact frozen first primary slot is the party leader, inviter, queue executor, alliance PF host, and managed teardown
  authority. A remote Slot1 follows its saved wake/relog policy, receives the authenticated assembly request, and returns
  its authoritative PartyList for coordinator verification. Slot1 identity is fail-closed across worker, account,
  character, and Content ID; either executable invite-authority setting resolves to that same slot.
- With **STOP: Target level**, the bottom target applies only to the first selected primary character when that row is
  blank. A target on that row overrides the bottom value; every other nonblank row target is additive, and DAD stops or
  skips only when all resolved targets are proven. **Any** requires the loaded character's live current job and level,
  while a selected job reads that job's ledger. Missing evidence and observed levels 0 or 1 remain unknown and continue
  under the existing safety cap. DAD freezes the exact effective LevelSeek rows and resolved targets before wake/relog,
  then re-evaluates both after exact readiness and requested-job acknowledgement, before planner dispatch. It refreshes
  the same target evidence after each completed run.
- **Disband** routes a held regular Crew Formation through the exact frozen roster. Slot1 receives the guarded disband
  first; after its terminal response, every follower independently leaves or proves authoritative solo state. DAD waits
  for every exact worker and reports one partial failure naming all affected slots instead of treating Slot1 alone as
  complete.
  When no Crew Formation is active, it first freezes the current authoritative party membership and requires a stable,
  out-of-duty, out-of-queue state, at least two nonzero members, and proven local leadership. Existing seven-attempt,
  fresh-prompt, unexpected-member, and sustained-solo safeguards remain unchanged.
- Home includes a one-step **Name this DAD** guide. It gives the immutable local client account a meaningful alias;
  normal crew choices show the alias only, while the session-only **Details** checkbox also shows the stable ID.
- Crew account tools list each account's characters, can show every matching roster row by clearing secondary filters,
  and forget remote account copies immediately with Ctrl+Shift. **Build Connected Crew** uses current participant
  projections and suppresses the mirrored local worker while retaining distinct remote sessions.
- Character Profiles and launch-profile scaffolding remain operational but are visible only with Debug UI enabled.
  Per-row account assignment controls are not part of the normal Crew browser; the assigned/unassigned filter and
  existing compatibility contracts remain available.
- Configuration schema v8 retains the v4 removal of serialized remote profile catalogs. Online remote catalogs are session-only and
  reconcile every 60 seconds; durable per-account character profiles stay in their separate account JSON files.
  Passive roster learning coalesces disk writes, and Crew projections/filter results are revision-cached. Schema-4 users
  migrate to an empty, disabled AutoParty configuration without generating an identity or initializing a network route.
- Shared configuration changes are coalesced at the end of the framework update after a 250 ms quiet period, with a
  one-second maximum delay. Storage failures stay outside window drawing, retry on a bounded cadence, and surface a
  memory-only warning with an explicit **Retry save** action; disposal makes one final write attempt.
- Per-account files load independently, so one corrupt account is reported and skipped without hiding later valid
  accounts. Required writes use a unique same-directory temporary file and atomic replacement; a failed write restores
  the prior in-memory account snapshot and revision. Account merge controls and CLR merge surfaces no longer exist;
  delete, forget remote copies, clear-all, and history behavior remain available.
- Durable run history keeps at most 50 compact completion snapshots. Status, timing, task/stop progress, step results,
  warnings, and scheduler failure evidence remain visible while requests, participants, leases, executor state, client
  and session IDs, and authority endpoints are removed from saved history.

## AutoParty Discord pairing and measured pilot

- AutoParty remains disabled by default. Configuration schema 6 preserves historical courier fields but does not attach,
  poll, or send through the file courier at runtime. Every installation connects its own bot directly from the plugin with
  pinned `Discord.Net.WebSocket` 3.20.1.
- In `/dad autoparty`, generate the immutable DAD endpoint identity, enter only that bot's token plus the shared Guild and
  private `#dad-pairing` Channel IDs, then use **Save & Connect**. The token is masked and stored only through Windows
  CurrentUser DPAPI; configuration contains an opaque token reference and authenticated Application/Bot User IDs.
- Invite each bot with zero server-wide permissions. In the private channel grant only View Channel, Send Messages, and
  Read Message History. Enable Message Content Intent; leave Presence and Server Members intents disabled.
- `dad.pairing/v1` messages contain bounded signed public endpoint metadata only. They never contain bot tokens, FFXIV
  identifiers, plans, schedules, requested jobs, Stop, or execution commands. Pairing uses a Coordinator-to-Client star;
  clients do not need to pair with each other. Presence refreshes about every 60 seconds and is stale after three minutes.
- Pair and Accept require the operator to review and confirm the peer's complete Ed25519 signing-key fingerprint. The exact
  Application ID, Bot User ID, DAD identity, endpoint fingerprint, signing key/fingerprint, role, and endpoint-key generation
  are pinned. Outbound requests are persisted as five-minute, single-use challenges, so a restart cannot turn an unsolicited
  or replayed `PairAccept` into trust. Schema-v8 migration retains older Discord pairing rows for audit but revokes any row
  that lacks the new operator-confirmation evidence; re-pairing is explicit.
- Discord discovery and pairing remain separate from DAD execution. DAD LAN hub protocol 4 carries the authenticated public
  Application ID, endpoint fingerprint, pairing health, and typed debug alliance-PF coordination, and rejects mixed builds.
  Optional `dad.alliance-pf/v1` Discord instructions are separately signed, exact-recipient, replay-bounded copies; the authenticated
  LAN hub remains authoritative. Existing plans, schedules, Stop, claims, leases, queues, and execution stay on the LAN path.
- The Coordinator exposes **Start measured pilot**, **Stop & Evaluate**, and **Resume pilot**. Evidence persists across reloads,
  counts unique terminal non-dry-run plan IDs, retains ordinary failures, hard-fails safety violations, and continues recording
  beyond minimum coverage until evaluation. Profile restoration is `not-applicable` for ordinary LAN plans.
- DAD continuously writes an atomic Ed25519-signed `dad.pilot-evidence/v1` receipt beneath
  `<PilotExchangeRoot>\pilot-receipts`, bound to the immutable Coordinator identity and exact `dad.dll` SHA-256. The legacy
  wizard consumes this receipt; Guild/Channel IDs and tokens never enter the wizard.
- `/dad fleet` opens the local Fleet/Crew Matrix. Its exact-schema TSV inventory is capped at 160 rows and protects
  spreadsheet formula prefixes; ordered Crew Sets are capped at 40 parties with eight members each. Reusable blueprints
  generate deterministic ordinary DAD Plans and manual or daily-reset Schedules through a non-mutating preview.
  Applying is separately disabled by default, refuses active DAD/Scheduler/Schedule work and unowned ID collisions, keeps
  queue authority local, updates Plans and Schedules as one revision, and stores one durable exact-undo snapshot.
- `/dad batch` (also **Open Batch Preset Wizard** on Plans) opens a separate session-draft generator for ordinary
  local Plans and Schedules. Choose ordered rotating account lanes and exact Active characters, ordered anchor
  account lanes with one exact character per named DC pool, non-overlapping DC groups and crew counts, then one or
  more existing Plan templates. Preview deterministically zips the same crew index across rotating lanes, appends
  anchors, reports shortages/unused rows and out-of-pool anchor warnings, blocks duplicate names/IDs and output above
  512, and never mutates the source templates. Apply appends the frozen preview as one configuration mutation only
  while DAD/Scheduler/Schedule mutation is safe. One session-only exact Undo is available until any Plan or Schedule
  changes; drift refuses Undo instead of overwriting newer work. Per-template Schedules are pool-major; the optional
  combined Schedule is pool, crew, then template order. A `DailyReset` template may set **Daily** on every generated
  primary row; leaving that option off preserves the template's existing row flags. The existing reward probe remains
  unchanged, so no checked rows means no probe and any
  unchecked, unknown, not-received, stale, contradictory, or unproven result still runs the ordinary preset.
- With `/dad debug` enabled, saved preset rows expose an explicit Alliance `A` through `G` assignment. Substitutes inherit
  their primary row; A-C remain required with one to eight effective characters each, optional D-G accept up to eight
  each, and a preset may contain three to 24 total.
  Status > Readiness > **Alliance Party Finder** validates a concrete selected preset and exact Alliance-A Slot1 host.
  **Create party** opens one private cross-world three-group recruitment for The Labyrinth of the Ancients on that local
  or authenticated remote worker; only after Slot1 reports its owned listing open does **Grab dads** dispatch every
  unresolved non-host target concurrently through the authenticated hub. Receivers open Party Finder only
  while hidden, select Private tab `2` and Raids category index `5`, and refresh once per retry cycle. DAD accepts exactly
  one standard or compact result list whose `OwnerNode` is visible and whose renderer storage is ready. For each visible
  renderer, DAD reads recruiter text node ID `28`, decodes its `NodeText` as a SeString, compares the plain `TextValue`
  exactly and ordinally with the Slot1 host name, and uses each bounded match's zero-based `ListItemIndex`. Missing,
  hidden, unready, ambiguous, unhydrated, differently cased, partial, and invalid rows send no listing callback; DAD waits
  under the existing five-second observation deadline instead of opening earlier unrelated rows. It opens the first
  matching row at or after its retained cursor and, after a same-name detail fails exact validation, closes it before
  trying the next matching row. Each requested row uses the exact ordered `[13, index]` then `[11, index]` group from the
  [raw callback source](Z:/logs/20260727.md:123): DAD validates the complete group, resolves `LookingForGroup` once, reuses
  that pointer, and fires both calls synchronously without lookup or observation between them.
  Integer-only callbacks retain the proven stack path; mixed subgroup callbacks retain a zeroed HGlobal `AtkValue` array
  with explicit null-terminated UTF-8 storage, and worker joining does not require ECommons global initialization. They
  accept only the exact leader and home world, Labyrinth duty, private flag, alliance mode, and three-party detail; listing
  comments are ignored. The exact detail is revalidated immediately before the one-shot A-G subgroup callback. Assignment
  A-G maps to zero-based selector and observed group indexes `0`-`6`; the callback is
  `LookingForGroupDetail`, `updateState=true`, payload `[12 + index, "Alliance X"]`, producing IDs `12`-`18`.
  B=`13` is user-observed; A=`12` and C=`14` through G=`18` are locally verified mappings only. A worker that enters
  recruitment mode or exposes the recruitment editor is blocked without callback retries or automatic cleanup.
  DAD requires no pre-existing Yes/No or private passcode prompt before that subgroup callback. Afterward, one fresh ready
  Yes/No may be acknowledged and clicked once before the private prompt, or a ready private prompt may appear directly and
  skip Yes. Simultaneous prompts and either visible-but-unready prompt send no callback and use the existing five-second
  retry. Revision 24's preserved acknowledgement flow then resolves only `LookingForGroupPrivate` and sends
  `[0, passcode]` with `updateState=true` once. It performs no further callback until a later snapshot shows that prompt
  gone. If the detail closed automatically, verification begins without a close callback; if a detail remains unready,
  DAD observes it without firing; and if it remains ready, DAD resolves it fresh and sends one raw signed `[-2]` with
  `updateState=false`, preserving the [captured callback values](Z:/logs/20260727.md:264). That deferred close must
  disappear before final exact subgroup verification, and early subgroup observation cannot bypass either acknowledgement.
  Transient search failures retry at a capped cadence until Stop, and a proven wrong subgroup is repaired only through
  guarded leave/rejoin.
  PF join, invite acceptance, participant departure, recruitment cleanup, and party teardown share one attempt-bound
  prompt rule: the prompt must be newly surfaced, visible, ready, operation-relevant, and still attached to unchanged
  frozen context. Unreadable or unmatched prompts fail closed. The global **Allow one fresh unproven prompt approval
  (unsafe)** setting is persisted and off by default; when enabled it permits only one fresh sole ready prompt for the
  current attempt, and every use emits explicit warning and audit evidence.
  Completion verifies every effective character, closes recruitment without disbanding the alliance, and never queues a
  duty. The preset's actual raid remains unchanged for the operator to queue manually afterward.
  Detailed local evidence is appended beneath
  `<plugin-config>\alliance-pf\logs`; native callback begin/returned markers contain only action, addon, ordinal, payload
  types, and update-state. The always-enabled **Check PFs** debug control writes a read-only UTF-8 addon-tree capture beneath
  `<plugin-config>\alliance-pf\diagnostics`; it may contain player or recruiter text and stays out of logs, hub/Discord
  transport, and public artifacts. Discord copies are deleted best-effort after completion or Stop. Creation clean-starts
  Party Finder by submitting only the fixed native `/pfinder` chat command and polls readiness at 250 ms. Current typed
  ClientStructs controls own Recruit Members/details and Submit; ECommons is limited to create-side stateless UI-input
  helpers. DAD dispatches Alliance, Raids, and the exact enabled Labyrinth row once each, requiring a later acknowledgement
  within five seconds after every action. If Alliance changes the stored group tab before the open editor reflects its radio,
  the same Create request closes conditions once. When typed Cancel resets `GroupTypeTab`, DAD restores Alliance tab `1`
  exactly once through its existing adapter and requires a later acknowledgement; this restore neither resends Alliance nor
  refreshes the preset. DAD then reuses or reopens the main PF window as needed, reopens conditions once, and requires the
  Alliance radio before continuing. A ready next action may dispatch on the next 250 ms poll. Duty selection lets the game
  populate the
  complete API-15 selector, including its opaque discriminator. DAD
  then captures the full current recruitment struct plus group tab and average-item-level state, overlays only the private
  passcode, API-15 cross-world byte `1`, no-duplicate-job setting, empty comment, Alliance A `3x8` membership, cleared stale
  members, and 23 unrestricted open slots, writes the full state once, and invokes one DAD-resolved editor refresh. The
  exact stored slot flags authorize unrestricted recruitment; the convenience checkbox remains diagnostic only. Objective,
  completion, language, loot, item-level, and opaque selector values remain game-owned. The
  implementation is source-adapted from PartyFinderPresets but does not discover, load, reflect into, call, or exchange IPC
  with that plugin. An unavailable refresh signature blocks before any agent write; an apply or refresh failure restores
  the complete original state. A mutation never advances the state machine by dispatch alone: a later snapshot must
  acknowledge the exact visible and stored editor state. A missing acknowledgement blocks that individual Create cycle
  without re-opening, redispatching, rewriting, refreshing, or submitting within the cycle. One **Create party** click may
  recover from the first pre-publication block only when condition `66` remains false: DAD runs the existing Stop-create
  close/reset path once, keeps the same request, preset, exact targets, and coordinator state, generates a fresh passcode,
  and runs the unchanged flow as final cycle 2. First-cycle success, an already-active recruitment, or Stop never starts
  cycle 2; a cycle-2 block remains blocked and never starts a third cycle. The conditions editor's temporary owner handle
  is never treated as publication authority. Success requires
  one Submit, the editor to close, full stored duty/password/group/slot/comment/open-slot-flag exactness,
  `UsingPartyFinder` condition `66`, and `ParticipatingInCrossWorldPartyOrAlliance` condition `84`; the opaque native PF
  owner handle may remain zero and is shown only as optional diagnostic data. Online-status row `26` is not recruitment
  authority. Creator solo/mutation safety applies only before
  Submit; later calls observe publication without another mutation gate. Exact success retains DAD ownership as
  `ListingOpen` and enables **Grab dads**; blocked, stopped, and unowned states keep Grab disabled.
  **Stop PF** beside Create/Grab uses DAD's existing Stop policy: it closes a pending or blocked editor or ends only DAD's
  owned recruitment through the acknowledged recruitment-only cleanup path; it never disbands or queues. Remote cleanup
  remains pending until the Slot1 host confirms that listing ownership is cleared, including when Stop races the first
  host response.
  Listing ownership is independent from the displayed PF state and survives Blocked/Stopped presentation until cleanup
  proves it cleared. Monotonic operation generations reject late asynchronous results; local and remote cleanup keep
  retrying within one fixed 60-second deadline, then surface terminal partial failure without hiding unresolved ownership.
  After that deadline DAD performs read-only, bounded-backoff observation only and clears the blocker when exact local or
  authenticated remote evidence proves that operator cleanup removed the listing.
  Stop and post-formation cleanup open the owned detail window, require a fresh
  recruitment-only confirmation, and acknowledge closure when condition `66` clears. Retained DAD ownership—not the
  diagnostic native owner handle—authorizes that cleanup.
  The local Create readiness row shows the first exact blocker and disables the button on Client Dads or any other failed
  prerequisite; rejected attempts remain visible and are audited.

## Operator status and cancellation

- `/dad status` keeps its existing behavior and prints the live shell report to chat.
- `/dad mini` toggles a compact, manually opened status window. It renders cached authority, run, scheduler,
  slot, queue, worker-heartbeat, failure, and Stop-all acknowledgement state without polling peers from Draw.
- The main activity banner, Status > Current Activity, and mini status project active scheduler work and active
  schedule/inter-entry work as `Running` when no DAD run is busy. This is display-only: runtime truth, authority,
  advancement, locking, cancellation, and transport behavior continue to use their existing state.
- An active Schedule shows the same `Running now` cursor in Status and in Schedules > Cadence & Actions:
  schedule and preset names, current entry/total entries, and current repeat/entry repeats. When the saved
  definition cannot supply a total, DAD omits that denominator instead of guessing.
- A blocked Schedule entry caused by an ordinary entry failure or coordinator/plugin reload can be resumed only
  through the operator's **Resume from failed entry** action. DAD creates a new run at the exact persisted cursor,
  retains prior history, requires all clients and DAD/scheduler work to be idle, and never replays automatically.
  Cancelled runs remain terminal and cannot be resumed.
- Scheduled worker commands with `TimeoutSeconds <= 0` have no outer command deadline and remain active until explicit
  cancellation or an executor/queue-pulse result completes them. Positive worker command values remain finite, begin at
  worker execution start, and clamp to 30–7,200 seconds; coordinator waits, IPC, scheduler advancement, and
  level-target skipping are unchanged.
- Worker commands are validated before serialization and immutable registration. One run owns all active and pending
  commands at a time; same-run work stays FIFO, cancellation drains that run completely, and historical duplicate status
  is returned without replacing the current globally visible worker state.
- Automatic schedule admission occurs only on the Coordinator and only while every conflicting scheduler/run/worker,
  queue, PF, formation, takeover, and cleanup owner is idle. Cadence advances only after admission or an explicit consumed
  skip; DAD performs fresh strict request validation immediately before dispatch. Cancellation and reward-probe cleanup
  retain their original acknowledgement deadlines and block conflicting admission until resolved.
- Regular Duty Finder starts from either a direct preset or a Schedule use the same exact-selection executor. Before
  Join, DAD requires the stable mapped row and character, callback ordinal, selected agent type/id, and interface-selected
  duty ID to agree. If API15 publishes the interface value late, DAD waits at most six seconds for two exact observations
  at least 250 milliseconds apart; contradiction or timeout restarts the full safe selection cycle.
- Regular and roulette queue mutations recapture strict local safety immediately before native writes and require visible,
  ready addons before callback or list dereference. Local and NPC queue ownership is released on entry and every owned
  terminal path. Local Duty, Duty Support, Trust, and Premade Duty use one lifecycle rule: after an unmatched duty exit they
  wait ten seconds for delayed matching `DutyCompleted` evidence before classifying abandonment, and completion wins even at
  the exact deadline. MOGTOME accepts only exact active-run results and preserves failed Stop responses.
  Durability ignores the soul-crystal slot but still treats zero-condition real gear as broken. Imported completion commands
  are shown verbatim and require explicit confirmation. Custom slash commands use Dalamud's registered-plugin command manager;
  only the AutoRetainer Grand Company command uses native chat input, and its exact root must be `/ays`. Control characters,
  plain text, multiline values, and every other GC root are rejected. A temporarily missing game UI module waits for at most
  five seconds and then exposes a typed failure. Historical close-client/shutdown values deserialize safely but are permanent
  no-ops and are not selectable.
- Questionable subscriber/gate reflection changes retain their exact pre-image before the first write and restore each still-
  owned value independently on failure, disable, reload drift, and disposal. AutoRetainer postprocess handoff now distinguishes
  Armed, RequestSent, and Owned generations; timeout re-arms only the same operation, late named callbacks are released, and
  DAD never calls the global finish channel for a merely pending request. Pilot receipt IO returns immutable results and applies
  its configuration path/save only from the framework update.
- Schedule preset rows turn orange when the scheduler's current effective-crew LevelSeek evaluation proves that every
  targeted row already meets its goal and would therefore be skipped. Hover the preset row for the same per-slot
  evidence used by execution. After required job changes and exact worker readiness, DAD repeats the frozen evaluation
  from fresh local/peer truth before it can dispatch; missing characters, unknown levels, missing presets, and
  untargeted rows stay normal.
- The selected Schedule's ordered preset rows show `SKIPPED` badges for exact skips in its active or latest non-dry run,
  aggregate repeated skips, explain them on hover, and disclose when bounded history retains fewer row details than the run's total skip count.
- Daily Roulette preset rows expose a per-character **Daily** checkbox, default off. It is consulted only when that
  preset runs as a `DailyReset` Schedule entry. For an entry with a LevelSeek target, DAD first loads every worker,
  acknowledges requested jobs, and re-evaluates LevelSeek: a proven target skips without querying Daily, while below or
  unknown evidence reaches the same eligible Daily check. Entries without targets retain the existing early Daily path.
  DAD wakes and inspects checked effective characters one at a time, never wakes unchecked rows for inspection, and
  skips only when every checked character has two matching native reads at least 250 milliseconds apart after Duty
  Finder proves the exact live roulette selection. DAD opens and hydrates Duty Finder when it owns a closed window, then
  closes only that owned window; a pre-existing window is preserved and is read only when it is already on the exact
  roulette. Any missing native state or route, identity or live-list drift, UI failure, timeout, stale or contradictory
  reply, unknown state, or not-received result runs the preset normally. A preflight that has started does not restart
  after its existing fail-open continuation. With `/dad debug` enabled, Status > Readiness also offers **Log Duty
  Roulette reward states**, which uses the same owned-UI path and logs every current live roulette row.
- Before a participant arms exact-inviter acceptance, DAD leaves an unrelated same- or cross-world party through a
  separate guarded recovery path and requires sustained authoritative solo state. Exact fresh invites keep their
  five-second retry cadence; a hidden invite prompt is restored from the notification list, and direct Yes is limited
  to the newly restored DAD-owned prompt or a prompt that proves the exact inviter. After the configured assembly
  window, DAD publishes a persistent warning but remains reachable and continues restore/accept retries without
  failing the entry, closing a client, or advancing the Schedule.
- Saved Duty Support, Trust, and Premade Duty presets can enable **Leveling Mode** with one plan goal, deterministic
  `Lowest first` or `Highest below goal` job rotation, and an ordered minimum-level-to-duty table. DAD requires exact
  fixed account/character identities and complete XADB job ledgers, excludes base classes and limited jobs, selects
  the threshold at or below the lowest selected party job, and never falls back when configuration or roster truth is
  uncertain. Each iteration is a new immutable, synced ordinary child run through the existing lane executor; success
  refreshes exact job truth before compiling the next child, while failure, timeout, or cancellation ends the outer
  operation without replay. Completed characters remain compatible fillers, parties are not retained between children,
  and fixed job/duty, LevelSeek, and ordinary stop settings are preserved but overridden only while the mode is enabled.
  The checkbox validates the currently visible Run Family/Submode draft and saves that lane into the selected preset in
  the same action, so a valid Leveling/NPC or Duty Finder/Premade selection does not require a separate preset update.
  Run Family and Submode remain locked until Leveling Mode is disabled.
- When a Client Dad loses its Coordinator route, a separate `DAD Client` window opens automatically with the
  target, current attempt, next retry, and last disconnect. Reconnect uses capped backoff while DAD remains
  enabled in Client, non-local mode; it stops when the route returns or the role, mode, or enabled state changes.
  The guarded `Disable DAD` confirmation is the reconnect window's explicit stop action.
- Item-level cancel buttons require a confirming second click and affect only the selected run, schedule, or job.
- `Stop all` requires confirmation within five seconds. The Coordinator snapshots every routable Client Dad,
  drains DAD-owned scheduler/run/worker/executor and pre-commit takeover work locally, then sends the same
  operation ID to each snapshotted client. The acknowledgement panel distinguishes acknowledged, rejected,
  disconnected, and timed-out workers.
- Stop-all, Disable, and unload share one idempotent local cleanup path across scheduler, formation, AutoParty, PF,
  coordinator, takeover, claims, worker/queue execution, presence ownership, and best-effort VERMAXION release. Disable
  and unload never broadcast. A remote Stop-all is accepted while idle or only from the authenticated active Coordinator;
  every other sender is rejected without mutation.
- Stop-all never broadly stops unrelated AutoRetainer or VERMAXION work. A takeover at or beyond the existing
  reset/relog commit boundary is preserved and reported as a partial result.
- Client Dad preserves one coordinator-visible wake identity, but assigns every actual VERMAXION v2 reservation epoch
  a fresh internal opaque token. Renewals, authority snapshots, grant matching, and exact cleanup keep that epoch token
  until release is verified; a retry allocates its next token only after cleanup completes. VERMAXION finishes owned
  work, disables/drains AutoRetainer, and emits a matching local grant; stale grants from an earlier epoch are ignored,
  and DAD can then prepare immediately with verified AR-off suppression. If v2 is unavailable while loaded VERMAXION
  v1 explicitly reports idle, AutoRetainer is readable
  and idle with Multi Mode off, and suppression is clear or DAD-owned, DAD may prepare from that verified-idle
  compatibility evidence. It revalidates the evidence before reset; final readiness still requires the exact
  target character and the post-AR gate. Logical wake orders do not expire. The five-second coordinator cadence
  and mini-window snapshots affect status freshness only, not local ownership timing.
- A connected Client Dad that is logged out can enter one separate title-idle login path only from a fresh exclusive
  ready `_TitleMenu`; Character Select is never eligible. The exact account route and requested catalog character must
  match, all condition flags must be clear, and title movies, connection/navigation, dialogs, multiple, unknown, or
  character-select surfaces remain wait-only. AutoRetainer must be readable and idle with clear ownership,
  VERMAXION must freshly report exactly `Idle`, and Lifestream must be readable, idle, and freshly prove
  `CanAutoLogin`. If AutoRetainer Multi Mode is on, DAD disables and verifies it once, then recaptures every gate; if
  already off, it skips that mutation. DAD then calls only the acknowledged
  `Lifestream.ConnectAndLogin(Name, HomeWorld)` IPC. Accepted calls never replay; explicit `false` may retry only after
  five seconds and complete fresh proof; an exception or uncertain result blocks without replay. Exact
  `MovieStaffList` may receive one Escape only while Multi Mode is already off and all three automation owners are
  readable and idle, and login still waits for a later fresh valid `_TitleMenu`. Busy or unreadable automation keeps
  waiting without a new timeout. The in-world `/ays relog` sequence remains unchanged, while a visiting source
  character's frozen identity, home destination, and relog target stay visible throughout Data Center return travel.
- The v2 wire format uses string reservation states (`Pending`, `Granting`, `Granted`, `Released`, `Rejected`). DAD
  also accepts the legacy VERMAXION numeric values `0` through `4` in that order. Unknown/malformed states fail
  closed; verified-idle compatibility is reserved for genuine v2 IPC unavailability, not invalid v2 responses. If
  `Reserve` directly returns a parsed v2 `Released` response, DAD cleans up, enters its existing five-second next
  epoch, and requests a fresh reservation without arming the AutoRetainer callback fallback. A transient unsafe
  world state still releases DAD suppression and VERMAXION ownership immediately, and no disable, reset, or relog
  command is sent until a replacement grant is verified.

## Deployment notes

- **Hub transport protocol is version 4.** Every paired dad — the Coordinator Dad
  and all Client Dads — should run the **same build**. Incompatible hub protocol versions are rejected with
  `protocol-mismatch`; exact assembly-build equality is not otherwise enforced.
- Non-loopback (LAN) connections require a matching `TransportSharedSecret` on every peer (HMAC-SHA256). Envelopes
  are now replay-resistant (signed nonce + timestamp); peers should keep clocks within ~30s of each other.
- A configured Coordinator endpoint is not presented as a live authority route until the authenticated handshake
  succeeds. Client liveness is checked from inbound frames, and a stale route is closed and reconnected.
- Transport dispatch binds authenticated source and target context to wake, claim, assembly, cancellation, PF, worker,
  roster/profile, and Stop-all mutations. Mutating IPC and roster/plugin state are framework-thread confined; bounded
  transport admission uses atomic capacity reservation. One production ingress normalizer handles transport requests,
  responses, and mutating CallGate inputs: optional null collections normalize compatibly, required identity/execution
  objects reject, malformed notifications drop with bounded diagnostics, and mutable request/status graphs are cloned.
  Signed AutoParty envelopes, share imports, account files, and helper projections retain their specialized validation
  paths; protocol/schema version and valid current/legacy wire shapes are unchanged.
- Remote worker status polling starts only after the worker returns a real acknowledgement matching the exact current
  run, command, worker, role, module, and frozen identity. A live status replaces the cached status only when its run
  and command match that assignment; an older reply is discarded and polled again, while a current-command role,
  module, worker, or identity contradiction still fails the strict validator. The newest exact cache is available only
  while that request is pending on an authenticated, routable connection; disconnected or non-pending requests return
  no substitute, leaving the existing missing-peer timeout active.
- Roster sync is passive: the Coordinator pushes a compact roster catalog (account / character / job→level) to all
  clients on connect, on change (incl. level-ups), and on a periodic reconcile. **Build Connected Crew** remains an
  explicit current-participant view; normal sync does not require a manual catalog pull.

See `changelog.txt` for the full history. Durable design/review notes live in the XIV KB under
`Dhog/Dad/` (e.g. `DAD_FIX_IMPLEMENTATION_GUIDE_2026-06-26.md`).
