# FOCUS-MIGRATION.md — Focus Manager migration log

The central Focus Manager (`Nuvio/Input/FocusCoordinator.cs`) replaces the
`NativeInputRouter`'s scattered focus ownership one page at a time, behind a
rollback flag. The legacy router code is never deleted during migration — it
stays as the rollback path. The design contract lives in `FOCUS.md` (draft);
this file logs what was actually wired, when, and what is still open. If the
two disagree, the implementation wins and `FOCUS.md` is updated in the same
change.

No visual change in any phase: the white static focus ring, sidebar visuals
and hero behaviour are untouched. No new packages, no WinUI 3 / WPF APIs.

## Rollback flag

`Nuvio/Input/FocusCoordinator.cs`, line ~33:

```csharp
internal static bool UseFocusCoordinator = true;
```

- Default: `true` (coordinator owns input dispatch, the Settings arrival
  settle, the navigation guard, dialog pause state and the focus registry
  for Settings).
- Flip: edit that line to `false` and rebuild. No other change needed.
- When flipped off, every page takes the legacy router paths: `Handle` runs
  the router's own dispatch/dedupe, `SettleArrivalFocus` runs the router's
  settle for every page, the guard is armed/cleared on the shell bit
  directly, `DialogOpen` only pauses the `ControllerBridge`, and the base
  recorder writes only to the router's registry. The coordinator is still
  constructed in `MainPage.OnLoaded` (construction has no side effects: no
  subscriptions, no timers, no focus moves), but nothing calls it. The
  harness still works through `SimulateFocusInput` — it drives the legacy
  path then.

## Phase 0 — harness + coordinator skeleton (2026-11-12)

Files added (not yet wired in this phase):

- `Nuvio/Input/FocusCoordinator.cs` — the central coordinator (settle,
  guard, registry, dialog pause, input entry point). Ported from the router
  with the same algorithms and constants. One deliberate deviation: its
  watchdog never runs the expiry fallback after a user takeover stopped the
  settle.
- `Nuvio/Diagnostics/FocusTestHarness.cs` — scripted focus-test engine.
  Drives input only through the pinned `MainPage.SimulateFocusInput` seam.
  Script 1: launch → hero Play (`TEST launch-hero-play PASS/FAIL` in the
  log). Its logged `settleAttempts` is its own readiness-poll count, not
  the router's/coordinator's internal attempt count.

Problems addressed: no scripted way to prove focus behaviour on the device;
no single owner for settle/guard/registry (state split across `MainPage`,
the router and per-page handlers).

## Phase 1 — integration: coordinator wired, Settings migrated (2026-11-12)

Files changed:

- `Nuvio/Nuvio.csproj` — added `<Compile>` entries for
  `Input\FocusCoordinator.cs` and `Diagnostics\FocusTestHarness.cs` (the
  legacy project lists files explicitly; without these the build never sees
  them).
- `Nuvio/MainPage.xaml.cs` — constructs the coordinator in `OnLoaded` next
  to the router and links both directions (`_router.Coordinator`,
  `Navigation.Coordinator`); adds the pinned seam
  `SimulateFocusInput(action, repeat)` and the shared
  `HandleControllerInput(action, repeat)` the poll callback and the seam
  both call; adds `LaunchArguments` + an `OnNavigatedTo` override that arms
  the harness; `DialogOpen` routes through the coordinator when the flag is
  on (pause + invoker capture only, no focus move) and keeps the
  `ControllerBridge` pause in both branches; `FocusSidebar` clears through
  the coordinator when the flag is on; the poll callback and
  `OnPreviewKeyDown` guard blocks cover the coordinator's settle when the
  flag is on (see below).
- `Nuvio/Input/NativeInputRouter.cs` — `Handle` delegates to the
  coordinator when linked and the flag is on; `SettleArrivalFocus` routes
  `SettingsPage` to `Coordinator.RequestArrival` and cancels the other
  owner's settle on every navigation (only one settle ever runs);
  `MoveFocus` / `HandleBack` are `internal` now (accessibility only) so
  `MainPage` can hand them to the coordinator as delegates; `HandleBack`
  arms through the coordinator when the flag is on; the `AttemptSettle` log
  line now appends `focusName=` / `focusTag=` in the coordinator's exact
  format (Phase 0b). Legacy bodies are untouched in behaviour.
- `Nuvio/Services/NavigationService.cs` — adds a `Coordinator` property;
  the `Navigate` choke-point and `GoBackOrHome` arm through the coordinator
  when the flag is on, else the shell directly.
- `Nuvio/Views/FocusablePage.cs` — the base recorder dual-writes while
  migrating: the router's registry exactly as before, plus the
  coordinator's registry when the flag is on (same key format, same Tag
  discipline — single-character glyph Tags are still skipped).
