<#
.SYNOPSIS
  Server canary and multiplayer soak (PLAN 1.9): the ServerHeadless build plus N bot clients, all
  bound to 127.0.0.1 so Windows Firewall never prompts [S23].

.DESCRIPTION
  -Clients 0 (M0-M3): the server canary. BackroadsServer.exe -batchmode -nographics -br-bind 127.0.0.1
  -br-quit-after <s>; passes when it exits 0, its log says "SERVER BOOT OK" and every socket it
  opened was on loopback. -Minutes N keeps the server up N minutes instead of 10 seconds.
  -Clients N > 0 needs the bot client (BotClient in Backroads.Testing, N lane, M4). Until its command
  line is confirmed the script refuses with a clear message; -DryRun prints the planned commands.
  -ServerLinux runs the Linux server in WSL2 (needs U4b).

.EXAMPLE
  tools\ci\soak.ps1 -Clients 0
  tools\ci\soak.ps1 -Clients 0 -Minutes 10
  tools\ci\soak.ps1 -Clients 4 -Minutes 10 -Net 150,20,2 -DryRun
#>
[CmdletBinding()]
param(
    [int]$Clients = 0,
    [int]$Minutes = 0,
    [string[]]$Net = @(),
    [switch]$ServerLinux,
    [string]$Milestone = '',
    [string]$ServerExe = '',
    [string]$ClientExe = '',
    [int]$Port = 7777,
    [int]$CanarySeconds = 10,
    [string]$Out = '',
    [string]$GateName = '',
    [switch]$WaitForUser,
    [switch]$DryRun
)
. "$PSScriptRoot\env.ps1"
. "$PSScriptRoot\ci-lib.ps1"

# Flip to $true when the N lane lands BotClient and confirms the bot flags in tools\ci\README.md.
$BOT_CLI_CONFIRMED = $false

$Milestone = Get-BrMilestone $Milestone
if (-not $GateName) { if ($Clients -eq 0) { $GateName = 'soak.server-canary' } else { $GateName = "soak.c$Clients" } }
if (-not $Out) { $Out = New-BrRunDir $GateName } else { New-Item -ItemType Directory -Force $Out | Out-Null }
$seconds = $CanarySeconds
if ($Minutes -gt 0) { $seconds = $Minutes * 60 }
$gate = New-BrGate -Gate $GateName -BudgetSeconds ($seconds + 300) -Out $Out -FallbackIfFail 'server canary: fix ServerMain boot; soak: see the N lane card'

$netSpec = (($Net | ForEach-Object { "$_" -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ }) -join ',')
if ($netSpec -and $netSpec -notmatch '^\d+,\d+,\d+(\.\d+)?$') { throw "-Net is latencyMs,jitterMs,lossPercent (got '$netSpec')" }
$gate.conditions = [ordered]@{ clients = $Clients; seconds = $seconds; net = $netSpec; bind = '127.0.0.1'; serverLinux = [bool]$ServerLinux; milestone = $Milestone }

function ConvertTo-WslPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    return ('/mnt/' + $full.Substring(0, 1).ToLowerInvariant() + ($full.Substring(2) -replace '\\', '/'))
}

