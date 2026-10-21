#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
Set-Location (Resolve-Path (Join-Path $PSScriptRoot '..'))
$proj = '.\DockerManager.Wpf\DockerManager.Wpf.csproj'
dotnet --version
if ($LASTEXITCODE -ne 0) { throw '请先安装 .NET 10 SDK' }
dotnet restore $proj
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore 失败' }
dotnet build $proj --no-restore -c Release
if ($LASTEXITCODE -ne 0) { throw 'dotnet build 失败' }
dotnet run --project '.\DockerManager.Tests\DockerManager.Tests.csproj' -c Release
if ($LASTEXITCODE -ne 0) { throw '测试失败' }
Write-Host '构建完成。可运行 dotnet run --project .\DockerManager.Wpf\DockerManager.Wpf.csproj'
