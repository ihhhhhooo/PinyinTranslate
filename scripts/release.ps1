param(
    [string] $Tag,
    [string] $PackagedDir,
    [ValidatePattern('^PinyinTranslate-[A-Za-z0-9-]+$')]
    [ValidateLength(1, 96)]
    [string] $OutputName,
    [switch] $ValidateConfigurationOnly,
    [switch] $Publish
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$repository = 'ihhhhhooo/trans'
$repoUrl = 'https://github.com/' + $repository
$packId = 'PinyinTranslate.Desktop'
$channel = 'win-x64'
$toolManifest = Join-Path $projectRoot '.config\dotnet-tools.json'

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

function Assert-Source([bool] $requireTag) {
    $commit = & git -C $projectRoot rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') { throw 'Cannot resolve the release source commit.' }
    $changes = @(& git -C $projectRoot status --porcelain --untracked-files=normal)
    if ($LASTEXITCODE -ne 0 -or $changes.Count -ne 0) { throw 'Release validation requires a clean, committed source tree.' }
    $tagCommit = & git -C $projectRoot rev-parse --verify --quiet ('refs/tags/' + $Tag + '^{commit}')
    if ($LASTEXITCODE -eq 0) {
        if ($tagCommit -cne $commit) { throw 'The release tag does not point to the checked-out source commit.' }
    }
    elseif ($requireTag) { throw 'Create the local version tag before publishing; this script never creates or pushes tags.' }
    if ($env:GITHUB_ACTIONS -eq 'true') {
        if ($env:GITHUB_REPOSITORY -cne $repository -or $env:GITHUB_REF -cne ('refs/tags/' + $Tag) -or $env:GITHUB_SHA -cne $commit) {
            throw 'The workflow repository, tag and source SHA must match this release.'
        }
    }
    return [string]$commit
}

function Assert-RegularFile([string] $path) {
    $file = Get-Item -LiteralPath $path -Force
    if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Not a regular release file: $($file.Name)." }
}

function Assert-SetupPayload([string] $path, [long] $packageLength, [string] $packageHash) {
    # The pinned, unsigned Velopack Setup appends the full nupkg at EOF.
    # Hash its embedded payload without launching or extracting the installer.
    $stream = [IO.File]::OpenRead($path)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        if ($stream.Length -le $packageLength -or $stream.ReadByte() -ne 0x4d -or $stream.ReadByte() -ne 0x5a) {
            throw 'Setup is not a bundled Windows executable.'
        }
        $null = $stream.Seek(-$packageLength, [IO.SeekOrigin]::End)
        if ([Convert]::ToHexString($sha256.ComputeHash($stream)) -ine $packageHash) {
            throw 'The installer does not contain the verified full update package.'
        }
    }
    finally { $sha256.Dispose(); $stream.Dispose() }
    $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($path)
    if ($versionInfo.ProductVersion -cnotin @($properties.Version, ($properties.Version + '.0'))) {
        throw 'The installer product version does not match the release.'
    }
}