try {
    $notReady = $null
    $profileName = 'ServerHeadless'
    if ($ServerLinux) { $profileName = 'ServerLinux' }
    if (-not $ServerExe) { $ServerExe = Resolve-BrPlayerExe $profileName $Milestone }
    if (-not $ServerExe) {
        $ServerExe = "<builds>\$Milestone-server\BackroadsServer.exe"
        if ($ServerLinux) { $notReady = "no Linux server build for $Milestone (needs U4b and build-player.ps1 -Profile ServerLinux)" }
        else { $notReady = "no ServerHeadless build for $Milestone yet: run tools\ci\build-player.ps1 -Profile ServerHeadless -Milestone $Milestone" }
    } elseif (-not [IO.Path]::IsPathRooted($ServerExe)) { $ServerExe = Join-Path $REPO $ServerExe }
    if ($Clients -gt 0) {
        if (-not $ClientExe) { $ClientExe = Resolve-BrPlayerExe 'Dev' $Milestone }
        if (-not $ClientExe) { $ClientExe = "<builds>\$Milestone-dev\Backroads.exe"; if (-not $notReady) { $notReady = "no Dev build for $Milestone for the bot clients" } }
        if (-not $BOT_CLI_CONFIRMED -and -not $notReady) {
            $notReady = 'bot clients need BotClient (Backroads.Testing, N lane, from M4) and a confirmed bot command line; until then only -Clients 0 (server canary) runs'
        }
    }

    $quitAfter = $seconds
    $srvArgs = @('-batchmode', '-nographics', '-br-bind', '127.0.0.1')
    if ($Clients -gt 0) { $srvArgs += @('-br-port', "$Port") }
    $srvArgs += @('-br-quit-after', "$quitAfter", $BR_PLAYER_MARKER, '-logFile')
    $srvLog = Join-Path $Out 'server.log'
    $srvFile = $ServerExe
    $srvFull = $srvArgs + @($srvLog)
    if ($ServerLinux) {
        $srvFile = 'wsl.exe'
        $srvFull = @('-d', $WSL_DISTRO, '--', (ConvertTo-WslPath $ServerExe)) + $srvArgs + @((ConvertTo-WslPath $srvLog))
    }
    $botCmds = @()
    for ($i = 1; $i -le $Clients; $i++) {
        $bo = Join-Path $Out "bot$i"
        $ba = @('-batchmode', '-nographics', '-br-bot', '-br-connect', "127.0.0.1:$Port", '-br-bind', '127.0.0.1')
        if ($netSpec) { $ba += @('-br-net', $netSpec) }
        $ba += @('-br-quit-after', "$quitAfter", '-br-out', $bo, $BR_PLAYER_MARKER, '-logFile', (Join-Path $bo 'player.log'))
        $botCmds += , @($bo, $ba)
    }

    if ($DryRun) {
        if ($notReady) { Add-BrWarning $gate "not runnable yet: $notReady" }
        Write-BrLog ('"' + $srvFile + '" ' + (ConvertTo-BrArgString $srvFull)) 'cmd'
        foreach ($b in $botCmds) { Write-BrLog ('"' + $ClientExe + '" ' + (ConvertTo-BrArgString $b[1])) 'cmd' }
        Write-BrLog 'checks: exit 0, "SERVER BOOT OK" in server.log, loopback-only sockets, hash logs equal (from M4)' 'cmd'
        Set-BrGateStatus $gate 'dryrun' 'dry run: commands printed, nothing started'
    } elseif ($notReady) {
        Set-BrGateStatus $gate 'error' $notReady
    } elseif (-not (Assert-BrUserNotPlaying -What 'a soak' -Wait:$WaitForUser)) {
        Set-BrGateStatus $gate 'blocked' ("the user is playing: $BR_PLAYING_REASON")
    } else {
        $problems = @()
        $state = @{ seen = @{}; bad = (New-Object System.Collections.ArrayList); next = 0 }
        $tick = {
            param($p, $el)
            if ($ServerLinux -or $el.TotalSeconds -lt $state.next) { return }
            $state.next = $el.TotalSeconds + 1
            foreach ($ep in (Get-BrProcessEndpoints (@($p.Id) + @(Get-BrChildProcessIds $p.Id)))) {
                $t = Test-BrEndpoint $ep $false
                $k = "$($ep.proto) $($ep.local):$($ep.localPort) $($ep.remote):$($ep.remotePort) $($ep.state)"
                if (-not $t.Ok -and -not $state.seen.ContainsKey($k)) { $state.seen[$k] = $true; [void]$state.bad.Add("$k : $($t.Reason)") }
            }
        }
        $bots = @()
        if ($Clients -gt 0) {
            # Server first, then the bots; the server is the process we wait on.
            foreach ($b in $botCmds) { New-Item -ItemType Directory -Force $b[0] | Out-Null }
        }
        Write-BrLog ('"' + $srvFile + '" ' + (ConvertTo-BrArgString $srvFull)) 'cmd'
        if ($Clients -gt 0) {
            $psi = New-Object System.Diagnostics.ProcessStartInfo
            $psi.FileName = $srvFile; $psi.Arguments = ConvertTo-BrArgString $srvFull; $psi.UseShellExecute = $false
            $srv = [System.Diagnostics.Process]::Start($psi)
            Start-Sleep -Seconds 5
            foreach ($b in $botCmds) {
                Write-BrLog ('"' + $ClientExe + '" ' + (ConvertTo-BrArgString $b[1])) 'cmd'
                $bpsi = New-Object System.Diagnostics.ProcessStartInfo
                $bpsi.FileName = $ClientExe; $bpsi.Arguments = ConvertTo-BrArgString $b[1]; $bpsi.UseShellExecute = $false
                $bots += [System.Diagnostics.Process]::Start($bpsi)
            }
            $deadline = (Get-Date).AddSeconds($quitAfter + 180)
            while (-not $srv.HasExited -and (Get-Date) -lt $deadline) { & $tick $srv ((Get-Date) - $srv.StartTime); Start-Sleep -Seconds 1 }
            if (-not $srv.HasExited) { [void](Stop-BrProcessTree $srv.Id); $problems += 'server did not quit in time' }
            $srv.WaitForExit()
            $code = $srv.ExitCode
            foreach ($bp in $bots) { if (-not $bp.WaitForExit(60000)) { [void](Stop-BrProcessTree $bp.Id); $problems += "bot pid $($bp.Id) did not quit" } elseif ($bp.ExitCode -ne 0) { $problems += "bot pid $($bp.Id) exited $($bp.ExitCode)" } }
        } else {
            $r = Invoke-BrProcess -FilePath $srvFile -ArgumentList $srvFull -TimeoutSec ($quitAfter + 120) -OnTick $tick -TickSec 1
            $code = $r.ExitCode
            if ($r.TimedOut) { $problems += 'server did not quit in time (-br-quit-after ignored?)' }
        }
        $gate.metrics.serverExitCode = $code
        Add-BrEvidence $gate $srvLog
        if ($code -ne 0) { $problems += "server exited $code" }
        $boot = $false
        if (Test-Path -LiteralPath $srvLog) { $boot = [bool](Select-String -LiteralPath $srvLog -Pattern 'SERVER BOOT OK' -SimpleMatch -Quiet) }
        $gate.metrics.serverBootOk = $boot
        if (-not $boot) { $problems += 'server.log has no "SERVER BOOT OK" line' }
        $gate.metrics.nonLoopbackSockets = @($state.bad)
        if ($state.bad.Count) { $problems += ('server opened non-loopback sockets: ' + ($state.bad -join '; ')) }
        if ($ServerLinux) { Add-BrWarning $gate 'socket check skipped: the server runs inside WSL2' }
        # Hash logs (from M4): every participant writes hashes.log; all must be identical.
        $hashLogs = @()
        foreach ($d in @($Out) + @($botCmds | ForEach-Object { $_[0] })) { $h = Join-Path $d 'hashes.log'; if (Test-Path -LiteralPath $h) { $hashLogs += $h } }
        if ($hashLogs.Count -ge 2) {
            $ref = (Get-FileHash -LiteralPath $hashLogs[0] -Algorithm SHA256).Hash
            $diff = @($hashLogs | Where-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash -ne $ref })
            $gate.metrics.hashLogsCompared = $hashLogs.Count
            if ($diff.Count) { $problems += ('hash logs differ: ' + (($diff | ForEach-Object { ConvertTo-BrRelPath $_ }) -join ', ')) }
        } else { $gate.metrics.hashLogsCompared = 0 }
        if ($problems.Count) { Set-BrGateStatus $gate 'fail' ($problems -join '; ') }
        else { Set-BrGateStatus $gate 'pass' ("server booted, quit cleanly after $quitAfter s, loopback only" + $(if ($Clients -gt 0) { ", $Clients bots" } else { '' })) }
    }
} catch {
    Set-BrGateStatus $gate 'error' ("soak.ps1: " + $_.Exception.Message)
} finally {
    Unlock-BrAll
}
exit (Complete-BrGate $gate -DryRun:$DryRun)
