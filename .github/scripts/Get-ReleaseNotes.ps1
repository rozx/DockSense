<#
.SYNOPSIS
    Extracts the release notes for one version from CHANGELOG.md.

.DESCRIPTION
    Returns the body of the "## [<version>] - <date>" section, up to the next version heading or the link
    references at the end of the file. Relative links are made absolute (pointing at the v<version> tag),
    so they also work on the GitHub release page. Fails when the section is missing or empty.

.EXAMPLE
    ./.github/scripts/Get-ReleaseNotes.ps1 -Version 0.2 -RepositoryUrl https://github.com/rozx/DockSense -OutFile notes.md
#>
param(
    [Parameter(Mandatory)] [string]$Version,
    [Parameter(Mandatory)] [string]$RepositoryUrl,
    [string]$Changelog = 'CHANGELOG.md',
    [string]$OutFile
)

$ErrorActionPreference = 'Stop'
$text = [IO.File]::ReadAllText((Resolve-Path $Changelog)) -replace "`r`n", "`n"

$pattern = "(?ms)^## \[$([regex]::Escape($Version))\][^\n]*\n(.*?)(?=^## \[|^\[[^\]]+\]:\s|\z)"
$match = [regex]::Match($text, $pattern)
if (-not $match.Success -or [string]::IsNullOrWhiteSpace($match.Groups[1].Value)) {
    throw "$Changelog has no notes for version $Version (expected a '## [$Version] - <date>' section)."
}

$notes = $match.Groups[1].Value.Trim()
$base = "$($RepositoryUrl.TrimEnd('/'))/blob/v$Version/"
$notes = [regex]::Replace($notes, '\]\((?!https?://|mailto:|#)([^)\s]+)\)', { param($m) "]($base$($m.Groups[1].Value))" })

if ($OutFile) {
    [IO.File]::WriteAllText($OutFile, $notes + "`n", (New-Object Text.UTF8Encoding($false)))
}

$notes
