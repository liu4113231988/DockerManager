using System.Windows;
using System.Windows.Controls;

namespace DockerManager.Wpf.Views;

/// <summary>Per-session SSH password prompt. The password is never persisted.</summary>
public partial class PasswordDialog : Window
{
    public string Password => PasswordBox.Password;

    public PasswordDialog(string destination)
    {
        InitializeComponent();
        Title = "SSH 密码";
        MessageText.Text = $"请输入 {destination} 的登录密码。\n密码仅用于本次连接，不会被保存或写入配置文件。";
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
