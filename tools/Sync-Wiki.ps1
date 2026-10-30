param(
    [Parameter(Mandatory = $true)]
    [string]$WikiPath
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$wikiRoot = (Resolve-Path -LiteralPath $WikiPath).Path
if (-not (Test-Path -LiteralPath (Join-Path $wikiRoot '.git'))) {
    throw 'WikiPath must be a cloned GitHub wiki repository.'
}
$repoUrl = 'https://github.com/pablo906/NvTgpHold'
$encoding = New-Object System.Text.UTF8Encoding($false)
$pages = Get-ChildItem -LiteralPath (Join-Path $repoRoot 'docs') -Filter '*.md'
foreach ($page in $pages) {
    $name = if ($page.Name -eq 'README.md') { 'Home' } else { $page.BaseName }
    $text = [IO.File]::ReadAllText($page.FullName)
    $text = [regex]::Replace($text, '\]\(([^)]+)\)', {
        param($match)
        $link = $match.Groups[1].Value
        if ($link -match '^(https?://|#|mailto:)') { return $match.Value }
        if ($link.StartsWith('../')) {
            return '](' + $repoUrl + '/blob/main/' + $link.Substring(3) + ')'
        }
        if ($link -match '^([^/#]+)\.md(#[^ ]*)?$') {
            $target = if ($Matches[1] -eq 'README') { 'Home' } else { $Matches[1] }
            return '](' + $repoUrl + '/wiki/' + $target + $Matches[2] + ')'
        }
        return '](' + $repoUrl + '/blob/main/docs/' + $link + ')'
    })
    [IO.File]::WriteAllText((Join-Path $wikiRoot ($name + '.md')), $text, $encoding)
}
$sidebar = @'
- [Home](Home)
- [Installation](Installation)
- [Usage](Usage)
- [Safety](Safety)
- [Troubleshooting](Troubleshooting)
- [Building from source](Building-from-Source)
- [EC CPU ladder reference](EC-Ladder)
'@
[IO.File]::WriteAllText((Join-Path $wikiRoot '_Sidebar.md'), $sidebar + "`n", $encoding)
Write-Output "Generated $($pages.Count) wiki pages and navigation."
