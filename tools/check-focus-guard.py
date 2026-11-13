#!/usr/bin/env python3
"""Focus guard (Phase 5 of the Focus Manager programme).

Fails CI when a `.Focus(` call appears anywhere it should not. Two rules:

F0 — no `.Focus(` at all under Nuvio/ViewModels, Nuvio/Playback,
     Nuvio/Services or Nuvio/Diagnostics. Those layers never place focus;
     focus is placed only by the focus system and by pages through their
     arrival/key contracts. (Zero hits on the audited head.)

F1 — every other `.Focus(` site must be an allowlisted
     (file, method, receiver) triple below. The allowlist is the complete
     inventory of the audited head (74 sites, 72 distinct triples); line
     numbers are NOT part of the key because they drift — the key is the
     method and the receiver, which is what makes a site legitimate:
       * the focus system itself (FocusCoordinator, NativeInputRouter);
       * a page's arrival/key methods (TryFocusArrival, TryFocusKey,
         FocusFirstCard*, TryFocusOpenedCard, TryFocusFirstResult,
         FocusCardInGrid, FocusChip, FocusEntryAt, FocusButton,
         OnFocusRequested, FocusDownFromSearchBox, FocusContinueNeighbour,
         FocusNextUpEpisode, RestoreEpisodeFocus, RestoreAddonActionFocus,
         RestoreDialogInvoker, FocusSidebar, FocusSidebarFirst);
       * the 4 documented deferred sites (dialog-restore ticks and
         ViewModel-driven auto-focus — see the DEFERRED marks below);
       * action handlers that focus as their documented effect
         (HandleAction, Show*MenuAsync dialog Opened, OnContinueRefreshed,
         RenderDebrid, ShowConfirmAsync, SearchField_Tapped,
         RequestTextAsync).
     A new site fails until it is added here deliberately, with review.

Method detection is brace-depth tracking over code with strings and
comments stripped; lambdas inside a method (e.g. a dialog Opened handler)
attribute to their enclosing method, which is the correct key. Known
limit: an expression-bodied method (`=> x.Focus(...);` on the signature
line) attributes to the enclosing method instead of itself — none exists
on the audited head; if one is ever added, fix this script, not the
product. Verbatim strings (@"...") are stripped before matching.

Usage: python3 tools/check-focus-guard.py   (exit 0 = clean, 1 = violations)
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

F0_DIRS = (
    "Nuvio/ViewModels",
    "Nuvio/Playback",
    "Nuvio/Services",
    "Nuvio/Diagnostics",
)

# (file, method, receiver) — audited head (Phase 5). The 4 deferred sites
# are marked DEFERRED: DetailsPage ShowEpisodeMenuAsync/control (:520),
# HomePage FocusContinueNeighbour/container (:1220),
# PlayerPage OnViewModelPropertyChanged/NextPlayButton (:259),
# PlayerPage OnViewModelPropertyChanged/TryNextButton (:270) and
# OnViewModelPropertyChanged/RetryButton (:271).
ALLOWLIST = {
    # MainPage — text-input focus + sidebar focus helpers.
    ("Nuvio/MainPage.xaml.cs", "RequestTextAsync", "input"),
    ("Nuvio/MainPage.xaml.cs", "RequestTextAsync", "control"),
    ("Nuvio/MainPage.xaml.cs", "FocusSidebar", "button"),
    ("Nuvio/MainPage.xaml.cs", "FocusSidebarFirst", "button"),
    # FocusCoordinator — the focus system itself.
    ("Nuvio/Input/FocusCoordinator.cs", "MoveFocus", "first"),
    ("Nuvio/Input/FocusCoordinator.cs", "TryMoveToAdjacentRow", "first"),
    ("Nuvio/Input/FocusCoordinator.cs", "FocusFirstCardOfEnteredRow", "first"),
    ("Nuvio/Input/FocusCoordinator.cs", "OnSettleWatchdogTick", "first"),
    ("Nuvio/Input/FocusCoordinator.cs", "NotifyOsdShown", "target"),
    ("Nuvio/Input/FocusCoordinator.cs", "RestoreDialogInvoker", "invoker"),
    ("Nuvio/Input/FocusCoordinator.cs", "NotifyContentChanged", "first"),
    ("Nuvio/Input/FocusCoordinator.cs", "TryFocusSibling", "candidate"),
    # NativeInputRouter — legacy (flag-off) settle paths.
    ("Nuvio/Input/NativeInputRouter.cs", "OnSettleWatchdogTick", "first"),
    ("Nuvio/Input/NativeInputRouter.cs", "MoveFocus", "first"),
    ("Nuvio/Input/NativeInputRouter.cs", "TryMoveToAdjacentRow", "first"),
    ("Nuvio/Input/NativeInputRouter.cs", "FocusFirstCardOfEnteredRow", "first"),
    # CatalogPage — arrival/key.
    ("Nuvio/Views/CatalogPage.xaml.cs", "TryFocusOpenedCard", "container"),
    ("Nuvio/Views/CatalogPage.xaml.cs", "FocusFirstCard", "container"),
    # DetailsPage — arrival/key + episode-menu restore.
    ("Nuvio/Views/DetailsPage.xaml.cs", "TryFocusArrival", "PlayButton"),
    ("Nuvio/Views/DetailsPage.xaml.cs", "TryFocusArrival", "RetryButton"),
    ("Nuvio/Views/DetailsPage.xaml.cs", "FocusNextUpEpisode", "container"),
    ("Nuvio/Views/DetailsPage.xaml.cs", "RestoreEpisodeFocus", "container"),
    ("Nuvio/Views/DetailsPage.xaml.cs", "ShowEpisodeMenuAsync", "playButton"),
    ("Nuvio/Views/DetailsPage.xaml.cs", "ShowEpisodeMenuAsync", "control"),  # DEFERRED :520
    # FocusablePage — flag-off dialog restore.
    ("Nuvio/Views/FocusablePage.cs", "RestoreDialogInvoker", "control"),
    # HomePage — arrival/key + Continue restore + hero actions.
    ("Nuvio/Views/HomePage.xaml.cs", "TryFocusArrival", "HeroPlayButton"),
    ("Nuvio/Views/HomePage.xaml.cs", "TryFocusArrival", "ManageAddonsButton"),
    ("Nuvio/Views/HomePage.xaml.cs", "TryFocusKey", "HeroPlayButton"),
    ("Nuvio/Views/HomePage.xaml.cs", "TryFocusKey", "container"),
    ("Nuvio/Views/HomePage.xaml.cs", "FocusCardInGrid", "container"),
    ("Nuvio/Views/HomePage.xaml.cs", "OnContinueRefreshed", "container"),
    ("Nuvio/Views/HomePage.xaml.cs", "ShowContinueMenuAsync", "detailsButton"),
    ("Nuvio/Views/HomePage.xaml.cs", "FocusContinueNeighbour", "container"),  # DEFERRED :1220
    ("Nuvio/Views/HomePage.xaml.cs", "HandleAction", "HeroDetailsButton"),
    ("Nuvio/Views/HomePage.xaml.cs", "HandleAction", "HeroPlayButton"),
    ("Nuvio/Views/HomePage.xaml.cs", "HandleAction", "container"),
    ("Nuvio/Views/HomePage.xaml.cs", "FocusFirstCardOfFirstRow", "container"),
    # PlaceholderPage — arrival.
    ("Nuvio/Views/PlaceholderPage.xaml.cs", "TryFocusArrival", "BackButton"),
    # PlayerPage — arrival/key + OSD focus requests + deferred auto-focus.
    ("Nuvio/Views/PlayerPage.xaml.cs", "TryFocusArrival", "BackButton"),
    ("Nuvio/Views/PlayerPage.xaml.cs", "FocusButton", "button"),
    ("Nuvio/Views/PlayerPage.xaml.cs", "OnFocusRequested", "SeekBar"),
    ("Nuvio/Views/PlayerPage.xaml.cs", "OnFocusRequested", "BackButton"),
    ("Nuvio/Views/PlayerPage.xaml.cs", "OnFocusRequested", "NextPlayButton"),
    ("Nuvio/Views/PlayerPage.xaml.cs", "OnFocusRequested", "TryNextButton"),
    ("Nuvio/Views/PlayerPage.xaml.cs", "OnFocusRequested", "RetryButton"),
    ("Nuvio/Views/PlayerPage.xaml.cs", "OnFocusRequested", "PlayPauseButton"),
    ("Nuvio/Views/PlayerPage.xaml.cs", "OnViewModelPropertyChanged", "NextPlayButton"),  # DEFERRED :259
    ("Nuvio/Views/PlayerPage.xaml.cs", "OnViewModelPropertyChanged", "TryNextButton"),  # DEFERRED :270
    ("Nuvio/Views/PlayerPage.xaml.cs", "OnViewModelPropertyChanged", "RetryButton"),  # DEFERRED :271
    ("Nuvio/Views/PlayerPage.xaml.cs", "ShowMenuDialogAsync", "first"),
    # SearchPage — arrival/key + box tap + actions.
    ("Nuvio/Views/SearchPage.xaml.cs", "TryFocusFirstResult", "container"),
    ("Nuvio/Views/SearchPage.xaml.cs", "TryFocusArrival", "SearchBox"),
    ("Nuvio/Views/SearchPage.xaml.cs", "TryFocusKey", "SearchBox"),
    ("Nuvio/Views/SearchPage.xaml.cs", "TryFocusKey", "container"),
    ("Nuvio/Views/SearchPage.xaml.cs", "SearchField_Tapped", "SearchBox"),
    ("Nuvio/Views/SearchPage.xaml.cs", "HandleAction", "SearchBox"),
    ("Nuvio/Views/SearchPage.xaml.cs", "HandleAction", "container"),
    ("Nuvio/Views/SearchPage.xaml.cs", "FocusDownFromSearchBox", "chip"),
    # SettingsPage — arrival/key + addon/debrid restore + confirm.
    ("Nuvio/Views/SettingsPage.xaml.cs", "TryFocusArrival", "target"),
    ("Nuvio/Views/SettingsPage.xaml.cs", "TryFocusKey", "rootButton"),
    ("Nuvio/Views/SettingsPage.xaml.cs", "TryFocusKey", "addonButton"),
    ("Nuvio/Views/SettingsPage.xaml.cs", "TryFocusKey", "InstallAddonButton"),
    ("Nuvio/Views/SettingsPage.xaml.cs", "TryFocusKey", "ResetAddonsButton"),
    ("Nuvio/Views/SettingsPage.xaml.cs", "TryFocusKey", "debridButton"),
    ("Nuvio/Views/SettingsPage.xaml.cs", "RestoreAddonActionFocus", "button"),
    ("Nuvio/Views/SettingsPage.xaml.cs", "RestoreAddonActionFocus", "InstallAddonButton"),
    ("Nuvio/Views/SettingsPage.xaml.cs", "RenderDebrid", "button"),
    ("Nuvio/Views/SettingsPage.xaml.cs", "RenderDebrid", "first"),
    ("Nuvio/Views/SettingsPage.xaml.cs", "ShowConfirmAsync", "primary"),
    # StreamsPage — debrid dialog + chip/entry focus.
    ("Nuvio/Views/StreamsPage.xaml.cs", "ShowDebridSettingsDialogAsync", "dialog"),
    ("Nuvio/Views/StreamsPage.xaml.cs", "FocusChip", "button"),
    ("Nuvio/Views/StreamsPage.xaml.cs", "FocusEntryAt", "container"),
}

METHOD_START = re.compile(
    r"^\s*(public|private|protected|internal)\s+"
    r"(?:static\s+|async\s+|override\s+|virtual\s+|sealed\s+|partial\s+)*"
    r"[\w\.<>\[\],\s\?]+\s+(\w+)\s*\("
)
FOCUS_CALL = re.compile(r"([\w\.\[\]]+)\.Focus\s*\(")


def strip_code(source):
    """Replace string/char literals and comments with spaces (newlines kept),
    so braces and .Focus( matches are counted only in real code."""
    out = []
    i = 0
    n = len(source)
    while i < n:
        c = source[i]
        nxt = source[i + 1] if i + 1 < n else ""
        if c == "/" and nxt == "/":
            while i < n and source[i] != "\n":
                out.append(" ")
                i += 1
        elif c == "/" and nxt == "*":
            out.append(" ")
            out.append(" ")
            i += 2
            while i < n and not (source[i] == "*" and i + 1 < n and source[i + 1] == "/"):
                out.append("\n" if source[i] == "\n" else " ")
                i += 1
            if i < n:
                out.append(" ")
                out.append(" ")
                i += 2
        elif c == "@" and nxt == '"':
            out.append(" ")
            out.append(" ")
            i += 2
            while i < n:
                if source[i] == '"' and i + 1 < n and source[i + 1] == '"':
                    out.append(" ")
                    out.append(" ")
                    i += 2
                    continue
                if source[i] == '"':
                    out.append(" ")
                    i += 1
                    break
                out.append("\n" if source[i] == "\n" else " ")
                i += 1
        elif c == '"':
            out.append(" ")
            i += 1
            while i < n and source[i] != "\n":
                if source[i] == "\\":
                    out.append(" ")
                    i += 1
                    if i < n:
                        out.append("\n" if source[i] == "\n" else " ")
                        i += 1
                    continue
                if source[i] == '"':
                    out.append(" ")
                    i += 1
                    break
                out.append(" ")
                i += 1
        elif c == "'":
            out.append(" ")
            i += 1
            while i < n and source[i] != "\n":
                if source[i] == "\\":
                    out.append(" ")
                    i += 1
                    if i < n:
                        out.append(" ")
                        i += 1
                    continue
                if source[i] == "'":
                    out.append(" ")
                    i += 1
                    break
                out.append(" ")
                i += 1
        else:
            out.append(c)
            i += 1
    return "".join(out)


def find_sites(relpath, lines):
    """Yield (line_number, method, receiver) for every .Focus( call site."""
    depth = 0
    stack = []  # (method_name, depth_at_open_brace)
    pending = None  # (method_name, paren_depth)
    for lineno, line in enumerate(lines, 1):
        if pending is None:
            m = METHOD_START.match(line)
            if m and "=" not in line.split("(")[0]:
                name = m.group(2)
                pd = line.count("(") - line.count(")")
                pending = (name, pd)
        else:
            pending = (pending[0], pending[1] + line.count("(") - line.count(")"))
        if pending is not None and pending[1] <= 0:
            if "{" in line:
                stack.append((pending[0], depth + 1))
                pending = None
            elif ";" in line:
                pending = None  # expression-bodied method: no block to track
        for fm in FOCUS_CALL.finditer(line):
            method = stack[-1][0] if stack else "(top-level)"
            yield lineno, method, fm.group(1)
        depth += line.count("{") - line.count("}")
        while stack and depth < stack[-1][1]:
            stack.pop()


def main():
    violations = []
    total = 0
    for root, dirs, files in os.walk(os.path.join(ROOT, "Nuvio")):
        dirs[:] = sorted(d for d in dirs if d not in ("obj", "bin"))
        for name in sorted(files):
            if not name.endswith(".cs"):
                continue
            path = os.path.join(root, name)
            relpath = os.path.relpath(path, ROOT).replace(os.sep, "/")
            with open(path, encoding="utf-8-sig") as handle:
                stripped = strip_code(handle.read())
            for lineno, method, receiver in find_sites(relpath, stripped.splitlines()):
                total += 1
                if any(relpath.startswith(d) for d in F0_DIRS):
                    violations.append(
                        "%s:%d: F0 .Focus( in a focus-forbidden layer "
                        "(%s in %s)" % (relpath, lineno, method, receiver)
                    )
                elif (relpath, method, receiver) not in ALLOWLIST:
                    violations.append(
                        "%s:%d: unallowlisted .Focus( (%s in %s) — add it "
                        "to tools/check-focus-guard.py deliberately, with "
                        "review" % (relpath, lineno, method, receiver)
                    )
    if violations:
        print("focus guard FAILED: %d violation(s), %d site(s) scanned" % (len(violations), total))
        for violation in violations:
            print("  " + violation)
        return 1
    print("focus guard OK: %d site(s), all allowlisted; F0 clean" % total)
    return 0


if __name__ == "__main__":
    sys.exit(main())
