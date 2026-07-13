# dad

Dalamud plugin for Dad-owned FFXIV run planning, authority, party assembly, queue routing, status, cancellation, and recovery.

## Build

```powershell
dotnet build .\dad.csproj -c Debug -p:Platform=x64
```

## Test

```powershell
dotnet test .\Tests\dad.Tests.csproj
```

## Operator status and cancellation

- `/dad status` keeps its existing behavior and prints the live shell report to chat.
- `/dad mini` toggles a compact, manually opened status window. It renders cached authority, run, scheduler,
  slot, queue, worker-heartbeat, failure, and Stop-all acknowledgement state without polling peers from Draw.
- When a Client Dad loses its Coordinator route, a separate `DAD Client` window opens automatically with the
  target, current attempt, next retry, and last disconnect. Reconnect uses capped backoff and never gives up;
  the only operator action that stops retries is the guarded `Disable DAD` confirmation.
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
- The v2 wire format uses string reservation states (`Pending`, `Granting`, `Granted`, `Released`, `Rejected`). DAD
  also accepts the legacy VERMAXION numeric values `0` through `4` in that order. Unknown/malformed states fail
  closed; verified-idle compatibility is reserved for genuine v2 IPC unavailability, not invalid v2 responses.

## Deployment notes

- **Hub transport protocol is version 2** (raised from 1 on 2026-06-27). Every paired dad — the Coordinator Dad
  and all Client Dads — must run the **same build**; mismatched versions are rejected with `protocol-mismatch`.
- Non-loopback (LAN) connections require a matching `TransportSharedSecret` on every peer (HMAC-SHA256). Envelopes
  are now replay-resistant (signed nonce + timestamp); peers should keep clocks within ~30s of each other.
- A configured Coordinator endpoint is not presented as a live authority route until the authenticated handshake
  succeeds. Client liveness is checked from inbound frames, and a stale route is closed and reconnected.
- Roster sync is passive: the Coordinator pushes a compact roster catalog (account / character / job→level) to all
  clients on connect, on change (incl. level-ups), and on a periodic reconcile — no manual "Populate roster" click
  is required for normal operation (that button remains as a debug fallback).

See `changelog.txt` for the full history. Durable design/review notes live in the XIV KB under
`Dhog/Dad/` (e.g. `DAD_FIX_IMPLEMENTATION_GUIDE_2026-06-26.md`).
