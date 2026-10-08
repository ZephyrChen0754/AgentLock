using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AgentLock;

internal sealed record GuardHeartbeat(int ProcessId, long ProcessStartTicks, bool Armed, DateTime UpdatedUtc, bool RecoveryRequested = false);

internal static class RuntimeState
{
    private static readonly object HeartbeatSync = new();
    private static readonly long ProcessStartTicks = GetProcessStartTicks();
    public static bool Armed { get; set; }
    public static bool RecoveryRequested { get; set; }
    public static string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentLock");
    public static string SessionPath { get; } = Path.Combine(Root, "session-" + Environment.ProcessId + ".json");
    public static void WriteHeartbeat()
    {
        lock (HeartbeatSync)
        {
            Directory.CreateDirectory(Root);
            WriteHeartbeatFile(SessionPath, new GuardHeartbeat(Environment.ProcessId, ProcessStartTicks, Armed, DateTime.UtcNow, RecoveryRequested));
        }
    }
    private static long GetProcessStartTicks()
    {
        using var process = Process.GetCurrentProcess();
        return process.StartTime.ToUniversalTime().Ticks;
    }

    internal static FileStream OpenHeartbeatReadStream(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    internal static GuardHeartbeat? ReadHeartbeat(string path)
    {
        using var stream = OpenHeartbeatReadStream(path);
        return JsonSerializer.Deserialize<GuardHeartbeat>(stream);
    }

    internal static void WriteHeartbeatFile(string path, GuardHeartbeat value)
    {
        lock (HeartbeatSync)
        {
            var temporary = path + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(value));
                // Replace retains a committed snapshot for existing readers on Windows.
                // Move(overwrite) can fail even when those readers share deletion.
                if (File.Exists(path)) File.Replace(temporary, path, destinationBackupFileName: null);
                else File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Root);
            var path = Path.Combine(Root, "events-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
            File.AppendAllText(path, DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine);
        }
        catch { }
    }
    public static void FailSafe(Exception? error)
    {
        var location = error is null ? null : new StackTrace(error).GetFrames()?
            .Select(frame => frame.GetMethod())
            .FirstOrDefault(method => method?.DeclaringType?.Namespace?.StartsWith("AgentLock", StringComparison.Ordinal) == true);
        Log("failure type=" + error?.GetType().Name + " location=" + location?.DeclaringType?.Name + "." + location?.Name);
        if (Armed)
        {
            RecoveryRequested = true;
            try { WriteHeartbeat(); } catch { }
            LockWorkStation();
        }
    }
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool LockWorkStation();
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
    [DllImport("kernel32.dll")] internal static extern uint SetThreadExecutionState(uint flags);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WTSRegisterSessionNotification(IntPtr window, uint flags);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WTSUnRegisterSessionNotification(IntPtr window);
}

internal static class Watchdog
{
    public static void Run(int pid, string heartbeatPath, bool dryRun)
    {
        bool wasArmed = false;
        long expectedStart = 0;
        DateTime lastValid = DateTime.UtcNow;
        DateTime lastRecovery = DateTime.MinValue;
        bool ready = false;
        while (true)
        {
            try
            {
                var state = RuntimeState.ReadHeartbeat(heartbeatPath);
                if (state is null || state.ProcessId != pid) throw new InvalidDataException();
                if (expectedStart != 0 && expectedStart != state.ProcessStartTicks) throw new InvalidDataException();
                expectedStart = state.ProcessStartTicks;
                wasArmed = state.Armed;
                lastValid = state.UpdatedUtc;
                using var parent = Process.GetProcessById(pid);
                bool stale = DateTime.UtcNow - lastValid > TimeSpan.FromSeconds(4);
                bool replaced = parent.StartTime.ToUniversalTime().Ticks != expectedStart;
                if (!ready && !replaced)
                {
                    File.WriteAllText(heartbeatPath + ".ready", Environment.ProcessId.ToString());
                    ready = true;
                }
                if (stale || replaced || parent.HasExited)
                {
                    if (wasArmed) Recover(dryRun, heartbeatPath, "guard-unresponsive", true);
                    return;
                }
                if (wasArmed && state.RecoveryRequested && DateTime.UtcNow - lastRecovery > TimeSpan.FromSeconds(2))
                {
                    Recover(dryRun, heartbeatPath, "lock-confirmation-pending");
                    lastRecovery = DateTime.UtcNow;
                }
            }
            catch
            {
                if (wasArmed && DateTime.UtcNow - lastValid > TimeSpan.FromSeconds(4))
                {
                    Recover(dryRun, heartbeatPath, "guard-unavailable", true);
                    return;
                }
                try { using var parent = Process.GetProcessById(pid); if (parent.HasExited) return; }
                catch
                {
                    // The parent may arm after our first read and exit before the
                    // process check. Re-read its final committed state before deciding.
                    try
                    {
                        var final = RuntimeState.ReadHeartbeat(heartbeatPath);
                        if (final is not null && final.ProcessId == pid && final.ProcessStartTicks == expectedStart) wasArmed |= final.Armed;
                    }
                    catch { }
                    if (wasArmed) Recover(dryRun, heartbeatPath, "guard-exited", true);
                    return;
                }
                if (!wasArmed && DateTime.UtcNow - lastValid > TimeSpan.FromSeconds(15)) return;
            }
            Thread.Sleep(400);
        }
    }
    private static void Recover(bool dryRun, string path, string reason, bool requireConfirmation = false)
    {
        if (dryRun) { File.WriteAllText(path + ".watchdog-result", reason); return; }
        RecoveryEngine.Run(new WindowsRecoveryBackend(path, reason), requireConfirmation);
    }

    private sealed class WindowsRecoveryBackend(string path, string reason) : ILockRecoveryBackend
    {
        public bool? IsLocked() => Native.SessionState.IsCurrentSessionLocked();
        public bool OwnerDisarmed()
        {
            // A resumed main process may have authenticated or observed an actual WTS lock.
            try
            {
                var state = RuntimeState.ReadHeartbeat(path);
                return state is not null && !state.Armed && DateTime.UtcNow - state.UpdatedUtc < TimeSpan.FromSeconds(4);
            }
            catch { return false; }
        }
        public bool RequestLock() => RuntimeState.LockWorkStation();
        public void RecordAttempt(int attempt, bool accepted) => RuntimeState.Log("watchdog recovery=" + reason + " requested=" + accepted + " confirmed=false attempt=" + attempt);
        public void RecordConfirmed() => RuntimeState.Log("watchdog recovery=" + reason + " confirmed=true");
        public void MarkUnconfirmed()
        {
            RuntimeState.Log("watchdog recovery=" + reason + " confirmed=false continuing-recovery=true");
            try { File.WriteAllText(path + ".recovery-unconfirmed", DateTimeOffset.Now.ToString("O")); } catch { }
        }
        public void Delay(int milliseconds) => Thread.Sleep(milliseconds);
    }
}
