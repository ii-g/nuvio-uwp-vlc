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
using Windows.ApplicationModel.Resources;
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
        private string _lastFocusedKey;
        private GridView _lastAnchoredRow;

        public HomePage()
        {
            NavigationCacheMode = Windows.UI.Xaml.Navigation.NavigationCacheMode.Required;
            InitializeComponent();
            var shell = MainPage.Current;
            _viewModel = new HomeViewModel(shell.Stremio, shell.Progress, shell.Settings, shell.Navigation);
            DataContext = _viewModel;
            // The page has no parent yet at construction time, so the inherited FlowDirection
            // still reads as the LTR default here. Apply the direction from the UI language
            // actually rendered, and re-apply it in OnNavigatedTo from the page's effective
            // FlowDirection once the frame has parented the page — build 162 proved the
            // constructor-time direction is not the rendered one (hero left, dots right,
            // yet the rows render RTL), so a one-time constructor value is not enough.
            ApplyFlowDirection(AppSettings.EffectiveIsRightToLeft());
            _heroTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HeroIntervalMs) };
            _heroTimer.Tick += OnHeroTimerTick;
            _backdropTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(BackdropDebounceMs) };
            _backdropTimer.Tick += OnBackdropTimerTick;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.HeroItems.CollectionChanged += (s, e) => UpdateDots();
            _viewModel.ContinueRefreshed += OnContinueRefreshed;
            GotFocus += OnGotFocus;
        }

        /// <summary>Applies every direction-dependent layout value from one RTL flag: the page
        /// flow, the content insets, and the hero block. The hero must dock to the rows'
        /// start edge (right in RTL) with the same 6 epx start inset as the row's card
        /// padding — in builds 125-162 the hero panel itself was never aligned, so it
        /// floated centered while its text took the LTR branch.</summary>
        private void ApplyFlowDirection(bool rtl)
        {
            FlowDirection = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            // Page content starts after the sidebar; in RTL the start edge is the right one.
            // Web content insets are 8.5rem (136 physical px) at the start and 4rem
            // (64 physical px) at the end; XAML on Xbox is scaled 200%, so those are
            // 68 and 32 epx from the screen edge. The sidebar is an overlay (it sits in
            // the shell grid's start column while the content spans both columns), so
            // the pad is the full inset: 68 at the start (clear of the collapsed 48 epx
            // strip), 32 at the end.
            ContentStack.Padding = rtl
                ? new Thickness(32, 0, 68, 0)
                : new Thickness(68, 0, 32, 0);
            // Hero content sits on the row's start edge (RTL: right), like the web rows,
            // with the row's own 6 epx start inset so the title starts on the same edge
            // as the first card. The dots sit at the flow end (web: inset-inline-end).
            // Meta/genre/button rows already flow RTL.
            HeroContent.HorizontalAlignment = rtl ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            HeroContent.Margin = rtl ? new Thickness(0, 0, 6, 20) : new Thickness(6, 0, 0, 20);
            HeroLogo.HorizontalAlignment = rtl ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            HeroTitle.TextAlignment = rtl ? TextAlignment.Right : TextAlignment.Left;
            HeroDescription.TextAlignment = rtl ? TextAlignment.Right : TextAlignment.Left;
            HeroDots.HorizontalAlignment = rtl ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        }

        protected override async void OnNavigatedTo(Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            // Re-apply from the effective flow direction: by now the frame has parented
            // the page, so FlowDirection is the one the rows actually render in.
            ApplyFlowDirection(FlowDirection == FlowDirection.RightToLeft);
            Log.Write("Home page flow direction: " + (FlowDirection == FlowDirection.RightToLeft ? "RTL" : "LTR"));
            _heroTimer.Start();
            // The page is cached, so this runs once; addons/progress events drive later refreshes.
            if (_loadedOnce) return;
            _loadedOnce = true;
            await _viewModel.LoadAsync().ConfigureAwait(true);
            if (!_focusedOnce && (_viewModel.HasHero || _viewModel.IsEmpty))
            {
                _focusedOnce = true;
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
                _focusedOnce = true;
                FocusFirst();
            }
        }

        private void FocusFirst()
        {
            if (_viewModel.HasHero)
            {
                HeroPlayButton.Focus(FocusState.Programmatic);
                return;
            }
            if (_viewModel.IsEmpty)
            {
                ManageAddonsButton.Focus(FocusState.Programmatic);
            }
            // Otherwise the input router focuses the first focusable element on first input.
        }

        // ---------- Hero ----------

        private void OnHeroTimerTick(object sender, object e)
        {
            // Don't change the title under the user's highlight.
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            if (focused != null && IsDescendantOf(focused, HeroSection)) return;
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
        }

        private void HeroPlayButton_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.PlayHero();
        }

        private void HeroDetailsButton_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.OpenTitle(_viewModel.CurrentHero);
        }

        // Anchor the focused element the way the old web UI did (focus.js): the
        // focused card stays at the row's start edge while the strip glides beneath
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
            if (row != null && item != null)
            {
                AnchorRow(row, item);
            }
            // One vertical move per row change: re-aligning the page on every horizontal
            // move made the page bob while the row glided (the "two scrolls" feeling).
            // The 16% pin applies only when the focused row changes; inside a row the
            // page does not move vertically at all.
            if (row == null || !ReferenceEquals(row, _lastAnchoredRow))
            {
                _lastAnchoredRow = row;
                if (row != null)
                {
                    AnchorPageVertical(row);
                }
            }
        }

        /// <summary>Anchors a focused card to the row's flow-start edge (web
        /// focusInHorizontalRow): right in RTL, left in LTR, one card-padding (6 epx)
        /// inside the edge — the same place the first card rests. The offset is computed
        /// from the card's on-screen rect, so no ratio is guessed: BringIntoView ratios
        /// are fractions of the flow extent, which is why 0.99 pinned the card to the
        /// flow end (the left edge in RTL) on build 162 instead of the start.</summary>
        private void AnchorRow(GridView row, GridViewItem item)
        {
            var viewer = FindScrollViewer(row);
            if (viewer == null || item == null) return;
            var rect = item.TransformToVisual(viewer).TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));
            var rtl = row.FlowDirection == FlowDirection.RightToLeft;
            // The row's own padding: the anchor sits where the first card rests (the web's
            // scroll-padding), and the 1.06 focus zoom only overflows ~2.6 epx past it.
            const double startInset = 6;
            double target;
            if (rtl)
            {
                // Start edge is the physical right; in RTL the offset grows as the content
                // moves right, so the item's right edge moves right with it.
                var anchor = viewer.ViewportWidth - startInset;
                var delta = anchor - rect.Right;
                if (Math.Abs(delta) <= 1) return;
                target = viewer.HorizontalOffset + delta;
            }
            else
            {
                var anchor = startInset;
                var delta = rect.Left - anchor;
                if (Math.Abs(delta) <= 1) return;
                target = viewer.HorizontalOffset + delta;
            }
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
                if (hero != null) ShowBackdrop(hero.Background ?? hero.Poster);
            }
        }

        private void ShowBackdrop(string url)
        {
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
                image = new BitmapImage(new Uri(url));
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
            // Hero cycling: left/right on a hero action button at the edge cycles hero items.
            if ((action == "left" || action == "right") && IsDescendantOf(focused, HeroSection))
            {
                var direction = action == "left" ? FocusNavigationDirection.Left : FocusNavigationDirection.Right;
                var before = FocusManager.GetFocusedElement();
                FocusManager.TryMoveFocus(direction);
                if (ReferenceEquals(FocusManager.GetFocusedElement(), before))
                {
                    var forward = FlowDirection == FlowDirection.RightToLeft ? action == "left" : action == "right";
                    _viewModel.SetHeroIndex(_viewModel.HeroIndex + (forward ? 1 : -1));
                    UpdateDots();
                }
                return true;
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
                var shape = (card.Meta != null ? card.Meta.PosterShape : null) ?? "poster";
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