- `Nuvio/Diagnostics/FocusTestHarness.cs` — `RunIfRequested` also arms on
  a flag file named exactly `focustest` (no extension) in
  `ApplicationData.Current.LocalFolder` (see "Harness gate" below).

No `SettingsPage` edit was needed: it already implements `IArrivalFocus`
(`TryFocusArrival` / `TryFocusKey`), raises `ContentReady` synchronously in
`OnNavigatedTo`, builds its rows in `ShowSection`, and has no
`IPageInputHandler` — the router delegation serves it with no page change.
No other page is migrated: Home/Search/Catalog/Details/Streams/Player/
Placeholder keep the legacy settle through the router.

### Guard routing and the recursion rule

`MainPage.Arm/ClearSidebarExpandSuppression` stay the low-level shell-bit
setters and NEVER delegate to the coordinator — the coordinator's
`Arm/ClearNavigationGuard` already call them, so delegating back would
recurse infinitely. The flag decision lives at the call sites only:

- Arm: `NavigationService.Navigate`, `NavigationService.GoBackOrHome`,
  `NativeInputRouter.HandleBack` — coordinator when the flag is on, shell
  directly when off. `ContentFrame_Navigated` still re-asserts the shell
  bit directly (belt-and-braces, same value the coordinator set).
- Clear: settle success / expiry / takeover inside the owner (coordinator
  for Settings, router for the rest), `FocusSidebar`, and genuine input
  with no settle pending of either kind.
- The poll callback and `OnPreviewKeyDown` evaluate the takeover for the
  settle that is actually running: the coordinator evaluates its own settle
  inside `HandleInput`; `MainPage` evaluates the router's legacy settle
  (`_router.NoteUserInput()`) only when the router is settling, and clears
  through the coordinator only when neither is settling. The coordinator's
  settle is never double-evaluated and its guard is never cleared mid-settle
  from `MainPage`.

### Harness gate (how to run the focus test on the device)

The harness runs the launch → hero Play script and writes one line to
`nuvio-vlc.log`:

```
TEST launch-hero-play PASS expected=hero/Button actual=U+E768/Button focused=Button settleAttempts=N ms=N
```

It arms in either of two ways (dormant otherwise — no auto-run):

1. Launch argument: start the app with arguments containing `focustest`
   (Visual Studio: Debug → command line arguments).
2. Flag file (msix fallback): a dashboard launch of the installed msix
   always carries empty Arguments, so the token can never fire there.
   Upload a file named exactly `focustest` (no extension, content
   irrelevant) into the app's LocalState folder
   (`%LocalAppData%\Packages\<package>\LocalState`) via Device Portal →
   File Explorer, then launch the app normally. Delete the file to disarm.
   No Settings UI row was added for this.

### Settings device test list (real controller, flag on)

Install the build, then run each step and check the log lines that prove it:

1. Launch → hero Play has focus. Press nothing; wait for the hero. Log:
   the sync attempt shows `settled=false` (hero not loaded yet), then a
   later attempt — `Settle attempt 4/10 (content-ready): entry=
   ...HomePage|..., settled=true, focused=Button,
   focusName=HeroPlayButton, focusTag=U+E768` — followed by `Settle
   succeeded`. The strip must stay collapsed.
2. Open Settings from the sidebar (or Home → Settings). Log:
   `Settle arrival: page=SettingsPage, entry=...SettingsPage|settings|root`
   then `Settle attempt ... settled=true, focusName=<first root row
   button>` — the ring must sit on the first row (Account), not on the
   sidebar, and the strip must stay collapsed.
3. Move down the root list (D-pad down, one row per press). Each press
   moves exactly one row; no double moves, no jump to the sidebar.
   Log: `Settings layout (loaded):` line (the alignment probe) plus the
   row buttons' Tags recorded under the `settings|root` entry.
