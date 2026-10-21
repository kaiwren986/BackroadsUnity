<#
.SYNOPSIS
  Pushes this project to its own private GitHub repository, and nowhere else (QUEUE U1, the user,
  2026-10-21: "lift the push block for this GitHub repo only").

.DESCRIPTION
  The agents' settings block every raw "git push" (.claude\settings.json), so nothing can be pushed by
  accident, above all not the browser game at D:\Backroads. This script is the one way through:
  - it pushes only the main checkout of this project (never a worktree's own copy, never another repo);
  - only to the remote named origin, and only when origin is $GitHubRemote below;
  - never with --force: a rejected push (the desktop pushed first) stops with a message, and the fix
    is to pull and merge;
  - Git LFS files go up with it (the LFS pre-push hook).
  Git asks Windows' credential manager for the GitHub login; if it shows a sign-in window, the user
  signs in there. Agents never type or see the credentials.
  By default it pushes every local branch and every tag. -Branch pushes one branch (and the tags).

.EXAMPLE
  tools\ci\push-github.ps1
  tools\ci\push-github.ps1 -Branch main
  tools\ci\push-github.ps1 -DryRun
#>
[CmdletBinding()]
param(
    [string]$Branch = '',
    [switch]$DryRun
)
. "$PSScriptRoot\env.ps1"
. "$PSScriptRoot\ci-lib.ps1"

$GitHubRemote = 'https://github.com/kaiwren986/BackroadsUnity.git'

function Invoke-Git([string[]]$GitArgs) {
    & git -C $MAIN_REPO @GitArgs
    if ($LASTEXITCODE -ne 0) { throw "git $($GitArgs -join ' ') failed (exit $LASTEXITCODE)" }
}

$repo = [IO.Path]::GetFullPath($MAIN_REPO).TrimEnd('\')
if ($repo -ieq [IO.Path]::GetFullPath($ORACLE).TrimEnd('\')) { throw "refusing: $repo is the browser game (the oracle), which is never pushed from here" }
$url = (& git -C $repo remote get-url origin 2>$null)
if ($LASTEXITCODE -ne 0 -or -not $url) { throw "refusing: $repo has no remote named origin (expected $GitHubRemote)" }
if ($url.Trim() -ne $GitHubRemote) { throw "refusing: origin is '$url', not $GitHubRemote" }

$refs = @('--all')
if ($Branch) {
    & git -C $repo rev-parse --verify --quiet "refs/heads/$Branch" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "no local branch '$Branch'" }
    $refs = @("refs/heads/${Branch}:refs/heads/$Branch")
}
$dry = @()
if ($DryRun) { $dry = @('--dry-run') }

$sw = [Diagnostics.Stopwatch]::StartNew()
Write-BrLog "pushing $repo to $GitHubRemote ($(if ($Branch) { "branch $Branch" } else { 'every branch' }) and every tag)$(if ($DryRun) { ', dry run' })"
Invoke-Git (@('push', '--set-upstream') + $dry + @('origin') + $refs)
Invoke-Git (@('push') + $dry + @('origin', '--tags'))
Write-BrLog ("push done in {0:N0} s" -f $sw.Elapsed.TotalSeconds) 'ok'
