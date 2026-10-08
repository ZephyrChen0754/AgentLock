[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ExePath,

    [string] $DotnetRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$watchdogExecutable = [System.IO.Path]::GetFullPath($ExePath)
if (-not [System.IO.File]::Exists($watchdogExecutable)) {
    throw "Watchdog executable was not found: $watchdogExecutable"
}

$testScopeRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine(
    [System.IO.Path]::GetTempPath(), 'AgentLock.WatchdogTests'))
if ([System.IO.Directory]::Exists($testScopeRoot) -and
    ([System.IO.File]::GetAttributes($testScopeRoot) -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'Refusing a reparse point as the temporary test scope.'
}
$fixtureRoot = [System.IO.Path]::Combine($testScopeRoot, [Guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
$ownedProcesses = New-Object 'System.Collections.Generic.List[object]'
$failures = New-Object 'System.Collections.Generic.List[string]'
$passed = 0

function Assert-Condition {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) { throw $Message }
}

function Register-OwnedProcess {
    param([System.Diagnostics.Process] $Process)
    $identity = [PSCustomObject]@{
        Process = $Process
        Id = $Process.Id
        StartTicks = $Process.StartTime.ToUniversalTime().Ticks
    }
    $ownedProcesses.Add($identity)
    return $identity
}

function Start-DummyProcess {
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = [System.IO.Path]::Combine($env:SystemRoot,
        'System32\WindowsPowerShell\v1.0\powershell.exe')
    $start.Arguments = '-NoLogo -NoProfile -NonInteractive -Command "Start-Sleep -Seconds 90"'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
    $process = [System.Diagnostics.Process]::Start($start)
    if ($null -eq $process) { throw 'Could not create the owned sleeping dummy process.' }
    return (Register-OwnedProcess -Process $process)
}

function Stop-OwnedProcess {
    param($Identity)
    $current = $null
    try {
        $current = [System.Diagnostics.Process]::GetProcessById($Identity.Id)
        $actualTicks = $current.StartTime.ToUniversalTime().Ticks
        if ($actualTicks -ne $Identity.StartTicks) {
            throw "Refusing to terminate reused process ID $($Identity.Id)."
        }
        if (-not $current.HasExited) {
            $current.Kill()
            if (-not $current.WaitForExit(3000)) {
                throw "Owned test process $($Identity.Id) did not stop."
            }
        }
    }
    catch [System.ArgumentException] {
        # The owned process already exited. No other process is targeted.
    }
    finally {
        if ($null -ne $current) { $current.Dispose() }
    }
}

function Write-TestHeartbeat {
    param($Dummy, [string] $Path, [bool] $Armed, [bool] $Stale, [bool] $RecoveryRequested)
    $timestamp = [DateTime]::UtcNow
    if ($Stale) { $timestamp = $timestamp.AddSeconds(-10) }
    $state = [ordered]@{
        ProcessId = $Dummy.Id
        ProcessStartTicks = $Dummy.StartTicks
        Armed = $Armed
        UpdatedUtc = $timestamp.ToString('O')
        RecoveryRequested = $RecoveryRequested
    }
    $json = $state | ConvertTo-Json -Compress
    [System.IO.File]::WriteAllText($Path, $json, (New-Object System.Text.UTF8Encoding($false)))
}

function Start-TestWatchdog {
    param($Dummy, [string] $HeartbeatPath)
    # A Windows path cannot contain a quote. Keep the only dynamic argument a generated fixture path.
    if ($HeartbeatPath.Contains('"') -or -not $HeartbeatPath.StartsWith(
        $fixtureRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing a heartbeat outside this temporary test fixture.'
    }
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $watchdogExecutable
    $start.Arguments = '--watchdog {0} "{1}" --dry-run' -f $Dummy.Id, $HeartbeatPath
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
    if (-not [string]::IsNullOrWhiteSpace($DotnetRoot)) {
        $start.EnvironmentVariables['DOTNET_ROOT'] = [System.IO.Path]::GetFullPath($DotnetRoot)
    }
    $process = [System.Diagnostics.Process]::Start($start)
    if ($null -eq $process) { throw 'Could not create the dry-run watchdog.' }
    return (Register-OwnedProcess -Process $process)
}

function Wait-TestFile {
    param([string] $Path, [int] $TimeoutMilliseconds = 5000)
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    while ($timer.ElapsedMilliseconds -lt $TimeoutMilliseconds) {
        if ([System.IO.File]::Exists($Path)) { return }
        Start-Sleep -Milliseconds 50
    }
    throw "Timed out waiting for fixture file: $([System.IO.Path]::GetFileName($Path))"
}

function Assert-RecoveryReason {
    param([string] $HeartbeatPath, [string[]] $ExpectedReasons)
    $marker = $HeartbeatPath + '.watchdog-result'
    Wait-TestFile -Path $marker
    $reason = [System.IO.File]::ReadAllText($marker).Trim()
    Assert-Condition -Condition ($ExpectedReasons -contains $reason) -Message (
        "Unexpected recovery reason '$reason'; expected " + ($ExpectedReasons -join ', '))
}

function Invoke-WatchdogCheck {
    param([string] $Name, [scriptblock] $Check)
    try {
        & $Check
        $script:passed++
        Write-Output "PASS  $Name"
    }
    catch {
        $failures.Add("$Name : $($_.Exception.Message)")
        Write-Output "FAIL  $Name : $($_.Exception.Message)"
    }
}

try {
    Invoke-WatchdogCheck -Name 'armed stale heartbeat requests dry-run recovery' -Check {
        $dummy = Start-DummyProcess
        $path = [System.IO.Path]::Combine($fixtureRoot, 'armed-stale.json')
        Write-TestHeartbeat -Dummy $dummy -Path $path -Armed $true -Stale $true -RecoveryRequested $false
        $watcher = Start-TestWatchdog -Dummy $dummy -HeartbeatPath $path
        Assert-RecoveryReason -HeartbeatPath $path -ExpectedReasons @('guard-unresponsive')
        Assert-Condition -Condition $watcher.Process.WaitForExit(3000) -Message 'Stale watcher should finish.'
        Stop-OwnedProcess -Identity $dummy
    }

    Invoke-WatchdogCheck -Name 'unarmed stale heartbeat never requests recovery' -Check {
        $dummy = Start-DummyProcess
        $path = [System.IO.Path]::Combine($fixtureRoot, 'unarmed-stale.json')
        Write-TestHeartbeat -Dummy $dummy -Path $path -Armed $false -Stale $true -RecoveryRequested $false
        $watcher = Start-TestWatchdog -Dummy $dummy -HeartbeatPath $path
        Assert-Condition -Condition $watcher.Process.WaitForExit(5000) -Message 'Unarmed stale watcher should finish.'
        Assert-Condition -Condition (-not [System.IO.File]::Exists($path + '.watchdog-result')) -Message (
            'An unarmed watcher must not request recovery.')
        Stop-OwnedProcess -Identity $dummy
    }

    Invoke-WatchdogCheck -Name 'armed parent exit requests dry-run recovery' -Check {
        $dummy = Start-DummyProcess
        $path = [System.IO.Path]::Combine($fixtureRoot, 'parent-exit.json')
        Write-TestHeartbeat -Dummy $dummy -Path $path -Armed $true -Stale $false -RecoveryRequested $false
        $watcher = Start-TestWatchdog -Dummy $dummy -HeartbeatPath $path
        Wait-TestFile -Path ($path + '.ready')
        Stop-OwnedProcess -Identity $dummy
        Assert-RecoveryReason -HeartbeatPath $path -ExpectedReasons @('guard-exited', 'guard-unresponsive')
        Assert-Condition -Condition $watcher.Process.WaitForExit(3000) -Message 'Exited-parent watcher should finish.'
    }

    Invoke-WatchdogCheck -Name 'live parent recovery request produces pending-lock marker' -Check {
        $dummy = Start-DummyProcess
        $path = [System.IO.Path]::Combine($fixtureRoot, 'recovery-pending.json')
        Write-TestHeartbeat -Dummy $dummy -Path $path -Armed $true -Stale $false -RecoveryRequested $true
        $watcher = Start-TestWatchdog -Dummy $dummy -HeartbeatPath $path
        Wait-TestFile -Path ($path + '.ready')
        Assert-RecoveryReason -HeartbeatPath $path -ExpectedReasons @('lock-confirmation-pending')
        Assert-Condition -Condition (-not $dummy.Process.HasExited) -Message 'Pending-lock recovery requires a live parent.'
        Stop-OwnedProcess -Identity $watcher
        Stop-OwnedProcess -Identity $dummy
    }

    Invoke-WatchdogCheck -Name 'ready acknowledgement identifies the started watchdog' -Check {
        $dummy = Start-DummyProcess
        $path = [System.IO.Path]::Combine($fixtureRoot, 'ready.json')
        Write-TestHeartbeat -Dummy $dummy -Path $path -Armed $false -Stale $false -RecoveryRequested $false
        $watcher = Start-TestWatchdog -Dummy $dummy -HeartbeatPath $path
        Wait-TestFile -Path ($path + '.ready')
        $acknowledgedId = [System.IO.File]::ReadAllText($path + '.ready').Trim()
        Assert-Condition -Condition ($acknowledgedId -eq $watcher.Id.ToString()) -Message 'Ready file must identify this watchdog.'
        Assert-Condition -Condition (-not [System.IO.File]::Exists($path + '.watchdog-result')) -Message 'Ready alone must not request recovery.'
        Stop-OwnedProcess -Identity $watcher
        Stop-OwnedProcess -Identity $dummy
    }
}
finally {
    foreach ($owned in $ownedProcesses) {
        try { Stop-OwnedProcess -Identity $owned }
        catch { $failures.Add("Cleanup process $($owned.Id) : $($_.Exception.Message)") }
        finally { $owned.Process.Dispose() }
    }
    $allowedPrefix = $testScopeRoot + [System.IO.Path]::DirectorySeparatorChar
    $resolvedFixture = [System.IO.Path]::GetFullPath($fixtureRoot)
    if (-not $resolvedFixture.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing recursive cleanup outside the generated temporary test scope.'
    }
    if (Test-Path -LiteralPath $resolvedFixture) {
        if (([System.IO.File]::GetAttributes($testScopeRoot) -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Refusing recursive cleanup beneath a replaced test scope.'
        }
        if (([System.IO.File]::GetAttributes($resolvedFixture) -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Refusing recursive cleanup of a reparse point.'
        }
        Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
    }
}

Write-Output "Result: $passed / 5 checks passed; all watchdog launches used --dry-run."
Write-Output 'Only generated TEMP heartbeats and owned sleeping dummy processes were used; Windows was never locked.'
if ($failures.Count -gt 0) {
    foreach ($failure in $failures) { Write-Output "Failure detail: $failure" }
    exit 1
}
exit 0
