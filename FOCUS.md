# FOCUS.md — Nuvio Focus System Contract

> **Status: final (Phase 5).** Finalized against the implemented
> `FocusCoordinator` and device-verified through Phases 1–4. If a rule here
> disagrees with the implementation, the implementation wins and this file
> is updated in the same change.
>
> **One-line summary:** every page derives from `FocusablePage`, declares one
> arrival target and a stable key scheme, never focuses anything itself, and
> routes all input and dialogs through the central `FocusCoordinator`.

## 1. The model

- **Single owner.** `FocusCoordinator` (`Nuvio/Input/FocusCoordinator.cs`) is
  the only owner of: the current focus element, the arrival settle, the focus
  memory registry, the sidebar navigation guard policy, the single input
  entry point, the dialog stack, the row/edge navigation model, and
  focus-validity observation. Pages, the shell, and the router do not keep
  their own copies of any of these. One exception to "only owner": the guard
  has a shell-side enforcement bit (`MainPage` holds it and the router can
  arm it directly); the coordinator is the authority on guard policy and the
  shell bit is the enforcement point — a stale-true read of the shell mirror
  is possible, so never treat the mirror as the policy.
- **Thin router.** `NativeInputRouter` is an input-action translator and
  dispatcher: `Handle(action, repeat)` delegates to
  `FocusCoordinator.HandleInput`. It keeps back policy (`HandleBack`) and the
  three page contracts (`IPageInputHandler`, `IArrivalFocus`, `IRowFocusInfo`).
  The router migrates Settings/Home/Search/Catalog to the coordinator settle;
  every other page keeps its legacy settle, unchanged.
- **Pages keep their contracts.** `TryFocusArrival()` / `TryFocusKey(key)` /
  `HandleAction(action, repeat)` stay implemented per page; the coordinator
  is the consumer. `MainPage` keeps hosting the `Frame` and the sidebar
  visuals; `NavigationService` keeps route definitions and arms the guard
  through the coordinator.
- **Tech.** UWP XAML/C# only (target 10.0.22621.0, min 10.0.22000.0). No WinUI 3
  or WPF APIs, no new packages. The white static focus ring, sidebar visuals,
  and hero behavior are design and do not change.

## 2. New page checklist

Every new page must satisfy all of these to join the system:

1. **Derive from `FocusablePage`** — never from `Page` directly. The XAML
   root must name `FocusablePage`; `NavigationService` refuses anything else.
2. **Implement `TryFocusArrival()`** — the single first element. It must exist
   in XAML or be guaranteed by `RaiseContentReady`. Return `false` if it
   cannot take focus yet.
3. **Implement `TryFocusKey(key)`** — resolve a recorded key to the exact
   element. Return `false` on a missing key. Never fall back silently inside
   the method — the coordinator applies the arrival fallback.
4. **Define a key scheme** — stable content ids only. Never stream URLs, API
   keys, or tokens. Manifest URLs are allowed as catalog identities. If the
   page takes a new navigation parameter type, register it in the
   coordinator's `EntryIdentity` (key format:
   `Type.FullName + "|" + EntryIdentity(parameter)`).
5. **Tag discipline** — the base recorder records the first ancestor `Button`'s
   `Tag` iff it is a string longer than 1 char. Single-char glyph `Tag`s are
   skipped by design. **Never put a multi-char `Tag` on a `Button` nested
   inside a card** — it would hijack the arrival key. Rows/chips that should
   be remembered get explicit keys (`"chip:<key>"`, `"entry:<Order>"`,
   `"root:<id>"`); chips that should not be remembered get no `Tag`.
6. **Declare `IPageInputHandler` only if the page intercepts input** — list
   every action handled and what falls through to the coordinator. If the page
   does not implement it, the coordinator's XYFocus + edge rules own all
   moves (verify they cover the page — the router migrates Settings/Home/
   Search/Catalog to the coordinator settle; any other page keeps the legacy
   settle until its own migration phase).
7. **Declare row membership** — if the page has multiple `GridView` rows,
   implement `IRowFocusInfo` for per-row memory; otherwise up/down is plain
   XYFocus.
8. **Declare sidebar membership** — full-screen page? It goes in the hide
   list (coordinator config; today `MainPage.ContentFrame_Navigated`).
   Otherwise the strip stays visible. **The strip opens on the current
   page's button:** any move toward the sidebar from content opens it and
   lands on the button for the page currently shown (`HomeButton` from
   Home, `SearchButton` from Search, `SettingsButton` from Settings,
   `LibraryButton` from Library; `HomeButton` when the page is unknown) —
   never the geometrically nearest button; the coordinator intercepts only
   when XYFocus predicts the move would land on the strip (or on nothing).
   Sidebar buttons do not accept focus while the strip is collapsed at
   launch (`IsTabStop=false` until a deliberate open or an expansion
   re-enables them). (History: an early rule said "always the first
   option"; it was superseded — the current page's button is the rule.
   Do not resurrect the old one.)
