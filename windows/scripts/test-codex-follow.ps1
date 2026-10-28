<#
.SYNOPSIS
  Exercises the Windows Codex follow setting with an isolated Debug build and a disposable stand-in process.
.EXAMPLE
  dotnet publish windows/src/AgentSpeed/AgentSpeed.csproj -c Debug -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist/follow-test
  pwsh windows/scripts/test-codex-follow.ps1 -Executable dist/follow-test/AgentSpeed.exe
#>
param([Parameter(Mandatory = $true)][string] $Executable)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$app = (Resolve-Path -LiteralPath $Executable).Path
$testId = [Guid]::NewGuid().ToString('N')
$folder = Join-Path $root "artifacts\follow-e2e\$testId"
$fake = Join-Path $folder 'fake-codex.exe'
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$taskName = "Agent Speed - Codex Follow - $sid.test.$testId"
$request = Join-Path $folder 'capture.request'
$settings = Join-Path $folder 'settings.json'

if (@(Get-CimInstance Win32_Process -Filter "Name = 'AgentSpeed.exe'" | Where-Object { $_.ExecutablePath -eq $app }).Count -ne 0) {
    throw 'The test executable is already running. Use a separate Debug build for this test.'
}
New-Item -ItemType Directory -Path $folder -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $env:WINDIR 'System32\PING.EXE') -Destination $fake
@'
{"language":"ZhHans","followCodex":false,"network":{"enabled":false},"showOrbs":false,"taskbar":{"show":false},"sources":{"codex":false,"claudeCode":false,"claudeDesktop":false,"deepseek":false,"opencode":false,"gemini":false,"omp":false,"antigravity":false,"workbuddy":false,"workbuddyAi":false,"zcode":false,"kimi":false,"grokBuild":false},"traffic":{"enabled":false}}
'@ | Set-Content -LiteralPath $settings -Encoding utf8
$env:AGENT_SPEED_TEST_INSTANCE = $testId
$env:AGENT_SPEED_TEST_CODEX_PATH = $fake
$env:AGENT_SPEED_SETTINGS_FILE = $settings
$env:AGENT_SPEED_LOGS_DIR = Join-Path $folder 'logs'
$env:AGENT_SPEED_HISTORY_FILE = Join-Path $folder 'history.sqlite'
$env:AGENT_SPEED_REPORT_DIR = Join-Path $folder 'report'
$fakeProcess = $null

function Wait-Condition([string]$name, [scriptblock]$condition, [int]$seconds = 20) {
    $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (& $condition) { Write-Output "PASS: $name"; return }
        Start-Sleep -Milliseconds 300
    }
    throw "Timed out: $name"
}
function Send-Capture([string]$command) {
    [IO.File]::WriteAllText($request, $command, [Text.Encoding]::UTF8)
    Wait-Condition "capture request $command" { -not (Test-Path -LiteralPath $request) } 15
}
function Test-Setting([bool]$value) {
    try { return ((Get-Content -Raw -LiteralPath $settings | ConvertFrom-Json).followCodex -eq $value) } catch { return $false }
}
function Test-TaskExists { return $null -ne (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) }
function Get-TestProcesses {
    @(Get-CimInstance Win32_Process -Filter "Name = 'AgentSpeed.exe'" | Where-Object { $_.ExecutablePath -eq $app })
}

