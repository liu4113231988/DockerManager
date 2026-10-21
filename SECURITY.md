# 安全说明

DockerManager WPF 通过本地 Docker CLI、OpenSSH 与 kubectl 连接远程系统，不需要开放无认证的 Docker TCP 2375 端口，也不需要在远程部署 Agent。

- **凭据**：本应用只保存显示名称、Docker SSH URI、Docker/K8s Context 和 kubeconfig 路径；不保存密码、私钥或 kubeconfig 内容。配置位于 `%APPDATA%\DockerManager\connections.json`。
- **主机密钥**：首次连接时先通过交互终端验证 SSH 主机指纹，不要禁用 StrictHostKeyChecking。
- **访问权限**：拥有 Docker socket 的用户通常对远程主机有很高的操作权限；K8s 凭据应启用最小权限 RBAC。
- **日志**：日志与详情可能含有敏感信息；导出时请注意文件存放位置，不要公开分享。
- **命令执行**：程序通过 `ProcessStartInfo.ArgumentList` 调用 Docker/kubectl，不通过 shell 执行用户输入。
- **发布**：任何从 GitHub Actions 下载的 EXE 都应确认来自你授权的仓库和构建工作流。
- **测试状态**：WPF 版属于 MVP，当前尚未通过真实 Windows 远程环境的端到端测试，不建议未经验证直接操作关键生产服务。

如发现安全问题，请不要在公开 issue 中粘贴密钥或真实服务器凭据。
