using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Nuvio.Core;
using Nuvio.Diagnostics;
using Nuvio.Input;
using Nuvio.Models;
using Nuvio.Services;
using Nuvio.ViewModels;
using Nuvio.Views.Shared;
using Windows.ApplicationModel.Resources;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;

namespace Nuvio.Views
{
    /// <summary>
    /// Home: hero carousel, Continue Watching row, one row per catalog. Ported from views/home.js.
    /// NavigationCacheMode is Required so scroll + focus survive back-navigation.
    /// </summary>
    internal sealed partial class HomePage : Page, IPageInputHandler
    {
        private const int HeroIntervalMs = 9000;
        private const int BackdropDebounceMs = 350;

        private readonly HomeViewModel _viewModel;
        private readonly ResourceLoader _strings = ResourceLoader.GetForViewIndependentUse("Resources");
        private readonly DispatcherTimer _heroTimer;
        private readonly DispatcherTimer _backdropTimer;
        private bool _focusedOnce;
        private bool _loadedOnce;
        private bool _backdropFrontIsA;
        private string _backdropCurrentUrl;
        private string _backdropPendingUrl;
        private GraduatedBackdropBlurBrush _backdropBlurBrush;
        private string _lastFocusedKey;
        private GridView _lastAnchoredRow;
        private bool _rowDirectionLogged;

