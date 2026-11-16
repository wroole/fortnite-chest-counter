# Publish on GitHub

## Repository

Create an empty repository, then upload the contents of the clean source folder, including `.gitignore` and `.gitattributes`. Do not upload the surrounding desktop bundle or the personal application folder. The repository contains source, documentation, fictional screenshots, and license notices.

Before a commit, run from the repository folder:

```powershell
git init -b main
git add .
powershell -NoProfile -ExecutionPolicy Bypass -File source/check-public-files.ps1
git diff --cached --stat
```

Local statistics, recordings, exports, backups, binaries and archives are ignored. Never force-add these files. The prepared clean source folder has no local statistics or replays.

## Ready-to-run download

1. Commit the source changes to GitHub.
2. Open **Releases → Draft a new release** and create a version tag for that commit.
3. Attach **FortniteChestCounter.zip** and its **FortniteChestCounter.zip.sha256** checksum file from the prepared release folder.
4. Publish the release. Users download this ZIP, extract it, and open **ChestCounter.exe**. No compilation is needed.

GitHub's automatic **Source code (zip)** download contains source only. Keep the portable asset name `FortniteChestCounter.zip` so the README instructions match.

The app uses two compiled executables: the window and `bin/replay-analyzer.exe`. Distribute the whole portable ZIP, not just the main EXE. Every public ZIP starts with an empty history; a `data` folder is created on first import.

## Build a new release

Close the application, install Rust and its Windows linker, then run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File source/build.ps1
git add .
powershell -NoProfile -ExecutionPolicy Bypass -File source/package.ps1
```

Upload the resulting portable ZIP and checksum from `dist`. Packaging uses the checked Git file list and excludes personal data. Review changes and scan the new build before distributing it.

Official guides: [GitHub Releases](https://docs.github.com/en/repositories/releasing-projects-on-github/managing-releases-in-a-repository), [Rust installation](https://rust-lang.org/install).
