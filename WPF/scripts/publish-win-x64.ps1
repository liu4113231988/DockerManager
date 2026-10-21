#Requires -Version 5.1
param([ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $root 'DockerManager.Wpf\DockerManager.Wpf.csproj'
$output = Join-Path $root "release\$Runtime"
dotnet publish $project -c Release -r $Runtime --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:PublishTrimmed=false -o $output
if ($LASTEXITCODE -ne 0) { throw '发布失败，确认已安装 .NET 10 SDK。' }
Write-Host "输出文件：$output\DockerManager.exe"
Write-Host '注意：EXE 内置 .NET 运行时，且不依赖 docker.exe / kubectl.exe / ssh.exe；需要可用 SSH 密钥和 K8s kubeconfig。'
