# Windows: floating orbs follow Codex focus

Settings → Floating orb → **Show only when Codex is in the foreground** controls all floating
orbs together. When enabled, Codex desktop focus shows configured visible orbs; switching to
another application, minimizing Codex, or leaving Codex tasks running in the background hides
them. Returning to Codex restores the same windows, data feeds and positions.

The orb master switch and individual visibility settings still apply. Temporary focus changes
never write those settings. Name plates, source chips, undo chips and alignment guides hide
with the orbs. The taskbar widget and background measurements continue normally.

The preference is `orbOnlyWhenCodexForeground` in settings JSON and defaults to false for
existing installations. It is independent of the existing Follow Codex startup/exit option.
When enabled, a 200 ms UI dispatcher timer reads the foreground window's PID and executable
with `PROCESS_QUERY_LIMITED_INFORMATION` and `QueryFullProcessImageNameW`. Only known Codex
desktop install paths qualify; Codex CLI, ordinary ChatGPT installs, missing windows and
unreadable processes do not. Window titles and contents are not read. Disabling the preference
stops monitoring and restores ordinary visibility.

Validation: Windows unit tests exercise persistence, transitions and disabled monitoring.
The `AgentSpeed.RadarHarness --orb-foreground <absolute-output-directory>` mode verifies both
WPF and GPU window lifecycles, saved visibility, live feeds, position retention and the settings
toggle. If started while Codex has focus, it also verifies native focus changes between its
own window and the real Codex window, using the real foreground reader and timer.