9. **Dialogs go through the coordinator** — open via
   `NotifyDialogOpened(invoker)` (captures the invoker, pauses input), close
   via `NotifyDialogClosed()` (restores the invoker, or the arrival target if
   the invoker is gone). Never hand-roll restore.
10. **Dynamic content** — after any re-render that rebuilds focusable
    elements, call `Coordinator.NotifyContentChanged(page)`; the coordinator
    re-resolves the recorded key (same key → same element; gone → nearest
    sibling → arrival). Never re-focus inline from the render method.
11. **Readiness** — call `RaiseContentReady()` exactly once, when the arrival
    target is focusable (sync pages: `OnNavigatedTo` after building rows;
    async pages: after the first data render). Never focus anything in
    `OnNavigatedTo` — arrival is coordinator-owned.
12. **Tests with the component** — follow the §7 template (arrival, keys,
    input contract, Back restore, dialog restore, sidebar membership,
    dynamic content, row memory) and register them in the harness registry
    (`FocusTestHarness.BuildTests`) in the same change. Keep new identifiers
    clear of the `guard` job's banned list (`.github/workflows/build.yml`)
    and new `.Focus(` sites clear of the allowlist in
    `tools/check-focus-guard.py` (add a site there only deliberately, with
    review); commit messages one line, no personal information.

## 3. Save / restore rules

- **Storage:** coordinator-owned dictionaries (entry → focus key;
  entry+row → focus key), process lifetime, with an eviction cap (FIFO —
  insertion-order queues, 200 entries; search-query keys capped separately,
  last 20 queries). Eviction drops the entry's row map with it.
- **Back:** `TryGetLastFocusKey(entryKey)` → page `TryFocusKey(key)`; on miss
  or `false` → arrival target. Row memory: `TryGetRowFocusKey(entryKey,
  rowKey)` consumed by up/down row moves.
- **Forward:** the recorded key is **ignored** — forward navigation always
  arrives fresh at the arrival target. A fresh (non-Back) arrival also
  clears the entry's per-row memory (`MainPage` clears it on the Navigated
  boundary), so row-to-row moves land on each row's first poster and the map
  rebuilds from this visit's GotFocus records; Back arrivals keep the map.
  This holds even on a Required-cache page re-navigated while already on it
  (Home/Search): the settle must not accept the page's stale in-page focus
  as settled — it must run the arrival target.
  Root navigations clear the frame BackStack, but only after a successful
  navigation (`NavigationService`: a refused/failed navigation must not wipe
  history).
- **Dialogs:** a stack of invokers (`Stack<Control> _dialogInvokers` — one
  per open dialog; UWP shows one `ContentDialog` at a time, but the stack
  is what makes nested dialogs restore correctly). Close restores the
  invoker, or the arrival target if it is gone. In a nested chain the outer
  invoker is restored only when the outer dialog closes, not when the inner
  one does.
- **Re-render:** after a re-render, re-resolve the recorded key; on miss,
  fall back in order: nearest valid sibling in the same container → the
  page arrival target → the first focusable element in the frame. Never
  leave focus null.
- **Guard timing:** the navigation guard has a hard timeout of 2 s (it must
  be shorter than the ~5 s watchdog so a stuck settle can never hold the
  sidebar past it), and a 200 ms ignore window after a settle starts during
  which input is ignored for takeover purposes.

## 4. Dynamic / hidden / disabled elements

1. Focus must never rest on a `Collapsed`, disabled, or detached element.
   After any content change the coordinator validates the current focus: if
   it is not `Visibility.Visible`, its `Control.IsEnabled` is false, or it is
   no longer in the live visual tree, focus moves — in order: (a) the same
   recorded key if it still resolves to a valid element; (b) nearest valid
   sibling in the same container (closest index); (c) the page arrival
   target. Never leave focus null.
2. Hidden elements are never focus targets. A recorded key resolving to a
   `Collapsed` element counts as "gone", not as a target.
