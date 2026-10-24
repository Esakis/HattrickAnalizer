# Weekly management workflow

The application now connects weekly training, opponent analysis, match preparation and post-match review. The home page (`/`) summarizes the next match, the saved plan, missing training minutes and recent squad changes.

## Prepare the week

1. Synchronize your team from the application header. Successful roster loading also starts history synchronization. The history endpoint fetches the authenticated team's players itself; client-supplied skills and team IDs are not accepted as observations.
2. In **Training** (`/training`), select trainees and assign positions, start minutes and durations for the league and friendly slots. The planner combines recorded and planned minutes, caps coverage at the best 90 minutes, and distinguishes full, half and unknown effects.
3. Check conflicts, unavailable players, missing dates and incomplete observations. Suggested assignments preserve existing assignments. Unknown historical minutes do not become zero minutes.
4. Send the selected match's trainees and locked starting positions to the optimizer. A real fixture is linked only when its identity or type/date can be matched to an upcoming match. Otherwise the result is a draft.

The current planner has league and friendly planning slots; it is not a complete cup or multiweek calendar. Projected minutes assume the planned appearances happen. Injuries, red cards and conditional substitutions can change the actual training received. The planner does not claim exact skill-up dates or transfer values.

The current optimizer excludes suspended players in both match types. To keep the handoff consistent, the planner also excludes them from suggestions and flags any existing manual assignments, including friendly assignments. This is a conservative application limitation, not a claim that every suspension applies to friendlies in Hattrick.

## Prepare and save a match

Use **Scouting** (`/scout`) to review the sample of recent matches, formations, tactics and sector strengths, then open the opponent in **Optimizer** (`/optimizer`). Select the correct upcoming fixture, required players, rested players and locked positions. The fixture determines the match ID and home/away context.

After optimization you can select an alternative, edit its players and individual orders, and recalculate the exact XI. The comparison shows sector changes and the estimated outcome against the reference recommendation. Sensitivity scenarios change the opponent's modeled strength; they are assumptions, not additional observations about the opponent.

Add the captain, set-piece taker, bench, penalty order and planned substitutions. Saving a match-linked plan validates its order structure first. The local statuses mean:

- `draft`: a working lineup, including scenarios without a linked match;
- `ready`: a locally prepared plan; it has not been sent to Hattrick;
- `sent`: Hattrick confirmed submission of the orders.

Sending is a separate action after reviewing the preview. The preview validates the request structure. Current roster, player availability and ownership of the upcoming match are checked again during submission. Live submission requires the appropriate CHPP authorization. Demo mode never sends match orders.

Changes to relevant team or match context can invalidate an earlier recommendation. Recalculate stale plans before using them. A saved local plan and a saved calibration forecast are distinct records: an incomplete forecast capture does not silently fill missing context or claim a successful capture.

## Review changes and forecasts

**Players** (`/players`) exposes history in production. Open a player to inspect skills, form and attributes across timestamped observations. Higher skill levels appear higher on the chart; TSI uses a separate axis. Identical repeated observations are not appended. The first observation establishes a baseline, and does not describe a change before tracking began.

Saving a complete live match plan also attempts to capture its pre-match forecast. Capture requires all eleven own players with private skills and complete context, including formation experience and weather. The server recalculates the ratings and verifies any supplied displayed ratings against the current model within `1e-8`. A mismatch or missing context returns `saved: false` with an explanation.

After the match, **Review** (`/review`) compares saved and actual sectors for up to ten recent matches. It displays sample counts, mean absolute error, chronological holdout results and exclusions. The latest 20% of evaluable matches are held out, with at least one held-out match when more than one exists. A lineup or context mismatch excludes a match.

New captures preserve the ratings and model version computed before kickoff. Older snapshots without stored ratings are replayed using the current model and labeled accordingly. Merely opening the report does not save a roster snapshot. Forecasts remain `unvalidated-low`; consistency tests and a small sample do not establish predictive accuracy. League simulations also expose their model provenance and sample limitations.

## Where data is stored

| Data | Location | Scope and persistence |
| --- | --- | --- |
| Match plans | Browser `localStorage`, keys starting `hattrick.matchPlans.v1.` | Per team, match and opponent; up to 16 plans per team. Bound to this browser profile and site origin. |
| Weekly training plans | Browser `localStorage`, keys starting `hattrick.training-plan.v1.` | Per team and training-week start. Bound to this browser profile and site origin. |
| Player history | Server `App_Data/player-history/team-<id>.json` | Persistent observations and recent changes, isolated by own team. |
| Calibration captures | Server `App_Data/calibration/team-<id>.json` | Persistent pre-match snapshots and forecasts, isolated by own team. |

Browser plans are not synchronized between devices. Clearing site data removes them. A browser-storage failure is reported; match-plan memory fallback lasts only for the current application session.

The server paths above are relative to the ASP.NET application content root (normally `Backend/`). Configure alternatives using `PlayerHistory:StoragePath` and `Calibration:StoragePath`, or environment variables `PlayerHistory__StoragePath` and `Calibration__StoragePath`. Relative configured paths are resolved against the same content root. Use persistent writable storage when deploying and include these directories in backups. JSON writes are atomic and serialized within one server process; use one writer per storage directory.

SQL is not required for player history. If `ConnectionStrings:HattrickDb` is configured and available, existing rows from `PlayerSkillHistory` are imported into the per-team local store. New history is stored in JSON; SQL is not a second synchronized copy. SQL unavailability does not disable local history.

With `UseMockData=true`, both server stores use a separate `demo/` subdirectory, and legacy SQL history is not imported. The interface labels demo data, and live CHPP requests and match-order writes remain disabled.
