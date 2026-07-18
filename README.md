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
- Configuration schema v4 no longer serializes remote profile catalogs. Online remote catalogs are session-only and
  reconcile every 60 seconds; durable per-account character profiles stay in their separate account JSON files.
  Passive roster learning coalesces disk writes, and Crew projections/filter results are revision-cached.

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
  navigation, connection, or error/dialog overlay. DAD sends `/ays m d` at most once, verifies Multi Mode is off,
  then sends the existing exact `/ays relog Name@World` command at most once and waits for world readiness. A generic
  title screen, stale/unknown evidence, route loss, busy AutoRetainer, or another automation owner remains wait-only;
  this path never starts a closed game process and does not alter the established in-world takeover sequence.
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
