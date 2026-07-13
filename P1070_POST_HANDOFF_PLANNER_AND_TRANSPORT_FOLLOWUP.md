# DAD post-handoff planner and transport shutdown follow-up

Date: 2026-07-12

This note is intentionally separate from the existing DAD ↔ VERMAXION handoff/liveness and AutoRetainer selection-guard documentation. Those contracts, committed operations, five-second boundaries, and slow or unbounded `LaunchIfOffline` waits are unchanged.

## Post-handoff planner failure

The W/X run reached the intended handoff result: both configured characters were exact-character, post-AR `Ready`. The subsequent strict planner preview then rebuilt from stale roster projection state. Peer `StatusText` (including healthy text such as `Idle on …`) and historical response warnings (including an earlier wrong-character warning) had also been promoted to character blockers. During catalog merge, cached XADB/offline blockers survived even when a current live row superseded the same character. The strict planner therefore downgraded X to offline/XADB-only truth and stopped before party invitation.

The follow-up behavior is:

- scheduler planning rebuilds its in-memory runtime overlay from current structured heartbeats plus the already-held XADB/catalog snapshot;
- it performs no extra peer/XADB pull and does not move the normal five-second refresh boundary;
- `StatusText` and historical warnings remain diagnostic only;
- structured stale, unavailable, ineligible, local-only, or genuinely blocked state still fails closed;
- a current runtime row replaces source-dependent cached blockers, after which operator visibility and roster-refresh policy blockers are reapplied;
- a failure unique to strict live-readiness validation remains schedulable and retries on the existing two-second cadence;
- static configuration/module rejection remains terminal;
- the 300-second strict-revalidation budget starts at the latest required slot `ReadyUtc`, so AR drain and relog time do not consume it;
- a per-run one-shot gate prevents more than one planner start after refreshed truth becomes ready.

## Transport unload race

Disabling or reloading DAD could dispose the raw connection/outbound `SemaphoreSlim` instances while an accept waiter, accepted session, or queued send still owned the corresponding release path. A late `Release()` could then raise `ObjectDisposedException` on a transport task during AppDomain/plugin teardown.

Connection and outbound limits now use deferred-disposal semaphore leases. A lifetime lease is registered before waiting, an accepted connection carries its lease for the full session, and send work releases only through its lease. Shutdown rejects new acquisitions immediately but physically disposes each semaphore only after all pre-existing waiters, sessions, and sends drain. Plugin disposal initiates transport cancellation and socket closure first without waiting on the framework thread. Late task completions remain observed, with Dalamud logging suppressed after transport teardown begins.

## Runtime acceptance still required

The source-level and unit checks do not replace the live W/X acceptance pass:

1. Allow the complete VERMAXION/AutoRetainer drain and relog flow to finish without a short wall-clock assumption.
2. Confirm both exact characters become post-AR `Ready`.
3. Confirm strict revalidation advances, the invite occurs, and Sastasha starts.
4. With X connected and transport work active, disable/reload DAD on W and confirm the client remains running without a `SemaphoreSlim` or AppDomain-unhandled exception.

W:, X:, `Z:\logs\dad2\`, AutoRetainer, installed plugin files, and client files remain read-only during acceptance.
