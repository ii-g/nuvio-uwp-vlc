using System;
using System.ComponentModel;
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
            // The page has no parent yet at construction time, so the inherited FlowDirection
            // still reads as the LTR default here. Set it explicitly from the language
            // setting, so the padding and hero alignment below (which read FlowDirection)
            // use the real direction instead of always taking the LTR branch.
            FlowDirection = AppSettings.IsRightToLeft(shell.Settings.AppLanguage)
                ? FlowDirection.RightToLeft
                : FlowDirection.LeftToRight;
            _viewModel = new HomeViewModel(shell.Stremio, shell.Progress, shell.Settings, shell.Navigation);
            DataContext = _viewModel;
            // Page content starts after the sidebar; in RTL the start edge is the right one.
            // Web content insets are 8.5rem (136 physical px) at the start and 4rem
            // (64 physical px) at the end; XAML on Xbox is scaled 200%, so those are
            // 68 and 32 epx from the screen edge. The sidebar is an overlay now (it no
            // longer takes a grid column), so the pad is the full inset: 68 at the
            // start (clear of the collapsed 48 epx strip), 32 at the end.
            ContentStack.Padding = FlowDirection == FlowDirection.RightToLeft
                ? new Thickness(32, 0, 68, 0)
                : new Thickness(68, 0, 32, 0);
            // Hero content sits on the row's start edge (RTL: right), like the web rows:
            // the logo/title/description were physically left-aligned, so the hero read
            // as LTR while the rows start at the right. The dots sit at the flow end
            // (web: inset-inline-end). Meta/genre/button rows already flow RTL.
            var rtl = FlowDirection == FlowDirection.RightToLeft;
            HeroLogo.HorizontalAlignment = rtl ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            HeroTitle.TextAlignment = rtl ? TextAlignment.Right : TextAlignment.Left;
            HeroDescription.TextAlignment = rtl ? TextAlignment.Right : TextAlignment.Left;
            HeroDots.HorizontalAlignment = rtl ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            _heroTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HeroIntervalMs) };
            _heroTimer.Tick += OnHeroTimerTick;
            _backdropTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(BackdropDebounceMs) };
            _backdropTimer.Tick += OnBackdropTimerTick;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.HeroItems.CollectionChanged += (s, e) => UpdateDots();
            _viewModel.ContinueRefreshed += OnContinueRefreshed;
            GotFocus += OnGotFocus;
        }

        protected override async void OnNavigatedTo(Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
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
        // it, and the focused row sits ~16% down the page. The framework's own
        // bring-into-view only guarantees the element is just inside the viewport,
        // so the highlight raced across the screen and every move looked like a jump.
        private void AnchorFocusedElement(DependencyObject focused)
        {
            var element = focused as UIElement;
            if (element == null) return;
            if (IsDescendantOf(focused, HeroSection))
            {
                // Hero: the page belongs at the top (web: [data-scroll-top]).
                _lastAnchoredRow = null;
                Scroller.ChangeView(null, 0, null, false);
                return;
            }
            // The row the focused card belongs to (null when focus is not in a row).
            var row = FindAncestorGridView(focused);
            // BringIntoView alignment ratios are fractions of the viewport's physical
            // extent (0 = left/top, 1 = right/bottom): on build 155 the 0.01 ratio
            // pinned the card to the LEFT, not the right. The web UI pins the focused
            // card to the row's start edge, which is the physical right in RTL, so the
            // ratio is 0.99 there (a hair inside the edge, for the 1.06 focus zoom)
            // and 0.01 in LTR. The direction is read at runtime from the row itself.
            var rtl = row != null
                ? row.FlowDirection == FlowDirection.RightToLeft
                : FlowDirection == FlowDirection.RightToLeft;
            var horizontalRatio = rtl ? 0.99 : 0.01;
            // One scroll per move: re-aligning the page vertically on every horizontal
            // move made the page bob while the row glided (the "two scrolls" feeling).
            // The 16% pin applies only when the focused row changes; inside a row the
            // page does not move vertically at all (NaN = only scroll if not visible).
            var verticalRatio = double.NaN;
            if (row == null || !ReferenceEquals(row, _lastAnchoredRow))
            {
                verticalRatio = 0.16;
                _lastAnchoredRow = row;
            }
            var options = new BringIntoViewOptions
            {
                AnimationDesired = true,
                HorizontalAlignmentRatio = horizontalRatio,
                VerticalAlignmentRatio = verticalRatio,
            };
            element.StartBringIntoView(options);
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
