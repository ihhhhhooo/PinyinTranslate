param(
    [ValidatePattern('^PinyinTranslate-[A-Za-z0-9-]+$')]
    [string] $OutputName = 'PinyinTranslate-win-x64'
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifactsDir = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts'))
$publishDir = [IO.Path]::GetFullPath((Join-Path $artifactsDir $OutputName))
$zipPath = Join-Path $artifactsDir ($OutputName + '.zip')
$stageDir = Join-Path $artifactsDir ('.stage-' + [guid]::NewGuid().ToString('N'))
$stageZip = Join-Path $artifactsDir ('.stage-' + [guid]::NewGuid().ToString('N') + '.zip')
$backupDir = Join-Path $artifactsDir ('.backup-' + [guid]::NewGuid().ToString('N'))
$publishPattern = '^' + [regex]::Escape($OutputName) + '$'
New-Item -ItemType Directory -Path $artifactsDir -Force | Out-Null
function Assert-SafeArtifactDirectory([string] $target, [string] $expectedLeafPattern) {
    $absolute = [IO.Path]::GetFullPath($target)
    if ((Split-Path -Parent $absolute) -ne $artifactsDir -or (Split-Path -Leaf $absolute) -notmatch $expectedLeafPattern) { throw 'Unexpected artifact path' }
    $parent = Get-Item -LiteralPath $artifactsDir -Force
    if (($parent.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Artifact parent cannot be a reparse point' }
    if (Test-Path -LiteralPath $absolute) {
        $existing = Get-Item -LiteralPath $absolute -Force
        if (($existing.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or -not $existing.PSIsContainer) { throw 'Artifact target is not a regular directory' }
    }
}
Assert-SafeArtifactDirectory $publishDir $publishPattern
Assert-SafeArtifactDirectory $stageDir '^\.stage-[0-9a-f]{32}$'
Assert-SafeArtifactDirectory $backupDir '^\.backup-[0-9a-f]{32}$'
$runningFromOutput = @(Get-Process -Name 'PinyinTranslate.App' -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -and $_.Path.StartsWith($publishDir.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) } catch { $false }
})
if ($runningFromOutput.Count -gt 0) { throw '目标发行目录中的程序正在运行，请指定另一个 OutputName，或先关闭该程序。' }
try {
    New-Item -ItemType Directory -Path $stageDir | Out-Null
    dotnet publish (Join-Path $projectRoot 'src\PinyinTranslate.App\PinyinTranslate.App.csproj') -c Release -r win-x64 --self-contained true -p:RuntimeFrameworkVersion=10.0.14 -p:PublishTrimmed=false -p:PublishAot=false -o $stageDir
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
    # Both executables share the same self-contained .NET runtime and Input DLL.
    # The companion isolates Windows candidate providers from WPF/UIA clients.
    dotnet publish (Join-Path $projectRoot 'src\PinyinTranslate.ImeBridge\PinyinTranslate.ImeBridge.csproj') -c Release -r win-x64 --self-contained true -p:RuntimeFrameworkVersion=10.0.14 -p:PublishTrimmed=false -p:PublishAot=false -o $stageDir
    if ($LASTEXITCODE -ne 0) { throw 'IME bridge publish failed' }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $stageDir -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination $stageDir -Force
    $reportDir = Join-Path $stageDir 'reports'
    New-Item -ItemType Directory -Path $reportDir -Force | Out-Null
    foreach ($report in @('selection-fix.md', 'auto-copy.md', 'acceptance.md', 'ime-prototype.md', 'ime-integration.md', 'ime-recovery.md', 'universal-input.md', 'tab-paste-fix.md', 'tab-paste-comparison.json', 'quality.md', 'decisions.md', 'api-budget.json')) {
        $source = Join-Path $projectRoot ('reports\' + $report)
        if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $reportDir -Force }
    }
    $licenseDir = Join-Path $stageDir 'licenses'
    New-Item -ItemType Directory -Path $licenseDir -Force | Out-Null
    $packageInfo = dotnet nuget locals global-packages --list
    if ($LASTEXITCODE -ne 0 -or $packageInfo -notmatch '^global-packages:\s*(.+?)\s*$') { throw 'Cannot locate NuGet global packages to include license texts' }
    $nugetRoot = $Matches[1]
    $texts = @(
        @('microsoft.netcore.app.runtime.win-x64\10.0.14\LICENSE.TXT', 'dotnet-runtime-LICENSE.txt'),
        @('microsoft.netcore.app.runtime.win-x64\10.0.14\THIRD-PARTY-NOTICES.TXT', 'dotnet-runtime-THIRD-PARTY-NOTICES.txt'),
        @('microsoft.windowsdesktop.app.runtime.win-x64\10.0.14\LICENSE', 'dotnet-windowsdesktop-LICENSE.txt')
    )
    foreach ($entry in $texts) { Copy-Item -LiteralPath (Join-Path $nugetRoot $entry[0]) -Destination (Join-Path $licenseDir $entry[1]) -Force }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses\FlaUI-MIT.txt') -Destination $licenseDir -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses\UIAutomation-Interop-MIT.txt') -Destination $licenseDir -Force
    Compress-Archive -Path (Join-Path $stageDir '*') -DestinationPath $stageZip
    Assert-SafeArtifactDirectory $publishDir $publishPattern
    Assert-SafeArtifactDirectory $stageDir '^\.stage-[0-9a-f]{32}$'
    Assert-SafeArtifactDirectory $backupDir '^\.backup-[0-9a-f]{32}$'
    if (Test-Path -LiteralPath $publishDir) { Move-Item -LiteralPath $publishDir -Destination $backupDir }
    try { Move-Item -LiteralPath $stageDir -Destination $publishDir }
    catch {
        if ((Test-Path -LiteralPath $backupDir) -and -not (Test-Path -LiteralPath $publishDir)) { Move-Item -LiteralPath $backupDir -Destination $publishDir }
        throw
    }
    Move-Item -LiteralPath $stageZip -Destination $zipPath -Force
    if (Test-Path -LiteralPath $backupDir) {
        Assert-SafeArtifactDirectory $backupDir '^\.backup-[0-9a-f]{32}$'
        try { Remove-Item -LiteralPath $backupDir -Recurse -Force }
        catch { Write-Warning "旧发行目录保留在 $backupDir：$($_.Exception.Message)" }
    }
    Write-Output "Created $zipPath"
}
finally {
    if (Test-Path -LiteralPath $stageDir) { Assert-SafeArtifactDirectory $stageDir '^\.stage-[0-9a-f]{32}$'; Remove-Item -LiteralPath $stageDir -Recurse -Force }
    if (Test-Path -LiteralPath $stageZip) { Remove-Item -LiteralPath $stageZip -Force }
}
