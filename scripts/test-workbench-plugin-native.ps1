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
$final = [ordered]@{ succeeded = $false; evidenceKind = 'source-linked-unmodified-presenter-compatibility'; runs = @(); limitations = @('Not the unchanged shipping Workbench EXE', 'No SDK uninstall: driver offline removal only after verified exit; trust/enabled preferences and data retained', 'No real DingTalk/DWS enterprise/model/Webhook integration', 'Windows Server automated acceptance does not replace Windows11 24H2 DPI/keyboard/visual/manual or multiday testing') }
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
        $stderr.WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult() | Set-Content -LiteralPath "$evidence/$Variant-$Phase.stderr.txt"
        if (Test-Path -LiteralPath $reportPath) { $result = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json }
        $passed = -not $timedOut -and $exitCode -eq 0 -and $null -ne $result -and $result.succeeded
        $phaseResult = [ordered]@{ succeeded = $passed; variant = $Variant; phase = $Phase; processId = $ownedPid; timedOut = $timedOut; exitCode = $exitCode; actualExitVerified = $true; provisionalNativeResult = $result }
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
    Copy-Item -LiteralPath "$root/evidence" -Destination "$evidence/$Variant-native" -Recurse
    # Do not upload even synthetic encrypted state/credentials; retain local root for diagnosis only.
    @{ variant = $Variant; runId = $runId; localRoot = $root; packageRemoved = $true; dataRetained = (Test-Path "$root/user/data/com.projectworkbench.dingtalk-insights/connection-draft.dpapi") } | ConvertTo-Json | Set-Content "$evidence/$Variant-offline-removal.json"
}
try {
    Run-Sequence "$build/host" "$build/package" 'published'
    $extracted = Join-Path $build 're-extracted'
    if (Test-Path $extracted) { throw 'Extraction target already exists' }
    New-Item -ItemType Directory -Path $extracted | Out-Null
    Expand-Archive -LiteralPath "$build/DingTalkInsights.Workbench.AcceptanceHost-win-x64.zip" -DestinationPath "$extracted/host"
    Expand-Archive -LiteralPath "$build/DingTalkInsights.Workbench-0.3.0-win-x64.zip" -DestinationPath "$extracted/package"
    if (-not (Test-Path -LiteralPath "$extracted/package/plugin.json")) { throw 'ZIP root does not contain plugin.json' }
    Run-Sequence "$extracted/host" "$extracted/package" 're-extracted'
    $final.succeeded = $true
}
catch { $final.failure = $_.Exception.Message; throw }
finally { $final | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath "$evidence/final.json" }