4. Open Addons, move to an addon action, press B. Log: Back arrival on
   the root list lands on the same row (`TryFocusKey` path — the
   coordinator's `Settle attempt ... isBack=True, settled=true` with the
   recorded row's `focusTag=root|addons`).
5. Open Addons again, press B from the root list. Focus returns to the
   exact row visited before (entry-keyed registry: `settings|root` vs
   `settings|addons` never overwrite each other).
6. Press B on the Settings root with an empty back stack. The app goes
   Home (not exit); Home's legacy settle lands on the hero.
7. Open the menu (right from Play / sidebar button), then press B.
   The strip collapses and focus is back on the page — the guard was
   armed before the navigation and cleared by the settle, not by the
   press itself.
8. Harness (optional): with the `focustest` flag file in LocalState,
   launch the app and check for the `TEST launch-hero-play PASS` line;
   then delete the flag file and relaunch — no `TEST` line must appear.

### Known Phase 1 limitations

- `OnPreviewKeyDown` stays dispatch-free. The console delivers one press
  twice (XAML key event + 30 ms controller poll); the coordinator's 150 ms
  dedupe covers directions only, so feeding back/menu/view/lb/rb from the
  key path would double-deliver them (double Back, double hero flip).
  The poll remains the single dispatch path, as before. Directions alone
  would be deduped, but a directions-only feed is asymmetric and held
  repeats bypass the dedupe, so no partial feed was added.
- The coordinator's nearest-sibling step in `NotifyContentChanged` is
  deferred to Phase 2 (it needs the row model). The fallback order today
  is: recorded key → arrival target → first focusable element.
- No eviction cap on either focus registry in Phase 1 (Phase 2 adds one).
- Dialog restore stays page-owned in Phase 1: the coordinator only pauses
  input and captures the invoker; `RequestTextAsync` / `ShowConfirmAsync`
  still restore focus themselves, and no focus move happens on dialog
  close from the coordinator.
- Guard-state mirror: the coordinator's `IsNavigationGuardArmed` only
  tracks coordinator-owned arms. After a legacy (non-Settings) settle
  clears the shell bit, the coordinator's bit can read stale-true until
  the next coordinator arm/clear. Unobservable in Phase 1 — nothing reads
  it, and the shell bit is the enforcement point — but Phase 2 should make
  the coordinator the single guard owner.
- The legacy settle's null-page branch clears the shell bit directly; if a
  coordinator settle was somehow pending there, the coordinator's bit
  would stay armed until the next navigation. Unreachable in practice
  (NavigationService refuses non-FocusablePage targets, so the content is
  never null).
- Direct per-page recorders (`HomePage` / `SearchPage` / `CatalogPage` /
  `DetailsPage` / `StreamsPage` calling `MainPage.RecordArrivalFocusKey`)
  still write only to the router's registry; only the `FocusablePage` base
  recorder dual-writes. Correct for Phase 1 (the coordinator settles
  Settings only, and Settings records only through the base recorder);
  Phase 2 moves recording to the single funnel.
- `FocusTestHarness`'s logged `settleAttempts` is its own readiness-poll
  count, not the router's/coordinator's internal attempt count.

## Fix round — guard hard timeout, Details error-path ready signal, probe removal (2026-11-13)

Files changed:

- `Nuvio/Input/FocusCoordinator.cs` (worker B) — hard timeout on the
  navigation guard: the guard clears at most 2 seconds after arming even
  if the settle never succeeds (enforced on the 500 ms watchdog tick, so
  effectively 2.0–2.5 s), so a slow/failed settle can no longer leave the
  strip unopenable for up to ~5 s (10 watchdog attempts). Also a FIFO
  registry cap in the coordinator (precaution, Mohammed 00:45): at most
  200 live entries, at most 20 search-keyed ones; evicting an entry also
  removes its row map. The legacy router registry stays unbounded behind
  the flag. (Coordinator fix: `IsSearchEntryKey` matches `"|search|"` —
  entry keys carry a type-name prefix, so `StartsWith("search|")` never
  matched and the search sub-cap was dead until this fix.)
- `Nuvio/ViewModels/DetailsViewModel.cs` + `Nuvio/Views/DetailsPage.xaml.cs`
  (worker A) — the Details error path now raises the content-ready signal
  on load failure instead of letting the settle burn all 10 attempts with
  the guard armed.
- `Nuvio/Views/SettingsPage.xaml.cs` (worker C) — removed both temporary
  Settings layout probes (v1 `Dispatcher.RunAsync` block in `OnNavigatedTo`
  logging `Settings layout: ...`, v2 `Loaded` handler logging
  `Settings layout (loaded): ...`) plus their TEMPORARY comments and the
  now-unused `using Windows.UI.Core;`. Pure deletion (23 lines removed,
  0 added). `_firstFocusTarget`, arrival logic, `Log` (still used by
  `Log.Error` elsewhere in the file) and `using Nuvio.Core;` /
  `using Nuvio.Diagnostics;` are untouched. The probes proved their point
  (device log: rows span 32..804 exactly as designed) and are done.

Rollback: flag off (`UseFocusCoordinator = false`) = legacy router paths
with the old guard behaviour and no coordinator hard timeout; the probe
removal is behaviour-neutral either way (the probes only wrote log lines).

Device test: the Phase 1 "Settings device test list" above still applies,
except step 3's `Settings layout (loaded):` log line no longer exists —
verify step 3 by the settle lines and the ring position instead. Plus:
Details load failure must settle promptly (guard clears, no ~5 s stall);
a stalled settle anywhere must clear the guard within the hard timeout.
