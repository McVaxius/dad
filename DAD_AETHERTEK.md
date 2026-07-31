# DAD

> Plan and coordinate duty runs across your FFXIV characters.

DAD is the control room for repeatable FFXIV duty runs. It works with one character, but it is especially useful when several game clients need to become the right party without manually checking every window.

Create a reusable preset that says who should run, which job each character should use, what content to queue, and when the run should stop. DAD checks the live roster and required plugins across the selected crew, readies the selected characters, assembles and verifies the party, starts a supported queue, and keeps progress and blockers visible from start to finish.

## What DAD Solves

Multibox duty setup normally means repeating the same chores across several clients: finding the right characters, changing jobs, checking other automation, forming the party, opening Duty Finder, and recovering when one client is stale or disconnected.

DAD turns that setup into a validated run:

- save a crew once and reuse it;
- choose exact characters, jobs, roles, and substitutes;
- coordinate wake or relog for an already connected same-account client when the preset allows it;
- wait for unsafe or busy client state instead of fighting it;
- build and verify the party before queueing;
- run presets immediately or place them in an ordered schedule;
- see why a run is waiting, blocked, skipped, cancelled, or complete.

## Build Reusable Crews

A DAD preset combines the activity with the characters who should perform it. Each crew row can carry:

- an account and exact character;
- a requested combat job or role requirement;
- explicit substitute rows for unavailable characters;
- a wake/relog policy;
- an ADS loot preference: no change, Need, Greed, or Pass;
- an optional Level seek target.

Level seek is useful for leveling schedules. DAD evaluates targeted primary rows and skips a preset only when every targeted, bound primary row has a known job level at or above its target. A bound target with an unknown or below-target level keeps the preset runnable; untargeted and empty placeholder rows are ignored.

Templates can keep the party roles without binding permanent characters. Instantiate a template when needed and DAD fills matching roster characters by role, leaving unresolved slots visible for review.

## Form A Crew Without Queueing

At the top of Plans, **Crew Tools** can prepare and form the selected saved preset without starting its duty. The card
shows the selected/effective preset, whether DAD resolved it as a regular party or alliance Party Finder formation, its
live state, and the first blocker.

- **Create group** becomes available as soon as the selected roster itself is valid. Pressing it submits one runtime-only,
  in-memory formation request immediately; characters that are offline, busy, waiting for post-AR, or need a permitted
  relog wait in the normal scheduler until ready. The request is single-active, is not restored after a plugin restart,
  does not edit the saved preset, and does not save a scheduler job.
- Formation uses exactly the selected primary crew rows, even when the saved activity normally queues with more players.
  Missing, duplicate, unresolved, or ambiguous identities, invalid requested jobs, an incompatible **Already online**
  wake policy, exact Slot1 authority, dependencies, transport, scheduler conflicts, and cancellation safety still block
  creation. The Coordinator operates the run but does not need its account or character in the selected crew. These
  formation-only rules do not relax normal Plan or Schedule validation.
- Regular parties use DAD's normal verified assembly and stop at **GroupReady**. DAD does not queue them.
- A selected non-PvP Duty Finder raid whose live catalog size is above eight uses the proven private alliance flow. DAD
  asks exact Slot1 to create and own the listing, waits for it to open, grabs the remaining exact preset characters once,
  verifies their assigned subgroups, and closes only the recruitment after Slot1 confirms ownership is cleared. DAD does
  not queue or automatically disband the alliance.
- Exact Slot1 is always the party leader, inviter, queue executor, alliance host, and managed teardown authority. This
  works whether that worker is local or remote; DAD rejects worker, account, character, or Content-ID drift and verifies
  formation from Slot1's returned PartyList.
- Leveling Mode resolves and freezes only its first effective child for this operation.
- **Disband** asks exact Slot1 to tear down the held regular crew and waits for its guarded terminal response. With no
  active Crew Formation, it can also disband the current
  party only when DAD proves a stable out-of-duty/out-of-queue state, at least two exact members, and local leadership.
  Membership drift, leadership drift, stale confirmations, and unrelated prompts remain fail-closed.

Use **Stop All** to cancel active Crew Tools preparation or formation. The normal Plan and saved preset remain intact.

## Supported Today

These lanes have guarded live execution in the current plugin. "Guarded" means DAD validates the selected content, roster, required-plugin truth, client state, and queue ownership before it changes game state.

