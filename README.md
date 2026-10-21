# DockerManager

Windows 远程容器管理器。**当前主版本为 C# / WPF / .NET 10**，不依赖 Python。

源码：[`WPF/DockerManager.Wpf`](WPF/DockerManager.Wpf/) ｜ [安装、运行、发布及连接说明](WPF/README.md)。

支持 Docker SSH / Docker Context、Docker Compose 项目分组、Kubernetes Pod 列表、实时日志、筛选、详情和 Docker 容器的启动、停止及重启。

在 Windows 10/11 安装 .NET 10 SDK 后：

```powershell
dotnet run --project .\WPF\DockerManager.Wpf\DockerManager.Wpf.csproj
```

生成自包含 EXE：执行 `WPF\scripts\publish-win-x64.bat`。EXE 不需 Python，但连接 Docker/K8s 需要本机 docker、OpenSSH 和/或 kubectl CLI。

GitHub Actions 在 Windows 上编译并发布 `DockerManager.exe` 构建附件。

**状态：MVP。当前尚未经 Windows 实机与真实远端集群端到端验证，请先在测试环境验证。**