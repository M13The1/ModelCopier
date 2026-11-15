using System.Windows;
using ModelCopier.ViewModels;

namespace ModelCopier.Views
{
    public partial class InquireBackupsDialog : Window
    {
        public InquireBackupsDialog(InquireBackupsViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }

        private void OnClose(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