function Assert-ReleaseBundle([string] $directory, [string] $commit) {
    $folder = Get-Item -LiteralPath $directory -Force
    if (-not $folder.PSIsContainer -or ($folder.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'PackagedDir must be a regular directory.' }
    $fullName = "$packId-$($properties.Version)-$channel-full.nupkg"
    $portableName = "$packId-$channel-Portable.zip"
    $setupName = "$packId-$channel-Setup.exe"
    $feedName = "releases.$channel.json"
    $assetName = "assets.$channel.json"
    $required = @($fullName, $portableName, $setupName, $feedName, $assetName)
    $allowed = @($required) + @("RELEASES-$channel", 'release-manifest.json', 'SHA256SUMS')
    $actualFiles = @(Get-ChildItem -LiteralPath $directory -Force)
    foreach ($file in $actualFiles) {
        if ($file.Name -cnotin $allowed) { throw "Unexpected file in the release directory: $($file.Name)." }
        Assert-RegularFile $file.FullName
    }
    foreach ($name in (@($required) + @('release-manifest.json', 'SHA256SUMS'))) {
        Assert-RegularFile (Join-Path $directory $name)
    }
    $manifest = Get-Content -LiteralPath (Join-Path $directory 'release-manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.SchemaVersion -ne 1 -or $manifest.PackageId -cne $packId -or $manifest.Title -cne 'PinyinTranslate' -or
        $manifest.Version -cne $properties.Version -or $manifest.VelopackVersion -cne $properties.VelopackVersion -or
        $manifest.RuntimeFrameworkVersion -cne $properties.PinnedRuntimeVersion -or $manifest.Runtime -cne 'win-x64' -or
        $manifest.Channel -cne $channel -or $manifest.SourceCommit -cne $commit -or
        $manifest.SourceTreeClean -isnot [bool] -or -not $manifest.SourceTreeClean) { throw 'The release manifest does not match the clean source, version or runtime configuration.' }

    $hashes = @{}
    $lengths = @{}
    foreach ($file in $actualFiles | Where-Object Name -CNE 'SHA256SUMS') {
        $hashes[$file.Name] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        $lengths[$file.Name] = $file.Length
    }
    $records = @($manifest.Files)
    $seen = @{}
    foreach ($record in $records) {
        $name = [string]$record.Name
        if ($name -cnotin (@($required) + @("RELEASES-$channel")) -or $seen.ContainsKey($name) -or
            -not $hashes.ContainsKey($name) -or $record.SHA256 -notmatch '^[0-9a-fA-F]{64}$' -or
            $record.SHA256 -ine $hashes[$name] -or $record.Length -ne $lengths[$name]) { throw 'A release manifest file or SHA256 does not match its asset.' }
        $seen[$name] = $true
    }
    if ($records.Count -ne $actualFiles.Count - 2) { throw 'The release manifest must cover every build asset exactly once.' }
    foreach ($name in $required) { if (-not $seen.ContainsKey($name)) { throw "The manifest omits $name." } }
    $sumNames = @{}
    foreach ($line in Get-Content -LiteralPath (Join-Path $directory 'SHA256SUMS')) {
        if ($line -notmatch '^([0-9a-fA-F]{64})  ([A-Za-z0-9][A-Za-z0-9_.-]*)$') { throw 'Malformed SHA256SUMS entry.' }
        $hash = $Matches[1]; $name = $Matches[2]
        if ($sumNames.ContainsKey($name) -or -not $hashes.ContainsKey($name) -or $hash -ine $hashes[$name]) { throw 'SHA256SUMS does not match the release files.' }
        $sumNames[$name] = $true
    }
    if ($sumNames.Count -ne $hashes.Count) { throw 'SHA256SUMS must cover every asset and release-manifest.json.' }

    $feed = Get-Content -LiteralPath (Join-Path $directory $feedName) -Raw | ConvertFrom-Json
    $entries = @($feed.Assets)
    if ($entries.Count -ne 1) { throw 'This release must have exactly one complete package and no delta packages.' }
    $entry = $entries[0]
    if ($entry.PackageId -cne $packId -or [string]$entry.Version -cne $properties.Version -or [string]$entry.Type -notin @('Full', '1') -or
        $entry.FileName -cne $fullName -or $entry.SHA256 -ine $hashes[$fullName] -or $entry.Size -ne $lengths[$fullName]) { throw 'The update feed does not match the verified full package.' }
    $assets = @(Get-Content -LiteralPath (Join-Path $directory $assetName) -Raw | ConvertFrom-Json)
    if ($assets.Count -ne 3 -or @($assets | Where-Object { $_.RelativeFileName -ceq $fullName -and [string]$_.Type -in @('Full', '1') }).Count -ne 1 -or
        @($assets | Where-Object { $_.RelativeFileName -ceq $portableName -and [string]$_.Type -in @('Portable', '3') }).Count -ne 1 -or
        @($assets | Where-Object { $_.RelativeFileName -ceq $setupName -and [string]$_.Type -cEQ 'Installer' }).Count -ne 1) {
        throw 'The vpk assets manifest must contain only the verified full package, portable ZIP and EXE installer.'
    }
    Assert-SetupPayload (Join-Path $directory $setupName) $lengths[$fullName] $hashes[$fullName]

    # Inspect archive entries only; never run the root launcher, app or updater.
    $zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $directory $portableName))
    try {
        foreach ($name in @('PinyinTranslate.exe', 'Update.exe', '.portable', 'current/PinyinTranslate.App.exe', 'current/PinyinTranslate.App.dll',
            'current/PinyinTranslate.ImeBridge.exe', 'current/PinyinTranslate.ImeBridge.dll', 'current/LICENSE', 'current/RELEASE-NOTES.md', 'current/reports/updates.md')) {
            if ($null -eq $zip.GetEntry($name)) { throw "Incomplete portable ZIP: $name." }
        }
        foreach ($name in @('PinyinTranslate.App', 'PinyinTranslate.ImeBridge')) {
            $configEntry = $zip.GetEntry("current/$name.runtimeconfig.json")
            if ($null -eq $configEntry) { throw "Missing portable runtime configuration for $name." }
            $textReader = [IO.StreamReader]::new($configEntry.Open())
            try { $config = $textReader.ReadToEnd() | ConvertFrom-Json } finally { $textReader.Dispose() }
            $frameworks = @($config.runtimeOptions.includedFrameworks)
            if ($frameworks.Count -eq 0 -or @($frameworks | Where-Object { $_.version -cne $properties.PinnedRuntimeVersion }).Count -gt 0 -or
                @($frameworks | Where-Object name -CEQ 'Microsoft.NETCore.App').Count -ne 1) { throw "The bundled runtime for $name is not the pinned self-contained version." }
        }
    }
    finally { $zip.Dispose() }
    return [pscustomobject]@{ Files = @($records.Name); FeedName = $feedName; BuildFiles = @($fullName, $portableName, $setupName) }
}

