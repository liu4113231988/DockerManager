using System.Diagnostics;
using System.Text;

namespace DockerManager.Wpf.Services;

public sealed record CommandResult(int ExitCode, string Output, string Error)
{
    public void ThrowOnFailure(string commandName)
    {
        if (ExitCode != 0)
        {
            var details = string.IsNullOrWhiteSpace(Error) ? Output : Error;
            throw new InvalidOperationException($"{commandName} 失败 (exit {ExitCode})：\n{details.Trim()}");
        }
    }
}

public sealed class CommandRunner
{
    private static ProcessStartInfo Build(string executable, IReadOnlyList<string> args)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in args) info.ArgumentList.Add(argument);
        return info;
    }

    public async Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> args,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var process = new Process { StartInfo = Build(executable, args) };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"无法启动 {executable}，请确认已安装并添加到 PATH。", ex);
        }

        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(source.Token);
            var stderr = process.StandardError.ReadToEndAsync(source.Token);
            await process.WaitForExitAsync(source.Token);
            return new CommandResult(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            if (!cancellationToken.IsCancellationRequested)
                throw new TimeoutException($"{executable} 执行超时（{timeout.TotalSeconds:0} 秒）。请检查网络、SSH 或认证配置。");
            throw;
        }
    }

    public async Task StreamAsync(string executable, IReadOnlyList<string> args,
        Action<string> onLine, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = Build(executable, args) };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"无法启动 {executable}，请确认已安装并添加到 PATH。", ex);
        }

        async Task PumpAsync(StreamReader reader, string prefix)
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null) break;
                onLine(prefix + line);
            }
        }

        try
        {
            await Task.WhenAll(
                PumpAsync(process.StandardOutput, ""),
                PumpAsync(process.StandardError, "[stderr] "),
                process.WaitForExitAsync(cancellationToken));
            if (process.ExitCode != 0) onLine($"[日志进程已退出，状态码 {process.ExitCode}]");
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }
}
