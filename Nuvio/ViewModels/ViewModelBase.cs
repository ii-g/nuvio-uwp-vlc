using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Nuvio.ViewModels
{
    /// <summary>MVVM-lite base (architecture D1): plain INotifyPropertyChanged, no framework.</summary>
    internal abstract class ViewModelBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private bool _isLoading;
        private string _errorMessage;

        public bool IsLoading
        {
            get { return _isLoading; }
            set { SetProperty(ref _isLoading, value); }
        }

        public string ErrorMessage
        {
            get { return _errorMessage; }
            set
            {
                if (SetProperty(ref _errorMessage, value)) RaisePropertyChanged(nameof(HasError));
            }
        }

        public bool HasError { get { return !string.IsNullOrEmpty(_errorMessage); } }

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            RaisePropertyChanged(propertyName);
            return true;
        }

        protected void RaisePropertyChanged(string propertyName)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