function Invoke-GitHub([string[]] $Arguments, [string] $InputText) {
    # Only the explicitly supplied publication token is exposed to this child.
    # It never appears in argv and no stored Git/GCM credentials are queried.
    $start = [Diagnostics.ProcessStartInfo]::new('gh')
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true; $start.RedirectStandardInput = $true
    $start.WorkingDirectory = $projectRoot
    $start.Environment['GH_TOKEN'] = $env:VPK_TOKEN
    $start.Environment['GH_HOST'] = 'github.com'
    $start.Environment['GH_PROMPT_DISABLED'] = '1'
    $start.Environment['GH_NO_UPDATE_NOTIFIER'] = '1'
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw 'Cannot start the GitHub CLI.' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if ($InputText) { $process.StandardInput.Write($InputText) }
        $process.StandardInput.Close()
        $process.WaitForExit()
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $stdout.GetAwaiter().GetResult(); Error = $stderr.GetAwaiter().GetResult() }
    }
    finally { $process.Dispose() }
}

$properties = Read-ReleaseProperties
if (-not $Tag) { $Tag = 'v' + $properties.Version }
if ($Tag -cne ('v' + $properties.Version)) { throw 'The stable release tag must exactly equal v<Version> from Directory.Build.props.' }
$tools = Get-Content -LiteralPath $toolManifest -Raw | ConvertFrom-Json
if ($tools.tools.vpk.version -cne $properties.VelopackVersion) { throw 'The pinned local vpk version does not match Directory.Build.props.' }
if ($ValidateConfigurationOnly -and ($Publish -or $PackagedDir)) { throw 'ValidateConfigurationOnly cannot build, validate assets or publish.' }
$commit = Assert-Source ($Publish -or $ValidateConfigurationOnly)
if ($ValidateConfigurationOnly) { Write-Output "Validated $Tag at $commit."; return }
if (-not $OutputName) { $OutputName = 'PinyinTranslate-' + $Tag.Replace('.', '-') + '-win-x64' }
if (-not $PackagedDir) {
    & (Join-Path $PSScriptRoot 'package.ps1') -OutputName $OutputName -Velopack
    $PackagedDir = Join-Path (Join-Path $projectRoot 'artifacts') ($OutputName + '-Releases')
}
$PackagedDir = [IO.Path]::GetFullPath($PackagedDir)
$bundle = Assert-ReleaseBundle $PackagedDir $commit
if (-not $Publish) { Write-Output "Validated $Tag at $commit in $PackagedDir. No remote changes were made."; return }
if ([string]::IsNullOrWhiteSpace($env:VPK_TOKEN)) { throw 'Publishing requires VPK_TOKEN in the process environment.' }

