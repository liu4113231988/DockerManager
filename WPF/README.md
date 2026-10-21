# DockerManager WPF — Windows 远程容器管理器

用 **C# / WPF / .NET 10** 编写的桌面应用，不需要 Python、pip 或 Docker Desktop 图形界面。

## 功能

- **Docker**：连接远端 Docker Engine（`ssh://user@host` 或已有 Docker Context），查看全部容器、运行状态、镜像、详情；启动、停止、重启容器。
- **Docker Compose**：基于 Docker Engine 的 `com.docker.compose.project`、`com.docker.compose.service` 标签自动识别项目，查看每个项目下的容器、日志、详情，并进行**容器级**操作。不依赖本地 Compose YAML，也无需在远端额外部署 Agent；当前版本**不支持**整项目 `up/down`。
- **Kubernetes**：使用本机 kubeconfig/context 访问集群，列出所有命名空间的 Pod/容器，查看每个容器的日志、Pod 的 JSON 详情；此版本不执行 K8s 工作负载写操作。
- **通用**：保存多个连接、切换连接、筛选、读取最近 N 行日志、实时跟踪日志、停止跟踪、导出日志。

## 环境要求

- Windows 10/11 x64，或 Windows ARM64（在对应架构发布）。
- 开发或本地构建：**.NET 10 SDK**，在 Windows 上构建 WPF。
- 使用自包含 EXE 时：**不需要额外安装 .NET 运行时**。
- 管理 Docker 时：需要 **Docker CLI** (`docker.exe`) 与 **OpenSSH Client** (`ssh.exe`) 能在 PATH 中运行；Docker CLI 可以来自 Docker Desktop 或独立安装。
- 管理 K8s 时：需要 **kubectl.exe** 在 PATH 中运行以及有效的 kubeconfig。
- Docker 远端须运行 Docker Engine，且远程 SSH 账号有 Docker socket 访问权限。

> 本应用不会在配置中保存密码、私钥或 kubeconfig 内容。`%APPDATA%\DockerManager\connections.json` 只保存连接的显示名称、主机 URI、上下文和文件路径；请保护你的 SSH 私钥与 kubeconfig。不要开放未经 TLS 验证的 Docker TCP 2375 端口。

## 直接运行源码（Windows）

安装 .NET 10 SDK，然后在 `WPF` 文件夹中执行：

```powershell
dotnet run --project .\DockerManager.Wpf\DockerManager.Wpf.csproj
```

或运行 `WPF\scripts\build.ps1` 完成构建和基础测试。

## 生成独立 EXE（Windows）

双击 `WPF\scripts\publish-win-x64.bat`，或执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish-win-x64.ps1
```

生成到 `WPF\release\win-x64\DockerManager.exe`。这是自包含单文件发布，不依赖系统的 .NET Runtime，但仍需 Docker CLI / kubectl 和连接所需的 SSH 认证。

GitHub Actions 也配置了 Windows 构建 + 发布，运行成功后可在 workflow 的 **Artifacts** 中下载 EXE。

## 快速配置 Docker SSH

先在 Windows PowerShell 验证 SSH 非交互登录，然后验证 Docker CLI：

```powershell
ssh user@my-server 'docker ps'
docker --host ssh://user@my-server ps -a
```

建议先在 Windows 的 `~/.ssh/config` 中配置服务器别名、密钥路径和 `BatchMode yes`，并将服务器公钥可信指纹加入已知主机。首次 SSH 主机验证应在终端完成，避免图形程序里隐藏的交互提示造成命令卡住。

在应用中点 **添加**，选择 `Docker SSH` 并填写 `ssh://user@my-server`。应用通过 `docker --host ssh://...` 访问，不会把服务器的 Docker socket 暴露到公网。

如已有 Docker Context，可以直接选择 `Docker Context`，输入 `docker context ls` 显示的名称。

## 快速配置 Kubernetes

先确认 kubectl 可连接：

```powershell
kubectl config get-contexts
kubectl --context your-context get pods -A
```

在应用中新增 `Kubernetes` 连接，输入 context 名称（留空使用当前 context）及可选的 kubeconfig 路径；需要对应集群的 RBAC 读取权限。

## 注意事项

1. 在本地删除一个“连接”只删除本地配置，不会删除 Docker 容器或 Kubernetes 资源。
2. Docker/Compose 停止、启动、重启的是**当前选中的单个容器**，不是整个 Compose 项目。
3. 当前日志窗口最多保留最近约 500,000 字符，以防实时高频日志使程序占用过多内存。
4. Docker SSH 推荐密钥认证；本程序不保存和输入 SSH 密码。密码式认证可能在 GUI 中挂起直到超时。
5. 本源码创建环境为 Linux，**无法在当前环境直接运行或验证 Windows WPF 桌面 UI**；Windows GitHub Actions 工作流和本地 Windows 构建脚本提供编译与冒烟测试途径。
