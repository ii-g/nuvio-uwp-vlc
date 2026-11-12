using Nuvio.Input;
using Nuvio.Services;
using Nuvio.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace Nuvio.Views
{
    /// <summary>"This screen lands in Milestone N" page (removed by M8, when no placeholders remain).</summary>
    internal sealed partial class PlaceholderPage : FocusablePage
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
            // Sync page: the arrival target (Back button) exists in XAML at
            // navigate time, so the content is ready now.
            RaiseContentReady();
            // Arrival focus (fresh or back) is owned by the router's
            // SettleArrivalFocus, which calls TryFocusArrival / TryFocusKey on
            // this page — nothing focuses here.
        }

        // ---------- Arrival focus (IArrivalFocus, router-owned) ----------

        /// <summary>Fresh- and back-arrival target: the Back button (the
        /// page's only control). Called by the router's SettleArrivalFocus,
        /// which retries when this returns false.</summary>
        public override bool TryFocusArrival()
        {
            return BackButton.Focus(FocusState.Programmatic);
        }

        /// <summary>Back-arrival target: the Back button as well.</summary>
        public override bool TryFocusKey(string key)
        {
            return TryFocusArrival();
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            // Through the navigation service so the sidebar guard is armed
            // before the back navigation (same as the controller Back path).
            MainPage.Current.Navigation.GoBackOrHome();
        }
    }
}
