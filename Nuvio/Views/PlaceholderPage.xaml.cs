using Nuvio.Services;
using Nuvio.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace Nuvio.Views
{
    /// <summary>"This screen lands in Milestone N" page (removed by M8, when no placeholders remain).</summary>
    internal sealed partial class PlaceholderPage : Page
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
            BackButton.Focus(FocusState.Programmatic);
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            var frame = Frame;
            if (frame != null && frame.CanGoBack) frame.GoBack();
            else MainPage.Current.Navigation.NavigateHome();
        }
    }
}
