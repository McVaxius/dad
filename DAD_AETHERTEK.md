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

Level seek is useful for leveling schedules. With **STOP: Target level**, the bottom target applies only to the first selected primary character when that row is blank. A value on that row overrides the bottom target, while every other nonblank row value adds another required target. DAD skips or stops only when all resolved targets are proven. **Any** uses the loaded character's live current job and level; a selected job uses that job's ledger. Pre-wake checks use the planner's merged live and stored roster, including XADB job levels, so a proven satisfied selected-job target can skip even when another character is logged in. Missing evidence and levels 0 or 1 are unknown, so the preset continues under its existing safety cap. DAD freezes the resolved characters, jobs, and targets before wake or relog, checks them again after exact readiness and requested-job preparation, and refreshes the evidence after each completed run.

With other STOP modes, the existing row Level-seek rule is unchanged: DAD skips only when every targeted, bound primary row has a known job level at or above its target. Unknown or below-target rows keep the preset runnable; untargeted and empty placeholder rows are ignored.

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
- **Disband** asks exact Slot1 to tear down the held regular crew first. After Slot1 returns a terminal result, every
  exact follower independently leaves or proves that authoritative party state is already solo. DAD waits for all
  workers and reports one partial failure naming any affected slots. With no
  active Crew Formation, it can also disband the current
  party only when DAD proves a stable out-of-duty/out-of-queue state, at least two exact members, and local leadership.
  Membership drift, identity drift, stale confirmations, and unrelated prompts remain fail-closed. Seven guarded
  attempts, the eight-second throttle, 60-second timeout, and sustained solo proof remain unchanged.

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

DAD validates each entry before it starts. A satisfied Level-seek entry is recorded as skipped and the schedule moves on. Successful party entries tear down every exact worker at the appropriate boundary so the next preset can form a fresh verified party. Dry-run validation is available before relying on a live schedule.

If an ordinary entry failure or a coordinator/plugin reload blocks a schedule, the Coordinator operator can use **Resume from failed entry**. DAD starts a new run at the exact persisted entry and repeat cursor, keeps the earlier run in history, and requires every client plus DAD and scheduler work to be idle. Reload abandonment never resumes automatically. A cancelled schedule is terminal and cannot be resumed.

One narrow automatic recovery is available for scheduled premade duties and daily roulettes: if the exact queue leader entered and then exited without a matching `DutyCompleted` signal after the existing ten-second grace period, DAD reserves **one additional attempt per entry/repeat**. This is structured failure evidence, not a timeout diagnosis or a match against log text. The failed attempt stays failed in run history and does not count as a completed schedule entry. The retry budget and frozen original run are persisted before any new job is queued; another failure stops the schedule, and explicit resume does not refill the consumed automatic budget for that entry.

Before retrying, DAD waits up to five minutes for acknowledged coordinator/worker/wake cleanup and fresh, safe, outside-duty readiness from every original account, character, Content ID and worker session. Missing/disconnected clients wait; contradictory identities block. DAD does not force a duty exit, rebind to another client, or automatically log a disconnected client back in. The retry goes through a fresh ordinary scheduler job/request/wake token, so current level targets and enabled daily-reward skip checks run again before queueing, and normal exact party formation runs as needed. A changed duty, roster, requested job or authority blocks automatic recovery. Cancel, unknown leader state, unrelated worker failures, shared-island identities and runs with already completed repetitions are not automatically retried.

The abandoned attempt retains its authenticated run assignments while the existing guarded party teardown settles: Slot1 disbands first, then every exact follower leaves or proves it is already solo. DAD waits for fresh world-ready evidence even if the Coordinator left the duty first. Only successful teardown on every slot makes the failure eligible for retry; missing or failed cleanup blocks recovery, and this teardown wait has its own five-minute deadline. The original attempt remains failed, with no successful-run completion actions. If level targets or daily rewards are already satisfied when normal retry preflight runs, the schedule records a skip and advances with the old party already removed. Otherwise the fresh attempt uses ordinary party assembly. Ordinary skips without a preceding failed duty do not issue teardown commands.

