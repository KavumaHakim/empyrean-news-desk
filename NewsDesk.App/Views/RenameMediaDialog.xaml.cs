using System.Windows;
using System.Windows.Input;

namespace NewsDesk.App.Views;

public partial class RenameMediaDialog : Window
{
    public string NewName => NameBox.Text.Trim();

    public RenameMediaDialog(string currentName)
    {
        InitializeComponent();
        NameBox.Text = currentName;
        NameBox.SelectAll();
        NameBox.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NewName))
        {
            MessageBox.Show(this, "Enter a name for the picture.", "Name required", MessageBoxButton.OK, MessageBoxImage.Information);
            NameBox.Focus();
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Save_Click(sender, e);
            e.Handled = true;
        }
    }
}