        public HomePage()
        {
            NavigationCacheMode = Windows.UI.Xaml.Navigation.NavigationCacheMode.Required;
            InitializeComponent();
            _backdropBlurBrush = BackdropBlurRect.Fill as GraduatedBackdropBlurBrush;
            var shell = MainPage.Current;
            _viewModel = new HomeViewModel(shell.Stremio, shell.Progress, shell.Settings, shell.Navigation);
            DataContext = _viewModel;
            // The page has no parent yet at construction time, so the inherited FlowDirection
            // still reads as the LTR default here. Apply the direction from the UI language
            // actually rendered, and re-apply it in OnNavigatedTo and on Loaded from the
            // stored language — build 162 proved the constructor-time direction is not the
            // rendered one (hero left, dots right, yet the rows render RTL), and build 170
            // proved OnNavigatedTo alone does not heal it either: the rows rendered RTL
            // while the hero's constructor-time LTR locals stayed put, so the page's
            // FlowDirection must have flipped after OnNavigatedTo (the frame parents the
            // hosted page). The Loaded re-assert runs after parenting, when the frame has
            // had its say, and re-derives from the stored tag instead of trusting the
            // page's own possibly-decoupled value.
            ApplyFlowDirection(AppSettings.EffectiveIsRightToLeft());
            Loaded += (s, e) => ApplyFlowDirection(AppSettings.EffectiveIsRightToLeft());
            _heroTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HeroIntervalMs) };
            _heroTimer.Tick += OnHeroTimerTick;
            _backdropTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(BackdropDebounceMs) };
            _backdropTimer.Tick += OnBackdropTimerTick;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.HeroItems.CollectionChanged += (s, e) => UpdateDots();
            _viewModel.ContinueRefreshed += OnContinueRefreshed;
            GotFocus += OnGotFocus;
        }

        /// <summary>Sets the page flow from one RTL flag. The hero block and the content
        /// insets follow the page's rendered FlowDirection through bindings (Self), not
        /// through one-time assignments here: build 170 proved the flag can be right
        /// while constructor-time assignments do not render (hero LTR, dots right,
        /// padding LTR), so the hero must read the direction the page actually
        /// renders in. A local assignment here would also clobber those bindings.</summary>
        private void ApplyFlowDirection(bool rtl)
        {
            FlowDirection = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        }

        protected override async void OnNavigatedTo(Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            // Re-derive from the stored language, never from this.FlowDirection: the
            // frame can set the hosted page's FlowDirection after construction (build
            // 170: rows RTL, hero LTR), so reading the page's own value here would copy
            // the decoupled value back onto the hero instead of healing it.
            ApplyFlowDirection(AppSettings.EffectiveIsRightToLeft());
            Log.Write("Home page flow direction: " + (FlowDirection == FlowDirection.RightToLeft ? "RTL" : "LTR"));
            _heroTimer.Start();
            // The page is cached, so this runs once; addons/progress events drive later refreshes.
            if (_loadedOnce) return;
            _loadedOnce = true;
            await _viewModel.LoadAsync().ConfigureAwait(true);
            if (!_focusedOnce && (_viewModel.HasHero || _viewModel.IsEmpty))
            {
                FocusFirst();
            }
        }

        protected override void OnNavigatedFrom(Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            _heroTimer.Stop();
            _backdropTimer.Stop();
            // Stop any running glide when the page leaves the frame.
            foreach (var state in _smoothScrolls.Values.ToArray()) state.Running = false;
            _smoothScrolls.Clear();
            if (_smoothScrollHandlerAttached)
            {
                _smoothScrollHandlerAttached = false;
                CompositionTarget.Rendering -= OnSmoothScrollRendering;
            }
        }

        private string S(string key, string fallback)
        {
            var text = _strings.GetString(key);
            return string.IsNullOrEmpty(text) ? fallback : text;
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(HomeViewModel.HeroIndex)) UpdateDots();
            if (!_focusedOnce && (_viewModel.HasHero || _viewModel.IsEmpty))
            {
                FocusFirst();
            }
        }

        /// <summary>Moves initial focus into the content (hero play button, or the manage
        /// addons button when the library is empty). Two launch bugs lived here: the
        /// PropertyChanged handler fires synchronously when HasHero flips, before the
        /// layout pass makes the hero button focusable, so a direct Focus() returns
        /// false — and the old code spent the one-shot _focusedOnce flag before calling
        /// FocusFirst, so the focus was never retried, initial focus stayed on
        /// HomeButton, and the sidebar stayed open (build 216). The focus is now
        /// deferred one dispatcher tick (after layout) and the flag is set only when
        /// Focus() actually lands; a miss is retried by the next property change or
        /// by OnNavigatedTo after LoadAsync. It also bails out if the user already
        /// moved focus into the page, so a late tick never yanks focus away.</summary>
        private void FocusFirst()
        {
            // Fire-and-forget: the IAsyncAction is deliberately not captured, so no
            // `var _` shadows a later discard (that shadow caused CS0266 in MainPage).
            Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                if (_focusedOnce) return;
                var focused = FocusManager.GetFocusedElement() as DependencyObject;
                if (focused != null && IsDescendantOf(focused, this))
                {
                    // The user already moved focus into the page: initial focus is done.
                    _focusedOnce = true;
                    return;
                }
                bool moved = false;
                if (_viewModel.HasHero)
                {
                    moved = HeroPlayButton.Focus(FocusState.Programmatic);
                }
                else if (_viewModel.IsEmpty)
                {
                    moved = ManageAddonsButton.Focus(FocusState.Programmatic);
                }
                else
                {
                    // No hero and not empty: nothing to focus here, the input router
                    // focuses the first focusable element on first input.
                    _focusedOnce = true;
                    return;
                }
                if (moved) _focusedOnce = true;
            });
        }

        // ---------- Hero ----------

        private void OnHeroTimerTick(object sender, object e)
        {
            // Don't change the title under the user's highlight.
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            if (focused != null && IsDescendantOf(focused, HeroSection)) return;
            // Freeze the carousel while the sidebar is expanded: the
            // strip's live blur samples the hero backdrop, so a timer flip
            // while the menu is open would change the frosted menu under
            // the user's hands (the change Mohammed saw: focus leaves the
            // hero, the timer flips the slide dark to bright, and the
            // strip's tones jump). The timer keeps ticking; ticks just
            // skip the advance until the strip collapses, so rotation
            // resumes on the next tick after collapse. The manual advance
            // from the hero Details button is untouched — it only runs
            // while focus is on the hero, with the strip collapsed.
            if (MainPage.Current != null && MainPage.Current.IsSidebarExpanded) return;
            _viewModel.AdvanceHero();
            UpdateDots();
        }

        private void UpdateDots()
        {
            HeroDots.Children.Clear();
            for (var i = 0; i < _viewModel.HeroItems.Count; i++)
            {
                var active = i == _viewModel.HeroIndex;
                HeroDots.Children.Add(new Border
                {
                    Width = active ? 13 : 4,
                    Height = 4,
                    CornerRadius = new CornerRadius(2),
                    Background = new SolidColorBrush(active
                        ? Windows.UI.Colors.White
                        : Windows.UI.Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF)),
                });
            }
            // The LB/RB hint only means something when there is more than
            // one slide to flip between.
            var showBumpers = _viewModel.HeroItems.Count > 1;
            LbChip.Visibility = showBumpers ? Visibility.Visible : Visibility.Collapsed;
            RbChip.Visibility = showBumpers ? Visibility.Visible : Visibility.Collapsed;
        }

        private void HeroPlayButton_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.PlayHero();
        }

        /// <summary>Pins the hero carousel to the Verity slide — the reference
        /// slide for the sidebar tones — and reports whether it did. Called by
        /// the shell when the sidebar expands: Mohammed's call is that the menu
        /// always shows the Verity tones (start edge about 115-122, body about
        /// 74-101, far edge about 23, std 36-44), whatever slide is current
        /// when it opens.
        ///
        /// Why the slide, not the tint or the backdrop: the tones follow the
        /// hero slide, and the driver is the hero LOGO, not the backdrop art.
        /// Proved from Mohammed's 1-second video (Hocus Pocus -&gt; Verity while
        /// the menu was open, focus on Library): the backdrop art is pixel-
        /// identical across the flip (mean abs diff about 17 in the art zones,
        /// the same witch image in both frames — the timer advance never calls
        /// ShowBackdrop, so the backdrop stays), yet the menu's start edge
        /// jumps 71 -&gt; 125 (mean abs diff 57). The only thing that changed
        /// was the slide: the dark purple Hocus Pocus logo became the white
        /// Verity script, and its bright halo leaks through the live blur at
        /// the strip's start edge. So pinning the slide to Verity restores the
        /// reference composite exactly in the common case (backdrop as it was,
        /// Verity logo, live rows), with the same blur math — no tint (a
        /// uniform tint can only shift the mean, never the edge band or the
        /// std; build 251 flattened the strip proving it), no painted streak
        /// (banned), and no frozen snapshot (the backdrop brush samples the
        /// live tree and cannot be frozen; a static plate could not reproduce
        /// the live rows below the fold that make up the lower half of the
        /// reference profile).
        ///
        /// Deliberate visible change, disclosed: the hero title, logo,
        /// description, genres and dots jump to Verity at the moment the menu
        /// opens. That is the price of the tones — the tones ARE the Verity
        /// slide — and Mohammed chose it ("always Verity tones"). The backdrop
        /// is deliberately NOT touched: in the reference it was the
        /// then-current slide's art (the witch), and Verity's own backdrop art
        /// is unseen, so swapping it in would gamble the top of the strip
        /// brighter than the reference (top 60-65). The hero auto-rotate is
        /// already frozen while the strip is expanded, so the slide stays on
        /// Verity until the strip collapses; rotation then resumes from Verity
        /// on the next timer tick, and the slide is NOT restored to the
        /// previous one (that would flip the hero a second time for no gain).
        /// No Verity slide in the list (catalog changed) or no hero at all:
        /// logs and leaves the slide alone — the strip then shows the current
        /// slide's tones, frozen by the rotate freeze.</summary>
        public bool PinHeroToReferenceSlide()
        {
            if (_viewModel == null || _viewModel.HeroItems.Count == 0) return false;
            for (var i = 0; i < _viewModel.HeroItems.Count; i++)
            {
                var name = _viewModel.HeroItems[i].Name;
                if (string.IsNullOrEmpty(name)) continue;
                if (name.IndexOf("Verity", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (_viewModel.HeroIndex == i) return true;
                _viewModel.SetHeroIndex(i);
                Log.Write("Sidebar tones: pinned hero to the Verity slide (index " + i + " of " + _viewModel.HeroItems.Count + ") on sidebar expand");
                return true;
            }
            Log.Write("Sidebar tones: no Verity slide in the hero list (" + _viewModel.HeroItems.Count + " items); hero left on the current slide");
            return false;
        }

        private void HeroDetailsButton_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.OpenTitle(_viewModel.CurrentHero);
        }

        // Anchor the focused element the way the old web UI did (focus.js): the
        // card before the focused one stays at the row's start edge while the strip glides beneath
        // it, and the focused row sits ~16% down the page. StartBringIntoView jumps
        // per move instead of gliding, which is why the UI felt fast even at a slower
        // repeat rate — so the anchor now drives the smooth scroller below, a port
        // of the web's smoothScrollTo, and BringIntoView is no longer used here.
        private void AnchorFocusedElement(DependencyObject focused)
        {
            var element = focused as UIElement;
            if (element == null) return;
            if (IsDescendantOf(focused, HeroSection))
            {
                // Hero: the page belongs at the top (web: [data-scroll-top]).
                _lastAnchoredRow = null;
                SmoothScrollTo(Scroller, null, 0);
                return;
            }
            // The row the focused card belongs to (null when focus is not in a row).
            var row = FindAncestorGridView(focused);
            var item = FindAncestorGridViewItem(focused);
            var rowChanged = row == null || !ReferenceEquals(row, _lastAnchoredRow);
            if (row != null && item != null)
            {
                // Within a row the card before the focused one is pinned to the start edge (AnchorRow);
                // entering a row from elsewhere only keeps the
                // card inside the padded viewport (web scrollIntoView) instead of
                // yanking the strip to the anchor.
                if (rowChanged) KeepCardInView(row, item);
                else AnchorRow(row, item);
            }
            // One vertical move per row change: re-aligning the page on every horizontal
            // move made the page bob while the row glided (the "two scrolls" feeling).
            // The 16% pin applies only when the focused row changes; inside a row the
            // page does not move vertically at all.
            if (rowChanged)
            {
                _lastAnchoredRow = row;
                if (row != null)
                {
                    AnchorPageVertical(row);
                }
            }
        }

        /// <summary>Guards the row's flow end: a target offset must never leave a
        /// card half-cut at the end edge. The card straddling the edge is found
        /// among the realized containers — not only the last card: the anchor
        /// pins the focused card to the start edge and the pitch (97 epx) does
        /// not divide the viewport (912 epx), so a middle card settles ~10 epx
        /// cut at the end edge (its title's first letter missing) while the
        /// last card is still far off-screen. If such a card is found, the
        /// target is nudged to the nearer offset where it is fully visible (its
        /// edge flush with the viewport edge — the end padding is 0 by
        /// Mohammed's call in build 241: the 68 epx end pad only disfigured
        /// the last poster) or fully hidden (its edge at the viewport edge).
        /// There is no cap on the nudge (Mohammed's call: "اشيل الحد هذا"):
        /// a wide Continue card can straddle by tens of epx and the straddle
        /// is always resolved, however large the shift. The shift moves the
        /// focused card too, so the direction is chosen with the focused card
        /// in mind: showing the straddler shifts the row toward the start
        /// edge, and when that would push the focused card out of the
        /// viewport the straddler is hidden instead — hiding shifts the row
        /// inward and can never cut the focused card. Which way is safe is
        /// read off the focused card's live rect at the target offset, never
        /// from a fixed threshold. Skipped when no realized card straddles
        /// the edge or the row does not scroll horizontally (the vertical
        /// catalog grid).</summary>
        private double SnapRowEnd(GridView row, ScrollViewer viewer, double target)
        {
            if (viewer.ScrollableWidth <= 0) return target;
            if (row.Items.Count == 0) return target;
            var rtl = row.FlowDirection == FlowDirection.RightToLeft;
            // The straddler at the target offset: walk the realized containers
            // from the far end; at most one slot can cross the edge.
            double straddleLeft = 0;
            double straddleRight = 0;
            var found = false;
            for (var i = row.Items.Count - 1; i >= 0; i--)
            {
                var item = row.ContainerFromIndex(i) as GridViewItem;
                if (item == null) continue;
                var rect = item.TransformToVisual(viewer).TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));
                var physicalLeft = rtl ? viewer.ViewportWidth - rect.Right : rect.Left;
                var physicalRight = rtl ? viewer.ViewportWidth - rect.Left : rect.Right;
                // The item's edges at the target offset: in RTL the offset grows as
                // content moves right, in LTR as it moves left.
                var shift = rtl ? target - viewer.HorizontalOffset : viewer.HorizontalOffset - target;
                var left = physicalLeft + shift;
                var right = physicalRight + shift;
                if (left < 0 && right > 0)
                {
                    straddleLeft = left;
                    straddleRight = right;
                    found = true;
                    break;
                }
            }
            if (!found) return target;
            // Build 241: no end padding at all — the straddling card ends flush with
            // the viewport edge (straddleLeft 0) or hidden whole (straddleRight 0),
            // never half-cut behind a gap.
            const double endPad = 0;
            var toVisible = endPad - straddleLeft;
            var toHidden = -straddleRight;
            var showing = Math.Abs(toVisible) <= Math.Abs(toHidden);
            var nudge = showing ? toVisible : toHidden;
            // No cap (see the doc comment): the straddle is always resolved.
            // But the shift moves the focused card as well — if the chosen
            // shift would push it out of the viewport, take the other shift
            // instead; it moves the focused card inward, so it is always
            // safe. When focus is not in this row there is nothing to
            // protect and the nearer shift is used as-is.
            if (WouldCutFocusedCard(row, viewer, target, nudge))
            {
                nudge = showing ? toHidden : toVisible;
            }
            var snapped = rtl ? target + nudge : target - nudge;
            return Math.Max(0, Math.Min(viewer.ScrollableWidth, snapped));
        }

        /// <summary>True when shifting this row's content by <paramref name="shift"/>
        /// (physical epx, + = content moves right, as SnapRowEnd applies its
        /// nudge) would push the focused card in this row out of the viewport.
        /// The focused card's live visual rect at the target offset is measured
        /// — the template root, so the 1.06 focus zoom and the focus ring are
        /// inside the measured bounds — and no fixed threshold is used: the
        /// headroom is whatever the card actually has. False when focus is not
        /// in this row (nothing in this row to protect) or the shift keeps the
        /// card fully inside.</summary>
        private bool WouldCutFocusedCard(GridView row, ScrollViewer viewer, double target, double shift)
        {
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            if (focused == null) return false;
            if (!ReferenceEquals(FindAncestorGridView(focused), row)) return false;
            var item = FindAncestorGridViewItem(focused);
            if (item == null) return false;
            // The template root carries the focus zoom (ScaleTransform), so its
            // bounds are the card's visual bounds; fall back to the item
            // itself if the template root is not reachable.
            FrameworkElement visual = null;
            if (VisualTreeHelper.GetChildrenCount(item) > 0)
                visual = VisualTreeHelper.GetChild(item, 0) as FrameworkElement;
            visual = visual ?? item;
            var rect = visual.TransformToVisual(viewer).TransformBounds(new Rect(0, 0, visual.ActualWidth, visual.ActualHeight));
            var rtl = row.FlowDirection == FlowDirection.RightToLeft;
            var physicalLeft = rtl ? viewer.ViewportWidth - rect.Right : rect.Left;
            var physicalRight = rtl ? viewer.ViewportWidth - rect.Left : rect.Right;
            var offsetShift = rtl ? target - viewer.HorizontalOffset : viewer.HorizontalOffset - target;
            var left = physicalLeft + offsetShift + shift;
            var right = physicalRight + offsetShift + shift;
            return left < 0 || right > viewer.ViewportWidth;
        }

        /// <summary>Tidies a row that is already where it should be: if a card
        /// is half-cut at the end edge from an earlier offset, scrolls it to the
        /// nearer fully-visible or fully-hidden offset (SnapRowEnd).</summary>
        private void TidyRowEnd(GridView row, ScrollViewer viewer)
        {
            var tidied = SnapRowEnd(row, viewer, viewer.HorizontalOffset);
            if (Math.Abs(tidied - viewer.HorizontalOffset) > 1) SmoothScrollTo(viewer, tidied, null);
        }

        /// <summary>Keeps a newly entered row's focused card inside the padded viewport
        /// (web scrollIntoView's row part): scrolls only when the card sticks out past
        /// the row padding, never yanks it to the anchor. The anchor is for moves
        /// inside the row (AnchorRow). The pads are 0 epx at the flow end (build 241:
        /// no end padding — the last card ends flush or hidden whole) and the
        /// hero-aligned inset at the flow start (RowStartAnchorInset: 108 epx RTL /
        /// 26 epx LTR — the same place the first card rests, rowHome padding).
        /// The offset signs mirror AnchorRow: in RTL the offset grows as content
        /// moves right, in LTR as it moves left. The end is also tidied (SnapRowEnd)
        /// so a leftover offset never leaves a card half-cut at the end edge.</summary>
        private void KeepCardInView(GridView row, GridViewItem item)
        {
            var viewer = FindScrollViewer(row);
            if (viewer == null || item == null) return;
            var rect = item.TransformToVisual(viewer).TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));
            var rtl = row.FlowDirection == FlowDirection.RightToLeft;
            // Physical edges (see AnchorRow): TransformToVisual is RTL-origin in RTL,
            // so rect.Left/Right are the start/end edges there, not the physical ones.
            var physicalLeft = rtl ? viewer.ViewportWidth - rect.Right : rect.Left;
            var physicalRight = rtl ? viewer.ViewportWidth - rect.Left : rect.Right;
            // The start pad is the hero-aligned inset (RowStartAnchorInset),
            // i.e. the row padding the first card rests at — a card past it
            // is pulled back to the hero edge.
            var startPad = FlowDirectionToThicknessConverter.RowStartAnchorInset(rtl);
            const double endPad = 0;
            double? target = null;
            if (rtl)
            {
                if (physicalLeft < endPad) target = viewer.HorizontalOffset + (endPad - physicalLeft);
                else if (physicalRight > viewer.ViewportWidth - startPad) target = viewer.HorizontalOffset + ((viewer.ViewportWidth - startPad) - physicalRight);
            }
            else
            {
                if (physicalLeft < startPad) target = viewer.HorizontalOffset + (physicalLeft - startPad);
                else if (physicalRight > viewer.ViewportWidth - endPad) target = viewer.HorizontalOffset + (physicalRight - (viewer.ViewportWidth - endPad));
            }
            if (target.HasValue)
            {
                SmoothScrollTo(viewer, SnapRowEnd(row, viewer, target.Value), null);
            }
            else
            {
                // The focused card is already inside, but the row's end may still be
                // half-cut from an earlier offset: tidy it without moving the card.
                TidyRowEnd(row, viewer);
            }
        }

        /// <summary>Anchors a row after an in-row focus move — but not on the
        /// focused card itself: on the card just before it toward the flow
        /// start (index k-1), whose start edge lands on the hero content's
        /// start edge (RowStartAnchorInset: 108 epx RTL / 26 epx LTR inside
        /// the track edge) — the same place the first card rests (rowHome
        /// padding). The focused card then rests one pitch inside the hero
        /// edge, with the previous card fully visible at the edge instead
        /// of sliding under the sidebar. Mohammed's call on build 292:
        /// with focus on Primetime (index 2) Halloween (index 1, the first
        /// fully-visible card from the start side) must sit exactly on the
        /// hero edge — pinning the focused card itself put Halloween 97 epx
        /// past the edge, at the sidebar seam, and the row visibly lost the
        /// hero alignment as soon as it scrolled (at rest, index 0, it was
        /// perfect). For index 0 there is no previous card: the target is
        /// the rest offset 0. The offset is computed from the anchored
        /// card's on-screen rect, so no ratio is guessed and wide Continue
        /// cards (176 epx) work the same way: BringIntoView ratios are
        /// fractions of the flow extent, which is why 0.99 pinned the card
        /// to the flow end (the left edge in RTL) on build 162 instead of
        /// the start.</summary>
        private void AnchorRow(GridView row, GridViewItem item)
        {
            var viewer = FindScrollViewer(row);
            if (viewer == null || item == null) return;
            // The rows inherit the page direction, so this line proves in the Xbox log
            // which way the rows actually render (build 170: Arabic labels with an LTR
            // layout left the user unable to tell whether the rows were RTL or LTR).
            if (!_rowDirectionLogged)
            {
                _rowDirectionLogged = true;
                Log.Write("Row flow direction: " + (row.FlowDirection == FlowDirection.RightToLeft ? "RTL" : "LTR"));
            }
            double target;
            if (!TryComputeRowAnchorTarget(row, item, viewer, out target)) return;
            // The anchor target is used exactly, without SnapRowEnd's
            // end-edge nudge — the nudge is what broke the alignment. The
            // pitch (97 epx) does not divide the viewport (912 epx), so at
            // the exact anchor a card always straddles the end edge by the
            // same amount as at rest (28 epx for the 88 epx poster cards),
            // and the nudge moved the strip by that straddle on every
            // in-row move, leaving the anchored card 28 epx off the hero
            // edge. At rest the anchor held only because the offset clamp
            // (0) blocked the nudge — which is exactly why the row looked
            // aligned at rest and lost it as soon as focus moved. The sliver
            // the nudge was hiding is the same 28 epx the approved rest
            // state already shows at the end edge (the anchor offsets are
            // exact multiples of the pitch from rest, so the end-edge
            // pattern is identical), so skipping the nudge here changes
            // nothing at the end edge — it only restores the anchor. The
            // guard stays active where the offset is not anchor-determined:
            // row entry (KeepCardInView) and TidyRowEnd.
            _anchorGlideViewers.Add(viewer);
            _anchorSettlePasses[viewer] = 0;
            SmoothScrollTo(viewer, target, null);
        }

        /// <summary>Computes the offset that puts the anchor card's start edge
        /// on the hero edge: the card before the focused one toward the flow
        /// start (index k-1), or the rest offset 0 when focus is on the first
        /// card. The anchor card is measured live (TransformToVisual reports
        /// RTL-origin coordinates — build 180 — so the physical edges are
        /// flipped back before use), which also makes the target correct
        /// mid-glide: the delta is read off the card's current on-screen
        /// rect, so retargeting during the 110 ms repeat only moves the
        /// destination, never the start point. Returns false when the row is
        /// already on the anchor (|delta| ≤ 1 epx — tidying there would apply
        /// SnapRowEnd's nudge and push the card off it) or when the anchor
        /// card is not realized (the caller then leaves the row alone; the
        /// focused card's neighbour is adjacent and on screen, so this is
        /// only a virtualization fallback).</summary>
        private bool TryComputeRowAnchorTarget(GridView row, GridViewItem item, ScrollViewer viewer, out double target)
        {
            target = 0;
            var index = row.IndexFromContainer(item);
            if (index < 0) return false;
            if (index == 0)
            {
                // No previous card: the anchor is the rest position.
                if (Math.Abs(viewer.HorizontalOffset) <= 1) return false;
                target = 0;
                return true;
            }
            var anchorItem = row.ContainerFromIndex(index - 1) as GridViewItem;
            if (anchorItem == null) return false;
            var rect = anchorItem.TransformToVisual(viewer).TransformBounds(new Rect(0, 0, anchorItem.ActualWidth, anchorItem.ActualHeight));
            var rtl = row.FlowDirection == FlowDirection.RightToLeft;
            // TransformToVisual reports RTL-origin coordinates (x measured from the
            // flow-start edge): build 180 proved it — sidebarX=0 for a strip visibly at
            // 912..960 — so in RTL rect.Left is the card's START edge (physical right)
            // and rect.Right its END edge (physical left), the reverse of the physical
            // reading the previous anchor used. Flip to physical edges so the anchor reads the
            // same in both directions.
            var physicalLeft = rtl ? viewer.ViewportWidth - rect.Right : rect.Left;
            var physicalRight = rtl ? viewer.ViewportWidth - rect.Left : rect.Right;
            var startInset = FlowDirectionToThicknessConverter.RowStartAnchorInset(rtl);
            double delta;
            if (rtl)
            {
                // Start edge is the physical right; in RTL the offset grows as the content
                // moves right, so the item's right edge moves right with it.
                delta = (viewer.ViewportWidth - startInset) - physicalRight;
            }
            else
            {
                delta = physicalLeft - startInset;
            }
            if (Math.Abs(delta) <= 1) return false;
            target = viewer.HorizontalOffset + delta;
            return true;
        }

        /// <summary>Settles an anchor glide: build 292's photo showed the row
        /// resting about one pitch short of its anchor after scrolling — the
        /// exponential glide's destination was right, but the strip never
        /// arrived. After a glide issued by AnchorRow snaps to its target,
        /// re-measure the anchor from the live rects and, if the row is still
        /// off by more than 1 epx, re-issue the glide toward the recomputed
        /// target. At most two re-issues per focus move, and only when the
        /// recomputed target is reachable (inside [0, ScrollableWidth] — at
        /// the row's tail the anchor cannot hold and the row is left where
        /// the clamp put it). Only anchor glides are verified: glides from
        /// KeepCardInView/TidyRowEnd deliberately do not yank the row to the
        /// anchor, so they are never in _anchorGlideViewers.</summary>
        private void VerifyRowAnchor(ScrollViewer viewer, double snappedTarget)
        {
            if (viewer == null || !_anchorGlideViewers.Contains(viewer)) return;
            _anchorGlideViewers.Remove(viewer);
            var row = FindAncestorGridView(viewer);
            if (row == null) return;
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            if (focused == null || !ReferenceEquals(FindAncestorGridView(focused), row)) return;
            var item = FindAncestorGridViewItem(focused);
            if (item == null) return;
            double target;
            if (!TryComputeRowAnchorTarget(row, item, viewer, out target)) return;
            if (target < 0 || target > viewer.ScrollableWidth) return;
            if (Math.Abs(target - snappedTarget) <= 1) return;
            int passes;
            _anchorSettlePasses.TryGetValue(viewer, out passes);
            if (passes >= 2) return;
            _anchorSettlePasses[viewer] = passes + 1;
            _anchorGlideViewers.Add(viewer);
            SmoothScrollTo(viewer, target, null);
        }

        /// <summary>Anchors the current row's block at 16% of the page height (web
        /// scrollIntoView), only when the row changes.</summary>
        private void AnchorPageVertical(GridView row)
        {
            var section = FindRowSection(row);
            if (section == null) return;
            var rect = section.TransformToVisual(Scroller).TransformBounds(new Rect(0, 0, section.ActualWidth, section.ActualHeight));
            var viewportHeight = Scroller.ViewportHeight;
            if (viewportHeight <= 0) return;
            var anchor = viewportHeight * 0.16;
            var delta = rect.Top - anchor;
            if (Math.Abs(delta) <= 2) return;
            SmoothScrollTo(Scroller, null, Scroller.VerticalOffset + delta);
        }

        private static FrameworkElement FindRowSection(GridView row)
        {
            // The row's block is the DataTemplate root (header + GridView): the first
            // StackPanel above the GridView. Falls back to the row itself.
            for (var node = VisualTreeHelper.GetParent(row); node != null; node = VisualTreeHelper.GetParent(node))
            {
                var panel = node as StackPanel;
                if (panel != null) return panel;
                if (node is ItemsControl) break;
            }
            return row;
        }

        private static ScrollViewer FindScrollViewer(DependencyObject root)
        {
            if (root == null) return null;
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var viewer = child as ScrollViewer;
                if (viewer != null) return viewer;
                viewer = FindScrollViewer(child);
                if (viewer != null) return viewer;
            }
            return null;
        }

        // ---------------------------------------------------------------------------
        // Smooth scrolling — a port of web/js/core/focus.js smoothScrollTo.
        // The web feels calm because each strip glides continuously toward a destination
        // that is retargeted every frame (exponential approach, tau 105 ms, no animation
        // queue). One loop per ScrollViewer, driven by CompositionTarget.Rendering;
        // retargeting only moves the destination, never restarts the glide.
        // ---------------------------------------------------------------------------

        /// <summary>Per-ScrollViewer glide state (web focus.js pendingScrolls).</summary>
        private sealed class SmoothScrollState
        {
            public ScrollViewer Viewer;
            public double? TargetH;
            public double? TargetV;
            public DateTime LastTime;
            public bool Running;
        }

        private const double SmoothTauMs = 105;
        private const double SmoothSnapEpsilon = 0.6;
        private readonly Dictionary<ScrollViewer, SmoothScrollState> _smoothScrolls = new Dictionary<ScrollViewer, SmoothScrollState>();
        private readonly HashSet<ScrollViewer> _anchorGlideViewers = new HashSet<ScrollViewer>();
        private readonly Dictionary<ScrollViewer, int> _anchorSettlePasses = new Dictionary<ScrollViewer, int>();
        private bool _smoothScrollHandlerAttached;

        /// <summary>Glides a ScrollViewer toward the given offsets (web smoothScrollTo).
        /// Retargeting only moves the destination; a running glide is never restarted.</summary>
        private void SmoothScrollTo(ScrollViewer viewer, double? targetH, double? targetV)
        {
            if (viewer == null) return;
            SmoothScrollState state;
            if (!_smoothScrolls.TryGetValue(viewer, out state))
            {
                state = new SmoothScrollState { Viewer = viewer };
                _smoothScrolls[viewer] = state;
            }
            if (targetH.HasValue) state.TargetH = Math.Max(0, Math.Min(viewer.ScrollableWidth, targetH.Value));
            if (targetV.HasValue) state.TargetV = Math.Max(0, Math.Min(viewer.ScrollableHeight, targetV.Value));
            state.LastTime = DateTime.UtcNow;
            if (!state.Running)
            {
                state.Running = true;
                if (!_smoothScrollHandlerAttached)
                {
                    _smoothScrollHandlerAttached = true;
                    CompositionTarget.Rendering += OnSmoothScrollRendering;
                }
            }
        }

        private void OnSmoothScrollRendering(object sender, object e)
        {
            var now = DateTime.UtcNow;
            var anyRunning = false;
            var snappedHorizontal = new List<KeyValuePair<ScrollViewer, double>>();
            foreach (var pair in _smoothScrolls.ToArray())
            {
                var viewer = pair.Key;
                var state = pair.Value;
                if (viewer == null || !viewer.IsLoaded)
                {
                    state.Running = false;
                    if (viewer != null) _smoothScrolls.Remove(viewer);
                    continue;
                }
                // Web: elapsed clamped to [1, 32] ms per frame.
                var elapsedMs = Math.Min(32, Math.Max(1, (now - state.LastTime).TotalMilliseconds));
                state.LastTime = now;
                var active = false;
                if (state.TargetH.HasValue)
                {
                    var current = viewer.HorizontalOffset;
                    var distance = state.TargetH.Value - current;
                    if (Math.Abs(distance) < SmoothSnapEpsilon)
                    {
                        snappedHorizontal.Add(new KeyValuePair<ScrollViewer, double>(viewer, state.TargetH.Value));
                        viewer.ChangeView(state.TargetH.Value, null, null, true);
                        state.TargetH = null;
                    }
                    else
                    {
                        var next = current + distance * (1 - Math.Exp(-elapsedMs / SmoothTauMs));
                        viewer.ChangeView(next, null, null, true);
                        active = true;
                    }
                }
                if (state.TargetV.HasValue)
                {
                    var current = viewer.VerticalOffset;
                    var distance = state.TargetV.Value - current;
                    if (Math.Abs(distance) < SmoothSnapEpsilon)
                    {
                        viewer.ChangeView(null, state.TargetV.Value, null, true);
                        state.TargetV = null;
                    }
                    else
                    {
                        var next = current + distance * (1 - Math.Exp(-elapsedMs / SmoothTauMs));
                        viewer.ChangeView(null, next, null, true);
                        active = true;
                    }
                }
                if (!active)
                {
                    state.Running = false;
                    _smoothScrolls.Remove(viewer);
                }
                else
                {
                    anyRunning = true;
                }
            }
            if (!anyRunning && _smoothScrollHandlerAttached)
            {
                _smoothScrollHandlerAttached = false;
                CompositionTarget.Rendering -= OnSmoothScrollRendering;
            }
            // Anchor settle: a snapped anchor glide is verified against the
            // live anchor (see VerifyRowAnchor) — a re-issue, if any,
            // re-attaches the Rendering handler through SmoothScrollTo.
            foreach (var snapped in snappedHorizontal)
            {
                VerifyRowAnchor(snapped.Key, snapped.Value);
            }
        }

        private static GridView FindAncestorGridView(DependencyObject element)
        {
            for (var node = element; node != null; node = VisualTreeHelper.GetParent(node))
            {
                var grid = node as GridView;
                if (grid != null) return grid;
            }
            return null;
        }

        // ---------- Backdrop ----------

        private void OnGotFocus(object sender, RoutedEventArgs e)
        {
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            if (focused == null) return;
            AnchorFocusedElement(focused);
            // Dynamic graduated blur: the clear zone follows focus — hero
            // focused keeps the top clear, any row focus pulls the blur up.
            _backdropBlurBrush?.SetRowState(!IsDescendantOf(focused, HeroSection));
            var item = FindAncestorGridViewItem(focused);
            if (item != null)
            {
                var card = item.Content as CatalogCardItem;
                if (card != null && card.Meta != null)
                {
                    _lastFocusedKey = "meta:" + card.Meta.Id;
                    ShowBackdrop(card.Meta.Background);
                    return;
                }
                var entry = item.Content as ProgressEntry;
                if (entry != null)
                {
                    _lastFocusedKey = "cw:" + entry.ContentId;
                    ShowBackdrop(entry.Background);
                    return;
                }
            }
            if (IsDescendantOf(focused, HeroSection))
            {
                _lastFocusedKey = "hero";
                var hero = _viewModel.CurrentHero;
                // No poster fallback: a 300x450 poster blown up 3.2x is the
                // "enlarged" look. ShowBackdrop ignores empty, so the previous
                // backdrop simply stays.
                if (hero != null) ShowBackdrop(hero.Background);
            }
        }

        // Dynamic graduated blur: the mask, its states and its animation
        // live in the one shared definition (Views/Shared/
        // GraduatedBackdropBlurBrush) — driven from OnGotFocus above.

        // Backdrop URLs are normalized once, here, before any storing, so
        // _backdropPendingUrl / _backdropCurrentUrl comparisons stay consistent:
        // MetaHub medium (1280x720) -> large (3840x2160), TMDB w780/w1280 -> w1920.
        private static string UpgradeBackdropUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            url = url.Replace("images.metahub.space/background/medium/",
                              "images.metahub.space/background/large/");
            url = url.Replace("image.tmdb.org/t/p/w780/", "image.tmdb.org/t/p/w1920/")
                     .Replace("image.tmdb.org/t/p/w1280/", "image.tmdb.org/t/p/w1920/");
            return url;
        }

        private void ShowBackdrop(string url)
        {
            url = UpgradeBackdropUrl(url);
            if (string.IsNullOrEmpty(url) || url == _backdropCurrentUrl) return;
            _backdropPendingUrl = url;
            _backdropTimer.Stop();
            _backdropTimer.Start();
        }

        private void OnBackdropTimerTick(object sender, object e)
        {
            _backdropTimer.Stop();
            var url = _backdropPendingUrl;
            if (string.IsNullOrEmpty(url) || url == _backdropCurrentUrl) return;
            Image back = _backdropFrontIsA ? BackdropB : BackdropA;
            Image front = _backdropFrontIsA ? BackdropA : BackdropB;
            BitmapImage image;
            try
            {
                // Decode at the physical width (1920): the 3840-wide large
                // source would otherwise decode fully (~33 MB per layer).
                image = new BitmapImage();
                image.DecodePixelWidth = 1920;
                image.UriSource = new Uri(url);
            }
            catch (Exception ex)
            {
                Log.Error("Backdrop image failed", ex);
                return;
            }
            // Swap only once the new art has actually loaded, so a bad URL keeps the old backdrop.
            image.ImageOpened += (s, args) =>
            {
                if (url != _backdropPendingUrl && url != _backdropCurrentUrl) return;
                _backdropCurrentUrl = url;
                FadeImage(back, 0, 1);
                FadeImage(front, 1, 0);
                _backdropFrontIsA = !_backdropFrontIsA;
            };
            back.Source = image;
        }

        private static void FadeImage(Image image, double from, double to)
        {
            var animation = new DoubleAnimation { From = from, To = to, Duration = TimeSpan.FromMilliseconds(600) };
            Storyboard.SetTarget(animation, image);
            Storyboard.SetTargetProperty(animation, "Opacity");
            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }

        // ---------- Continue Watching ----------

        private void ContinueGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var entry = e.ClickedItem as ProgressEntry;
            if (entry != null) _viewModel.ResumeEntry(entry);
        }

        private void OnContinueRefreshed(object sender, EventArgs e)
        {
            // Focus memory: re-focus the Continue card that had focus before the refresh.
            if (_lastFocusedKey == null || !_lastFocusedKey.StartsWith("cw:", StringComparison.Ordinal)) return;
            var contentId = _lastFocusedKey.Substring(3);
            for (var i = 0; i < ContinueGridView.Items.Count; i++)
            {
                var entry = ContinueGridView.Items[i] as ProgressEntry;
                if (entry != null && entry.ContentId == contentId)
                {
                    var container = ContinueGridView.ContainerFromIndex(i) as Control;
                    if (container != null) container.Focus(FocusState.Programmatic);
                    return;
                }
            }
        }

        private async Task ShowContinueMenuAsync(ProgressEntry entry)
        {
            var dialog = new ContentDialog
            {
                Title = entry.Title ?? string.Empty,
                CloseButtonText = S("Cancel", "Cancel"),
            };
            var panel = new StackPanel { Spacing = 8, MinWidth = 360 };
            string choice = null;
            var detailsButton = new Button { Content = S("GoToDetails", "Go to details"), HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 56 };
            detailsButton.Click += (s, e) => { choice = "details"; dialog.Hide(); };
            panel.Children.Add(detailsButton);
            if (!entry.NextUp)
            {
                var restartButton = new Button { Content = S("StartFromBeginning", "Start from beginning"), HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 56 };
                restartButton.Click += (s, e) => { choice = "restart"; dialog.Hide(); };
                panel.Children.Add(restartButton);
            }
            var removeButton = new Button { Content = S("Remove", "Remove"), HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 56 };
            removeButton.Click += (s, e) => { choice = "remove"; dialog.Hide(); };
            panel.Children.Add(removeButton);
            dialog.Content = panel;
            dialog.Opened += (s, e) => detailsButton.Focus(FocusState.Programmatic);
            var shell = MainPage.Current;
            shell.DialogOpen = true;
            try
            {
                await dialog.ShowAsync().AsTask().ConfigureAwait(true);
            }
            finally
            {
                shell.DialogOpen = false;
            }
            if (choice == "details")
            {
                _viewModel.OpenTitle(new Meta { Id = entry.ContentId, Type = entry.ContentType, Name = entry.Title });
            }
            else if (choice == "restart")
            {
                shell.Navigation.ResumeEntry(entry, restart: true);
            }
            else if (choice == "remove")
            {
                _viewModel.DismissContinueWatching(entry.ContentId);
            }
        }

        // ---------- Catalog rows ----------

        private void CatalogRowGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var grid = sender as GridView;
            var row = grid != null ? grid.DataContext as CatalogRowViewModel : null;
            var card = e.ClickedItem as CatalogCardItem;
            if (card == null) return;
            if (card.IsViewAll)
            {
                _viewModel.OpenCatalog(row);
                return;
            }
            if (card.Meta != null) _viewModel.OpenTitle(card.Meta);
        }

        private void ManageAddonsButton_Click(object sender, RoutedEventArgs e)
        {
            MainPage.Current.Navigation.NavigatePlaceholder("Settings", "Settings lands in Milestone 6.", "Milestone 6", "settings");
        }

        // ---------- Controller input (IPageInputHandler) ----------

        public bool HandleAction(string action, bool repeat)
        {
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            if (focused == null) return false;
            // Up from a hero button goes nowhere: nothing focusable sits above the
            // hero, so the router's XYFocus move carried focus to an unrelated
            // element (build 241: focus landed somewhere unknown, most plausibly a
            // sidebar button, and the strip opened). Swallow it; focus stays on the
            // button.
            if (action == "up" && IsDescendantOf(focused, HeroSection)) return true;
            // Down from a hero button lands on the first card (index 0) of the
            // first row: XYFocus would pick the geometrically nearest card under
            // the buttons (an end card in RTL, since the buttons sit mid-row
            // under the hero's start edge), not the row's first card. When no
            // row has items yet, fall through to the router's XYFocus.
            if (action == "down" && IsDescendantOf(focused, HeroSection))
            {
                if (FocusFirstCardOfFirstRow()) return true;
            }
            // RB / LB flip the hero carousel while focus stays on the hero
            // button: RB advances one slide, LB goes back one, wrapping at
            // both ends (SetHeroIndex wraps). Only while focus sits on the
            // hero — the same scope the old D-pad flip had. The bridge sends
            // "lb"/"rb" edge-triggered only (no hold-repeat), so one press is
            // exactly one slide; the dots, backdrop and auto-rotate timer
            // follow the new slide, as the manual flip always did. RB/LB are
            // unused anywhere else in the native UI (the router ignores them
            // outside the player, and the player is M4), so nothing else
            // loses a binding.
            if ((action == "lb" || action == "rb") && IsDescendantOf(focused, HeroSection))
            {
                if (_viewModel.HeroItems.Count < 2) return true;
                _viewModel.SetHeroIndex(_viewModel.HeroIndex + (action == "rb" ? 1 : -1));
                UpdateDots();
                var hero = _viewModel.CurrentHero;
                if (hero != null) ShowBackdrop(hero.Background);
                _heroTimer.Stop();
                _heroTimer.Start();
                return true;
            }
            // Hero buttons: left/right only moves focus between Play and
            // Details (the pre-build-241 behaviour). Flow-aware: towardStart
            // is right in RTL, left in LTR; towardEnd is the opposite. Play
            // sits at the start edge, Details at the end edge. Right never
            // flips a slide, and left from Details no longer flips one
            // either — slide flipping moved to RB/LB (above), which wraps.
            // Toward the end edge from Details there is nowhere to go, so
            // focus stays on Details. Toward the start edge from Play opens
            // the sidebar from any slide.
            if ((action == "left" || action == "right") && IsDescendantOf(focused, HeroSection))
            {
                var rtl = FlowDirection == FlowDirection.RightToLeft;
                var towardStart = rtl ? action == "right" : action == "left";
                if (IsDescendantOf(focused, HeroPlayButton))
                {
                    if (!towardStart)
                    {
                        HeroDetailsButton.Focus(FocusState.Programmatic);
                        return true;
                    }
                    // Toward the start edge from Play: open the sidebar from
                    // any slide. The old first-slide-only rule left the strip
                    // unreachable from the hero in practice: the carousel
                    // auto-rotates every 9 s and advances by hand, so the
                    // hero is almost never on slide 0. No flip, no wrap.
                    MainPage.Current?.FocusSidebar();
                    return true;
                }
                if (IsDescendantOf(focused, HeroDetailsButton))
                {
                    if (towardStart)
                    {
                        HeroPlayButton.Focus(FocusState.Programmatic);
                        return true;
                    }
                    // Toward the end edge from Details: nowhere to go —
                    // focus stays on Details. (Slide flipping used to live
                    // here; it moved to RB/LB above.)
                    return true;
                }
                return false;
            }
            // Row moves by DOM index, like web focus.js horizontalRowNeighbor: the index
            // is stable while the strip glides, while XYFocus measures moving cards (and
            // the 1.06 focus zoom) and can skip or mis-pick a neighbour mid-glide. Falls
            // through to XYFocus when the neighbour is not realized yet.
            if (action == "left" || action == "right")
            {
                var item = FindAncestorGridViewItem(focused);
                var grid = item != null ? ItemsControl.ItemsControlFromItemContainer(item) as GridView : null;
                if (grid != null)
                {
                    var index = grid.IndexFromContainer(item);
                    var rtl = grid.FlowDirection == FlowDirection.RightToLeft;
                    var step = action == "left" ? (rtl ? 1 : -1) : (rtl ? -1 : 1);
                    var target = index + step;
                    if (target >= 0 && target < grid.Items.Count)
                    {
                        var container = grid.ContainerFromIndex(target) as GridViewItem;
                        if (container != null)
                        {
                            container.Focus(FocusState.Programmatic);
                            return true;
                        }
                    }
                    else
                    {
                        // Past the row's end. Forward stops dead (build 148: the row must
                        // not drop to the row below); toward the sidebar falls through so
                        // the router can move focus into the strip, as in the web UI.
                        var forward = rtl ? action == "left" : action == "right";
                        if (forward) return true;
                    }
                }
            }
            // Y / Menu on a Continue Watching card → the card's choice menu.
            if (action == "y" || action == "menu")
            {
                var item = FindAncestorGridViewItem(focused);
                var entry = item != null ? item.Content as ProgressEntry : null;
                if (entry != null)
                {
                    var _ = ShowContinueMenuAsync(entry);
                    return true;
                }
            }
            return false;
        }

        private static GridViewItem FindAncestorGridViewItem(DependencyObject element)
        {
            for (var node = element; node != null; node = VisualTreeHelper.GetParent(node))
            {
                var item = node as GridViewItem;
                if (item != null) return item;
            }
            return null;
        }

        /// <summary>Focuses the first card (index 0) of the first row with items
        /// (the Continue row when it has items, else the first catalog row).
        /// Returns false when no row has a realized first card, so the caller
        /// falls through to the router's XYFocus.</summary>
        private bool FocusFirstCardOfFirstRow()
        {
            var grid = FindFirstRowGrid();
            if (grid == null || grid.Items.Count == 0) return false;
            var container = grid.ContainerFromIndex(0) as GridViewItem;
            if (container == null)
            {
                grid.ScrollIntoView(grid.Items[0]);
                grid.UpdateLayout();
                container = grid.ContainerFromIndex(0) as GridViewItem;
            }
            if (container == null) return false;
            container.Focus(FocusState.Programmatic);
            return true;
        }

        /// <summary>Finds the first row grid in page order (Continue first, then
        /// catalog rows), skipping grids with no items or no size — a collapsed
        /// section (Continue with no items) has neither.</summary>
        private GridView FindFirstRowGrid()
        {
            return FindFirstRowGrid(ContentStack);
        }

        private static GridView FindFirstRowGrid(DependencyObject root)
        {
            if (root == null) return null;
            var grid = root as GridView;
            if (grid != null && grid.Items.Count > 0 && grid.ActualWidth > 0) return grid;
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var found = FindFirstRowGrid(VisualTreeHelper.GetChild(root, i));
                if (found != null) return found;
            }
            return null;
        }

        private static bool IsDescendantOf(DependencyObject element, DependencyObject ancestor)
        {
            for (var node = element; node != null; node = VisualTreeHelper.GetParent(node))
            {
                if (node == ancestor) return true;
            }
            return false;
        }
    }

    // ---------- Shared value converters used by Views/Shared/CardTemplates.xaml ----------

    /// <summary>Poster/logo URL → BitmapImage with a DecodePixelWidth matched to the poster size (decision D9).</summary>
    internal sealed class PosterImageConverter : Windows.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var url = value as string;
            if (string.IsNullOrEmpty(url)) return null;
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return null;
            var kind = parameter as string ?? "poster";
            int width;
            switch (kind)
            {
                case "landscape": width = 425; break;
                case "square": width = 277; break;
                case "wide": width = 468; break;
                case "logo": width = 398; break;
                default: width = PosterDecodeWidth(); break;
            }
            try
            {
                var image = new BitmapImage();
                image.DecodePixelWidth = width;
                image.UriSource = uri;
                return image;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static int PosterDecodeWidth()
        {
            var size = AppSettings.Current != null ? AppSettings.Current.PosterSize : "standard";
            switch (size)
            {
                case "compact": return 181;
                case "large": return 277;
                case "extraLarge": return 319;
                default: return 234;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>posterShape → card art width in px (CSS: poster 11rem, landscape 20rem, square 13rem; poster size setting scales the poster width).</summary>
    internal sealed class ShapeWidthConverter : Windows.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var shape = (value as string ?? "poster").ToLowerInvariant();
            if (shape == "landscape") return 160;
            if (shape == "square") return 104;
            var size = AppSettings.Current != null ? AppSettings.Current.PosterSize : "standard";
            switch (size)
            {
                case "compact": return 68;
                case "large": return 104;
                case "extraLarge": return 120;
                default: return 88;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>posterShape → card art height in px (poster 2/3, landscape 16/9, square 1/1).</summary>
    internal sealed class ShapeHeightConverter : Windows.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var shape = (value as string ?? "poster").ToLowerInvariant();
            if (shape == "landscape") return 90;
            if (shape == "square") return 104;
            var size = AppSettings.Current != null ? AppSettings.Current.PosterSize : "standard";
            switch (size)
            {
                case "compact": return 102;
                case "large": return 156;
                case "extraLarge": return 180;
                default: return 132;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>Card art height for the focus ring (web: the ring wraps the art only, not the title).</summary>
    internal sealed class CardArtHeightConverter : Windows.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var card = value as CatalogCardItem;
            if (card != null)
            {
                // The View All card has no Meta; it carries the row's shape on PosterShape.
                var shape = card.PosterShape ?? (card.Meta != null ? card.Meta.PosterShape : null) ?? "poster";
                shape = shape.ToLowerInvariant();
                if (shape == "landscape") return 90;
                if (shape == "square") return 104;
                var size = AppSettings.Current != null ? AppSettings.Current.PosterSize : "standard";
                switch (size)
                {
                    case "compact": return 102;
                    case "large": return 156;
                    case "extraLarge": return 180;
                    default: return 132;
                }
            }
            if (value is ProgressEntry) return 99;
            return 132;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    internal sealed class BooleanToVisibilityConverter : Windows.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return value is bool && (bool)value ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    internal sealed class InverseBooleanToVisibilityConverter : Windows.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return value is bool && (bool)value ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    internal sealed class StringToVisibilityConverter : Windows.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    internal sealed class InverseStringToVisibilityConverter : Windows.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    internal sealed class DoubleToVisibilityConverter : Windows.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return value is double && (double)value > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>View All chevron: always a right-pointing chevron, in either direction.
    /// The web's viewAllCard uses icon('chevron-right') unconditionally (no RTL flip in
    /// code or CSS), and the old RTL screenshots show a right-pointing chevron too — the
    /// previous converter's left chevron in RTL was a misreading of build 170, where the
    /// chevron only looked wrong because the whole row was laid out LTR.</summary>
    internal sealed class ViewAllArrowConverter : Windows.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            // E76C = ChevronRight. The glyph is a physical Segoe MDL2 chevron and does
            // not mirror with the page.
            return "\uE76C";
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>Flow direction → physical alignment (XAML has no logical "start"
    /// alignment): RTL docks to the right, LTR to the left. Parameter "invert" flips
    /// it (the hero dots sit at the flow end). Bound to the element's own inherited
    /// FlowDirection, so the hero follows the page's rendered direction instead of
    /// a constructor-time flag — build 170 proved the flag can be right while the
    /// constructor-time assignments do not render (hero LTR, dots right, padding LTR).</summary>
    internal sealed class FlowDirectionToHorizontalAlignmentConverter : Windows.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var rtl = value is FlowDirection && (FlowDirection)value == FlowDirection.RightToLeft;
            if (parameter as string == "invert") rtl = !rtl;
            return rtl ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>Flow direction → text alignment (TextAlignment has no "start" value):
    /// RTL aligns right, LTR aligns left. Bound to the element's own inherited
    /// FlowDirection, like the alignment converter above.</summary>
    internal sealed class FlowDirectionToTextAlignmentConverter : Windows.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var rtl = value is FlowDirection && (FlowDirection)value == FlowDirection.RightToLeft;
            return rtl ? TextAlignment.Right : TextAlignment.Left;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>Flow direction → content insets. Parameter "content": the page padding
    /// (web --content-left/right: 68 at the start, 32 at the end). Parameter "hero":
    /// the hero's start margin — in RTL the web reserves --rtl-safe-start (156 epx:
    /// the expanded sidebar's footprint) so the hero never slides under the open
    /// strip, i.e. 88 past the 68 page pad; in LTR the hero aligns with the first
    /// card (6 past the pad). Parameters "rowHome" and "rowHeader": the home rows'
    /// track padding and title inset — the start pad/inset is RowStartAnchorInset,
    /// so the first card and the row title start on the hero content's start edge;
    /// "row" keeps the old 6 epx start pad for pages with no hero (Search).</summary>
    internal sealed class FlowDirectionToThicknessConverter : Windows.UI.Xaml.Data.IValueConverter
    {
        /// <summary>The home row's start anchor inset: the hero's start margin
        /// (88 epx RTL / 6 epx LTR, the same value as the "hero" branch) plus
        /// the start bleed (20 epx, the same value as the "rowBleed" branch) —
        /// 108 epx RTL / 26 epx LTR inside the track edge. The first card rests
        /// here (rowHome padding) and AnchorRow pins the focused card here, so
        /// the row's start edge sits on the hero content's start edge.</summary>
        internal static double RowStartAnchorInset(bool rtl) => (rtl ? 88 : 6) + 20;

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var rtl = value is FlowDirection && (FlowDirection)value == FlowDirection.RightToLeft;
            if (parameter as string == "hero")
                return rtl ? new Thickness(0, 0, 88, 20) : new Thickness(6, 0, 0, 20);
            if (parameter as string == "divider")
                // Sidebar divider: on the content-facing edge (left in RTL, right in LTR).
                return rtl ? new Thickness(1, 0, 0, 0) : new Thickness(0, 0, 1, 0);
            if (parameter as string == "activeUnderline")
                // Active-item underline Margin: the line sits under the icon column,
                // 6 epx in from the flow-start edge and 3 epx up from the button's
                // bottom edge.
                return rtl ? new Thickness(0, 0, 6, 3) : new Thickness(6, 0, 0, 3);
            if (parameter as string == "row")
                // Horizontal row track padding: the flow-start side carries one
                // card-padding (6 epx); the flow-END side carries none (0) — build
                // 241: Mohammed asked for the end padding to go entirely ("no use
                // at the row's end but to disfigure the poster"), so the last card
                // ends flush with the viewport edge or hidden whole, never half-cut
                // behind a gap. Of the page's 68 epx start inset, 20 epx now
                // sit inside the track (the start-side bleed to the sidebar's
                // inner edge) and the collapsed strip's own 48 epx remain
                // outside it. The vertical catalog grid keeps its own 6 epx padding (its
                // end is the bottom, not a horizontal edge).
                return rtl ? new Thickness(0, 9, 6, 12) : new Thickness(6, 9, 0, 12);
            if (parameter as string == "rowHome")
                // The home rows' track padding: same as "row" except the
                // flow-start pad is RowStartAnchorInset — so the first card
                // rests on the hero content's start edge (x=804 RTL, x=74
                // LTR) instead of 6 epx inside the track edge. The track
                // edge itself does not move (rowBleed still opens it to the
                // sidebar's inner edge); only the rest position moves.
                // Search rows keep "row": that page has no hero to align to.
                return rtl ? new Thickness(0, 9, RowStartAnchorInset(rtl), 12) : new Thickness(RowStartAnchorInset(rtl), 9, 0, 12);
            if (parameter as string == "rowHeader")
                // The row title's start inset: the hero margin's start inset
                // (88 epx RTL / 6 epx LTR — the same value as the "hero"
                // branch), with no bottom margin — so the title starts on
                // the hero content's start edge, on the same edge as the
                // first card.
                return rtl ? new Thickness(0, 0, 88, 0) : new Thickness(6, 0, 0, 0);
            if (parameter as string == "rowBleed")
                // Two-sided bleed for the horizontal row track. The flow-END
                // side is opened by the page's end inset (32 epx), so the
                // track reaches the window edge on that side. The flow-START
                // side is opened by 20 epx — the page's 68 epx start inset
                // minus the collapsed sidebar's 48 epx — so the track edge
                // lands on the sidebar's inner edge (x=912 collapsed, RTL)
                // and a card is never cut 20 epx short of the strip. The
                // home rows' first card rests on the hero edge (716..804 at
                // rest in RTL, rowHome padding — build 2a55b17 moved it off
                // the old 6 epx pad); search rows keep the 6 epx pad. The
                // edge stays fixed at the collapsed strip: the sidebar is an
                // overlay above the content, so expanding it never moves the
                // track. RTL opens left 32 / right 20, LTR opens left 20 /
                // right 32 — the direction comes from the live FlowDirection,
                // never from the language.
                return rtl ? new Thickness(-32, 0, -20, 0) : new Thickness(-20, 0, -32, 0);
            return rtl ? new Thickness(32, 0, 68, 0) : new Thickness(68, 0, 32, 0);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>Continue card progress bar: visible only while unfinished and past the start.</summary>
    internal sealed class ContinueProgressVisibilityConverter : Windows.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var entry = value as ProgressEntry;
            return entry != null && !entry.NextUp && entry.ProgressFraction > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>Picks the poster template, or the View All template for the trailing card.</summary>
    internal sealed class CatalogCardTemplateSelector : DataTemplateSelector
    {
        public DataTemplate PosterTemplate { get; set; }
        public DataTemplate ViewAllTemplate { get; set; }

        protected override DataTemplate SelectTemplateCore(object item)
        {
            var card = item as CatalogCardItem;
            if (card != null && card.IsViewAll) return ViewAllTemplate;
            return PosterTemplate;
        }

        protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
        {
            return SelectTemplateCore(item);
        }
    }
}
