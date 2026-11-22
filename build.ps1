param(
    [Parameter(Mandatory = $true)]
    [string]$dnspy_bin_dir,
    [switch]$run_tests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$dnspy_bin_dir = (Resolve-Path -LiteralPath $dnspy_bin_dir).Path
if (-not (Test-Path -LiteralPath (Join-Path $dnspy_bin_dir 'dnSpy.Contracts.DnSpy.dll') -PathType Leaf)) {
    throw 'Specify the dnSpy bin directory containing dnSpy.Contracts.DnSpy.dll.'
}

Push-Location -LiteralPath $PSScriptRoot
try {
    $project = Join-Path $PSScriptRoot 'call_graph.extension.csproj'
    if ($run_tests) {
        dotnet build 'validation/call_graph.validation.csproj' -c Release "-p:DnSpyBinDir=$dnspy_bin_dir" --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Validation build failed.' }
        dotnet 'validation/bin/Release/net10.0-windows/CallGraph.Validation.dll'
        if ($LASTEXITCODE -ne 0) { throw 'Validation failed.' }
    }
    dotnet build $project -c Release "-p:DnSpyBinDir=$dnspy_bin_dir" --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    $release_dir = Join-Path $PSScriptRoot 'artifacts/release'
    New-Item -ItemType Directory -Path $release_dir -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'bin/Release/CallGraph.Extension.x.dll') -Destination $release_dir -Force
    Write-Output (Join-Path $release_dir 'CallGraph.Extension.x.dll')
} finally {
    Pop-Location
}
