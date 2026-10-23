using System.Windows;
using System.Windows.Controls;

namespace DockerManager.Wpf.Views;

/// <summary>
/// Per-session SSH password prompt. The password is only persisted when the user opts in.
/// Values are copied into plain fields on OK so callers on other threads never touch WPF controls.
/// </summary>
public partial class PasswordDialog : Window
{
    public string Password { get; private set; } = "";

    public bool Remember { get; private set; }

    public PasswordDialog(string destination)
    {
        InitializeComponent();
        Title = "SSH 密码";
        MessageText.Text = $"请输入 {destination} 的登录密码。\n" +
                           "密码仅用于本次连接；勾选“记住密码”后会用 Windows 当前用户加密保存在本机（不明文落盘）。";
        PasswordBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (PasswordBox.Password.Length == 0)
        {
            MessageBox.Show(this, "密码不能为空；如不想使用密码请选择取消。", Title);
            return;
        }
        // Snapshot the controls while still on the UI thread.
        Password = PasswordBox.Password;
        Remember = RememberBox.IsChecked == true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
