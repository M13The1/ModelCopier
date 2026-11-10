using ModelCopier.ViewModels;
using System.Windows;

namespace ModelCopier.Views
{
    public partial class AddEditDialog : Window
    {
        private readonly AddEditViewModel _vm;

        public AddEditDialog(AddEditViewModel viewModel)
        {
            InitializeComponent();
            _vm = viewModel;
            DataContext = viewModel;
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            if (_vm.Validate())
            {
                DialogResult = true;
                Close();
            }
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
