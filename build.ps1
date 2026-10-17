param([string]$DotnetPath = 'dotnet')

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\Goalkeeper\Goalkeeper.csproj'
$output = Join-Path $PSScriptRoot 'artifacts\publish'
$dotnet = Get-Command $DotnetPath -CommandType Application -ErrorAction Stop

Push-Location $PSScriptRoot
try {
    & $dotnet.Source publish $project -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
        -o $output --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
} finally {
    Pop-Location
}
Write-Output "Built: $(Join-Path $output 'Goalkeeper.exe')"