Push-Location $projectRoot
try {
    & dotnet tool restore --tool-manifest $toolManifest
    if ($LASTEXITCODE -ne 0) { throw 'Restoring the pinned vpk tool failed; no release was created.' }
    $currentCommit = Assert-Source $true
    if ($currentCommit -cne $commit) { throw 'The checked-out release commit changed during preparation; no release was created.' }
    $bundle = Assert-ReleaseBundle $PackagedDir $commit
    $repo = Invoke-GitHub @('api', ('repos/' + $repository)) ''
    if ($repo.ExitCode -ne 0) { throw 'GitHub repository preflight failed; no release was created.' }
    if (($repo.Output | ConvertFrom-Json).visibility -cne 'public') { throw 'The release repository must be public before publishing.' }
    $existing = Invoke-GitHub @('api', ('repos/' + $repository + '/releases/tags/' + $Tag)) ''
    if ($existing.ExitCode -eq 0) { throw 'This tag already has a release or draft. Existing versions are never overwritten; inspect a failed draft manually.' }
    if ($existing.Error -notmatch 'HTTP 404') { throw 'Cannot determine whether the tag already has a release; no release was created.' }
    $draft = @{
        tag_name = $Tag; target_commitish = $commit; name = 'PinyinTranslate ' + $properties.Version
        draft = $true; prerelease = $false; body = [IO.File]::ReadAllText((Join-Path $projectRoot 'RELEASE-NOTES.md'))
    } | ConvertTo-Json
    $created = Invoke-GitHub @('api', ('repos/' + $repository + '/releases'), '--method', 'POST', '--input', '-') $draft
    if ($created.ExitCode -ne 0) { throw 'Draft creation failed. Inspect GitHub before retrying; no automatic overwrite or rollback is attempted.' }

    # Add integrity metadata before vpk uploads its own files and publishes.
    # --merge is safe here because this new draft contains no release feed yet.
    $metadata = @('release-manifest.json', 'SHA256SUMS') + @($bundle.Files | Where-Object { $_ -cne $bundle.FeedName -and $_ -cnotin $bundle.BuildFiles })
    $uploadArguments = @('release', 'upload', $Tag, '--repo', $repository)
    foreach ($name in $metadata) { $uploadArguments += Join-Path $PackagedDir $name }
    $uploaded = Invoke-GitHub $uploadArguments ''
    if ($uploaded.ExitCode -ne 0) { throw 'Draft metadata upload failed. Inspect the draft manually before retrying.' }
    $vpkArguments = @('vpk', '--skip-updates', 'upload', 'github', '--repoUrl', $repoUrl, '--outputDir', $PackagedDir,
        '--channel', $channel, '--tag', $Tag, '--releaseName', ('PinyinTranslate ' + $properties.Version),
        '--targetCommitish', $commit, '--merge', '--publish')
    & dotnet @vpkArguments
    if ($LASTEXITCODE -ne 0) { throw 'Velopack upload or publish failed. Inspect the draft/release manually; this script does not retry, delete or overwrite it.' }
    Write-Output "Published https://github.com/$repository/releases/tag/$Tag"
}
finally { Pop-Location }
