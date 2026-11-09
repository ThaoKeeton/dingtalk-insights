[CmdletBinding()]
param([string]$OutDir = (Join-Path $PSScriptRoot '../artifacts/workbench-plugin'))
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$out = [IO.Path]::GetFullPath($OutDir)
$allowed = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts')) + [IO.Path]::DirectorySeparatorChar
if (-not $out.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Build outputs must be under this repository artifacts directory' }
if ((dotnet --version).Trim() -ne '10.0.403') { throw 'Select .NET SDK 10.0.403 before building' }
if (Test-Path $out) { throw 'Output directory already exists; retain evidence or explicitly remove the previous owned build before retrying' }
New-Item -ItemType Directory -Path $out | Out-Null
Set-Content -LiteralPath (Join-Path $out '.dti-build-owner') -Value 'DingTalkInsights.Workbench 0.3.3' -NoNewline
$pin = Join-Path $repo 'eng/vendor/workbench-27154465'
$toolProject = Join-Path $repo 'tools/DingTalkInsights.PackageTool/DingTalkInsights.PackageTool.csproj'
$hostProject = Join-Path $repo 'tests/DingTalkInsights.Workbench.AcceptanceHost/DingTalkInsights.Workbench.AcceptanceHost.csproj'
$pluginProject = Join-Path $repo 'src/DingTalkInsights.Workbench/DingTalkInsights.Workbench.csproj'
function Invoke-Dotnet([string[]]$CommandArgs) {
    & dotnet @CommandArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed ($LASTEXITCODE): $($CommandArgs[0])" }
}
Invoke-Dotnet @('build', $toolProject, '-c', 'Release', "-p:ArtifactsPath=$out/tool-build", '-o', "$out/tool", '-p:UseSharedCompilation=false')
$tool = Join-Path $out 'tool/Microsoft.DingTalkInsights.PackageTool.dll'
Invoke-Dotnet @($tool, 'verify-source', $pin)
Invoke-Dotnet @($tool, 'self-test')
try {
    Invoke-Dotnet @('restore', $hostProject, '-r', 'win-x64', "-p:ArtifactsPath=$out/host-build")
    Invoke-Dotnet @('publish', $hostProject, '--no-restore', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', "-p:ArtifactsPath=$out/host-build", '-p:UseSharedCompilation=false', '-o', "$out/host")
    Invoke-Dotnet @('build', (Join-Path $repo 'tests/DingTalkInsights.Workbench.RuntimeFixture/DingTalkInsights.Workbench.RuntimeFixture.csproj'), '-c', 'Release', "-p:ArtifactsPath=$out/control-build", '-p:UseSharedCompilation=false', '-o', "$out/control-fixture")
    New-Item -ItemType Directory -Path "$out/host/control-fixture" | Out-Null
    Copy-Item -LiteralPath "$out/control-fixture/DingTalkInsights.Workbench.RuntimeFixture.dll" -Destination "$out/host/control-fixture/"
    $mainPri = Join-Path $out 'host/DingTalkInsights.Workbench.AcceptanceHost.pri'
    if (-not (Test-Path $mainPri) -or (Get-Item $mainPri).Length -eq 0) { throw 'Native acceptance host main PRI missing/empty' }
    if (Test-Path (Join-Path $out 'host/Plugins')) { throw 'Host must not bundle samples or unrelated plugins' }
    Invoke-Dotnet @('restore', $pluginProject, '-r', 'win-x64', "-p:ArtifactsPath=$out/plugin-build")
    Invoke-Dotnet @('build', $pluginProject, '--no-restore', '-c', 'Release', '-r', 'win-x64', "-p:ArtifactsPath=$out/plugin-build", '-p:UseSharedCompilation=false', '-o', "$out/package-build")
    $productCommit = (git -C $repo rev-parse HEAD).Trim()
    Invoke-Dotnet @($tool, 'stage', "$out/package-build", "$out/host", "$out/package", "$pin/provenance.json", $productCommit)
    Copy-Item -LiteralPath "$pin/provenance.json" -Destination "$out/source-provenance.json"
    Invoke-Dotnet @($tool, 'verify-package', "$out/package", "$out/host")
    Compress-Archive -Path "$out/package/*" -DestinationPath "$out/DingTalkInsights.Workbench-0.3.3-win-x64.zip"
    Compress-Archive -Path "$out/host/*" -DestinationPath "$out/DingTalkInsights.Workbench.AcceptanceHost-win-x64.zip"
    $zipHashes = @(Get-FileHash -Algorithm SHA256 "$out/*.zip" | Select-Object Path, Hash)
    $zipHashes | ConvertTo-Json | Set-Content "$out/zip-sha256.json"
    foreach ($zipHash in $zipHashes) { Write-Host "ZIP SHA256 $([IO.Path]::GetFileName($zipHash.Path)) $($zipHash.Hash)" }
}
finally { Invoke-Dotnet @($tool, 'verify-source', $pin) }
Write-Host "Native plugin and separate complete host artifacts: $out"
