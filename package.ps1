param(
    [string]$DotnetPath = 'dotnet',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
if (!$SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -DotnetPath $DotnetPath }

[xml]$project = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'src\Goalkeeper\Goalkeeper.csproj') -Raw
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected a numeric three-part project version.' }
$output = Join-Path $PSScriptRoot 'artifacts\release'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$exe = Join-Path $PSScriptRoot 'artifacts\publish\Goalkeeper.exe'
$archivePath = Join-Path $output "Goalkeeper-v$version-windows-x64.zip"
$temporaryArchive = Join-Path $output ('.package-' + [Guid]::NewGuid().ToString('N') + '.zip')

# Explicitly list public source files. Never package settings, logs, or SDK caches.
$files = [ordered]@{ 'Goalkeeper.exe' = $exe }
foreach ($relative in @(
    'README.md', 'LICENSE', 'build.ps1', 'package.ps1', 'global.json',
    '.gitignore', '.gitattributes', '.github/workflows/build.yml',
    'src/Goalkeeper/Program.cs', 'src/Goalkeeper/Goalkeeper.csproj', 'src/Goalkeeper/app.manifest'
)) { $files[$relative] = Join-Path $PSScriptRoot $relative }

$hashes = @{}
$checksumLines = foreach ($relative in $files.Keys) {
    $hashes[$relative] = (Get-FileHash -LiteralPath $files[$relative] -Algorithm SHA256).Hash
    '{0}  {1}' -f $hashes[$relative].ToLowerInvariant(), $relative
}
$checksumText = ($checksumLines -join "`n") + "`n"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($temporaryArchive, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($relative in $files.Keys) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive, $files[$relative], "Goalkeeper/$relative", [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
    $entry = $archive.CreateEntry('Goalkeeper/SHA256SUMS.txt')
    $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
    try { $writer.Write($checksumText) } finally { $writer.Dispose() }
} finally { $archive.Dispose() }

# Reopen the actual ZIP and verify every uncompressed entry before publishing it.
$archive = [IO.Compression.ZipFile]::OpenRead($temporaryArchive)
try {
    if ($archive.Entries.Count -ne $files.Count + 1) { throw 'Unexpected ZIP entry count.' }
    foreach ($relative in $files.Keys) {
        $entry = $archive.GetEntry("Goalkeeper/$relative")
        if ($null -eq $entry) { throw "Missing ZIP entry: $relative" }
        $stream = $entry.Open()
        $sha256 = [Security.Cryptography.SHA256]::Create()
        try {
            $actual = [BitConverter]::ToString($sha256.ComputeHash($stream)).Replace('-', '')
        } finally { $sha256.Dispose(); $stream.Dispose() }
        if ($actual -ne $hashes[$relative]) { throw "ZIP hash mismatch: $relative" }
    }
    $reader = [IO.StreamReader]::new($archive.GetEntry('Goalkeeper/SHA256SUMS.txt').Open())
    try {
        if ($reader.ReadToEnd() -ne $checksumText) { throw 'ZIP checksum manifest mismatch.' }
    } finally { $reader.Dispose() }
} finally { $archive.Dispose() }

Move-Item -LiteralPath $temporaryArchive -Destination $archivePath -Force
$releasedExe = Join-Path $output 'Goalkeeper.exe'
Copy-Item -LiteralPath $exe -Destination $releasedExe -Force
$releaseChecksums = foreach ($file in @($archivePath, $releasedExe)) {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant(), [IO.Path]::GetFileName($file)
}
[IO.File]::WriteAllLines((Join-Path $output 'SHA256SUMS.txt'), $releaseChecksums, [Text.UTF8Encoding]::new($false))
Write-Output "Verified $($files.Count + 1) bundle entries: $archivePath"
