<#
.SYNOPSIS
  Tag, push and publish a GitHub release for the version in VERSION, attaching the built exe.
  Uses the GitHub credential that git already stores on this PC (Git Credential Manager) - no gh CLI needed.
.EXAMPLE
  .\tools\publish.ps1 -Notes release-notes.md          # after committing; builds, tags v<VERSION>, pushes, releases
  .\tools\publish.ps1 -Notes notes.md -ReplaceAsset    # re-upload the exe on an existing release
#>
param(
    [Parameter(Mandatory = $true)][string]$Notes,
    [string]$Owner = 'Oldandcranky',
    [string]$Repo = 'VRModLoader',
    [switch]$ReplaceAsset,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()
$tag = "v$version"
if ((git status --porcelain | Where-Object { $_ -notmatch 'src/Version.cs' })) { throw "Working tree has uncommitted changes - commit first." }

if (-not $NoBuild) { & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'build.ps1') -NoShortcut; if ($LASTEXITCODE -ne 0) { throw 'build failed' } }
$exe = Join-Path $root 'VRModLoader.exe'
$assetName = "VRModLoader-$version.exe"

$sha = (git rev-parse --short HEAD).Trim()
if (-not (git tag -l $tag)) { git tag -a $tag -m "VR Mod Loader $version" | Out-Null; Write-Host ">> tagged $tag at $sha" }
elseif ((git rev-list -n 1 $tag).Substring(0, 7) -ne $sha) { throw "$tag already exists on another commit - move it deliberately (git tag -f) or bump VERSION." }

# git reports push progress on stderr; under PowerShell 5.1 + ErrorActionPreference=Stop that would abort us.
function Push-Ref([string]$ref) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & git push origin $ref 2>&1
        $code = $LASTEXITCODE
        $out | ForEach-Object { Write-Host "  $_" }
        if ($code -ne 0) { throw "git push origin $ref failed with exit code $code" }
    }
    finally { $ErrorActionPreference = $prev }
}
Push-Ref 'HEAD'
Push-Ref $tag

# token from Git Credential Manager, kept in memory only
$fill = @(@('protocol=https', 'host=github.com', '') | & "$env:ProgramFiles\Git\mingw64\bin\git-credential-manager.exe" get 2>$null)
$pw = [string](@($fill | Where-Object { $_ -like 'password=*' } | Select-Object -First 1) -replace '^password=', '')
if (-not $pw) { throw 'No GitHub credential available from Git Credential Manager.' }
$h = @{ Authorization = "token $pw"; Accept = 'application/vnd.github+json'; 'User-Agent' = 'VRModLoader-publish' }
$api = "https://api.github.com/repos/$Owner/$Repo"

$body = Get-Content $Notes -Raw
try { $rel = Invoke-RestMethod -Headers $h -Uri "$api/releases/tags/$tag"; Write-Host ">> release exists: $($rel.html_url)" }
catch {
    $rel = Invoke-RestMethod -Method Post -Headers $h -Uri "$api/releases" -ContentType 'application/json' `
        -Body (@{ tag_name = $tag; name = "VR Mod Loader $version"; body = $body; draft = $false; prerelease = $false } | ConvertTo-Json)
    Write-Host ">> release created: $($rel.html_url)"
}
$existing = @($rel.assets | Where-Object { $_.name -eq $assetName })
if ($existing -and $ReplaceAsset) { foreach ($a in $existing) { Invoke-RestMethod -Method Delete -Headers $h -Uri "$api/releases/assets/$($a.id)" | Out-Null }; $existing = @() }
if (-not $existing) {
    # upload from a copy: a running launcher locks the real exe
    $copy = Join-Path $env:TEMP $assetName
    Copy-Item $exe $copy -Force
    $up = ([string]$rel.upload_url -replace '\{.*\}', '') + "?name=$assetName"
    $asset = Invoke-RestMethod -Method Post -Headers ($h + @{ 'Content-Type' = 'application/octet-stream' }) -Uri $up -InFile $copy
    Write-Host ">> asset: $($asset.browser_download_url) ($($asset.size) bytes)"
} else { Write-Host ">> asset already present (use -ReplaceAsset to overwrite)" }
Write-Host ">> done: $version ($sha)"
