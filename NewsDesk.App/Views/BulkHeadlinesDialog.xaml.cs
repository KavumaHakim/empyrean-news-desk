using System.Windows;
using System.Windows.Controls;
using NewsDesk.Core.Models;

namespace NewsDesk.App.Views;

public partial class BulkHeadlinesDialog : Window
{
    public List<(string Title, string Caption)> Headlines => HeadlineListText.Parse(ListBox.Text);

    public BulkHeadlinesDialog(string currentText)
    {
        InitializeComponent();
        ListBox.Text = currentText;
        ListBox.CaretIndex = ListBox.Text.Length;
        ListBox.Focus();
        UpdateCount();
    }

    private void ListBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateCount();

    private void UpdateCount()
    {
        if (CountText == null) return;
        var items = Headlines;
        int withCaption = items.Count(i => i.Caption.Length > 0);
        CountText.Text = $"{items.Count} headline(s), {withCaption} with a caption";
    }

    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