3. Disabled elements are never focus targets (same treatment as hidden).
4. A newly rendered element never *steals* focus; it only becomes eligible
   for the next move/restore. (Exception: the arrival settle's `ContentReady` /
   layout triggers, which are the page's declared readiness, not a steal.)
5. A missing key always falls through to `TryFocusArrival()`.
6. A bare `ItemsControl` container is never a valid focus target — even
   though it is visible, enabled, and in the live tree, it has no ring and
   no interaction (`IsFocusValid` rejects anything that is not a real focus
   target, and the settle log says so instead of claiming a container was
   parked on; null focus is logged as null, not as a container).

## 5. Player / menus / dialogs rules

- **Player OSD:** the ViewModel owns visibility (`ControlsVisible`) and
  requests focus only via `FocusRequested` hints. The full hint vocabulary
  is `back` / `play` / `seek` / `rewind` / `forward` / `subtitles` / `audio` /
  `speed` / `aspect` / `sources` / `episodes` / `next` / `error` (each
  resolved by `PlayerPage.TryFocusKey`; `next` targets the Next-episode
  card's Play button, not the OSD Next button; `error` targets TryNext when
  visible, else Retry). The OSD hides by setting `OsdRoot.Opacity` to 0 —
  the buttons stay in the tree but invisible — so on hide, if focus is
  inside the OSD, the coordinator parks it on the Back button (the player
  arrival target, via `TryFocusKey("back")`) and remembers the last OSD
  control; on re-show, focus returns to the remembered control, or the hint
  target (`"seek"` → SeekBar, `"play"` → PlayPause). The show-restore runs
  synchronously inside the `ControlsVisible` property change, before the
  ViewModel raises its focus hint, so the hint's never-yank rule keeps the
  restored element. It is skipped entirely when a dialog is open (the dialog
  owns focus) or an arrival settle is running — the remembered slot is
  cleared in both cases. Exception: when focus is on a visible Next-card or
  error-box button (outside the OSD root), nothing is parked and that button
  keeps focus. X toggles pause (there is no stop action). Back: cancel a
  pending scrub first; if controls are visible and playing, hide only;
  otherwise exit. (Paused + visible exits immediately — documented
  asymmetry.)
- **Sidebar menu:** states Hidden | Collapsed (48 epx) | Expanded (136 epx).
  Open: focus landing on a sidebar button, pointer hover, or explicit
  `FocusSidebar()` (e.g. from the Hero Play start-edge). Gated on real user
  input and a clear guard. **A move toward the sidebar from content opens
  the strip and lands on the button for the current page — never on the
  geometrically nearest button;** the coordinator intercepts only when
  XYFocus predicts the move would land on the strip (or on nothing), so
  mid-row moves in multi-column grids are untouched. While the strip is
  collapsed at launch its buttons do not accept focus. Close: focus
  entering content (`ContentFrame.GotFocus` collapses it), choosing a
  button, pointer leaving. No explicit close button, no auto-collapse timer.
  B does not close the strip directly — it navigates back, and the strip
  collapses when the new page's settle puts focus back in content; B on
  Home with an empty back stack shows the exit toast and leaves the strip
  alone. Opening pins the Home hero to the Verity slide.
- **Dialogs:** all menus/popups are `ContentDialog`s. While one is open,
  input is paused (one switch in the coordinator) and the dialog owns focus
  natively. B/Esc/system-back closes natively. On close, focus returns to
  the invoker, or to the arrival target if the invoker is gone.

## 6. Controller rules (condensed)

- Poll: 30 ms, all gamepads OR-ed, buttons down at startup ignored.
- D-pad priority Up > Down > Left > Right, else thumbstick threshold 0.6,
  dominant axis. D-pad = arrows: both swallowed in the key path; only the
  poll moves focus; GamepadA passes for native activation.
- Repeat: first move immediate; hold repeats after 400 ms, then every
  110 ms, no acceleration. A/B/X/Y/Menu/View/LB/RB/LT/RT are edge-triggered
  only. Same initial direction within 150 ms is dropped (dedupe); repeats
  pass.
- Generic move: `FocusManager.TryMoveFocus(direction)`; if nothing is
  focused, the first focusable element in the frame.
- Row left/right: index-based neighbour, forward-stop at the row end
  (RTL-aware), never drops to the next row. Up/down between rows lands on
  the remembered poster or index 0.
- Pinned anchor (المرساة): in-row Left/Right keeps the focus ring pinned at
  the second slot — the card before the focused one sits on the hero edge
  (k−1 target, build 292; RTL 619..707, LTR 123..211) — and the row scrolls
  underneath it (pre-scroll before the focus move, instant jump, no glide).
  Which card is focused, the order, and per-row memory are unchanged. At
  the row tail (offset clamped at ScrollableWidth) and in short rows the
  ring detaches and follows the card. Catalog has no anchor (single
  wrapping 2-D grid); Search pins to its own 6 epx start edge.
- Edges: a forward move past the last card stops dead; the row-start edge is
  open so a move toward the sidebar enters it. Up on the first / Down on the
  last sidebar button stops dead.
- Back (single): the page first; then arm the guard, `GoBack()`, else
  `NavigateHome()`, else a toast. Back twice on Home within 2500 ms exits
  the app (first press shows a toast).
- Arrival: settle sync → `ContentReady` → first-layout → 500 ms watchdog
  (max 10). Success = focus verifiably inside the page. Expiry fallback =
  first focusable element in the frame. A press that cannot move focus does
  **not** cancel the settle; a press that moves focus does (takeover).
  A D-pad press inside the strip during a settle is not a takeover; focus
  parked on a bare container (or null focus) during a settle is not one
  either — the settle keeps running and the arrival target lands when the
  content loads.

## 7. Tests

- **Harness:** `Nuvio/Diagnostics/FocusTestHarness.cs` — the Phase 5
  package: 48 tests (46 automated in the registry, plus 2 manual gate
  checks below). Scripted action sequences driven through the pinned
  `MainPage.SimulateFocusInput` seam — the same code path the
  ControllerBridge poll callback uses — on the UI thread with realistic
  inter-press timing (200 ms between presses, above the 150 ms dedupe).
  Each test asserts focus identity/type, no focus loss, no stuck guard,
  settle timing, Back-restore exactness, dedupe/repeat behavior, and
  dialog pause/restore. Results are written as
  `TEST <name> PASS|FAIL|SKIP <detail>` lines into `nuvio-vlc.log`, plus
  `TEST suite START …` / `TEST suite DONE passed=N failed=M skipped=S ms=N`.
  SKIP means the fixture was absent (no Continue items, no catalog data, no
  playable stream, cold-start-only) — it is not a pass and not a failure.
  **Arming the gate:** the suite is dormant unless the launch arguments
  contain the token `focustest` (Visual Studio: Debug → command line
  arguments) OR a file named exactly `focustest` (no extension) exists in
  the app's local folder (LocalState — the msix fallback, since a dashboard
  launch carries no arguments; upload it via Device Portal File Explorer
  and delete it to disarm). The gate decision stands as built: inert
  behind the gate in Release, NOT `#if DEBUG` — the package must run on
  the release msix on the Xbox. The 2 manual checks (no in-app suite can
  observe its own non-execution): (1) launch with neither gate armed and
  confirm no `TEST suite` lines appear in the log; (2) arm with the flag
  file, run once, delete the file, relaunch, confirm no `TEST suite` lines.
