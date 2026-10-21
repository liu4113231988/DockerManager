# DockerManager WPF（免 CLI 版）

Windows 10/11 原生桌面程序，使用 **C#、WPF、.NET 10**。底层操作直接通过 API 完成，不运行 `docker.exe`、`kubectl.exe`、`ssh.exe`，也不需要在远程 Linux 上安装管理 Agent。Windows 自包含发布后不需要另外安装 .NET Runtime。

## 主要功能

- Docker：SSH 隧道访问远程 Linux 的 Unix Socket，再通过 Docker Engine HTTP API 查询容器、镜像、运行状态、详情、启停/重启、最近 N 行日志及持续跟踪日志。
- Docker Compose：直接读取 Engine 容器的 `com.docker.compose.project` / `.service` 标签，对容器按项目分组；支持项目中的容器级操作。**不执行 `compose up/down`**。
- Kubernetes：官方 `KubernetesClient` 从 kubeconfig 读取当前或指定 context，列举所有 namespace 的 Pod/容器、Pod JSON 详情、流式日志；不执行工作负载写操作。
- 通用：多连接、本地搜索、日志导出、自动取消上一批请求、日志长度限制。

## 连接要求

- Docker SSH：远程主机需要 OpenSSH Server 和 Docker Engine；OpenSSH 必须允许 `direct-streamlocal` Unix Socket 访问。SSH 用户需对 `/var/run/docker.sock` 有权限，亦可自定义 Socket 路径。**该权限等价于高权限主机访问，务必使用可信账号**。
- SSH 主机指纹：使用 Tmds.Ssh 的 OpenSSH `~/.ssh/known_hosts` 校验行为，首次连接陌生服务器时，程序显示主机 SHA256 指纹，要求用户通过服务器控制台或可信管理员独立核对，并明确选择是否信任。确认后的指纹保存在 `%APPDATA%\DockerManager\trusted-ssh-hosts.json`；以后自动校验。指纹变化、撤销或现有 OpenSSH known_hosts 发生冲突时均拒绝连接。使用本机 `~/.ssh` 默认密钥或 OpenSSH config 配置认证；不把密码、私钥内容写入连接配置。**不会自动接受未知主机指纹，也不会禁用密钥校验**。
- Kubernetes：读取已存在的 kubeconfig（默认 `%USERPROFILE%\.kube\config`）。支持标准证书/Token 方式，**故意拒绝** `exec` / `auth-provider` 外部认证机制，避免暗中启动 `gcloud`、`aws` 等 CLI。也会拒绝 `insecure-skip-tls-verify`。其他复杂认证方案、组合多个 KUBECONFIG 文件还未支持。
- 旧版 Docker Context 已停用：升级后请重新添加 `ssh://用户名@服务器[:端口]` 的 Docker SSH 连接。

> 兼容性限制：旧 Linux 上 SSH 算法、服务器不允许 Unix Socket 转发、Docker Socket 权限、受限容器日志驱动、企业 K8s exec 认证都可能导致部分功能不可用。为安全起见，程序不会自动降低安全验证标准。


### 首次连接出现 UntrustedPeer

1. 新版程序首次连接未知主机时会提示 **服务器地址、端口和 SHA256 指纹**。这是 SSH 服务器身份校验，不是 Docker 错误。
2. 请在服务器本地控制台（或通过独立可信的运维渠道）核对主机公钥，例如运行 `ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub`。若 SSH 使用其他主机密钥类型，核对对应的 `.pub` 文件。
3. **完全一致**再点“是”；不一致或无法核对则点“否”。已接受的指纹记录在 `%APPDATA%\DockerManager\trusted-ssh-hosts.json`；不会因连接失败自动忽略身份验证。
4. 如果服务器重装后主机密钥确实发生变化，应经管理员核验后再手工维护可信记录及 `~/.ssh/known_hosts`，不要关闭指纹校验。

## 下载与运行

GitHub Actions 工作流 `WPF Windows Build` 在 Windows 上运行编译、基础测试，并发布自包含单文件 EXE 构建附件。成功后在 Actions 的对应运行记录中下载 `DockerManager-win-x64`；解压运行 `DockerManager.exe`。

开发环境需要 **Windows + .NET 10 SDK**：

```powershell
dotnet run --project .\DockerManager.Wpf\DockerManager.Wpf.csproj
```

或发布：

```powershell
.\scripts\publish-win-x64.bat
```

也支持在 `WPF` 目录运行 `dotnet run --project .\DockerManager.Tests\DockerManager.Tests.csproj` 执行基础测试。

## 测试建议

1. 用非管理员 Windows，确认在没有 Docker、kubectl、OpenSSH CLI 的机器上启动并连接。
2. 在受信任 Linux 上准备有权访问 Docker Socket 的 SSH 用户，验证容器列表和 Compose 分组。
3. 同时测试 TTY / 非 TTY 容器日志，以及取消持续跟踪。
4. 使用证书或 Token kubeconfig 验证 Pod 列表、详情、日志；使用 exec 插件 kubeconfig 验证清晰的拒绝提示。
5. 使用不同 Docker Engine / Kubernetes API Server 版本确认兼容性。

## 安全与状态

`%APPDATA%\DockerManager\connections.json` 仅保存主机地址、context 与 kubeconfig **路径**，不会主动存储密钥或 Token。该路径及对应权限仍是敏感信息，需要保护。Docker API 调用与 SSH 连接由托管库完成，日志跟踪采用取消令牌控制。

**状态：可供 Windows 构建及测试的 MVP；本地代码是在非 Windows 环境中生成，尚未完成 Windows 实机与真实集群的端到端验证。**