| Run type | What DAD handles |
| --- | --- |
| MSQ Story Duty (NPC) | Runs a selected story duty through Trust first, then Duty Support when Trust is unavailable. |
| Duty Support | Queues a selected supported duty, or chooses the highest eligible leveling duty for the current job. |
| Trust | Queues selected Trust content, or chooses the highest eligible leveling duty and, by default, refreshes NPC level data before selection. |
| Local Duty / Unsync | Runs a one-character regular Duty Finder queue in synced or unrestricted/unsynced mode. |
| Premade Duty | Assembles a verified crew and starts a guarded regular Duty Finder queue in synced or unsynced mode. |
| Daily Roulette | Queues an eligible four-player non-PvP roulette with the verified crew. |
| Custom Duty | Uses a selected Duty Finder entry and routes it through local or premade execution according to party size. |
| Commendation | Runs the supported Under the Armour attempt loop. |
| MOGTOME | Hands a run to MOGTOME through readiness, start, status, stop, and attempt-limited IPC. |

Saved Local Duty presets also adapt to their configured primary rows: one primary character stays on the local path, while a multi-character crew uses the premade party path.

## Schedules And Repeat Runs

A schedule chains saved presets in an exact order. Each entry can repeat from 1 to 99 times. Schedules can be started manually or assigned to the FFXIV daily reset boundary at 15:00 UTC.

DAD validates each entry before it starts. A satisfied Level-seek entry is recorded as skipped and the schedule moves on. Successful party entries tear down at the appropriate boundary so the next preset can form a fresh verified party. Dry-run validation is available before relying on a live schedule.

If an ordinary entry failure or a coordinator/plugin reload blocks a schedule, the Coordinator operator can use **Resume from failed entry**. DAD starts a new run at the exact persisted entry and repeat cursor, keeps the earlier run in history, and requires every client plus DAD and scheduler work to be idle. It never resumes or replays automatically. A cancelled schedule is terminal and cannot be resumed.

## Solo And Multibox Use

Local-only runs stay on one DAD instance. Multibox runs use one **Coordinator Dad** and one or more **Client Dads**:

- the Coordinator owns orchestration, the selected roster, and the party plan, and does not need to be in that roster;
- exact frozen Slot1 owns party leadership, invitations, queue execution, alliance hosting, and managed teardown;
- each Client Dad proves its current account, character, job, and readiness;
- the Coordinator and every selected Client Dad publish fresh required-plugin truth on their heartbeat;
- non-leader participants become ready before the queue leader is released;
- the frozen run roster prevents a late or mismatched client from silently replacing the selected character.

Clients on the same machine can use loopback transport. LAN setups require the same DAD build on every client and a matching shared secret.

## How A Run Works

1. You select a saved preset and choose **Recheck readiness (does not run)**.
2. DAD rebuilds one current planner/scheduler snapshot and checks crew, content, and required plugins without starting the Plan.
3. DAD freezes the intended crew so the run cannot drift underneath you.
4. A missing game process must be started manually. A connected same-account client can follow its wake/relog takeover policy.
5. Requested jobs are prepared through valid saved gearsets and rechecked against live game state.
6. DAD waits for AutoRetainer, VERMAXION, world loading, queue state, and other safety gates to become clear.
7. Every selected worker proves its identity and required readiness.
8. DAD sends party formation to exact Slot1, verifies Slot1's returned PartyList, and starts the supported queue there.
9. During and after the duty, DAD reports progress, completion, blockers, and recovery state.
10. At the final party boundary, DAD refreshes exact Slot1 identity and waits for Slot1's guarded teardown result.

## Integrations

DAD coordinates a plugin stack; it does not try to replace every part of it. Whenever DAD is enabled, every participating client must have these dependencies loaded:

| Required plugin | Readiness rule |
| --- | --- |
| Fren Rider | `FrenRider` must be loaded. |
| AI Duty Solver | `ADS` must be loaded. |
| vnavmesh | `vnavmesh` must be loaded. |
| XA Database | `XADatabase` must be loaded at `0.0.0.39` or newer. |
| XA Slave | `XASlave` must be loaded. |
| BossMod | Either `BossModReborn` or `BossMod` must be loaded. |

The **DAD Dependencies** window stays open while local DAD is enabled and any requirement is missing, disabled, outdated, stale, or still being checked. It offers filtered Plugin Installer searches and closes automatically when truth is ready. Disabling DAD suppresses the window. No plugin is enabled silently.

- **XA Database** can provide stored roster and job-level truth. Local runtime and connected DAD snapshots also contribute current roster state.
- **AutoRetainer** is observed so DAD does not relog or take over while retainer work is still active.
- **VERMAXION** provides reservation and handoff safety around post-processing, and can launch a selected DAD preset or schedule through DAD's public IPC.
- **Lifestream** supports configured character relog handling.
- **Fren Rider** is an unconditional DAD dependency and is the default in-duty owner for movement, combat coordination, and exit behavior when that mode is selected.
- **AI Duty Solver (ADS)** is an unconditional DAD dependency and applies the selected loot configuration to exact workers before multiplayer queue mutation.
- **MOGTOME** owns its supported farming run after DAD performs the helper handoff.
- **Questionable** compatibility is available through DAD's guarded bridge when enabled.

