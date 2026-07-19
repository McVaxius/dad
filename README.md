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
dotnet build .\dad.csproj -c Debug -p:Platform=x64
```

## Test

```powershell
dotnet test .\Tests\dad.Tests.csproj
```

## Crew and persistence

- Home includes a one-step **Name this DAD** guide. It gives the immutable local client account a meaningful alias;
  normal crew choices show the alias only, while the session-only **Details** checkbox also shows the stable ID.
- Crew account tools list each account's characters, can show every matching roster row by clearing secondary filters,
  and forget remote account copies immediately with Ctrl+Shift. **Build Connected Crew** uses current participant
  projections and suppresses the mirrored local worker while retaining distinct remote sessions.
- Character Profiles and launch-profile scaffolding remain operational but are visible only with Debug UI enabled.
  Per-row account assignment controls are not part of the normal Crew browser; the assigned/unassigned filter and
  existing compatibility contracts remain available.
- Configuration schema v5 retains the v4 removal of serialized remote profile catalogs. Online remote catalogs are session-only and
  reconcile every 60 seconds; durable per-account character profiles stay in their separate account JSON files.
  Passive roster learning coalesces disk writes, and Crew projections/filter results are revision-cached. Schema-4 users
  migrate to an empty, disabled AutoParty configuration without generating an identity or initializing a network route.
- Shared configuration changes are coalesced at the end of the framework update after a 250 ms quiet period, with a
  one-second maximum delay. Storage failures stay outside window drawing, retry on a bounded cadence, and surface a
  memory-only warning with an explicit **Retry save** action; disposal makes one final write attempt.
- Durable run history keeps at most 50 compact completion snapshots. Status, timing, task/stop progress, step results,
  warnings, and scheduler failure evidence remain visible while requests, participants, leases, executor state, client
  and session IDs, and authority endpoints are removed from saved history.

## AutoParty development boundary

- AutoParty is an unreleased, disabled-by-default integration. `/dad autoparty` exposes explicit endpoint generation,
  public pilot export, enrollment import, local pairing, formation-only fixture import, transport, execution, status-receipt, rotation, and Owner Stop
  controls. Loading or migration does not generate an identity or enable any of the three local gates.
- **Pilot exchange root** defaults to `Z:\autopartypilot` and may be changed to a fully qualified, writable, non-root
  drive or UNC path only while transport, pairing, and execution are disabled. Applying it immediately derives and
  creates `pilot-input`, `pilot-receipts`, `pilot-courier`, and `plugin` without requiring a DAD reload. Existing schema-5
  configurations migrate to the shared default; schema number, LAN behavior, IPC v1, and Fleet TSV stay unchanged.
- The AutoParty courier connector is separate from the existing LAN `DadTransportService`. It uses a bounded outbound-only
  file spool beneath `<PilotExchangeRoot>\pilot-courier` and opens no inbound listener. LAN behavior and public DAD IPC remain unchanged.
  DAD consumes the versioned `Dad.AutoParty.Protocol` package instead of an absolute project path.
- Local grants are immutable and intersect exact owner, island, opaque character, requested job, activity, permission,
  expiry, replay, reservation, preflight, lease, and revocation truth. Owner Stop, DAD disable, local safety, expiry, and
  revocation override remote traffic. Execution exposes typed Prepare, Reserve, Form, Queue, Cancel, Settle, and Restore
  operations only; there is no string-command or arbitrary-JSON execution route.
- A Schedule request with an explicit AutoParty proposal ID waits inside DAD until local authorization is active. Requests
  without that ID—including existing local and LAN presets—keep their established route. Formation-only execution ends at
  the appended `GroupReady` phase, preserves the party, and denies queue and settle behavior.
- Endpoint signing and encryption private keys are generated only after the operator presses **Generate endpoint identity**
  and are stored through CurrentUser DPAPI under DAD's configuration root. Public `.apidentity` exports contain no private
  keys or FFXIV identifiers. Pairing requires an artifact-bound owner-acceptance receipt plus **Confirm pilot pairings
  locally**. **Import formation-only pilot fixture** then validates the same artifact hash, exact enrolled fingerprints,
  one consented queue authority, numeric requested jobs, and a safe duty before creating a local `GroupReady` plan;
  execution cannot enable until transport, receipt, pairing, fixture, and an end-to-end paired courier probe are healthy.
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

## Operator status and cancellation

- `/dad status` keeps its existing behavior and prints the live shell report to chat.
- `/dad mini` toggles a compact, manually opened status window. It renders cached authority, run, scheduler,
  slot, queue, worker-heartbeat, failure, and Stop-all acknowledgement state without polling peers from Draw.
- An active Schedule shows the same `Running now` cursor in Status and in Schedules > Cadence & Actions:
  schedule and preset names, current entry/total entries, and current repeat/entry repeats. When the saved
  definition cannot supply a total, DAD omits that denominator instead of guessing.
- A blocked Schedule entry caused by an ordinary entry failure or coordinator/plugin reload can be resumed only
  through the operator's **Resume from failed entry** action. DAD creates a new run at the exact persisted cursor,
  retains prior history, requires all clients and DAD/scheduler work to be idle, and never replays automatically.
  Cancelled runs remain terminal and cannot be resumed.
- Regular Duty Finder starts from either a direct preset or a Schedule use the same exact-selection executor. Before
  Join, DAD requires the stable mapped row and character, callback ordinal, selected agent type/id, and interface-selected
  duty ID to agree. If API15 publishes the interface value late, DAD waits at most six seconds for two exact observations
  at least 250 milliseconds apart; contradiction or timeout restarts the full safe selection cycle.
- Schedule preset rows turn orange when the scheduler's current effective-crew LevelSeek evaluation proves that every
  targeted row already meets its goal and would therefore be skipped. Hover the preset row for the same per-slot
  evidence used by execution; missing characters, unknown levels, missing presets, and untargeted rows stay normal.
- Daily Roulette preset rows expose a per-character **Daily** checkbox, default off. It is consulted only when that
  preset runs as a `DailyReset` Schedule entry and only after LevelSeek declines to skip. DAD wakes and inspects checked
  effective characters one at a time, never wakes unchecked rows for inspection, and skips only when stable exact
  Duty Finder evidence says every checked character already received the selected roulette reward. Any missing route,
  identity mismatch, timeout, stale or contradictory reply, unknown state, or not-received result runs the preset
  normally. DAD closes Duty Finder only when this inspection opened it.
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
- Stop-all never broadly stops unrelated AutoRetainer or VERMAXION work. A takeover at or beyond the existing
  reset/relog commit boundary is preserved and reported as a partial result.
- Client Dad registers one renewable VERMAXION v2 reservation per wake operation. VERMAXION finishes owned work,
  disables/drains AutoRetainer, and emits a local grant; DAD can then prepare immediately with verified AR-off
  suppression. If v2 is unavailable while loaded VERMAXION v1 explicitly reports idle, AutoRetainer is readable
  and idle with Multi Mode off, and suppression is clear or DAD-owned, DAD may prepare from that verified-idle
  compatibility evidence. It revalidates the evidence before reset; final readiness still requires the exact
  target character and the post-AR gate. Logical wake orders do not expire. The five-second coordinator cadence
  and mini-window snapshots affect status freshness only, not local ownership timing.
- A connected Client Dad that is logged out can enter one separate title-idle login path only while AutoRetainer
  Multi Mode demonstrably owns the state: the stable account route and requested catalog character must match,
  AutoRetainer IPC and ownership state must be readable and idle, and a fresh ready `_TitleMenu` must have no title
  navigation, connection, or error/dialog overlay. Lifestream must also be readable, idle, and freshly prove
  `CanAutoLogin` before DAD sends `/ays m d` at most once. After Multi Mode is proven off, DAD calls the acknowledged
  `Lifestream.ConnectAndLogin(Name, HomeWorld)` IPC. `true` advances to the existing world-readiness wait; explicit
  `false` may retry only after five seconds and a complete fresh proof; an exception is uncertain and blocks without
  replay. A generic title screen, stale/unknown evidence, route loss, busy AutoRetainer/Lifestream, or another
  automation owner remains wait-only. This path never starts a closed game process and does not alter the established
  in-world `/ays relog`, home-world return, or takeover sequence.
- The v2 wire format uses string reservation states (`Pending`, `Granting`, `Granted`, `Released`, `Rejected`). DAD
  also accepts the legacy VERMAXION numeric values `0` through `4` in that order. Unknown/malformed states fail
  closed; verified-idle compatibility is reserved for genuine v2 IPC unavailability, not invalid v2 responses. If
  `Reserve` directly returns a parsed v2 `Released` response, DAD cleans up, enters its existing five-second next
  epoch, and requests a fresh reservation without arming the AutoRetainer callback fallback. A transient unsafe
  world state still releases DAD suppression and VERMAXION ownership immediately, and no disable, reset, or relog
  command is sent until a replacement grant is verified.

## Deployment notes

- **Hub transport protocol is version 2** (raised from 1 on 2026-06-27). Every paired dad — the Coordinator Dad
  and all Client Dads — should run the **same build**. Incompatible hub protocol versions are rejected with
  `protocol-mismatch`; exact assembly-build equality is not otherwise enforced.
- Non-loopback (LAN) connections require a matching `TransportSharedSecret` on every peer (HMAC-SHA256). Envelopes
  are now replay-resistant (signed nonce + timestamp); peers should keep clocks within ~30s of each other.
- A configured Coordinator endpoint is not presented as a live authority route until the authenticated handshake
  succeeds. Client liveness is checked from inbound frames, and a stale route is closed and reconnected.
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
