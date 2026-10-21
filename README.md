# DockerManager

Windows 远程容器管理工具，**现在优先使用 C# / WPF 版本**。原 Python/Tkinter 原型已移至 `legacy/python/`，不再作为主要应用。

## WPF 版（新开发）

源码：[WPF/DockerManager.Wpf](WPF/DockerManager.Wpf/)  ·  [运行 / 构建 / 配置指南](WPF/README.md)

| 模块 | 已实现（MVP） |
| --- | --- |
| Docker SSH / Docker Context | 多连接、查看全部容器、状态、搜索、详情、单容器启动/停止/重启 |
| Docker Compose | 自动按项目分组、查看 Compose 容器和服务、日志、详情及单容器操作 |
| Kubernetes | kubeconfig/context、跨命名空间 Pod/容器列表、详情、日志 |
| 日志 | 最近 N 行、实时跟踪、手动停止、保存到文件、窗口容量限制 |

桌面程序仅在 Windows 上运行；使用本机 Docker CLI + OpenSSH Client 访问远程 Docker Engine，使用 kubectl 访问 Kubernetes，不必在远程服务器安装 Agent。

### Windows 上启动

安装 **.NET 10 SDK**，在仓库根目录执行：

```powershell
dotnet run --project .\WPF\DockerManager.Wpf\DockerManager.Wpf.csproj
```

若要生成单文件的 **Windows EXE（无需 Python、无需预装 .NET Runtime）**，进入 `WPF` 后运行 `scripts\publish-win-x64.bat`；输出到 `WPF\release\win-x64\DockerManager.exe`。

> EXE 仍依赖 Windows 主机上的 `docker.exe` 或 `kubectl.exe`，以及 `ssh.exe` / kubeconfig 等远程认证基础设施；无需 Docker Desktop 本地 Engine。

### 项目目录

```text
DockerManager/
├── WPF/
│   ├── DockerManager.Wpf/       # WPF 桌面应用、Models、Services、Views
│   ├── DockerManager.Tests/     # 不依赖测试框架的基础 CLI 参数测试
│   ├── scripts/                 # Windows 构建与单文件 EXE 发布
│   └── README.md                # WPF 使用说明
├── legacy/python/               # 旧原型保留备查（不推荐继续使用）
├── .github/workflows/
│   ├── wpf-windows.yml          # Windows 构建、测试、生成 EXE Artifact
│   └── tests.yml                # 旧 Python 测试（仅旧版变动时）
├── SECURITY.md
└── README.md
```

## 验证说明

WPF 的 XML 文件可在 Linux 中进行语法解析检查，但 WPF 本身只能在 Windows 上运行。由于当前工作环境不提供 Windows/.NET SDK，本版没有声称完成 Windows 实机编译或连接远端集群测试。仓库接入 GitHub 后，可通过 Windows GitHub Actions 自动完成构建并上传 EXE。

## 安全提醒

远程 Docker socket 管理权限很高；建议通过 SSH 密钥认证，不要把 Docker 2375 明文端口暴露到网络，Kubernetes 使用最小权限的 kubeconfig。不要把密钥、kubeconfig、令牌、生产日志提交到仓库。详见 [SECURITY.md](SECURITY.md)。
