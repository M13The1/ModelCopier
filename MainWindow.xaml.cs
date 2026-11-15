using System.Windows;
using System.Windows.Controls;
using ModelCopier.ViewModels;

namespace ModelCopier
{
    public partial class MainWindow : Window
    {
        private bool _hadTextWhileEditing = false;
        private bool _suppressWarning = false;

        public MainWindow()
        {
            InitializeComponent();
        }

        private void OnSuffixGotFocus(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                _hadTextWhileEditing = !string.IsNullOrWhiteSpace(vm.TargetFolderSuffix);
            }
        }

        private void OnSuffixTextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox tb && !string.IsNullOrWhiteSpace(tb.Text))
            {
                _hadTextWhileEditing = true;
            }
        }

        private void OnSuffixLostFocus(object sender, RoutedEventArgs e)
        {
            if (_suppressWarning) return;

            if (DataContext is MainViewModel vm && sender is TextBox tb)
            {
                // If the user deleted the suffix from the text box
                if (_hadTextWhileEditing && string.IsNullOrWhiteSpace(tb.Text))
                {
                    _suppressWarning = true;
                    try
                    {
                        MessageBox.Show(
                            "Having a suffix is helpful in avoiding clash with live Tekla Structures model when both are opened.\n\n" +
                            "It is strongly recommended to specify a suffix (e.g., QC, RevA, Backup) so the backup model can be identified and opened safely alongside the active model.",
                            "Suffix Recommended",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    }
                    finally
                    {
                        _suppressWarning = false;
                    }
                }

                _hadTextWhileEditing = !string.IsNullOrWhiteSpace(tb.Text);
            }
        }
    }
}
