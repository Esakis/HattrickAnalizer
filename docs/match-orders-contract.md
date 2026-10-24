# Match orders and manual lineup evaluation

`POST /api/optimizer/evaluate` accepts `{ context: OptimizerRequest, positions: { GK: { playerId, behaviour }, ... }, tactic }`. It resolves players from the current team roster and evaluates the exact eleven without search or changes. Unknown, unavailable, duplicated, excluded or incorrectly positioned players are rejected.

Optimization accepts `requiredPlayerIds`, `excludedPlayerIds` and `lockedPositions` (slot name to player ID). Locks imply inclusion. Every returned alternative respects these constraints; impossible combinations return a validation error.

Manual evaluation also accepts `context.opponentRatingScale` (default 1, range 0.5–1.5). This scales resolved opponent sectors and indirect set-piece ratings together, preserving the original tactic and observed/home context. Scale 1 reproduces the unscaled evaluation. The optimize endpoint rejects non-unit scales; this control is for sensitivity analysis, not a new estimate of opponent skills.

`GET /api/team/upcoming-matches` returns the authenticated team's upcoming `NextOpponentInfo[]` in date order. A training plan must select its actual league/friendly match ID before it can be sent; the next league match is not an interchangeable substitute for a planned friendly.

`POST /api/matchorders/preview` validates a request and returns `{ payload, liveWriteAllowed, message }`. Preview does not contact CHPP. Sending to `POST /api/matchorders` checks the current match and roster again without cache. Demo mode blocks sending before any network request.

The extended request retains the existing eleven, tactic, attitude, captain and set-piece taker and adds:

- `bench: [{ playerId, positionCode }]`: indices 0–6 are primary GK, central defender, wing back, inner midfielder, forward, winger, extra. Indices 7–13 are backup roles in the same order and must reference a primary substitute.
- `kickers: [playerId, ...]`: up to eleven distinct penalty takers, in order, from the selected squad.
- `substitutions: [{ playerOutId, playerInId, minute, position, behaviour, standing }]`: at most five orders. Equal player IDs mean a behaviour/position change. Otherwise the incoming player must be on the bench.
- `minute`: -1 (any time), 46 (half time), 91 (before extra time), or five-minute marks 5–115.
- `position`: -1 to inherit, or 0–13 in pitch order GK, RWB, RCD, CD, LCD, LWB, RW, RIM, IM, LIM, LW, RFW, FW, LFW.
- `behaviour`: -1 to inherit, 0 normal, 1 offensive, 2 defensive, 3 toward middle, 4 toward wing. Position legality is validated where the target position is explicit.
- `standing`: -1 any, 0 tied, 1 ahead, 2 behind, 3 ahead by more than one, 4 behind by more than one, 5 not behind, 6 not ahead, 7 ahead by more than two, 8 behind by more than two, 9 not tied.

The wire serializer emits positional arrays of 14 field slots, 14 bench slots and 11 kicker slots. Empty slots are zero IDs. Substitution wire keys are `playerin`, `playerout`, `orderType`, `min`, `pos`, `beh`, `card`, `standing`.

## Verification limits

The [official CHPP matchorders documentation](https://www.hattrick.org/en/Community/CHPP/NewDocs/File.aspx?name=matchorders#ref_Lineup_30) returned HTTP 403 during implementation on 2026-10-23. The wire shape was checked against the author's [CHPP library serializer](https://github.com/lucianoq/hattrick/blob/master/chpp/type_lineup.go), [substitution serializer](https://github.com/lucianoq/hattrick/blob/master/chpp/type_lineup_substitution_orders.go) and its typed enums. This is a source-code compatibility check, not a successful live CHPP submission or independent confirmation of the inaccessible official document. Local tests use HTTP fixtures; they do not submit real match orders.

The editor currently represents substitutions and behaviour changes. Red-card conditions are sent as ignore (`-1`), and position-swap orders, coach style and man marking are not exposed.
