[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BuildDirectory,
    [int]$TimeoutSeconds = 180
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Genuine WinUI acceptance requires Windows' }
if ($TimeoutSeconds -lt 30 -or $TimeoutSeconds -gt 600) { throw 'Timeout must be between 30 and 600 seconds' }
$build = [IO.Path]::GetFullPath($BuildDirectory)
if (-not (Test-Path -LiteralPath "$build/.dti-build-owner")) { throw 'Build owner marker missing' }
$evidence = Join-Path $build 'evidence'
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$tool = Join-Path $build 'tool/Microsoft.DingTalkInsights.PackageTool.dll'
$final = [ordered]@{ succeeded = $false; evidenceKind = 'source-linked-unmodified-presenter-compatibility'; runs = @(); limitations = @('Not the unchanged shipping Workbench EXE', 'SDK maintenance uses a guarded fresh helper before discovery; unchanged shipping App and generic management UI do not apply it; KeepData is unconditional', 'No real DingTalk/DWS enterprise/model/Webhook integration', 'Windows Server automated acceptance does not replace Windows11 24H2 DPI/keyboard/visual/manual or multiday testing') }
function Reject-Links([string]$Path) {
    $part = [IO.Path]::GetFullPath($Path)
    while ($part) {
        if ((Test-Path -LiteralPath $part) -and ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Reparse path rejected' }
        $part = [IO.Path]::GetDirectoryName($part)
    }
}
function Assert-OwnedRoot([string]$Root, [string]$RunId) {
    $expected = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) "DingTalkInsights-WorkbenchAcceptance/$RunId"))
    if ([IO.Path]::GetFullPath($Root) -ne $expected) { throw 'Arbitrary acceptance root rejected' }
    Reject-Links $Root
    $owner = Get-Content -LiteralPath "$Root/.dti-acceptance-owner.json" -Raw | ConvertFrom-Json
    if ($owner.runId -ne $RunId) { throw 'Acceptance marker mismatch' }
}
function Safe-NativeLabel([object]$Value, [int]$Limit = 220) {
    $text = [string]$Value
    $text = [regex]::Replace($text, '[^\p{L}\p{N} .,:;_()/+\-]', ' ')
    if ($text.Length -gt $Limit) { $text = $text.Substring(0, $Limit) }
    return $text
}
function Safe-NativeFrames($Frames) {
    if ($null -eq $Frames) { return @() }
    return @($Frames | Select-Object -First 5 | ForEach-Object {
        [ordered]@{ type = (Safe-NativeLabel $_.Type 160); method = (Safe-NativeLabel $_.Method 100); file = (Safe-NativeLabel $_.File 100); line = [int]$_.Line }
    })
}
function Safe-CloseTask($Task) {
    if ($null -eq $Task) { return $null }
    return [ordered]@{ id = [int]$Task.id; status = (Safe-NativeLabel $Task.status 40); completedSuccessfully = $Task.completedSuccessfully }
}
function Safe-ClosePresenter($Presenter) {
    if ($null -eq $Presenter) { return $null }
    return [ordered]@{ closing = $Presenter.closing; stopped = $Presenter.stopped; allowClose = $Presenter.allowClose }
}
function New-NativeFailureSummary($Result, [bool]$ParseFailed, [string]$StderrText, $Close) {
    $stage = if ($ParseFailed) { 'invalid-native-report' } elseif ($null -eq $Result) { 'missing-native-report' } else { $Result.stage }
    $errorType = $Result.errorType
    # Compatibility with original RunOptions failure artifacts: extract the type only, never the exception body.
    if (-not $errorType -and $Result.error -match '^([A-Za-z_][A-Za-z0-9_.+]{0,159}):') { $errorType = $Matches[1] }
    $hresult = if ([string]$Result.errorHResult -match '^0x[0-9A-Fa-f]{8}$') { [string]$Result.errorHResult } else { '' }
    $checks = @($Result.checks)
    $startup = @($Result.startupStages)
    $factory = $Result.pluginDiagnostics.ViewFailure
    $page = $Result.pluginDiagnostics.Page
    return [ordered]@{
        stage = (Safe-NativeLabel $stage)
        errorType = (Safe-NativeLabel $errorType 160)
        hResult = $hresult
        acceptanceStep = (Safe-NativeLabel $Result.acceptanceStep 100)
        ownedAssertion = (Safe-NativeLabel $Result.ownedAssertion)
        lastCompletedCheck = if ($checks.Count) { Safe-NativeLabel $checks[-1] } else { '' }
        lastStartupStage = if ($startup.Count) { Safe-NativeLabel $startup[-1].stage 100 } else { '' }
        factoryView = (Safe-NativeLabel $factory.View 40)
        factoryErrorType = (Safe-NativeLabel $factory.ErrorType 160)
        factoryHResult = if ([string]$factory.HResult -match '^0x[0-9A-Fa-f]{8}$') { [string]$factory.HResult } else { '' }
        frames = @(Safe-NativeFrames $Result.frames)
        factoryFrames = @(Safe-NativeFrames $factory.Frames)
        stderrHResults = @([regex]::Matches($StderrText, '(?i)\bHRESULT\s*[:=]?\s*(0x[0-9a-f]{8})\b') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique -First 3)
        pageReady = $page.Ready
        pageOutcome = (Safe-NativeLabel $page.Outcome 40)
        pageLastAction = (Safe-NativeLabel $page.LastAction 80)
        pageSaveStage = (Safe-NativeLabel $page.SaveStage 40)
        pageFailureCategory = (Safe-NativeLabel $page.FailureCategory 80)
        pageStatusOrigin = (Safe-NativeLabel $page.StatusOrigin 20)
        actionStartedOnUi = $page.ActionStartedOnUi
        saveResumedOnUi = $page.SaveResumedOnUi
        settingsDraftExists = $Result.settingsFixture.DraftExists
        settingsDraftBytes = $Result.settingsFixture.DraftBytes
        settingsPreferencesMatch = $Result.settingsFixture.PreferencesMatch
        settingsObservationErrorType = (Safe-NativeLabel $Result.settingsFixture.ErrorType 80)
        stderrCharacters = $StderrText.Length
        closeBoundary = (Safe-NativeLabel $Close.boundary 80)
        closeEvents = @($Close.events | ForEach-Object { Safe-NativeLabel $_ 40 })
        closingObserved = $Close.closingObserved
        closedObserved = $Close.closedObserved
        destroyingObserved = $Close.destroyingObserved
        originalPresenterBefore = (Safe-ClosePresenter $Close.originalPresenterBefore)
        originalPresenter = (Safe-ClosePresenter $Close.originalPresenter)
        originalCloseTask = (Safe-CloseTask $Close.originalCloseTask)
        originalLoop = (Safe-CloseTask $Close.originalLoop)
        controllerRetirement = (Safe-CloseTask $Close.controllerRetirement)
        sessionRetirement = (Safe-CloseTask $Close.sessionRetirement)
        closeActive = $Close.active
        closeMonitoring = $Close.monitoring
        closeResources = if ($null -ne $Close.resources) { [ordered]@{
            pendingOperations = $Close.resources.PendingOperations; subscriptions = $Close.resources.Subscriptions; storeOwned = $Close.resources.StoreOwned
            supervisorPresent = $Close.resources.SupervisorPresent; monitoringCompleted = $Close.resources.MonitoringCompleted
            dispatches = $Close.resources.Dispatches; ownedStoreWrites = $Close.resources.OwnedStoreWrites
        } } else { $null }
        closePendingPersistence = $Close.pendingPersistence
        closeLeases = @($Close.leases | Where-Object { $null -ne $_ } | ForEach-Object { [ordered]@{
            name = (Safe-NativeLabel $_.name 40); acquired = $_.acquired; errorType = (Safe-NativeLabel $_.errorType 80)
            hResult = if ([string]$_.hResult -match '^0x[0-9A-Fa-f]{8}$') { [string]$_.hResult } else { '' }
        } })
        closeDiagnosticErrorType = (Safe-NativeLabel $Close.diagnosticErrorType 80)
    }
}
function Get-OwnedDataFingerprint([string]$Path) {
    Reject-Links $Path
    [string[]]$relativePaths = @([IO.Directory]::EnumerateFiles($Path, '*', [IO.SearchOption]::AllDirectories) | ForEach-Object { [IO.Path]::GetRelativePath($Path, $_).Replace('\', '/') })
    [Array]::Sort($relativePaths, [StringComparer]::Ordinal)
    $hash = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        foreach ($relative in $relativePaths) {
            $file = Join-Path $Path $relative
            Reject-Links $file
            $hash.AppendData([Text.Encoding]::UTF8.GetBytes($relative))
            $hash.AppendData([byte[]]@(0))
            $hash.AppendData([IO.File]::ReadAllBytes($file))
            $hash.AppendData([byte[]]@(0))
        }
        return [Convert]::ToHexString($hash.GetHashAndReset())
    }
    finally { $hash.Dispose() }
}
function Invoke-Phase([string]$HostDir, [string]$PackageDir, [string]$Root, [string]$RunId, [string]$Phase, [string]$Variant) {
    Assert-OwnedRoot $Root $RunId
    $exe = Join-Path $HostDir 'DingTalkInsights.Workbench.AcceptanceHost.exe'
    Reject-Links $exe
    $reportPath = Join-Path $Root "evidence/$Phase.json"
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.WorkingDirectory = $HostDir
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment.Clear()
    # OS/runtime values only; no GitHub/API/CI credentials, proxies or arbitrary inherited variables.
    foreach ($name in @('SystemRoot','windir','SystemDrive','PATH','PATHEXT','ComSpec','TEMP','TMP','USERPROFILE','APPDATA','LOCALAPPDATA','ProgramFiles','ProgramFiles(x86)','ProgramW6432','PROCESSOR_ARCHITECTURE','NUMBER_OF_PROCESSORS')) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if ($null -ne $value) { $start.Environment[$name] = $value }
    }
    foreach ($arg in @('--run-root',$Root,'--run-id',$RunId,'--phase',$Phase,'--package',$PackageDir)) { $start.ArgumentList.Add($arg) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $timedOut = $false
    $result = $null
    $exitCode = $null
    try {
        if (-not $process.Start()) { throw 'Owned native acceptance process did not start' }
        $ownedPid = $process.Id
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $timedOut = $true
            # Only the exact Process object started above, including its owned test descendants.
            $process.Kill($true)
            if (-not $process.WaitForExit(10000)) { throw 'Owned process did not exit after timeout termination; root remains untouched' }
        }
        $exitCode = $process.ExitCode
        $stdout.WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult() | Set-Content -LiteralPath "$evidence/$Variant-$Phase.stdout.txt"
        $childStderr = $stderr.WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult()
        $childStderr | Set-Content -LiteralPath "$evidence/$Variant-$Phase.stderr.txt"
        $parseFailed = $false
        if (Test-Path -LiteralPath $reportPath) {
            try { $result = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json }
            catch { $parseFailed = $true }
        }
        # Preserve bounded callback evidence even when the child exits before final success.
        $close = $null
        $closeTrace = Join-Path $Root "evidence/$Phase.close.jsonl"
        Reject-Links $closeTrace
        if (Test-Path -LiteralPath $closeTrace) {
            if ((Get-Item -LiteralPath $closeTrace).Length -gt 131072) { throw 'Native close observation bound exceeded' }
            Copy-Item -LiteralPath $closeTrace -Destination "$evidence/$Variant-$Phase.close.jsonl"
            try { $close = Get-Content -LiteralPath $closeTrace | Select-Object -Last 1 | ConvertFrom-Json }
            catch { $parseFailed = $true }
        }
        $passed = -not $timedOut -and $exitCode -eq 0 -and -not $parseFailed -and $null -ne $result -and $result.succeeded -and $null -ne $close -and -not $close.diagnosticErrorType
        $postExitLeasesAcquired = $false
        $postExitDataUnchanged = $false
        if ($passed) {
            # The exact owner has exited; prove host, installed demo and both real
            # source-linked canonical stores accept a new exclusive owner.
            foreach ($relative in @('user/host.lock','user/data/com.projectworkbench.dingtalk-insights/demo/state.dpapi.lock','pending-stop/initial-readiness/canonical/state.dpapi.lock','pending-stop/one-shot-read/canonical/state.dpapi.lock')) {
                $leasePath = Join-Path $Root $relative
                Reject-Links $leasePath
                $lease = [IO.FileStream]::new($leasePath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
                $lease.Dispose()
            }
            $postExitLeasesAcquired = $true
            if ([string]$close.destroyedDataSha256 -notmatch '^[0-9A-F]{64}$' -or (Get-OwnedDataFingerprint (Join-Path $Root 'user/data/com.projectworkbench.dingtalk-insights')) -ne $close.destroyedDataSha256) { throw 'Native data changed between actual destruction and verified process exit' }
            $postExitDataUnchanged = $true
        }
        $summary = $null
        if (-not $passed) {
            $summary = New-NativeFailureSummary $result $parseFailed $childStderr $close
            Write-Host ("Native failure summary ($Variant/$Phase, exit=$exitCode, timeout=$timedOut): " + ($summary | ConvertTo-Json -Compress -Depth 8))
        }
        $phaseResult = [ordered]@{ succeeded = $passed; variant = $Variant; phase = $Phase; processId = $ownedPid; timedOut = $timedOut; exitCode = $exitCode; actualExitVerified = $true; postExitLeasesAcquired = $postExitLeasesAcquired; postExitDataUnchanged = $postExitDataUnchanged; closeObservation = $close; failureSummary = $summary; provisionalNativeResult = $result }
        $phaseResult | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath "$evidence/$Variant-$Phase.final.json"
        $script:final.runs += $phaseResult
        if (-not $passed) { throw "Native phase failed: $Variant/$Phase; timeout=$timedOut; exit=$exitCode. Provisional success cannot override this failure." }
    }
    finally { $process.Dispose() }
}
function Run-Sequence([string]$HostDir, [string]$PackageDir, [string]$Variant) {
    Reject-Links $HostDir
    Reject-Links $PackageDir
    & dotnet $tool verify-package $PackageDir $HostDir
    if ($LASTEXITCODE -ne 0) { throw 'Package inventory/shared identity validation failed' }
    if ((Get-Item -LiteralPath "$HostDir/DingTalkInsights.Workbench.AcceptanceHost.pri").Length -eq 0) { throw 'Extracted main PRI empty' }
    $runId = [Guid]::NewGuid().ToString('N')
    $root = Join-Path ([IO.Path]::GetTempPath()) "DingTalkInsights-WorkbenchAcceptance/$runId"
    Reject-Links ([IO.Path]::GetDirectoryName($root))
    New-Item -ItemType Directory -Path $root | Out-Null
    @{ runId = $runId; purpose = 'synthetic-native-acceptance'; createdUtc = [DateTimeOffset]::UtcNow } | ConvertTo-Json | Set-Content -LiteralPath "$root/.dti-acceptance-owner.json"
    Invoke-Phase $HostDir $PackageDir $root $runId 'initial' $Variant
    # The prior exact process has exited before starting a new owner of host/state leases.
    Invoke-Phase $HostDir $PackageDir $root $runId 'restart' $Variant
    Assert-OwnedRoot $root $runId
    $installed = Join-Path $root 'user/Plugins/com.projectworkbench.dingtalk-insights'
    Reject-Links $installed
    $installedInfo = Join-Path $installed 'build-info.json'
    Add-Content -LiteralPath $installedInfo -Value ' '
    Invoke-Phase $HostDir $PackageDir $root $runId 'changed' $Variant
    # This is driver-owned offline removal, not an SDK uninstall or assembly unload.
    Assert-OwnedRoot $root $runId
    Reject-Links $installed
    if ([IO.Path]::GetFullPath($installed) -ne [IO.Path]::GetFullPath((Join-Path $root 'user/Plugins/com.projectworkbench.dingtalk-insights'))) { throw 'Owned package target mismatch' }
    Remove-Item -LiteralPath $installed -Recurse -Force
    Invoke-Phase $HostDir $PackageDir $root $runId 'absent' $Variant
    # New SDK maintenance is separate from the retained old offline-delete regression.
    # Helpers only install/stage. The exact platform HostRuntime owns its lease and applies pending work before Discover/Restore.
    $maintenanceZip = Join-Path $root 'actual-plugin.zip'
    Compress-Archive -Path "$PackageDir/*" -DestinationPath $maintenanceZip
    function Invoke-OwnedMaintenance([string]$Action, [string]$Extra = '') {
        Assert-OwnedRoot $root $runId
        $arguments = @($tool, 'maintenance', $Action, $root, $runId)
        if ($Extra) { $arguments += $Extra }
        & dotnet @arguments
        if ($LASTEXITCODE -ne 0) { throw "SDK owned maintenance failed: $Action" }
        $lease = [IO.FileStream]::new((Join-Path $root 'user/host.lock'), [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $lease.Dispose()
    }
    Invoke-OwnedMaintenance 'install' $maintenanceZip
    Invoke-Phase $HostDir $PackageDir $root $runId 'maintained' $Variant
    Invoke-Phase $HostDir $PackageDir $root $runId 'removed' $Variant
    Invoke-OwnedMaintenance 'rollback'
    Invoke-Phase $HostDir $PackageDir $root $runId 'rolledback' $Variant
    Copy-Item -LiteralPath "$root/evidence" -Destination "$evidence/$Variant-native" -Recurse
    # Do not upload even synthetic encrypted state/credentials; retain local root for diagnosis only.
    @{ variant = $Variant; runId = $runId; localRoot = $root; sdkCodeRollbackRetained = $true; dataRetained = (Test-Path "$root/user/data/com.projectworkbench.dingtalk-insights/connection-draft.dpapi") } | ConvertTo-Json | Set-Content "$evidence/$Variant-offline-removal.json"
}
try {
    Run-Sequence "$build/host" "$build/package" 'published'
    $extracted = Join-Path $build 're-extracted'
    if (Test-Path $extracted) { throw 'Extraction target already exists' }
    New-Item -ItemType Directory -Path $extracted | Out-Null
    Expand-Archive -LiteralPath "$build/DingTalkInsights.Workbench.AcceptanceHost-win-x64.zip" -DestinationPath "$extracted/host"
    Expand-Archive -LiteralPath "$build/DingTalkInsights.Workbench-0.3.3-win-x64.zip" -DestinationPath "$extracted/package"
    if (-not (Test-Path -LiteralPath "$extracted/package/plugin.json")) { throw 'ZIP root does not contain plugin.json' }
    Run-Sequence "$extracted/host" "$extracted/package" 're-extracted'
    $final.succeeded = $true
}
catch { $final.failure = $_.Exception.Message; throw }
finally { $final | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath "$evidence/final.json" }
