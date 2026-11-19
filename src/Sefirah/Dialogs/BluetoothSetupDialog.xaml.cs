using Sefirah.Data.Models;
using Sefirah.ViewModels.Dialogs;

namespace Sefirah.Dialogs;

public sealed partial class BluetoothSetupDialog : ContentDialog
{
    public BluetoothSetupViewModel ViewModel { get; }

    public BluetoothSetupDialog(PairedDevice phone)
    {
        ViewModel = new BluetoothSetupViewModel(phone, Hide);
        InitializeComponent();
        Root.DataContext = ViewModel;

        // One click on the page is enough: the dialog starts discovering as soon as it opens.
        Opened += (_, _) => ViewModel.StartSetupCommand.Execute(null);
    }

    private void CloseDialogButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CancelCommand.Execute(null);
    }
}