Retry readiness evaluates heartbeat freshness against a clock sampled **after** live snapshot capture. This avoids treating the Coordinator's newly captured local heartbeat as future evidence; genuinely future or stale heartbeats still fail closed. Live retry/next-step execution was confirmed by the operator on 2026-10-19; the subsequent teardown-before-skip addition has automated coverage but still needs four-client live acceptance.

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
10. At the final party boundary, DAD refreshes the exact roster, waits for Slot1's guarded disband result, then waits for every follower to leave or prove solo.
    A worker's `Finalizing` phase is not the coordinator's finalization permission. If Slot1 exits the duty before the followers, the coordinator continues polling the complete worker set; it cannot bypass managed teardown. Successful completion requires a fresh per-run party-boundary acknowledgement (or the existing explicit rosterless/repeat-preservation exception), never proof retained from a previous preset.
    Party teardown and participant leave recovery freeze the prompt baseline before each command attempt. A fresh dialog may need several frames to become ready and populate its text; those intermediate frames do not make it a pre-existing dialog. Exact operation/text checks and one approval per attempt remain mandatory. Participant leave recovery never approves a disband confirmation.

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
- For saved presets/schedules, a temporary `Any`-job live/world-ready gap at final Coordinator binding returns structured pre-admission `RuntimeReadiness` evidence. DAD keeps the same frozen request, ownership and entry, waits on the ordinary two-second scheduler refresh and fully rechecks readiness/contract before trying again; it does not record a failed job or spend a dungeon-exit retry. This only reopens a start claim for an exact rejected result proving no accepted execution. All bindings are checked so hard identity/CID/route contradictions override a wait; missing/noncombat live jobs, generic rejection, uncertain dispatch and accepted runs remain non-replayable. Exact class/job freeze and cancellation safety are unchanged.
- An accepted in-world `/ays relog` command is not proof of arrival. An aborted attempt can retry indefinitely while the same operation remains authorized, but only after five continuously fresh same-location world-stable idle seconds on the exact original source character (also if it returned after an aborted logout). Each retry requires the connected coordinator, matching account/catalog identity, readable active DAD-owned suppression, Multi Mode off and idle available AutoRetainer/Lifestream with no external automation hold. DAD forces fresh safety/identity reads before dispatch, keeps the original target/ownership and does not repeat reset or cancel travel. Busy services, title/logout/login/loading/casting, stale or non-advancing cached location observations, observation gaps and coordinator loss reset the idle window. A different stable character or changed source identity fails closed. The prerequisite frozen home-world return also retries accepted aborts after the same safe idle window, and explicit-false calls every five seconds, with no attempt cap. Uncertain IPC or relog dispatch failures remain terminal; the separate title `ConnectAndLogin` accepted-call policy is unchanged. Arrival or cancellation ends recovery. These safeguards still require live multi-client soak testing before unattended 24/7 operation can be considered verified.
- After a planner run reaches a terminal result, DAD releases each exact wake/relog operation as well as its early requested-job assignments. This includes successful runs and runs that needed no requested-job assignment. Any DAD-owned suppression/reservation cleanup and in-place restoration of the captured AutoRetainer Multi Mode state must acknowledge before the next queued preset starts. Cleanup never relogs back to the previous character. Cancelling a token that was never accepted is an idempotent acknowledgement and cannot release another active token's ownership; conflicting new takeover requests still fail closed.
- **VERMAXION** optionally provides reservation and handoff safety around post-processing, and can launch a selected DAD preset or schedule through DAD's public IPC. When it is confirmed absent, DAD can use readable idle AutoRetainer with its own verified suppression lease; no new character-postprocess callback is required. Busy, unreadable, or conflicting ownership still blocks takeover. The title-login path likewise accepts freshly confirmed VERMAXION absence without bypassing its other safety gates.
- **Lifestream** supports configured character relog handling and guarded travel to the frozen Slot1 data center. A successful `ChangeWorld` return means the travel request was accepted, not that arrival succeeded. If it later aborts, DAD retries after five seconds of continuously fresh evidence that the exact assigned worker/account/character remains world-ready on the same wrong world/DC, AutoRetainer and external automation are safe, and Lifestream is available and idle. Busy/unsafe observations, stale or missing location evidence, identity interruptions, location changes and observation gaps restart that stability window. Explicit-false results likewise retry at five-second intervals after fresh safety checks. There is no travel-specific attempt limit or total timeout while the same active assignment remains authorized: recovery continues until confirmed arrival or operator cancellation. An ongoing Lifestream operation or character logout/login/loading stays wait-only, even if Lifestream's busy flag is momentarily false; DAD never cancels a running journey to make room for another. The attempt count remains diagnostic only. Immutable identity/target contradictions and uncertain IPC results still fail closed, and destination/region/OCE capacity policy is revalidated before any retry. Cancellation removes the active travel assignment; title-screen login remains a separate, unchanged path. No party assembly is authorized on the wrong DC.
- **Fren Rider** is an unconditional DAD dependency and is the default in-duty owner for movement, combat coordination, and exit behavior when that mode is selected.
- **AI Duty Solver (ADS)** is an unconditional DAD dependency and applies the selected loot configuration to exact workers before multiplayer queue mutation.
- **MOGTOME** owns its supported farming run after DAD performs the helper handoff.
- **Questionable** compatibility is available through DAD's guarded bridge when enabled.

