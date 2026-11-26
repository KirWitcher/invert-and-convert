$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$icon = Join-Path $root 'Rename.ico'
$version = '1.6.0'
$build = Join-Path ([IO.Path]::GetTempPath()) ('rename-build-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($build)
$archive = Join-Path $build 'libwebp.zip'
$decoder = Join-Path $build 'dwebp.exe'
try {
    Invoke-WebRequest -UseBasicParsing -Uri "https://storage.googleapis.com/downloads.webmproject.org/releases/webp/libwebp-$version-windows-x64.zip" -OutFile $archive
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $entry = $zip.GetEntry("libwebp-$version-windows-x64/bin/dwebp.exe")
        if ($null -eq $entry) { throw 'The WebP decoder was not found in the archive.' }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $decoder)
    } finally { $zip.Dispose() }

    [void][IO.Directory]::CreateDirectory($dist)
    $output = Join-Path $dist 'Rename.exe'
    $arguments = @(
        '/nologo', '/platform:x64', '/target:winexe', "/out:$output",
        "/win32icon:$icon", "/resource:$icon,Rename.ico", "/resource:$decoder,dwebp.exe",
        "/resource:$(Join-Path $root 'WebP-LICENSE'),WebP-LICENSE",
        "/resource:$(Join-Path $root 'WebP-PATENTS'),WebP-PATENTS",
        "/r:$framework\WPF\PresentationCore.dll", "/r:$framework\WPF\WindowsBase.dll", '/r:System.Xaml.dll',
        (Join-Path $root 'Rename.cs'), (Join-Path $root 'Converter.cs')
    )
    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) { throw "Compilation failed (exit code $LASTEXITCODE)." }
    Write-Output $output
} finally {
    foreach ($file in @($archive, $decoder)) { [IO.File]::Delete($file) }
    [IO.Directory]::Delete($build)
}

