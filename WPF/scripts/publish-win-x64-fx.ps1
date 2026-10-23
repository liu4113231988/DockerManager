#Requires -Version 5.1
# 依赖框架（framework-dependent）单文件发布：体积小，但目标机器必须安装 .NET 10 Desktop Runtime。
param([ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $root 'DockerManager.Wpf\DockerManager.Wpf.csproj'
$output = Join-Path $root "release\$Runtime-fx"
$exe = Join-Path $output 'DockerManager.exe'

dotnet publish $project -c Release -r $Runtime --self-contained false `
  -p:PublishSingleFile=true -p:PublishTrimmed=false -o $output
if ($LASTEXITCODE -ne 0) { throw '发布失败，确认已安装 .NET 10 SDK。' }
if (-not (Test-Path $exe)) { throw "发布结束但未找到输出文件：$exe" }

$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "输出文件：$exe（$size MB）"

# 同一目录里如果出现散落的 dll/runtimeconfig，说明单文件没生效，直接提示而不是静默通过。
$loose = Get-ChildItem $output -File -Filter '*.dll' -ErrorAction SilentlyContinue
if ($loose) { Write-Warning "目录内仍有 $($loose.Count) 个 .dll，单文件打包可能未生效。" }

$desktop = (dotnet --list-runtimes 2>$null) -match '^Microsoft\.WindowsDesktop\.App 10\.'
if (-not $desktop) {
  Write-Warning '本机没有 Microsoft.WindowsDesktop.App 10.x。目标机器需要安装 .NET 10 Desktop Runtime（含 WPF），程序才能启动。'
}
Write-Host '注意：此版本不含 .NET 运行时；目标机器需安装 .NET 10 Desktop Runtime。'
Write-Host '需要免安装运行时请改用 .\scripts\publish-win-x64.ps1（自包含，体积更大）。'
