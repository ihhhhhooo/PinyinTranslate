param(
    [switch]$HeadlessOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$credentialTest = 'PinyinTranslate.Core.Tests.CoreTests.OptionalProvidedCredentialCanBeDecryptedWithoutPrintingIt'

# This is a reviewed source inventory, not a runtime sandbox. Headless mode runs
# every test in these projects except the one explicitly named credential test.
# A new source file must be reviewed here before it can join a headless run.
$suites = @(
    [pscustomobject]@{
        Directory = 'tests\PinyinTranslate.Core.Tests'
        Project = 'PinyinTranslate.Core.Tests.csproj'
        Sources = @(
            'BudgetLedgerIntegrityTests.cs'
            'CandidateBridgeProtocolTests.cs'
            'CandidateTranslationCoordinatorTests.cs'
            'CoreTests.cs'
            'EditableContextAnchorTests.cs'
            'EditableContextCapabilitiesTests.cs'
            'EditableInputProtocolTests.cs'
            'GenericTextInsertionTests.cs'
            'ImeTabInputProtocolTests.cs'
            'InputSessionTests.cs'
            'TranslationCacheTests.cs'
            'TranslationClipboardWriterTests.cs'
            'UpdateQuiescenceTests.cs'
            'VerifiedPasteWorkflowTests.cs'
        )
        ExcludedTest = $credentialTest
    }
    [pscustomobject]@{
        Directory = 'tests\PinyinTranslate.Selection.Tests'
        Project = 'PinyinTranslate.Selection.Tests.csproj'
        Sources = @('NativeSelectionReaderTests.cs', 'SelectionActivityMonitorTests.cs', 'SelectionGenerationTests.cs', 'SelectionGuardTests.cs', 'SelectionMonitorTests.cs', 'UiaSelectionReaderTests.cs')
        ExcludedTest = $null
    }
    [pscustomobject]@{
        Directory = 'tests\PinyinTranslate.Screenshot.Tests'
        Project = 'PinyinTranslate.Screenshot.Tests.csproj'
        Sources = @('HeadlessRuntimeFixture.cs', 'ImeAutoCopyRegressionTests.cs', 'ImeClipboardStatusTests.cs', 'ImePreviewCapabilityTests.cs', 'ImeStartupBindingTests.cs', 'ImeTabAcceptanceRegressionTests.cs', 'ImeWriteDiagnosticsTests.cs', 'ScreenRectangleTests.cs', 'SelectionTranslationFlowTests.cs', 'StartupCommandPolicyTests.cs', 'UpdatePreparationTests.cs')
        ExcludedTest = $null
    }
    [pscustomobject]@{
        Directory = 'tests\PinyinTranslate.Update.Tests'
        Project = 'PinyinTranslate.Update.Tests.csproj'
        Sources = @('UpdateServiceTests.cs', 'VelopackBackendTests.cs')
        ExcludedTest = $null
    }
)

if ($HeadlessOnly) {
    # Check every project before starting any test. Unknown sources cause a clear
    # failure instead of silently disappearing behind a broad name filter.
    foreach ($suite in $suites) {
        $testDirectory = Join-Path $projectRoot $suite.Directory
        $actualSources = @(Get-ChildItem -LiteralPath $testDirectory -Recurse -File -Filter '*.cs' |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
            ForEach-Object { $_.FullName.Substring($testDirectory.Length + 1) })
        $unreviewed = @($actualSources | Where-Object { $_ -notin $suite.Sources })
        $missing = @($suite.Sources | Where-Object { $_ -notin $actualSources })
        if ($unreviewed.Count -gt 0 -or $missing.Count -gt 0) {
            throw "Headless inventory mismatch in $($suite.Directory). Unreviewed: [$($unreviewed -join ', ')]. Missing: [$($missing -join ', ')]. Review and update the explicit inventory before running."
        }
        Write-Host "Headless reviewed: $($suite.Directory) ($($actualSources.Count) source files; all tests included except any explicit exclusion below)."
    }
    Write-Host "Headless exclusion: $credentialTest (may read an externally supplied protected credential)."
    Write-Host 'Headless scope: managed fakes, synthetic UIA snapshots/protocols, temporary test files, and DPAPI round-trip of a fake test key. No live UIA, hooks, clipboard, process launch or input injection.'
}

foreach ($suite in $suites) {
    $project = Join-Path (Join-Path $projectRoot $suite.Directory) $suite.Project
    $testArguments = @('test', $project, '-c', 'Release')
    if ($HeadlessOnly -and $null -ne $suite.ExcludedTest) {
        $testArguments += @('--filter', "FullyQualifiedName!=$($suite.ExcludedTest)")
    }
    & dotnet @testArguments
    if ($LASTEXITCODE -ne 0) { throw "Test failed: $project" }
}
