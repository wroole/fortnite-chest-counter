param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$taskRepository = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'check-public-files.ps1')
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $taskRepository 'dist' }
$taskOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskPublicFiles = @(git -c "safe.directory=$taskRepository" -C $taskRepository -c core.quotepath=false ls-files --cached)
if ($LASTEXITCODE -ne 0 -or $taskPublicFiles.Count -eq 0) { throw 'Cannot read the reviewed Git file list.' }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Write-PublicArchive([string]$Name, [bool]$IncludeBinaries) {
    $taskFiles = @($taskPublicFiles)
    if ($IncludeBinaries) {
        $taskFiles += @('ChestCounter.exe', 'ChestCounter.exe.config', 'bin/replay-analyzer.exe')
        $taskChecksums = [System.Text.StringBuilder]::new()
        foreach ($taskRelative in $taskFiles) {
            $taskDigest = Get-FileHash -LiteralPath (Join-Path $taskRepository $taskRelative) -Algorithm SHA256
            [void]$taskChecksums.AppendLine($taskDigest.Hash.ToLowerInvariant() + '  ' + $taskRelative.Replace('\', '/'))
        }
    }
    $taskArchive = Join-Path $taskOutput $Name
    $taskFileStream = [System.IO.File]::Open($taskArchive, [System.IO.FileMode]::Create)
    try {
        $taskZip = [System.IO.Compression.ZipArchive]::new($taskFileStream, [System.IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            foreach ($taskRelative in $taskFiles) {
                $taskSource = [System.IO.Path]::GetFullPath((Join-Path $taskRepository $taskRelative))
                if (-not $taskSource.StartsWith($taskRepository + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Archive path escaped repository.' }
                if (-not (Test-Path -LiteralPath $taskSource -PathType Leaf)) { throw ('Required file is missing: ' + $taskRelative) }
                if ((Get-Item -LiteralPath $taskSource -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint) { throw ('Links are not allowed in distribution files: ' + $taskRelative) }
                $taskEntry = 'FortniteChestCounter/' + $taskRelative.Replace('\', '/')
                [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($taskZip, $taskSource, $taskEntry, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
            }
            if ($IncludeBinaries) {
                $taskManifest = $taskZip.CreateEntry('FortniteChestCounter/SHA256SUMS.txt')
                $taskWriter = [System.IO.StreamWriter]::new($taskManifest.Open(), [System.Text.UTF8Encoding]::new($false))
                try { $taskWriter.Write($taskChecksums.ToString()) } finally { $taskWriter.Dispose() }
            }
        } finally { $taskZip.Dispose() }
    } finally { $taskFileStream.Dispose() }
    Write-Output $taskArchive
    $taskArchiveHash = (Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText($taskArchive + '.sha256', $taskArchiveHash + '  ' + $Name + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
    Write-Output ($taskArchive + '.sha256')
}
Write-PublicArchive 'FortniteChestCounter-source.zip' $false
Write-PublicArchive 'FortniteChestCounter.zip' $true