try {
    $gui = Start-Process -FilePath $app -ArgumentList @('--capture-dir', "`"$folder`"") -WindowStyle Hidden -PassThru
    Wait-Condition 'isolated application started' { $null -ne (Get-Process -Id $gui.Id -ErrorAction SilentlyContinue) }
    Send-Capture 'op page-general'
    Send-Capture 'settings general'
    Wait-Condition 'general settings screenshot' { Test-Path -LiteralPath (Join-Path $folder 'general.png') }

    Send-Capture 'op follow-on'
    Wait-Condition 'setting enabled and task registered' { (Test-Setting $true) -and (Test-TaskExists) }
    Wait-Condition 'background watcher started' { @(Get-TestProcesses | Where-Object CommandLine -Match '--follow-codex --follow-settings').Count -eq 1 }
    $task = Get-ScheduledTask -TaskName $taskName
    if ($task.Settings.ExecutionTimeLimit -ne 'PT0S' -or $task.Settings.DisallowStartIfOnBatteries -or $task.Settings.StopIfGoingOnBatteries) {
        throw 'Task settings do not match the intended continuous, battery-safe watcher.'
    }
    Write-Output 'PASS: task has no time or battery limit'

    Send-Capture 'op follow-off'
    Wait-Condition 'setting disabled and task removed' { (Test-Setting $false) -and -not (Test-TaskExists) }
    Wait-Condition 'watcher stopped after disabling' { @(Get-TestProcesses | Where-Object CommandLine -Match '--follow-codex --follow-settings').Count -eq 0 }
    Send-Capture 'op follow-on'
    Wait-Condition 'setting re-enabled and task registered' { (Test-Setting $true) -and (Test-TaskExists) }
    Wait-Condition 'watcher restarted' { @(Get-TestProcesses | Where-Object CommandLine -Match '--follow-codex --follow-settings').Count -eq 1 }

    $fakeProcess = Start-Process -FilePath $fake -ArgumentList @('-t', '127.0.0.1') -WindowStyle Hidden -PassThru
    Wait-Condition 'watcher observed Codex startup' { (Get-Content -Raw -LiteralPath (Join-Path $folder 'logs\agentspeed.log') -ErrorAction SilentlyContinue) -match 'Codex follow: started Agent Speed' }
    Start-Sleep -Seconds 3
    if (@(Get-TestProcesses | Where-Object CommandLine -NotMatch '--follow-codex --follow-settings').Count -ne 1) { throw 'Unexpected duplicate UI process.' }
    Write-Output 'PASS: existing UI was not duplicated'
    $fakeProcess.Kill()
    $fakeProcess.WaitForExit()
    $fakeProcess = $null
    Wait-Condition 'UI exited after Codex closed' { $null -eq (Get-Process -Id $gui.Id -ErrorAction SilentlyContinue) } 20
    if ($gui.ExitCode -ne 0) { throw "UI exited with $($gui.ExitCode)" }

    $fakeProcess = Start-Process -FilePath $fake -ArgumentList @('-t', '127.0.0.1') -WindowStyle Hidden -PassThru
    Wait-Condition 'UI restarted for next Codex session' { @(Get-TestProcesses | Where-Object CommandLine -Match '--follow-codex-ui').Count -eq 1 } 25
    $ui = Get-TestProcesses | Where-Object CommandLine -Match '--follow-codex-ui' | Select-Object -First 1
    $uiProcess = Get-Process -Id $ui.ProcessId
    $uiProcess.Kill()
    $uiProcess.WaitForExit()
    Start-Sleep -Seconds 4
    if (@(Get-TestProcesses | Where-Object CommandLine -Match '--follow-codex-ui').Count -ne 0) { throw 'Manual exit was not respected.' }
    Write-Output 'PASS: manual exit stays closed during the same Codex session'

    $fakeProcess.Kill()
    $fakeProcess.WaitForExit()
    $fakeProcess = $null
    Start-Sleep -Seconds 3
    $fakeProcess = Start-Process -FilePath $fake -ArgumentList @('-t', '127.0.0.1') -WindowStyle Hidden -PassThru
    Wait-Condition 'UI starts for a short Codex session' { @(Get-TestProcesses | Where-Object CommandLine -Match '--follow-codex-ui').Count -eq 1 } 25
    $fakeProcess.Kill()
    $fakeProcess.WaitForExit()
    $fakeProcess = $null
    Wait-Condition 'UI exits even when Codex closes during startup' { @(Get-TestProcesses | Where-Object CommandLine -Match '--follow-codex-ui').Count -eq 0 } 20
    Write-Output 'E2E COMPLETE'
}
finally {
    if ($null -ne $fakeProcess -and -not $fakeProcess.HasExited) { $fakeProcess.Kill(); $fakeProcess.WaitForExit() }
    & schtasks.exe /Delete /TN $taskName /F 2>$null | Out-Null
    foreach ($item in Get-TestProcesses) {
        $process = Get-Process -Id $item.ProcessId -ErrorAction SilentlyContinue
        if ($null -ne $process -and $process.Path -eq $app) { $process.Kill(); $process.WaitForExit() }
    }
}
