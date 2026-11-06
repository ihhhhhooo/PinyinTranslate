param(
    [ValidatePattern('^PinyinTranslate-[A-Za-z0-9-]+$')]
    [ValidateLength(1, 96)]
    [string] $OutputName = 'PinyinTranslate-win-x64',
    [switch] $Velopack
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifactsDir = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts'))
$publishDir = Join-Path $artifactsDir $OutputName
$releaseDir = Join-Path $artifactsDir ($OutputName + '-Releases')
$zipPath = Join-Path $artifactsDir ($OutputName + '.zip')
$stageDir = Join-Path $artifactsDir ('.stage-' + [guid]::NewGuid().ToString('N'))
$stageRelease = Join-Path $artifactsDir ('.stage-' + [guid]::NewGuid().ToString('N'))
$stageZip = Join-Path $artifactsDir ('.stage-' + [guid]::NewGuid().ToString('N') + '.zip')
$backupDir = Join-Path $artifactsDir ('.backup-' + [guid]::NewGuid().ToString('N'))
$releaseBackup = Join-Path $artifactsDir ('.backup-' + [guid]::NewGuid().ToString('N'))
$packId = 'PinyinTranslate.Desktop'
$channel = 'win-x64'

function Read-ReleaseProperties {
    $document = [Xml.XmlDocument]::new()
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create((Join-Path $projectRoot 'Directory.Build.props'), $settings)
    try { $document.Load($reader) } finally { $reader.Dispose() }
    $result = @{}
    foreach ($name in @('Version', 'VelopackVersion', 'PinnedRuntimeVersion')) {
        $nodes = @($document.SelectNodes('/Project/PropertyGroup/' + $name))
        if ($nodes.Count -ne 1 -or $nodes[0].InnerText -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
            throw "Directory.Build.props must contain exactly one literal stable $name."
        }
        $result[$name] = $nodes[0].InnerText
    }
    return $result
}

function Assert-SafeArtifactDirectory([string] $target, [string] $expectedLeafPattern) {
    $absolute = [IO.Path]::GetFullPath($target)
    if ((Split-Path -Parent $absolute) -ne $artifactsDir -or (Split-Path -Leaf $absolute) -notmatch $expectedLeafPattern) { throw 'Unexpected artifact path.' }
    $parent = Get-Item -LiteralPath $artifactsDir -Force
    if (($parent.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Artifact parent cannot be a reparse point.' }
    if (Test-Path -LiteralPath $absolute) {
        $existing = Get-Item -LiteralPath $absolute -Force
        if (($existing.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or -not $existing.PSIsContainer) { throw 'Artifact target is not a regular directory.' }
    }
}

function Get-SourceState {
    $commit = & git -C $projectRoot rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') { throw 'A committed Git source tree is required for a Velopack release.' }
    $changes = @(& git -C $projectRoot status --porcelain --untracked-files=normal)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect release source state.' }
    return [pscustomobject]@{ Commit = [string]$commit; Clean = $changes.Count -eq 0 }
}

function Install-StagedDirectory([string] $stage, [string] $target, [string] $backup, [string] $targetPattern) {
    Assert-SafeArtifactDirectory $stage '^\.stage-[0-9a-f]{32}$'
    Assert-SafeArtifactDirectory $target $targetPattern
    Assert-SafeArtifactDirectory $backup '^\.backup-[0-9a-f]{32}$'
    if (Test-Path -LiteralPath $target) { Move-Item -LiteralPath $target -Destination $backup }
    try { Move-Item -LiteralPath $stage -Destination $target }
    catch {
        if ((Test-Path -LiteralPath $backup) -and -not (Test-Path -LiteralPath $target)) { Move-Item -LiteralPath $backup -Destination $target }
        throw
    }
    if (Test-Path -LiteralPath $backup) {
        Assert-SafeArtifactDirectory $backup '^\.backup-[0-9a-f]{32}$'
        try { Remove-Item -LiteralPath $backup -Recurse -Force }
        catch { Write-Warning "Old artifact directory remains at $backup." }
    }
}

$properties = Read-ReleaseProperties
$runtimeVersion = $properties.PinnedRuntimeVersion
$toolManifest = Join-Path $projectRoot '.config\dotnet-tools.json'
$source = $null
if ($Velopack) {
    $tools = Get-Content -LiteralPath $toolManifest -Raw | ConvertFrom-Json
    if ($tools.tools.vpk.version -cne $properties.VelopackVersion) { throw 'The local vpk manifest version must match VelopackVersion in Directory.Build.props.' }
    $source = Get-SourceState
}
New-Item -ItemType Directory -Path $artifactsDir -Force | Out-Null
Assert-SafeArtifactDirectory $publishDir ('^' + [regex]::Escape($OutputName) + '$')
foreach ($stage in @($stageDir, $stageRelease)) { Assert-SafeArtifactDirectory $stage '^\.stage-[0-9a-f]{32}$' }
if ($Velopack) { Assert-SafeArtifactDirectory $releaseDir ('^' + [regex]::Escape($OutputName + '-Releases') + '$') }
$runningFromOutput = @(Get-Process -Name 'PinyinTranslate.App', 'PinyinTranslate.ImeBridge' -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -and $_.Path.StartsWith($publishDir.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) } catch { $false }
})
if ($runningFromOutput.Count -gt 0) { throw 'The target artifact directory is in use. Close its application or choose another OutputName.' }

Push-Location $projectRoot
try {
    if ($Velopack) {
        & dotnet tool restore --tool-manifest $toolManifest
        if ($LASTEXITCODE -ne 0) { throw 'Restoring the pinned local vpk tool failed.' }
    }
    New-Item -ItemType Directory -Path $stageDir | Out-Null
    # The app and its isolated IME bridge share one self-contained runtime.
    foreach ($project in @('src\PinyinTranslate.App\PinyinTranslate.App.csproj', 'src\PinyinTranslate.ImeBridge\PinyinTranslate.ImeBridge.csproj')) {
        $publishArguments = @('publish', (Join-Path $projectRoot $project), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
            "-p:RuntimeFrameworkVersion=$runtimeVersion", '-p:PublishTrimmed=false', '-p:PublishAot=false', '-o', $stageDir)
        & dotnet @publishArguments
        if ($LASTEXITCODE -ne 0) { throw "Publish failed for $project." }
    }
    foreach ($required in @('PinyinTranslate.App.exe', 'PinyinTranslate.App.dll', 'PinyinTranslate.App.deps.json', 'PinyinTranslate.App.runtimeconfig.json',
        'PinyinTranslate.ImeBridge.exe', 'PinyinTranslate.ImeBridge.dll', 'PinyinTranslate.ImeBridge.deps.json', 'PinyinTranslate.ImeBridge.runtimeconfig.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $stageDir $required) -PathType Leaf)) { throw "Incomplete application bundle: $required." }
    }
    foreach ($name in @('README.md', 'THIRD-PARTY-NOTICES.md', 'LICENSE', 'RELEASE-NOTES.md')) {
        $file = Join-Path $projectRoot $name
        if ($name -eq 'RELEASE-NOTES.md' -and -not $Velopack -and -not (Test-Path -LiteralPath $file)) { continue }
        Copy-Item -LiteralPath $file -Destination $stageDir -Force
    }
    $reportDir = Join-Path $stageDir 'reports'
    New-Item -ItemType Directory -Path $reportDir -Force | Out-Null
    foreach ($report in @('updates.md', 'selection-bidirectional.md', 'selection-fix.md', 'auto-copy.md', 'acceptance.md', 'ime-prototype.md', 'ime-integration.md', 'ime-recovery.md',
        'universal-input.md', 'tab-paste-fix.md', 'tab-paste-comparison.json', 'quality.md', 'decisions.md', 'api-budget.json')) {
        $reportSource = Join-Path $projectRoot ('reports\' + $report)
        if (Test-Path -LiteralPath $reportSource) { Copy-Item -LiteralPath $reportSource -Destination $reportDir -Force }
        elseif ($Velopack -and $report -eq 'updates.md') { throw 'reports/updates.md is required for the update-enabled distribution.' }
    }
    $licenseDir = Join-Path $stageDir 'licenses'
    New-Item -ItemType Directory -Path $licenseDir -Force | Out-Null
    $packageInfo = & dotnet nuget locals global-packages --list
    if ($LASTEXITCODE -ne 0 -or $packageInfo -notmatch '^global-packages:\s*(.+?)\s*$') { throw 'Cannot locate NuGet runtime license texts.' }
    $nugetRoot = $Matches[1]
    $texts = @(
        @("microsoft.netcore.app.runtime.win-x64\$runtimeVersion\LICENSE.TXT", 'dotnet-runtime-LICENSE.txt'),
        @("microsoft.netcore.app.runtime.win-x64\$runtimeVersion\THIRD-PARTY-NOTICES.TXT", 'dotnet-runtime-THIRD-PARTY-NOTICES.txt'),
        @("microsoft.windowsdesktop.app.runtime.win-x64\$runtimeVersion\LICENSE", 'dotnet-windowsdesktop-LICENSE.txt')
    )
    foreach ($entry in $texts) { Copy-Item -LiteralPath (Join-Path $nugetRoot $entry[0]) -Destination (Join-Path $licenseDir $entry[1]) -Force }
    foreach ($licenseFile in Get-ChildItem -LiteralPath (Join-Path $projectRoot 'licenses') -File) {
        if (($licenseFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Bundled license files cannot be reparse points.' }
        Copy-Item -LiteralPath $licenseFile.FullName -Destination $licenseDir -Force
    }

    if ($Velopack) {
        New-Item -ItemType Directory -Path $stageRelease | Out-Null
        # Pinned vpk's integration check reads PE/IL; it does not launch the app.
        # Keep that static check. No app, installer or updater is executed here.
        $packArguments = @('vpk', '--skip-updates', 'pack', '--packId', $packId, '--packTitle', 'PinyinTranslate',
            '--packVersion', $properties.Version, '--packDir', $stageDir, '--mainExe', 'PinyinTranslate.App.exe',
            '--runtime', 'win-x64', '--channel', $channel, '--delta', 'None', '--outputDir', $stageRelease,
            '--releaseNotes', (Join-Path $projectRoot 'RELEASE-NOTES.md'))
        & dotnet @packArguments
        if ($LASTEXITCODE -ne 0) { throw 'Velopack packaging failed.' }
        $requiredAssets = @("$packId-$($properties.Version)-$channel-full.nupkg", "$packId-$channel-Portable.zip", "$packId-$channel-Setup.exe", "releases.$channel.json", "assets.$channel.json")
        foreach ($name in $requiredAssets) {
            if (-not (Test-Path -LiteralPath (Join-Path $stageRelease $name) -PathType Leaf)) { throw "Missing Velopack asset: $name." }
        }
        $after = Get-SourceState
        if ($after.Commit -cne $source.Commit) { throw 'The source commit changed while packaging.' }
        $files = @(Get-ChildItem -LiteralPath $stageRelease -File | Sort-Object Name | ForEach-Object {
            if (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Release assets must be regular files.' }
            [ordered]@{ Name = $_.Name; Length = $_.Length; SHA256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
        })
        $manifest = [ordered]@{
            SchemaVersion = 1; PackageId = $packId; Title = 'PinyinTranslate'; Version = $properties.Version
            VelopackVersion = $properties.VelopackVersion; RuntimeFrameworkVersion = $runtimeVersion
            Runtime = 'win-x64'; Channel = $channel; SourceCommit = $source.Commit; SourceTreeClean = ($source.Clean -and $after.Clean); Files = $files
        }
        $utf8 = [Text.UTF8Encoding]::new($false)
        $newline = [Environment]::NewLine
        [IO.File]::WriteAllText((Join-Path $stageRelease 'release-manifest.json'), ($manifest | ConvertTo-Json -Depth 8) + $newline, $utf8)
        $sums = @(Get-ChildItem -LiteralPath $stageRelease -File | Sort-Object Name | ForEach-Object {
            (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.Name
        })
        [IO.File]::WriteAllText((Join-Path $stageRelease 'SHA256SUMS'), ($sums -join $newline) + $newline, $utf8)
    }
    else { Compress-Archive -Path (Join-Path $stageDir '*') -DestinationPath $stageZip }

    Install-StagedDirectory $stageDir $publishDir $backupDir ('^' + [regex]::Escape($OutputName) + '$')
    if ($Velopack) {
        Install-StagedDirectory $stageRelease $releaseDir $releaseBackup ('^' + [regex]::Escape($OutputName + '-Releases') + '$')
        Write-Output "Created Velopack assets: $releaseDir"
        Write-Output "Raw publish directory: $publishDir"
    }
    else {
        Move-Item -LiteralPath $stageZip -Destination $zipPath -Force
        Write-Output "Created $zipPath"
    }
}
finally {
    Pop-Location
    foreach ($stage in @($stageDir, $stageRelease)) {
        if (Test-Path -LiteralPath $stage) { Assert-SafeArtifactDirectory $stage '^\.stage-[0-9a-f]{32}$'; Remove-Item -LiteralPath $stage -Recurse -Force }
    }
    if (Test-Path -LiteralPath $stageZip) { Remove-Item -LiteralPath $stageZip -Force }
}
