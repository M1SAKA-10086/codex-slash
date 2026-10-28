$ErrorActionPreference = 'Stop'
if (Get-Process CodexSlash -ErrorAction SilentlyContinue) {
    throw '请先从系统托盘退出 Codex Slash，再重新构建。'
}
$taskRoot = $PSScriptRoot
$taskScratch = Join-Path ([System.IO.Path]::GetTempPath()) ('CodexSlash-build-' + [Guid]::NewGuid().ToString('N'))
$taskObj = (Join-Path $taskScratch 'obj') + [System.IO.Path]::DirectorySeparatorChar
$taskBin = (Join-Path $taskScratch 'bin') + [System.IO.Path]::DirectorySeparatorChar
& dotnet publish (Join-Path $taskRoot 'src\CodexSlash.csproj') -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:NuGetAudit=false "-p:BaseIntermediateOutputPath=$taskObj" "-p:BaseOutputPath=$taskBin" -o $taskRoot
if ($LASTEXITCODE -ne 0) { throw '构建失败。' }
Write-Host "构建成功：$taskRoot\CodexSlash.exe"
