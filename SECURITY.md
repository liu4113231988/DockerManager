# 安全说明

DockerManager WPF 通过托管 SSH/K8s 客户端连接远程系统，不需要开放无认证的 Docker TCP 2375 端口，也不需要在远程部署 Agent，更不运行 `docker.exe` / `ssh.exe` / `kubectl.exe`。

- **凭据**：本应用只保存显示名称、Docker SSH URI、Docker/K8s Context 和 kubeconfig 路径；不保存私钥或 kubeconfig 内容。配置位于 `%APPDATA%\DockerManager\connections.json`。
- **记住的密码**：可选功能。勾选“记住密码”后，密码用 Windows DPAPI（`DataProtectionScope.CurrentUser`）加密后存于 `%APPDATA%\DockerManager\credentials.json`，**不以明文落盘**，只有同一 Windows 用户可以解密。服务器拒绝该密码时会自动清除；删除 `credentials.json` 即可全部作废。
- **主机密钥**：首次连接时先独立核对 SSH 主机指纹；确认后的指纹保存在 `%APPDATA%\DockerManager\trusted-ssh-hosts.json`。不要禁用指纹校验。
- **访问权限**：拥有 Docker socket 的用户通常对远程主机有很高的操作权限；K8s 凭据应启用最小权限 RBAC。
- **日志**：日志与详情可能含有敏感信息；导出时请注意文件存放位置，不要公开分享。
- **发布**：任何从 GitHub Actions 下载的 EXE 都应确认来自你授权的仓库和构建工作流。
- **测试状态**：WPF 版属于 MVP，尚未在真实生产环境完成长期验证，不建议未经验证直接操作关键生产服务。

如发现安全问题，请不要在公开 issue 中粘贴密钥或真实服务器凭据。
