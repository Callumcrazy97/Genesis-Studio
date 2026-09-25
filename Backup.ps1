<#
.SYNOPSIS
    Timestamped clean source backup of Genesis Studio.

.DESCRIPTION
    Creates a clean, portable ZIP containing strictly what is required for the application
    to be sent to another machine and built/run out of the zip:
    - Source code (Source/)
    - Test suites (Tests/)
    - Themes & design assets (Themes/, Assets/)
    - Build automation & installer tooling (BuildTools/, Installer/)
    - Documentation & specifications (Documentation/)
    - Solution & build entry points (Genesis.Application.slnx, Build.bat, DeveloperRequirementsInstaller.ps1, etc.)

    All build outputs (bin, obj, .build, Dist, Genesis Application, TestResults), legacy archives
    (Ignore/), temporary IDE/AI folders, and root scratch files are strictly excluded and pruned
    during directory traversal so deep or volatile build paths never cause path-too-long or
    missing-file errors.

.PARAMETER Destination
    Where to write the archive. Defaults to a "Backups" folder beside the project root.

.PARAMETER Label
    Optional suffix for the archive name, e.g. -Label "pre-T1".

.PARAMETER KeepLast
    How many archives to retain; older ones are pruned. Default 10. Use 0 to keep everything.

.EXAMPLE
    .\Backup.ps1 -Label "clean-source"
#>
[CmdletBinding()]
param(
    [string]$Destination,
    [string]$Label,
    [int]$KeepLast = 10
)

$ErrorActionPreference = 'Stop'

$projectRoot = $PSScriptRoot
$projectName = Split-Path $projectRoot -Leaf

if (-not $Destination) {
    $Destination = Join-Path (Split-Path $projectRoot -Parent) 'Backups'
}

if (-not (Test-Path -LiteralPath $Destination)) {
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
}

# 1. Top-level directories that are part of the active product source & assets
$criticalRootDirs = @(
    'Source',
    'Tests',
    'Themes',
    'Assets',
    'BuildTools',
    'Installer',
    'Documentation'
)

# 2. Directory names to PRUNE (never recurse into these anywhere in the tree)
$excludedDirNames = [System.Collections.Generic.HashSet[string]]::new(
    [string[]]@(
        'bin',
        'obj',
        '.vs',
        '.git',
        'TestResults',
        'Workspace',
        'node_modules',
        'packages',
        'runtimes',
        '.build',
        'Dist',
        'Genesis Application',
        'Rendering Backends',
        'Ignore',
        'AiControl',
        '.claude',
        '.gemini',
        '.codex-remote-attachments',
        '.validation',
        '--nologo',
        'Backups',
        'EditorReview',
        'Application Design Images',
        'redist'
    ),
    [System.StringComparer]::OrdinalIgnoreCase
)

# 3. File extensions that should never be in a source backup
$excludedExtensions = [System.Collections.Generic.HashSet[string]]::new(
    [string[]]@(
        '.zip',
        '.log',
        '.bak',
        '.tmp',
        '.suo',
        '.user',
        '.cache',
        '.exe',
        '.dll',
        '.pdb',
        '.lib',
        '.so',
        '.dylib',
        '.nupkg',
        '.msi'
    ),
    [System.StringComparer]::OrdinalIgnoreCase
)

# 4. Root-level files that are critical to build and maintain the project
$criticalRootFiles = @(
    'Genesis.Application.slnx',
    'Build.bat',
    'Backup.ps1',
    'DeveloperRequirementsInstaller.ps1',
    'AGENTS.md',
    'README.md',
    '.gitignore',
    'Genesis_Studio_Logo.jpg'
)

$stamp = Get-Date -Format 'yyyy-MM-dd_HHmmss'
$suffix = if ($Label) { "_$($Label -replace '[^\w\-\.]', '-')" } else { '' }
$archivePath = Join-Path $Destination "$projectName`_$stamp$suffix.zip"

Write-Host "Backing up : $projectRoot"
Write-Host "To         : $archivePath"
Write-Host ''

$selectedFiles = [System.Collections.Generic.List[string]]::new()

# Collect critical root files
foreach ($fileName in $criticalRootFiles) {
    $filePath = [System.IO.Path]::Combine($projectRoot, $fileName)
    if ([System.IO.File]::Exists($filePath)) {
        $selectedFiles.Add($filePath)
    }
}

# Recursively collect files from critical directories with early directory pruning
function Enumerate-DirectoryClean([string]$currentDir) {
    $dirName = [System.IO.Path]::GetFileName($currentDir)
    if ($excludedDirNames.Contains($dirName)) {
        return
    }

    try {
        foreach ($filePath in [System.IO.Directory]::EnumerateFiles($currentDir)) {
            $ext = [System.IO.Path]::GetExtension($filePath)
            if (-not $excludedExtensions.Contains($ext)) {
                $selectedFiles.Add($filePath)
            }
        }
    }
    catch [System.Exception] {
        Write-Warning "Could not read files in $currentDir : $_"
    }

    try {
        foreach ($subDir in [System.IO.Directory]::EnumerateDirectories($currentDir)) {
            Enumerate-DirectoryClean $subDir
        }
    }
    catch [System.Exception] {
        Write-Warning "Could not enumerate subdirectories in $currentDir : $_"
    }
}

foreach ($dirName in $criticalRootDirs) {
    $dirPath = [System.IO.Path]::Combine($projectRoot, $dirName)
    if ([System.IO.Directory]::Exists($dirPath)) {
        Enumerate-DirectoryClean $dirPath
    }
}

if ($selectedFiles.Count -eq 0) {
    throw "Found no critical files to back up under '$projectRoot'."
}

$staging = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "GenesisBackup_$stamp")
[System.IO.Directory]::CreateDirectory($staging) | Out-Null

try {
    $totalBytes = 0
    foreach ($srcPath in $selectedFiles) {
        $relative = $srcPath.Substring($projectRoot.Length).TrimStart('\', '/')
        $destPath = [System.IO.Path]::Combine($staging, $relative)
        $destDir = [System.IO.Path]::GetDirectoryName($destPath)

        if (-not [System.IO.Directory]::Exists($destDir)) {
            [System.IO.Directory]::CreateDirectory($destDir) | Out-Null
        }

        [System.IO.File]::Copy($srcPath, $destPath, $true)
        $totalBytes += ([System.IO.FileInfo]::new($srcPath)).Length
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $staging,
        $archivePath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false
    )

    $archive = [System.IO.FileInfo]::new($archivePath)
    $sourceMb = [math]::Round($totalBytes / 1MB, 1)
    $archiveMb = [math]::Round($archive.Length / 1MB, 1)

    Write-Host ("Files      : {0:N0}" -f $selectedFiles.Count)
    Write-Host ("Source     : $sourceMb MB")
    Write-Host ("Archive    : $archiveMb MB")
    Write-Host ''
    Write-Host "BACKUP COMPLETE" -ForegroundColor Green
    Write-Host $archivePath
}
finally {
    if ([System.IO.Directory]::Exists($staging)) {
        [System.IO.Directory]::Delete($staging, $true)
    }
}

if ($KeepLast -gt 0) {
    $old = Get-ChildItem -Path $Destination -Filter "$projectName`_*.zip" |
        Sort-Object LastWriteTime -Descending |
        Select-Object -Skip $KeepLast
    foreach ($stale in $old) {
        Write-Host "Pruning old backup: $($stale.Name)"
        Remove-Item -LiteralPath $stale.FullName -Force
    }
}
