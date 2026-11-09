---
name: release
description: Prepare and publish a DockSense release end to end. It picks the version, writes the CHANGELOG entry, updates both READMEs (English + Chinese), bumps the version, opens the release/vX.Y pull request with the "release" label, checks CI and bot reviews, and after the user merges, verifies the published GitHub release. Use when the user asks to release, ship, publish a version, cut a release, 发布, 发版, or 做一个 release.
argument-hint: "[version, e.g. 0.2 or 1.2.3]"
---

# Release DockSense

A release is published by `.github/workflows/release.yml` only when **all three** hold:
1. a PR into `main` is **merged**,
2. its branch is `release/v<major>.<minor>` or `release/v<major>.<minor>.<patch>`,
3. the PR has the **`release`** label.

The workflow reads the version from the branch name, runs the tests, builds self-contained single-file
`DockSense-<version>-win-x64.zip` / `-win-arm64.zip`, tags the merge commit `v<version>`, and publishes the release.
The release description is the version's CHANGELOG section (`.github/scripts/Get-ReleaseNotes.ps1`) followed by
GitHub's generated PR list. CI fails on a release PR without that section. **Never push `v*` tags by hand** and
never create releases with `gh release create`.

Claude cannot merge the PR. The auto-mode classifier blocks merging, and the user wants to approve releases.
Stop at step 8 and hand the merge to the user.

## Shell notes (Windows PowerShell 5.1)
- Refresh PATH first: `$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')`
- Don't use `gh ... --jq '...'` with quotes. Pipe the JSON to `ConvertFrom-Json` instead.
- Commit messages and PR bodies: create a **new** file with the Write tool and pass it via `git commit -F` /
  `gh pr create --body-file`. Don't reuse an old file, and don't use `Set-Content -Encoding utf8`; both put a BOM in
  front of the message.

## Steps

### 1. Gather the facts
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .claude/skills/release/scripts/collect-changes.ps1 [-Version <x.y>]
```
It prints the last tag, the commits and merged PRs since then, validates the version (format, unused tag and
branch), and shows the branch and working-tree state. Start from an up-to-date, clean `main`
(`git switch main; git pull`). If the tree is dirty, ask the user what to do with the changes.

### 2. Pick the version
- If the user passed a version, use it, once the script has validated it.
- Otherwise propose one from the changes: **minor** (`0.1` → `0.2`) for new features, **patch** (`0.2` → `0.2.1`) for
  fixes only, **major** for breaking config or behavior changes. Confirm it with the user before continuing.
- Two-part versions (`0.2`) are fine. Keep the scheme the project has been using.

### 3. Write the CHANGELOG entry
This section becomes the GitHub release description word for word, so write it for the release page.
Check it renders as expected with
`./.github/scripts/Get-ReleaseNotes.ps1 -Version <version> -RepositoryUrl https://github.com/rozx/DockSense`.
Edit `CHANGELOG.md` ([Keep a Changelog](https://keepachangelog.com/en/1.1.0/)):
- Add `## [<version>] - <YYYY-MM-DD>` directly under the intro, above the previous release.
- Sort entries under `### Added`, `### Changed`, `### Fixed`, `### Removed` (only the sections that have entries).
- Write for users, not for the git log. Describe each change by its effect ("Docked mode now ignores virtual
  displays"), and merge related commits into one entry. Leave out CI, refactors and docs-only commits unless they
  matter to users. Credit outside contributors with `(#PR, @user)`.
- Add the link reference at the bottom: `[<version>]: https://github.com/rozx/DockSense/releases/tag/v<version>`.

### 4. Update the READMEs (always both)
`README.md` (English) and `README.zh-CN.md` (Chinese) mirror each other section by section. Go through these:
- **Features** list: add new capabilities.
- **Roadmap**: tick (`[x]`) or remove items that shipped, and add newly planned ones.
- **How it works** table and diagram: update if the mode detection or default profiles changed.
- **Configuration** table: add new `config.json` keys with their defaults.
- **Supported hardware**: add devices confirmed in issues or PRs.
- **FAQ**: add answers to questions that came up.
- **Screenshots**: if the UI changed visibly, re-capture them (see below).
Never update only one language. If unsure about a Chinese phrasing, reuse the terms already in `README.zh-CN.md` and
`src/DockSense.App/Localization/Strings.zh-Hans.resx`.

Re-capturing screenshots (`docs/images/*.png` and `*.zh-CN.png`, 1080×760 window):
1. Build. Close any running DockSense, then run `src/DockSense.App/bin/Debug/net10.0-windows10.0.19041.0/DockSense.exe --show`.
2. Click through the pages with UI Automation (match navigation items by name) and grab the window with `PrintWindow`
   (flag `PW_RENDERFULLCONTENT` = 2), so windows on top don't get into the shot.
3. For the Chinese set, set `"ui": { "language": "zh-Hans" }` in `%APPDATA%\DockSense\config.json`, restart, and
   capture again. **Restore the user's previous language afterwards.**

### 5. Bump the version
Set `<Version>` in `Directory.Build.props` to the release version, padded to three parts (`0.2` → `0.2.0`). The
workflow passes `-p:Version` anyway; this keeps local builds and the About page in sync.

### 6. Verify locally
```powershell
dotnet build DockSense.slnx -c Release
dotnet test tests/DockSense.Core.Tests -c Release
```
Both must pass with 0 warnings (`TreatWarningsAsErrors` is on). Stop and fix if not.

### 7. Branch, commit, push, open the PR
```powershell
git switch -c release/v<version>
git add CHANGELOG.md README.md README.zh-CN.md Directory.Build.props docs/images   # plus anything else you changed
git commit -F <fresh message file>          # e.g. "Release v<version>" plus a short summary
git push -u origin release/v<version>
gh pr create --base main --head release/v<version> --title "Release v<version>" --body-file <fresh body file> --label release
```
PR body: a link to the CHANGELOG section, the highlights, and what was tested on real hardware. Say plainly that
merging publishes the release.

### 8. Checks and reviews, then hand over
- `gh pr checks <n> --repo rozx/DockSense --watch --interval 10`. CI (`build`) must pass.
- Read the bot reviews (Kilo Code Review, Codex):
  - `gh pr view <n> --comments`
  - `gh api repos/rozx/DockSense/pulls/<n>/comments | ConvertFrom-Json` for inline comments
  - `gh api repos/rozx/DockSense/issues/<n>/reactions | ConvertFrom-Json` (Codex reacts 👍 when it found nothing)
- Fix real findings on the release branch and push again. Summarize the rest for the user.
- Then **stop** and ask the user to merge, either with GitHub's "Merge pull request" button or with
  `! gh pr merge <n> --repo rozx/DockSense --merge`.

### 9. After the user merges
```powershell
gh run list --repo rozx/DockSense --workflow release.yml --limit 3
gh run watch <run-id> --repo rozx/DockSense --exit-status --interval 15
gh release view v<version> --repo rozx/DockSense --json name,tagName,url,assets | ConvertFrom-Json
```
- Confirm that both zips are attached, the tag points at the merge commit, and the release description starts with
  the CHANGELOG notes (`gh release view v<version> --json body`), not only GitHub's PR list.
- Smoke-test the x64 build: download it into a fresh scratch folder, extract it, and run
  `DockSense.exe --probe <scratch>\probe.txt`. The report must list the adapters and a resolved mode.
- If the workflow skipped the release job, check the branch name pattern and the label. A wrong label or branch
  name can't be fixed after the merge: open a new release PR from a correctly named branch.
- Finally: `git switch main; git pull`, and give the user the release URL and the highlights.