Other integrations can still vary by lane. The six dependencies above do not vary by lane or combat mode. New work waits until the Coordinator and every frozen selected client report fresh ready truth; unselected clients do not block a Plan.

## What DAD Does Not Do

DAD is not a standalone combat bot. It owns planning, readiness, party formation, queueing, schedules, status, cancellation, and recovery. Once inside a duty, combat, movement, and leave behavior belong to the player or the configured FrenRider/ADS workflow.

DAD also does not treat missing proof as success. Depending on context, an unknown job level stays visible as a blocker or keeps a Level-seek entry runnable; stale characters, ambiguous Duty Finder rows, mismatched workers, unavailable helpers, and unsafe world state remain blocked until trustworthy state is available.

DAD does not start a missing FFXIV process and does not execute stored `.bat` paths. Launch-profile/account metadata remains compatibility scaffolding visible only with `/dad debug`; it is optional, is never required to finish Build the Crew, and is preserved when hidden. Everyday **Wake/relog** waits for the same-account client and can coordinate takeover or relog after that client exists.

## Planner-Visible, Not Live Yet

The planner also shows future or research-backed lanes so their configuration shape can be reviewed. Their live executors remain intentionally blocked:

- Blunderville;
- Astrope;
- Squadron command missions;
- Variant / VVD.

The proposed in-plugin DAD Hub/community integration is also future design work. The current plugin only provides an external Dumpster Fire Discord support link in About & Support.

## Safety And Recovery

- Exact Slot1 account, character, Content ID, worker, requested-job, and returned PartyList evidence protect live mutations.
- Readiness and queue blockers are shown before the start action instead of being hidden in logs.
- Required-plugin truth is fail-closed across the frozen crew: missing, disabled, outdated, malformed, legacy, null, or stale truth waits safely.
- Dependency loss opens the local dependency window but never cancels, rewrites, or revalidates work that already crossed its start gate.
- Client Dad reconnect uses capped backoff while DAD remains enabled in Client, non-local mode; it stops when the route returns or that operating mode changes.
- Per-run, per-job, and per-schedule cancellation targets only the selected work.
- **Stop All** uses a confirmation step and drains DAD-owned work across the currently routable clients without broadly stopping unrelated AutoRetainer or VERMAXION work.
- A compact status window shows authority, active run, schedule, queue, worker heartbeat, failures, and Stop All acknowledgements.
- Issue reports best-effort anonymize known local identifiers such as selected character/account keys, configured hosts, and client/worker IDs.
- Non-loopback transport uses a shared secret, signed envelopes, timestamps, and replay protection.

## Quick Start

1. Install the same DAD version and every required plugin listed above on each participating game client.
2. Open DAD with `/dad`, turn on **DAD enabled**, and enable **Allow DAD to automate this character**. Resolve the persistent dependency window on each client.
3. For multibox use, choose one Coordinator Dad and configure the remaining instances as Client Dads. Apply the same LAN shared secret when using a non-loopback endpoint.
4. Start every game client that the Plan needs; DAD waits rather than launching a missing process.
5. Refresh the roster and confirm the intended accounts, characters, jobs, and freshness are visible.
6. Create or select a preset, choose a supported run type, assign the crew, and set requested jobs, substitutes, loot modes, or Level-seek targets as needed.
7. Choose **Recheck readiness (does not run)** and resolve the first visible crew, content, or plugin blocker.
8. Start the preset and watch the main status surface or `/dad mini`.
9. When the preset is proven, add it to a manual or daily schedule if desired.

## Useful Commands

| Command | Action |
| --- | --- |
| `/dad` | Toggle the main DAD window. |
| `/dad config` | Open settings. |
| `/dad on` / `/dad off` | Enable or disable DAD. |
| `/dad status` | Print the current run summary to chat. |
| `/dad mini` | Toggle compact status, scoped cancellation, and Stop All controls. |
| `/dad cancel` | Cancel the active orchestration run. |
| `/dad report` | Write an anonymized diagnostic report for support. |
| `/dad ws` | Reset DAD windows to position 1,1. |
| `/dad j` | Move DAD windows to a random visible position. |
| `/dad debug` | Reveal or hide verbose diagnostics and optional launch-profile scaffolding without changing stored profile data. |

Most normal runs should be created, validated, and started from the DAD interface so the full crew and blocker summary is visible before execution.
