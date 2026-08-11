using System.Windows;

namespace SyncSAW.App;

public partial class ConfirmationDialog : Window
{
    private ConfirmationDialog(
        Window owner,
        string title,
        string message,
        string confirmText)
    {
        InitializeComponent();
        Owner = owner;
        Title = title;
        TitleTextBlock.Text = title;
        MessageTextBlock.Text = message;
        ConfirmButton.Content = confirmText;
    }

    public static bool Show(
        Window owner,
        string title,
        string message,
        string confirmText) =>
        new ConfirmationDialog(owner, title, message, confirmText).ShowDialog() == true;

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
