# Windows model scores and recommendations

The dashboard's bottom section shows three task recommendations. Click it to open the
resizable full score window. The matrix follows the public feed's model and effort order;
missing configurations are shown as untested. Select a cell to inspect the four task scores.
The mouse wheel scrolls the full page even over score cells; the matrix's horizontal scrollbar
remains available when the window is narrow.

## Data and schedule

Only `GET https://codexradar.com/data/codex-four-task-results.json` is requested. There is no
HTML scraping, website API key, account, browser runtime or usage upload. The feed is a
versioned public JSON resource used as the score API. Costs are API-equivalent USD estimates
for the benchmark round, and durations are the sum of its four task durations.

The four component scores are weighted with `task_weights` from the feed; candy correctness
is 100 or 0. The published maximum is currently 150. Costs and durations sum across the four
tasks; a missing measurement remains unknown. Unsupported protocols, partial rounds,
duplicate configurations and invalid measurements are rejected.

After startup, check immediately if due; then check the persisted three-hour interval every
minute and after resume. Failures retry after three hours and preserve the last valid data.
The manual Check now button checks immediately. The network master switch cancels in-flight
requests and disables both automatic and manual requests. Re-enabling checks if due.

Normalized data and scheduling metadata are atomically saved to
`%LOCALAPPDATA%\AgentSpeed\radar\radar.json`. Equal data retains the same snapshot and data
change timestamp; only the last check metadata changes. A different upstream verification
timestamp alone is not a new score. No schema fallback reads the website.

## Local recommendation rules (version 1)

Every available model/effort participates. Let `best` be this round's maximum total score.

- **Hard problems:** highest score, then lowest known cost, then shortest known duration.
- **Daily development:** score at least `0.80 * best`, with known cost and duration. Maximize
  `(score / best)^0.50 * ((minCost + 0.01) / (cost + 0.01))^0.30 *
  ((minTime + 1) / (time + 1))^0.20`. Minima are among eligible daily configurations; cost is
  USD, time is seconds. Small offsets handle actual zero measurements. Ties prefer higher
  score, lower cost, shorter time.
- **Simple tasks:** score at least `0.65 * best`, with known cost. Choose lowest cost, then
  highest score, then shortest known duration.

Remaining ties use stable model/effort IDs. Unknown measurements are never zero. No eligible
candidate means no recommendation for that tier. The same model can win multiple tiers.
These are recommendations for this benchmark round, not universal model capabilities.
The full window explains the rules, thresholds and weights. Recommendations are recalculated
from each accepted snapshot and do not copy the website's stationmaster picks.

Development capture: `op radar` opens the window, `radar <name>` renders its content through
the existing `--capture-dir` facility. `AGENT_SPEED_RADAR_DIR` can isolate test cache files.
