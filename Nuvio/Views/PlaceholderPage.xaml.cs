using Nuvio.Input;
using Nuvio.Services;
using Nuvio.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace Nuvio.Views
{
    /// <summary>"This screen lands in Milestone N" page (removed by M8, when no placeholders remain).</summary>
    internal sealed partial class PlaceholderPage : Page, IArrivalFocus
    {
        private readonly PlaceholderViewModel _viewModel = new PlaceholderViewModel();

        public PlaceholderPage()
        {
            InitializeComponent();
            DataContext = _viewModel;
        }

        protected override void OnNavigatedTo(Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _viewModel.Load(e.Parameter as PlaceholderNavigation);
            // Arrival focus (fresh or back) is owned by the router's
            // SettleArrivalFocus, which calls TryFocusArrival / TryFocusKey on
            // this page — nothing focuses here.
        }

        // ---------- Arrival focus (IArrivalFocus, router-owned) ----------

        /// <summary>Fresh- and back-arrival target: the Back button (the
        /// page's only control). Called by the router's SettleArrivalFocus,
        /// which retries when this returns false.</summary>
        public bool TryFocusArrival()
        {
            return BackButton.Focus(FocusState.Programmatic);
        }

        /// <summary>Back-arrival target: the Back button as well.</summary>
        public bool TryFocusKey(string key)
        {
            return TryFocusArrival();
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            var frame = Frame;
            if (frame != null && frame.CanGoBack) frame.GoBack();
            else MainPage.Current.Navigation.NavigateHome();
        }
    }
}
