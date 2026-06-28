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

## Deployment notes

- **Hub transport protocol is version 2** (raised from 1 on 2026-06-27). Every paired dad — the Coordinator Dad
  and all Client Dads — must run the **same build**; mismatched versions are rejected with `protocol-mismatch`.
- Non-loopback (LAN) connections require a matching `TransportSharedSecret` on every peer (HMAC-SHA256). Envelopes
  are now replay-resistant (signed nonce + timestamp); peers should keep clocks within ~30s of each other.
- Roster sync is passive: the Coordinator pushes a compact roster catalog (account / character / job→level) to all
  clients on connect, on change (incl. level-ups), and on a periodic reconcile — no manual "Populate roster" click
  is required for normal operation (that button remains as a debug fallback).

See `changelog.txt` for the full history. Durable design/review notes live in the XIV KB under
`Dhog/Dad/` (e.g. `DAD_FIX_IMPLEMENTATION_GUIDE_2026-06-26.md`).
