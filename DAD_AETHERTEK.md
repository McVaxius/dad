# DAD

> Plan and coordinate duty runs across your FFXIV characters.

DAD is the control room for repeatable FFXIV duty runs. It works with one character, but it is especially useful when several game clients need to become the right party without manually checking every window.

Create a reusable preset that says who should run, which job each character should use, what content to queue, and when the run should stop. DAD checks the live roster, readies the selected characters, assembles and verifies the party, starts a supported queue, and keeps progress and blockers visible from start to finish.

## What DAD Solves

Multibox duty setup normally means repeating the same chores across several clients: finding the right characters, changing jobs, checking other automation, forming the party, opening Duty Finder, and recovering when one client is stale or disconnected.

DAD turns that setup into a validated run:

- save a crew once and reuse it;
- choose exact characters, jobs, roles, and substitutes;
- wake or relog clients when the preset allows it;
- wait for unsafe or busy client state instead of fighting it;
- build and verify the party before queueing;
- run presets immediately or place them in an ordered schedule;
- see why a run is waiting, blocked, skipped, cancelled, or complete.

## Build Reusable Crews

A DAD preset combines the activity with the characters who should perform it. Each crew row can carry:

- an account and exact character;
- a requested combat job or role requirement;
- explicit substitute rows for unavailable characters;
- a launch profile and wake policy;
- an ADS loot preference: no change, Need, Greed, or Pass;
- an optional Level seek target.

Level seek is useful for leveling schedules. DAD evaluates targeted primary rows and skips a preset only when every targeted, bound primary row has a known job level at or above its target. A bound target with an unknown or below-target level keeps the preset runnable; untargeted and empty placeholder rows are ignored.

Templates can keep the party roles without binding permanent characters. Instantiate a template when needed and DAD fills matching roster characters by role, leaving unresolved slots visible for review.

## Supported Today

These lanes have guarded live execution in the current plugin. "Guarded" means DAD validates the selected content, roster, client state, and queue ownership before it changes game state.

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

## Solo And Multibox Use

Local-only runs stay on one DAD instance. Multibox runs use one **Coordinator Dad** and one or more **Client Dads**:

- the Coordinator owns the run, selected roster, party plan, and queue decision;
- each Client Dad proves its current account, character, job, and readiness;
- non-leader participants become ready before the queue leader is released;
- the frozen run roster prevents a late or mismatched client from silently replacing the selected character.

Clients on the same machine can use loopback transport. LAN setups require the same DAD build on every client and a matching shared secret.

## How A Run Works

1. You select a saved preset and ask DAD to validate it.
2. DAD freezes the intended crew so the run cannot drift underneath you.
3. Offline or wrong-character clients follow their configured launch or relog policy.
4. Requested jobs are prepared through valid saved gearsets and rechecked against live game state.
5. DAD waits for AutoRetainer, VERMAXION, world loading, queue state, and other safety gates to become clear.
6. Every selected worker proves its identity and required readiness.
7. DAD assembles the party and starts the supported queue through the correct leader.
8. During and after the duty, DAD reports progress, completion, blockers, and recovery state.
9. At the final party boundary, DAD verifies leadership and membership before disbanding.

## Integrations

DAD coordinates a plugin stack; it does not try to replace every part of it.

- **XA Database** can provide stored roster and job-level truth. Local runtime and connected DAD snapshots also contribute current roster state.
- **AutoRetainer** is observed so DAD does not relog or take over while retainer work is still active.
- **VERMAXION** provides reservation and handoff safety around post-processing, and can launch a selected DAD preset or schedule through DAD's public IPC.
- **Lifestream** supports configured character relog handling.
- **FrenRider** is the default in-duty owner for movement, combat coordination, and exit behavior when that mode is selected.
- **ADS** supports the force-command Duty Support flow and applies the selected loot configuration to exact workers before multiplayer queue mutation.
- **MOGTOME** owns its supported farming run after DAD performs the helper handoff.
- **Questionable** compatibility is available through DAD's guarded bridge when enabled.

Requirements vary by lane and combat mode. DAD shows a blocker when a required integration is missing, unavailable, stale, or rejects the handoff.

## What DAD Does Not Do

DAD is not a standalone combat bot. It owns planning, readiness, party formation, queueing, schedules, status, cancellation, and recovery. Once inside a duty, combat, movement, and leave behavior belong to the player or the configured FrenRider/ADS workflow.

DAD also does not treat missing proof as success. Depending on context, an unknown job level stays visible as a blocker or keeps a Level-seek entry runnable; stale characters, ambiguous Duty Finder rows, mismatched workers, unavailable helpers, and unsafe world state remain blocked until trustworthy state is available.

## Planner-Visible, Not Live Yet

The planner also shows future or research-backed lanes so their configuration shape can be reviewed. Their live executors remain intentionally blocked:

- Blunderville;
- Astrope;
- Squadron command missions;
- Variant / VVD.

The proposed in-plugin DAD Hub/community integration is also future design work. The current plugin only provides an external Dumpster Fire Discord support link in About & Support.

## Safety And Recovery

- Exact account, character, Content ID, worker, requested-job, and party evidence protect live mutations.
- Readiness and queue blockers are shown before the start action instead of being hidden in logs.
- Client Dad reconnect uses capped backoff while DAD remains enabled in Client, non-local mode; it stops when the route returns or that operating mode changes.
- Per-run, per-job, and per-schedule cancellation targets only the selected work.
- **Stop All** uses a confirmation step and drains DAD-owned work across the currently routable clients without broadly stopping unrelated AutoRetainer or VERMAXION work.
- A compact status window shows authority, active run, schedule, queue, worker heartbeat, failures, and Stop All acknowledgements.
- Issue reports best-effort anonymize known local identifiers such as selected character/account keys, configured hosts, and client/worker IDs.
- Non-loopback transport uses a shared secret, signed envelopes, timestamps, and replay protection.

## Quick Start

1. Install the same DAD version on every participating game client.
2. Open DAD with `/dad`, turn on **DAD enabled**, and enable **Allow DAD to automate this character**.
3. For multibox use, choose one Coordinator Dad and configure the remaining instances as Client Dads. Apply the same LAN shared secret when using a non-loopback endpoint.
4. Refresh the roster and confirm the intended accounts, characters, jobs, and freshness are visible.
5. Import launch profiles if DAD should start offline clients.
6. Create or select a preset, choose a supported run type, assign the crew, and set requested jobs, substitutes, loot modes, or Level-seek targets as needed.
7. Validate the preset and resolve the first visible blocker.
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

Most normal runs should be created, validated, and started from the DAD interface so the full crew and blocker summary is visible before execution.
