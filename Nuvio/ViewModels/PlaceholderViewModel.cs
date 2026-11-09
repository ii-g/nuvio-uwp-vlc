using Nuvio.Services;

namespace Nuvio.ViewModels
{
    /// <summary>Title + message for not-yet-built pages.</summary>
    internal sealed class PlaceholderViewModel : ViewModelBase
    {
        private string _title;
        private string _message;
        private string _milestone;

        public string Title
        {
            get { return _title; }
            set { SetProperty(ref _title, value); }
        }

        public string Message
        {
            get { return _message; }
            set { SetProperty(ref _message, value); }
        }

        public string Milestone
        {
            get { return _milestone; }
            set { SetProperty(ref _milestone, value); }
        }

        public void Load(PlaceholderNavigation navigation)
        {
            Title = navigation != null && !string.IsNullOrEmpty(navigation.Title) ? navigation.Title : string.Empty;
            Message = navigation != null && !string.IsNullOrEmpty(navigation.Message) ? navigation.Message : string.Empty;
            Milestone = navigation != null && !string.IsNullOrEmpty(navigation.Milestone) ? navigation.Milestone : string.Empty;
        }
    }
}
