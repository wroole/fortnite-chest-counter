$ErrorActionPreference = 'Stop'
$taskRepository = Split-Path -Parent $PSScriptRoot
$taskFiles = @(git -c "safe.directory=$taskRepository" -C $taskRepository -c core.quotepath=false ls-files --cached)
if ($LASTEXITCODE -ne 0 -or $taskFiles.Count -eq 0) { throw 'No Git file list found. Run git add . from the repository first.' }
$taskBlocked = @($taskFiles | Where-Object {
    $_ -match '(^|/)(data|replays|Demos|exports|logs|work|dist|target|bin|\.local|\.git)(/|$)' -or
    $_ -match '(?i)\.(replay|dump|json|csv|tsv|log|bak|tmp)(\.|$)' -or
    $_ -match '(?i)\.(exe|dll|pdb|zip|7z)$' -or
    $_ -match '(^|/)\.env($|\.)' -or $_ -eq 'SHA256SUMS.txt' -or $_ -eq 'ChestCounter.exe.config'
})
if ($taskBlocked.Count -gt 0) { throw ('Private or generated files in Git file list: ' + ($taskBlocked -join ', ')) }
Write-Output ('Privacy check passed: ' + $taskFiles.Count + ' public files; no replay, statistics, export, backup, or executable files.')
