using System.Windows;
using System.Windows.Controls;

namespace DockerManager.Wpf.Views;

/// <summary>Small prompt window used for creating a Docker network or volume.</summary>
public partial class InputDialog : Window
{
    public string Value => NameBox.Text.Trim();
    public string Driver => (DriverBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
    public bool Checked => ExtraBox.IsChecked == true;

    public InputDialog(string title, string message, string defaultValue, string[] drivers, string? checkLabel = null)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        NameBox.Text = defaultValue;
        foreach (var driver in drivers)
            DriverBox.Items.Add(new ComboBoxItem { Content = driver });
        DriverBox.SelectedIndex = 0;
        if (checkLabel is null)
        {
            ExtraBox.Visibility = Visibility.Collapsed;
            ExtraLabel.Visibility = Visibility.Collapsed;
        }
        else
        {
            ExtraLabel.Text = checkLabel;
        }
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Value.Length == 0)
        {
            MessageBox.Show(this, "名称不能为空。", Title);
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
