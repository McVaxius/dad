# TODO

- Persist takeover restoration state and reconcile it at startup after a game, plugin, or machine crash.
- Future paid-party/rental design only (I84; no runtime, payment, configuration, or protocol implementation yet): define
  a trustworthy gil-receipt proof; bind exact payer, payee, renter, and rental identity; specify until-disband, elapsed
  hours, and completed-instance semantics; decide persistence and restart recovery; define expiry; handle refunds; and
  establish abuse, dispute, replay, and nonpayment handling before implementation.
