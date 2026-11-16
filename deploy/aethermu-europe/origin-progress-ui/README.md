# Origin progress UI candidate — paired client build required

This is a draft protocol/UI change, not a completed Windows gameplay acceptance.
The installed Main.exe (SHA256
`94191e71652c2e8809b8c042eed9a79ae52a1e5258b30ff553db53687188aec3`)
has no verified source/build mapping here. In the available MuMain source,
the F3/32 handler does not assign Resets; only world entry does. A matching
handler must be verified before extra bytes can be treated as a UI fix.

The server appends a little-endian UInt16 Resets field to the custom F3/32
BaseStatsExtended packet, keeping the original five UInt32 fields at their
existing offsets. The original Season 6 packets are unchanged.
A MuMain built from the unpatched source ignores this suffix and continues
to show stale resets until re-entry. Installed-binary behavior is not verified.

`mumain-reset-refresh.patch` adds a length-checked optional suffix reader to
MuMain's existing base-stat handler. The original 24-byte packet remains valid.
It updates the existing character attribute without loading the world, clearing
equipment or selecting a character. The patch applies to the locally available
MuMain source commit `3388868f31ca55c3887045a695bf747ec46ee97e`. This
source has not been established as the source of the installed executable.
The patch has not been compiled or tested in a running game.

After a successful GR, the server publishes the command-window Prestige
snapshot again, bypassing only the client-request throttle. It reads the
committed NUMERIC GR/APP counters. Publication does not call the exchange,
change inventory, or grant points. A failed send stays outside the transaction
rollback path. Existing reset eligibility and the player persistence lock reject
a repeated GR with Reset=0.

The Prestige catalogue is currently enabled only by AETHER_LOCAL_STAGING=true.
This change does not extend that deployment boundary or activate other realms.

## Acceptance still required

Build an authenticated, compatible MuMain source with the patch and verify its
existing 24-byte base-stat struct size against the F3/32 wire layout. Keep the
original client and candidate database backup. Use an isolated QA character
and server; never copy over the owner's original client.

In the same entered-world session, perform Origin's 25 Reset -> 1 GR. Check the
character panel immediately shows Reset=0 and level=1, and the command-window
Prestige panel shows GR incremented once and APP increased by 100. Preserve
both equipped and carried items. Repeat read-only refresh requests and rejected
GR requests, then compare the PostgreSQL counters, ledger count and complete
inventory fingerprint. Repeat with a dropped UI notification, reconnect and
restart checks. Do not call the exchange from a refresh handler.

Until that paired-client test passes, keep PR #6 Draft and do not deploy this
candidate to production or report the Windows UI defect as fixed. Marketplace
and main/master are outside this change.
