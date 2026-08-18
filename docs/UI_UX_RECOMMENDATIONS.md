# dad UI/UX Recommendations

**Review date:** 2026-08-18  
**Scope:** UI code review only; no runtime behaviour or implementation changes are included in this document.

## Product goal

Plan and run multibox duties across accounts and clients while keeping ownership, readiness, scheduling, recovery, and global cancellation understandable.

## Reviewed surfaces

- `Windows/MainWindow.cs`
- `Windows/DadGuideFlow.cs`
- `Windows/DadMiniStatusWindow.cs`
- `Windows/DadDependenciesWindow.cs`

## What is already working

- The Home page already offers guided tasks and expert shortcuts.
- Planning, schedules, crew, clients, status, dependencies, and a compact status window cover the full operator lifecycle.
- Readiness, wake/relog, reconnect, reservations, recent results, and Stop All are represented rather than hidden.

## Prioritized recommendations

| Priority | Recommendation | Rationale and completion signal |
| --- | --- | --- |
| P0 | Define a stable novice versus expert experience. | Default Home to guided tasks for new/incomplete setups and remember an expert preference. Both routes should use the same names and land on the same underlying objects. |
| P0 | Keep operational scope visible everywhere. | Show the active DAD, account, character/profile, preset, schedule, and coordinator role as a compact breadcrumb above editors and actions. |
| P0 | Turn readiness failures into a fix queue. | Order blockers by dependency, client, character, party, job, and duty; each blocker should name the affected slot and link to the exact corrective surface. |
| P0 | Make Stop All unmistakable and verifiable. | Keep it persistently reachable during active work, confirm its scope once, then show per-client acknowledgements and unresolved workers without burying them in diagnostics. |
| P1 | Clarify Plan versus Schedules. | Use `Build a run` for one-off preset planning and `Schedules` for saved/chained execution; add short tab subtitles or empty-state examples. |
| P1 | Reduce roster and schedule table burden. | Use saved filters, sticky selected account, bulk assignment review, and a side inspector rather than exposing every editable property across wide tables. |
| P1 | Create a shared vocabulary layer. | Provide concise inline definitions for DAD, client, crew, launch profile, preset, schedule, run, worker, and reservation, then use those terms consistently. |
| P2 | Make the mini window exception-first. | Show active run, next action, failed/offline slots, and Stop All first; collapse healthy worker and reservation detail. |

## Suggested information hierarchy

1. Scope breadcrumb and active run
2. Next action/blockers
3. Guided task or expert workspace
4. Results and recovery
5. Advanced diagnostics

## Validation checklist

- A new user can identify the primary action and current blocker within five seconds.
- Every disabled control has a nearby plain-language reason and, when possible, a direct corrective action.
- Healthy, warning, error, running, and disabled states remain distinguishable without colour.
- The UI remains usable at narrow window widths and common Dalamud UI scales without clipped labels or unreachable controls.
- Destructive, global, or high-impact actions identify their scope and require confirmation or provide a safe undo.
- Empty, loading, stale-data, success, partial-success, and failure states each provide an appropriate next action.
- Settings clearly identify whether they apply globally, per account, per character, per preset, or only for the current session.
- Advanced diagnostics are still reachable but do not compete with the everyday workflow.

## Recommended implementation order

1. Implement P0 items and validate the primary workflow plus blocker recovery.
2. Implement P1 information-architecture and configuration improvements.
3. Apply P2 polish, then test at multiple UI scales with both fresh and mature configurations.
