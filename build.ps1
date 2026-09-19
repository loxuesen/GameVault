# 构建脚本
#
# 先发布单文件 exe，再用 Inno Setup 编译安装包。
# 用法：  pwsh -File build.ps1
# 需要：  .NET SDK 8+，Inno Setup 6（路径见下面的 $InnoCompiler）

param(
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64',
    [string] $InnoCompiler = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$src = Join-Path $root 'src'
$publish = Join-Path $src 'publish'
$dist = Join-Path $root 'dist'

Write-Host '==> 清理旧输出'
Remove-Item $publish, $dist -Recurse -Force -ErrorAction SilentlyContinue

Write-Host '==> 发布单文件 exe（自带 .NET 运行库）'
dotnet publish (Join-Path $src 'GameVault.csproj') `
    -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish 失败' }

Copy-Item (Join-Path $root 'README.md') $publish -Force

Write-Host '==> 打包便携版 zip'
$portable = Join-Path $dist 'GameVault-Portable'
New-Item -ItemType Directory -Force -Path $portable | Out-Null
Copy-Item "$publish\*" $portable -Force
$zip = Join-Path $dist 'GameVault-1.0.0-Portable.zip'
Compress-Archive -Path $portable -DestinationPath $zip -CompressionLevel Optimal

Write-Host '==> 编译安装包'
if (-not (Test-Path $InnoCompiler)) {
    Write-Warning "找不到 Inno Setup：$InnoCompiler，跳过安装包。"
} else {
    & $InnoCompiler (Join-Path $root 'installer\一切游戏管理家.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup 编译失败' }
}

Write-Host ''
Write-Host '==> 完成，产物在 dist\：'
Get-ChildItem $dist -File | ForEach-Object { '    {0}  ({1:N1} MB)' -f $_.Name, ($_.Length / 1MB) }
