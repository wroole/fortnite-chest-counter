param([string]$Cargo = 'cargo')
$ErrorActionPreference = 'Stop'
$taskSource = $PSScriptRoot
$taskApp = Split-Path -Parent $taskSource
$taskBuild = Join-Path $taskSource 'target'
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $taskCompiler -PathType Leaf)) { throw 'The Windows x64 .NET Framework compiler is missing. Use the portable ZIP on Windows 10/11 x64 with .NET Framework 4.8.' }
if (-not (Get-Command -Name $Cargo -ErrorAction SilentlyContinue)) { throw 'Rust/Cargo is not installed or is not on PATH. Install Rust from https://rust-lang.org/install, accept the C++ build tools when prompted, then reopen this launcher. For instant use without build tools, download FortniteChestCounter.zip from GitHub Releases.' }
$env:CARGO_TARGET_DIR = $taskBuild
# Keep machine/user paths out of diagnostic strings embedded in public binaries.
$taskPreviousEncodedFlags = $env:CARGO_ENCODED_RUSTFLAGS
$taskRustFlags = @()
if ($taskPreviousEncodedFlags) { $taskRustFlags = @($taskPreviousEncodedFlags.Split([char]31)) }
elseif ($env:RUSTFLAGS) { $taskRustFlags = @($env:RUSTFLAGS.Trim() -split '\s+') }
if ($env:USERPROFILE) { $taskRustFlags += '--remap-path-prefix=' + $env:USERPROFILE + '=build-user' }
$taskRustFlags += '--remap-path-prefix=' + $taskApp + '=app'
try {
    $env:CARGO_ENCODED_RUSTFLAGS = $taskRustFlags -join [char]31
    & $Cargo build --release --locked --manifest-path (Join-Path $taskSource 'Cargo.toml')
    if ($LASTEXITCODE -ne 0) { throw 'Rust build failed' }
} finally { $env:CARGO_ENCODED_RUSTFLAGS = $taskPreviousEncodedFlags }
New-Item -ItemType Directory -Path (Join-Path $taskApp 'bin') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $taskBuild 'release\replay-analyzer.exe') -Destination (Join-Path $taskApp 'bin\replay-analyzer.exe')
& $taskCompiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 "/win32manifest:$taskSource\ui\app.manifest" "/out:$taskApp\ChestCounter.exe" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll (Join-Path $taskSource 'ui\ChestCounter.cs')
if ($LASTEXITCODE -ne 0) { throw 'UI build failed' }
Copy-Item -LiteralPath (Join-Path $taskSource 'ui\ChestCounter.exe.config') -Destination (Join-Path $taskApp 'ChestCounter.exe.config')
Write-Output ('Built: ' + (Join-Path $taskApp 'ChestCounter.exe'))