- **Limits (honest):** the harness cannot fake a physical Gamepad — it drives
  the post-poll callback, so poll-level behavior (repeat cadence, thumbstick
  thresholds, missed edges) stays untested. Focus tests cannot run headless
  in CI (they need a live visual tree, UI thread, and window); CI verifies
  compile + guard patterns (`tools/check-focus-guard.py`: no `.Focus(` in
  ViewModels/Playback/Services/Diagnostics, every other site allowlisted by
  file/method/receiver), the Xbox device runs the suite. Visual
  verification of the focus ring is a device-eye check, not automatable.
  The harness cannot close a dialog through the seam (input is gated while
  a dialog is open, and the native system-back cannot be raised
  synthetically), so dialog tests close with the dialog's own `Hide()` and
  choose options with `ButtonAutomationPeer.Invoke()` — the restore and
  navigation that run after the close are the real production paths.
- **Adding tests for a new page/component:** (1) arrival — which control,
  sync vs after what data; (2) keys — scheme + `EntryIdentity` registration;
  (3) input contract — handled actions vs fall-through; (4) Back restore —
  leave → Back → assert the exact `Tag`; (5) dialog restore if it opens any
  dialog; (6) sidebar membership; (7) dynamic content — focus survives
  re-render + key-gone → arrival fallback; (8) row memory if multiple rows.
  Register in the harness registry (`FocusTestHarness.BuildTests`) with the
  page's `EntryKey` and expected `Tag`s.

## 8. What NOT to do

- No WinUI 3 / WPF APIs; no new packages; no visual change (white static
  ring, sidebar visuals, hero behavior untouched).
- No renaming page contracts until all pages are migrated.
- No reintroducing a parallel focus walker: `FocusScrollHelper` was absorbed
  by the coordinator's row model in Phase 2 (only the flag-off path still
  calls it) — new pages use the coordinator's row model, not a second one.
- No auto-running harness in Release (gate only, §7); no switching the gate
  to `#if DEBUG` without a design reason.
- No new `.Focus(` call sites outside the allowlist in
  `tools/check-focus-guard.py` — a new site is added there deliberately,
  with review, never silently.
- No editing `.github/workflows/build.yml` in a code change — the focus and
  alignment guard patterns and the rolling-draft race fix live as a spec in
  `FOCUS-MIGRATION.md` and are applied through the GitHub web editor.
- No "fixing" incomplete pages (M5/M6 placeholders) as a side effect —
  migration is per-phase, per-page.
- No removing the 150 ms dedupe until the single entry point is proven on
  the device.
- Never focus anything from `OnNavigatedTo`, from a render method, or from
  a dialog's `Opened` handler outside the coordinator's restore path.
- Never delete functionality or a component to fix a compile error; never
  disable warnings or work around errors as a solution. Fix root causes.
- Commit messages one line, no personal information, never attribute a
  decision to the user by name.