Questionable's Duty Support leveling interruption needs no manual preset: DAD selects the highest eligible curated
dungeon for the equipped class/job, using its actual level, equipped item level, duty unlocks and Duty Support data.
Questionable keeps its quest goal and requests one dungeon at a time. When it stops leveling, DAD requests `/ads leave`
once and shows **Leaving duty** until the duty exit and loading finish. ADS handles combat and other leave restrictions.
If the leave command is rejected, the blocker remains visible; leave manually to let the exit guard finish.
For manual runs, **Plans > Built-in leveling preset** creates an ordinary editable one-run Duty Support preset for the
current character and equipped job. Porta Decumana is a manual cutscene-route choice; automatic selection uses the standard route.

Other integrations can still vary by lane. The six dependencies above do not vary by lane or combat mode. New work waits until the Coordinator and every frozen selected client report fresh ready truth; unselected clients do not block a Plan.

## What DAD Does Not Do

DAD is not a standalone combat bot. It owns planning, readiness, party formation, queueing, schedules, status, cancellation, and recovery. Once inside a duty, combat, movement, and leave behavior belong to the player or the configured FrenRider/ADS workflow.

DAD also does not treat missing proof as success. Depending on context, an unknown job level stays visible as a blocker or keeps a Level-seek entry runnable; stale characters, ambiguous Duty Finder rows, mismatched workers, unavailable helpers, and unsafe world state remain blocked until trustworthy state is available.

DAD does not start a missing FFXIV process and does not execute stored `.bat` paths. Launch-profile/account metadata remains compatibility scaffolding visible only with `/dad debug`; it is optional, is never required to finish Build the Crew, and is preserved when hidden. Everyday **Wake/relog** waits for the same-account client and can coordinate takeover or relog after that client exists.

A Client Dad can also run its own saved solo **Duty Support** presets and a Schedule containing only those presets,
including character switches on that same account. No second Coordinator is needed for these independent solo runs.
Every preset must bind one Slot1 character to this Client's exact account; mixed activities, other accounts, unresolved
identities, multiplayer and shopping-associated plans still require a Coordinator. Keep the global **Keep runs on this
client only** switch off for the normal typed Wake/relog path; a solo duty's internal LocalOnly authority is separate.

### Independent Duty Support Setup

1. On each running game client, enable DAD and character automation, resolve required-plugin readiness, and leave
   **This client coordinates the crew** unchecked. A Coordinator connection is not needed for its own solo schedule.
2. Save each preset as **Duty Support**, party size 1, with exactly one primary Slot1 character bound to this
   instance's own account. Account ownership is checked by stable ID, not by the displayed alias. Any substitutes
   must use the same account and Slot1.
3. Add only those presets to the schedule in the desired order. Different characters on this account are allowed;
   other accounts, missing preset references or a single non-Duty-Support entry make it Coordinator-only.
4. Leave **Keep runs on this client only** off for typed Wake/relog. Start the saved preset with **Wake/relog and run**
   or the schedule with **Run now**. Each account executes and switches characters independently; normal job,
   Level-seek, readiness and takeover safety checks still apply.
5. Use **Cancel** to stop the selected schedule. Resolve a failed entry's blocker and wait for idle cleanup before
   **Resume from failed entry**. Cancellation is terminal and never resumes automatically.

