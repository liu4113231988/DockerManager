using System.Windows;
using System.Windows.Controls;

namespace DockerManager.Wpf.Views;

/// <summary>Per-session SSH password prompt. The password is only persisted when the user opts in.</summary>
public partial class PasswordDialog : Window
{
    public string Password => PasswordBox.Password;

    public bool Remember => RememberBox.IsChecked == true;

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
        if (Password.Length == 0)
        {
            MessageBox.Show(this, "密码不能为空；如不想使用密码请选择取消。", Title);
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
