using System;
using Nuvio.Models;
using Nuvio.Services;
using Nuvio.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace Nuvio.Views
{
    /// <summary>
    /// "View All": one catalog as a grid, genre filters, infinite scroll.
    /// Ported from views/catalog.js. NavigationCacheMode is Disabled: the page is
    /// rebuilt from its CatalogNavigation parameter every time, like the web view
    /// re-load()s on each push.
    /// </summary>
    internal sealed partial class CatalogPage : Page
    {
        private const double NearBottomPx = 600;

        private readonly FocusScrollHelper _scrollHelper = new FocusScrollHelper();
        private CatalogViewModel _viewModel;
        private ScrollViewer _gridScroller;
        /// <summary>Last card opened into Details from a catalog, kept outside
        /// the page instance (the page is rebuilt on every navigation) so
        /// back-navigation can restore focus to the opened card instead of the
        /// first card. Guarded by the catalog key: a different catalog never
        /// restores another catalog's card. Cleared once the restore runs.</summary>
        internal static string LastOpenedMetaId;
        internal static string LastOpenedCatalogKey;

        private bool _navigatedBack;
        private string _catalogKey;

        public CatalogPage()
        {
            NavigationCacheMode = NavigationCacheMode.Disabled;
            InitializeComponent();
            ApplyFlowDirection(AppSettings.EffectiveIsRightToLeft());
            Loaded += OnLoaded;
            GotFocus += OnGotFocus;
        }

        /// <summary>Same re-assert as HomePage: the frame can set the hosted page's
        /// FlowDirection after construction, so the direction is re-derived from the
        /// stored language here, in OnNavigatedTo and on Loaded.</summary>
        private void ApplyFlowDirection(bool rtl)
        {
            FlowDirection = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ApplyFlowDirection(AppSettings.EffectiveIsRightToLeft());
            // The grid's internal scroller only exists once the GridView is laid out.
            _gridScroller = FocusScrollHelper.FindScrollViewer(CatalogGridView);
            if (_gridScroller != null) _gridScroller.ViewChanged += OnGridScrollerViewChanged;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ApplyFlowDirection(AppSettings.EffectiveIsRightToLeft());
            _navigatedBack = e.NavigationMode == NavigationMode.Back;
            var shell = MainPage.Current;
            var navigation = e.Parameter as CatalogNavigation;
            _catalogKey = navigation == null ? null : navigation.Type + "|" + navigation.Id + "|" + navigation.TransportUrl;
            _viewModel = new CatalogViewModel(shell.Stremio, shell.Progress, shell.Navigation, navigation);
            DataContext = _viewModel;
            _viewModel.FirstPageLoaded += OnFirstPageLoaded;
            await _viewModel.LoadAsync().ConfigureAwait(true);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            _scrollHelper.Stop();
            if (_gridScroller != null)
            {
                _gridScroller.ViewChanged -= OnGridScrollerViewChanged;
                _gridScroller = null;
            }
        }

        private void OnFirstPageLoaded(object sender, EventArgs e)
        {
            // Web focus.restore(el): first card, unless focus is already inside the page.
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            if (focused != null && FocusScrollHelper.IsDescendantOf(focused, this)) return;
            // On the way back from Details, prefer the card that was opened
            // (the shell's navigation suppression already covers the reload
            // race); first card when there is nothing to restore.
            if (_navigatedBack && TryFocusOpenedCard()) return;
            FocusFirstCard();
        }

        /// <summary>Focuses the card opened into Details on the way back, when
        /// it is on the loaded first page of this same catalog. Returns false
        /// when there is nothing to restore (different catalog, or the card sits
        /// on a page not loaded yet) so the caller falls back to the first card.</summary>
        private bool TryFocusOpenedCard()
        {
            var metaId = LastOpenedMetaId;
            if (string.IsNullOrEmpty(metaId) || LastOpenedCatalogKey != _catalogKey) return false;
            for (var i = 0; i < CatalogGridView.Items.Count; i++)
            {
                var card = CatalogGridView.Items[i] as CatalogCardItem;
                if (card == null || card.Meta == null || card.Meta.Id != metaId) continue;
                var container = CatalogGridView.ContainerFromIndex(i) as GridViewItem;
                if (container == null)
                {
                    CatalogGridView.ScrollIntoView(CatalogGridView.Items[i]);
                    CatalogGridView.UpdateLayout();
                    container = CatalogGridView.ContainerFromIndex(i) as GridViewItem;
                }
                if (container != null && container.Focus(FocusState.Programmatic))
                {
                    LastOpenedMetaId = null;
                    LastOpenedCatalogKey = null;
                    return true;
                }
                return false;
            }
            return false;
        }

        private void FocusFirstCard()
        {
            if (CatalogGridView.Items.Count == 0) return;
            CatalogGridView.UpdateLayout();
            var container = CatalogGridView.ContainerFromIndex(0) as GridViewItem;
            if (container != null) container.Focus(FocusState.Programmatic);
        }

        private void GenreChip_Click(object sender, RoutedEventArgs e)
        {
            var chip = (sender as Button)?.DataContext as GenreChip;
            if (chip == null || _viewModel == null) return;
            _viewModel.SetGenre(chip.Name);
        }

        private void CatalogGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var card = e.ClickedItem as CatalogCardItem;
            if (card != null && card.Meta != null)
            {
                LastOpenedMetaId = card.Meta.Id;
                LastOpenedCatalogKey = _catalogKey;
                _viewModel.OpenTitle(card.Meta);
            }
        }

        private void OnGridScrollerViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            // More pages when the scroller gets within 600 px of the bottom (web catalog.js).
            var viewer = _gridScroller;
            if (viewer == null || _viewModel == null || viewer.ExtentHeight <= 0) return;
            if (viewer.VerticalOffset + viewer.ViewportHeight > viewer.ExtentHeight - NearBottomPx)
            {
                _viewModel.RequestLoadMore();
            }
        }

        private void OnGotFocus(object sender, RoutedEventArgs e)
        {
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            if (focused == null) return;
            var item = FocusScrollHelper.FindAncestorGridViewItem(focused);
            if (item == null) return;
            var grid = ItemsControl.ItemsControlFromItemContainer(item) as GridView;
            if (grid != CatalogGridView || _viewModel == null) return;
            _viewModel.MaybeLoadMore(grid.IndexFromContainer(item));
            _scrollHelper.KeepGridItemInView(grid, item);
        }
    }

    /// <summary>Genre chip states, ported from web .chip: selected is surface-3
    /// (#29FFFFFF) with white text and a transparent border; unselected is the
    /// surface (#0FFFFFFF) with text-2 and the --border hairline. (The old code
    /// used a solid accent fill for selected, which the web never does.)</summary>
    internal sealed class ChipSelectedBackgroundConverter : Windows.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return value is bool && (bool)value
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x29, 0xFF, 0xFF, 0xFF))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF));
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    internal sealed class ChipSelectedBorderConverter : Windows.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return value is bool && (bool)value
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    internal sealed class ChipSelectedForegroundConverter : Windows.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return value is bool && (bool)value
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF));
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
