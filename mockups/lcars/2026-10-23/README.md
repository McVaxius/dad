# DAD LCARS concepts

Approved LCARS design concepts. Implementation is selected for the repositories in Z:\myrepos.txt, with the rollout tracked in Z:\!todo\2026-10-22GOATFUCK0002.md. These images are design references, not runtime or test evidence.

- [Main window comparison](01-main-regular-compact.png)
- [Settings and supporting owners](02-settings-mini-and-auxiliary.png)
- [Exact source-checked prompts](PROMPTS.md)

The image links are the planned sheet filenames; root review publishes the final generated artwork. Regular/compact compare spacing at the same readable text size. A separate Mini, wizard, access loader or browser view stays a separate owner. Selected supporting views are shown at useful scale; other source windows and conditional states retain their existing layouts and actions, rather than each receiving an illegible thumbnail. Generated text and sample values are proposals, not test results.

## Dedicated Mini windows

- [DAD Monitor, regular and compact](03-mini-monitor-regular-compact-v1.png).
- [Expanded scheduler, takeover, failures, clients, acknowledgements and guarded actions](04-mini-monitor-expanded-states-v1.png).

These are distinct DAD Monitor windows, separate from ordinary Plan and Trust.
The expanded raster places a disclosure triangle on the ordinary terminal-failure
heading; retain the real heading and existing conditional actions in code.
Titlebar glyphs are conceptual and do not prove native host rendering.
## Source scope

Source commit: `9537d207ec0f8b6fc0d85faad2fbf9bea8ed9e22`. Existing dirty paths were inspected by count (58); none are included or changed by this mockup task.

No policy picker exists in current Trust setup. It is intentionally excluded. Trust catalog data/compatibility is DAD-owned; no XADB claim. NPC and player-job targets remain independent.

## Verified controls and owners

- Windows/MainWindow.cs:298 — outer navigation; Plan numbered sections at 4565; readiness/run actions at 4771.
- Windows/MainWindow.Trust.cs:33 — inner tabs; preset actions at 72; literal NPC/job controls at 256; per-role multiselect at 286.
- Windows/MainWindow.Trust.cs:320 — fixed Trust group and compatible dungeon selector; separate action labels at 117.
- Windows/DadMiniStatusWindow.cs:73 — distinct Mini; DAD Monitor header at 120.

## Existing UI source inventory

- `Models/DadTimeWindow.cs`
- `Windows/ConfigWindow.cs`
- `Windows/DadAutoPartyFleetMatrixWindow.cs`
- `Windows/DadAutoPartyWindow.cs`
- `Windows/DadClientReconnectWindow.cs`
- `Windows/DadDependenciesWindow.cs`
- `Windows/DadMiniStatusWindow.cs`
- `Windows/DadPresetBatchWizardWindow.cs`
- `Windows/DadQuickPanelWindow.cs`
- `Windows/DadShoppingWizardWindow.cs`
- `Windows/MainWindow.Accounts.cs`
- `Windows/MainWindow.GatherParty.cs`
- `Windows/MainWindow.MemberAssignments.cs`
- `Windows/MainWindow.Trust.cs`
- `Windows/MainWindow.cs`
- `Windows/SetupWizardWindow.cs`

## Shared appearance proposal

Auto / Picker / LCARS remain in that order, with LCARS third. Preserve native actions, IDs, saved scope and semantic status colours. Krangle obfuscates identifying text. Proposed shared UI tones default off; titlebar speaker and Settings use the same value, with no volume slider. Browser controls remain in the browser header; existing script-free owners do not gain unsupported timed fade. This appearance contract is approved for the allowlisted rollout; implementation and local verification remain separate from artwork approval.
