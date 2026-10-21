using System.Windows;
using System.Windows.Controls;
using DockerManager.Wpf.Models;

namespace DockerManager.Wpf.Views;

public partial class ConnectionDialog : Window
{
    public ConnectionProfile? Profile { get; private set; }

    public ConnectionDialog(ConnectionProfile? existing = null)
    {
        InitializeComponent();
        if (existing is null) return;
        Title = "编辑连接";
        NameBox.Text = existing.Name;
        KindBox.SelectedIndex = (int)existing.Kind;
        TargetBox.Text = existing.Kind == ConnectionKind.DockerSsh ? existing.DockerHost : existing.Context;
        ConfigBox.Text = existing.Kind == ConnectionKind.Kubernetes ? existing.Kubeconfig : existing.DockerSocketPath;
        Profile = existing;
    }

    private void KindBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TargetBox is null || ConfigBox is null || TargetLabel is null || ConfigLabel is null) return;
        switch (KindBox.SelectedIndex)
        {
            case 0:
                TargetLabel.Text = "SSH 地址（ssh://user@host[:port]）";
                TargetBox.Text = "ssh://user@192.168.1.100";
                ConfigLabel.Text = "远端 Docker Socket 路径";
                ConfigBox.Text = "/var/run/docker.sock";
                ConfigBox.IsEnabled = true;
                break;
            case 1:
                TargetLabel.Text = "Docker Context 名称（docker context ls）";
                TargetBox.Text = "";
                ConfigLabel.Text = "此模式已经停止维护";
                ConfigBox.Text = "";
                ConfigBox.IsEnabled = false;
                break;
            case 2:
                TargetLabel.Text = "Kubernetes Context 名称（留空使用当前上下文）";
                TargetBox.Text = "";
                ConfigLabel.Text = "kubeconfig 文件路径（留空为 ~/.kube/config）";
                ConfigBox.Text = "";
                ConfigBox.IsEnabled = true;
                break;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var target = TargetBox.Text.Trim();
        var kind = (ConnectionKind)KindBox.SelectedIndex;
        if (name.Length == 0 || (kind != ConnectionKind.Kubernetes && target.Length == 0))
        {
            MessageBox.Show(this, "连接名称和必要的目标地址不能为空。", "连接配置");
            return;
        }
        if (kind == ConnectionKind.DockerContext)
        {
            MessageBox.Show(this, "旧版 Docker Context 依赖 docker.exe，请重新添加 Docker SSH 连接。", "连接配置");
            return;
        }
        if (kind == ConnectionKind.DockerSsh &&
            (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme != "ssh" || string.IsNullOrEmpty(uri.Host)
             || (uri.UserInfo?.Contains(':') ?? false)))
        {
            MessageBox.Show(this, "Docker SSH 地址必须为 ssh://user@host 格式。", "连接配置");
            return;
        }
        Profile = new ConnectionProfile
        {
            Id = Profile?.Id ?? Guid.NewGuid(), Name = name, Kind = kind,
            DockerHost = kind == ConnectionKind.DockerSsh ? target : "",
            Context = kind == ConnectionKind.DockerSsh ? "" : target,
            Kubeconfig = kind == ConnectionKind.Kubernetes ? ConfigBox.Text.Trim() : "",
            DockerSocketPath = kind == ConnectionKind.DockerSsh ? ConfigBox.Text.Trim() : "/var/run/docker.sock"
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
