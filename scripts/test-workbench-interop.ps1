[CmdletBinding()]
param([string]$EvidenceDirectory = (Join-Path $PSScriptRoot '../artifacts/workbench-plugin/interop-evidence'))
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repo 'tests/DingTalkInsights.InteropFixture/DingTalkInsights.InteropFixture.csproj'
$id = [Guid]::NewGuid().ToString('N')
$root = Join-Path ([IO.Path]::GetTempPath()) "DingTalkInsights-Interop/$id"
New-Item -ItemType Directory -Path $root | Out-Null
Set-Content -LiteralPath "$root/.fixture-owner" -Value 'synthetic-runtime-compatibility'
New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null
$checks = @()
foreach ($cipher in @('synthetic','dpapi')) {
    if ($cipher -eq 'dpapi' -and -not $IsWindows) { continue }
    foreach ($framework in @('net8.0','net10.0')) {
        $mode = if ($framework -eq 'net8.0') { 'write' } else { 'read' }
        $output = & dotnet run --project $project -c Release -f $framework "-p:ArtifactsPath=$repo/artifacts/interop-build" -- $mode $root $cipher
        $output | Write-Host
        if ($LASTEXITCODE -ne 0) { throw "Runtime interoperability $framework/$cipher failed" }
        $checks += @{ framework = $framework; mode = $mode; cipher = $cipher; exitCode = $LASTEXITCODE; output = ($output -join "`n") }
    }
    # Re-read under net8 after net10 has ingested the state. The unknown-send sidecar remains canonical.
    & dotnet run --project $project -c Release -f net8.0 "-p:ArtifactsPath=$repo/artifacts/interop-build" -- read $root $cipher
    if ($LASTEXITCODE -ne 0) { throw 'Return-to-net8 interoperability failed' }
    Copy-Item -LiteralPath "$root/$cipher-facts.json" -Destination "$EvidenceDirectory/$cipher-facts.json"
}
@{ succeeded = $true; checks = $checks; scope = 'net8 writer -> actual net10 reader/engine -> net8 reader, synthetic and Windows DPAPI; no transport dispatch' } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath "$EvidenceDirectory/result.json"