Do not change ports merely to enable solo Client scheduling. If readiness fails, inspect the first exact blocker:
account bindings, plugin availability, character state and cancellation cleanup remain required. DAD recognizes its
own local NPC runtime even when the solo request uses internal LocalOnly authority; it does not accept an isolated
peer or stale/unavailable character as ready. `/dad report` shows the loaded diagnostic build and load time for
troubleshooting. Review the report for personal identifiers before sharing it.

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
2. Open DAD with `/dad`, turn on **DAD enabled**, and enable **Allow DAD to automate this character**. Resolve the persistent dependency window on each client. Open `/dad wizard` for guided setup. **Save and Next** saves each step; **Back** keeps completed work, and **Finish** completes the guide.
3. Use **Name this DAD** to label each instance. For multibox use, choose **Set up a Coordinator** on one instance and **Connect a Client** on the others. Match the Coordinator endpoint and apply the same LAN shared secret when using a non-loopback endpoint.
4. Start every game client that the Plan needs; DAD waits rather than launching a missing process. Use **Build the Crew** to refresh local and connected rosters, assign account ownership, and resolve stale or unassigned Active rows.
5. Open **Create a Preset** to choose the activity, name, crew, and stop/finish rules, then validate. To edit an existing preset, use the wizard's **Plan** button to select it before opening its guide, and leave **Create a new preset** unchecked. Check it only when creating a new preset. Set requested jobs, substitutes, loot modes, or Level-seek targets as needed.
6. In **Plan**, choose **Recheck readiness (does not run)** and resolve the first visible crew, content, or plugin blocker. Start the preset when ready and follow progress in the main window or `/dad mini`.
7. To build or edit a schedule, return to `/dad wizard` and open **Build a Schedule**. Leave **Create a new schedule** unchecked and choose an **Existing schedule** to edit, or check it to create one. Arrange saved presets and repeat counts, choose manual or daily cadence, resolve blockers, and use **Run dry-run** to validate before finishing.
8. To attach shopping, first prepare the lists in ADS, then open the saved Plan or Schedule's **Member shopping & final commands** rows. Use **Refresh ADS presets**, choose each member's optional **ADS preset** and independent **Final slash command**, then **Save member assignments**. A command can be used without a list. **Apply this row to all members** explicitly copies choices to the displayed members while retaining each identity and its own credits. Schedule rows use the same exact primary account/character in every referenced Plan. The shopping wizard can also update one member at a time; **Preview only** uses the current client's character and does not purchase items.

Final commands run once after the entire successful round and its shopping. Schedules wait for every entry and repeat. The last applicable preset command wins for a member; a nonblank schedule command overrides it. Blank commands are off. Remote clients need their existing remote-command permission. Acknowledgement means the registered command was submitted; check the external tool for its actual effect. Cancellation, incomplete shopping, changed/unavailable members and uncertain submissions do not grant permission to replay commands.

## Gathering materials with connected clients

Choose **GBR Gather Party** in the preset activity dropdown and select the directly connected DAD clients to use.
Import a GBR export or Teamcraft quantity-first text, or add materials from the searchable picker. Set the desired
combined quantity on each row, then **Refresh carried stock** to see stock, remaining quantity and each client's share.
Only carried inventory counts. **Even split** clears edited shares; **Clear list** removes all targets. Review the
allocations before Start: Start reads fresh stock again, then freezes each client's order. Clients work independently;
there is no party formation, combat preparation, automatic redistribution or replay. Supported timed nodes may wait.

Each client needs compatible GatherBuddyReborn IPC v2, a valid MIN/BTN gathering location and its normal gathering
prerequisites. Stop independently running GBR before Start. DAD restores only the list selections it changed and
requires real carried counts before completion. A disconnected worker's share stays unfinished. If the coordinator
disappears, already dispatched clients can finish only their own shares. Failed owned cleanup stays visible; review
the error and use another explicit Stop before starting new work. Registered-island gathering is unsupported.
The current build has synthetic lifecycle verification; actual GBR, timed-node and game UI acceptance remain pending.

## Useful Commands

| Command | Action |
| --- | --- |
| `/dad` | Toggle the main DAD window. |
| `/dad wizard` | Open guided setup, preset editing, and schedule building, with shopping setup instructions. |
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
