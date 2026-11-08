# collect-changes.ps1 - facts the release skill needs, gathered in one read-only pass.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File .claude/skills/release/scripts/collect-changes.ps1 [-Version 0.2]
# Prints the last release tag, commits and merged PRs since then, checks the proposed version, and shows the tree state.
param([string]$Version)

$ErrorActionPreference = 'Stop'
# Tools installed after the shell started (git, gh, dotnet) are missing from PATH in older sessions.
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')

$root = (git rev-parse --show-toplevel).Trim()
Set-Location $root
git fetch --quiet --tags origin

$lastTag = (git tag --list 'v*' --sort=-v:refname | Select-Object -First 1)
if ($lastTag) {
    $lastDate = [DateTimeOffset]::Parse((git log -1 --format=%cI $lastTag).Trim())
    Write-Output "Last release: $lastTag ($($lastDate.ToString('yyyy-MM-dd')))"
    $range = "$lastTag..origin/main"
} else {
    $lastDate = [DateTimeOffset]::MinValue
    Write-Output 'Last release: (none)'
    $range = 'origin/main'
}

Write-Output ''
Write-Output 'Commits since the last release (origin/main, no merges):'
git log $range --no-merges --format='  %h %s'

Write-Output ''
Write-Output 'Pull requests merged into main since the last release:'
$prs = gh pr list --state merged --base main --limit 200 --json number,title,mergedAt,labels | ConvertFrom-Json
$recent = @($prs | Where-Object { [DateTimeOffset]::Parse($_.mergedAt) -gt $lastDate })
if ($recent.Count -eq 0) { Write-Output '  (none)' }
foreach ($pr in $recent) {
    $labels = ($pr.labels | ForEach-Object { $_.name }) -join ', '
    Write-Output ("  #{0} {1}{2}" -f $pr.number, $pr.title, $(if ($labels) { "  [$labels]" } else { '' }))
}

if ($Version) {
    Write-Output ''
    if ($Version -notmatch '^\d+\.\d+(\.\d+)?$') {
        throw "Version '$Version' must be <major>.<minor> or <major>.<minor>.<patch> (the release workflow requires release/v<that>)."
    }
    if (git tag --list "v$Version") { throw "Tag v$Version already exists." }
    if (git ls-remote --heads origin "release/v$Version") { throw "Branch release/v$Version already exists on origin." }
    Write-Output "Version $Version is valid and unused: branch release/v$Version, tag v$Version."
}

Write-Output ''
Write-Output "Current branch: $((git branch --show-current).Trim())"
$dirty = git status --short
Write-Output $(if ($dirty) { "Working tree has changes:`n$($dirty -join "`n")" } else { 'Working tree clean.' })
