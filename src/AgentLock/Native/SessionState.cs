using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AgentLock.Native;

/// <summary>Read-only confirmation of the caller's Windows session lock state.</summary>
public static class SessionState
{
    private const int WtsSessionInfoEx = 25;

    // This is deliberately only the non-sensitive prefix of WTSINFOEXW on win-x64.
    // The native union begins at offset 8 because its LARGE_INTEGER fields require
    // 8-byte alignment. No station, user, domain, timestamps or counters are read.
    [StructLayout(LayoutKind.Explicit, Size = 20)]
    private struct WtsInfoExPrefix
    {
        [FieldOffset(0)] internal uint Level;
        [FieldOffset(8)] internal uint SessionId;
        [FieldOffset(12)] internal int ConnectionState;
        [FieldOffset(16)] internal int SessionFlags;
    }

    /// <returns>True only for confirmed locked, false only for confirmed unlocked, null if unavailable or unexpected.</returns>
    public static bool? IsCurrentSessionLocked()
    {
        // Windows 7's reversed flag defect does not apply to this supported target.
        if (IntPtr.Size != 8 || !OperatingSystem.IsWindowsVersionAtLeast(10)) return null;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            using var process = Process.GetCurrentProcess();
            int sessionId = process.SessionId;
            if (sessionId < 0) return null;
            if (!WTSQuerySessionInformationW(IntPtr.Zero, (uint)sessionId, WtsSessionInfoEx,
                out buffer, out uint bytes) || buffer == IntPtr.Zero || bytes < 20)
                return null;

            var prefix = Marshal.PtrToStructure<WtsInfoExPrefix>(buffer);
            if (prefix.Level != 1 || prefix.SessionId != (uint)sessionId) return null;
            return prefix.SessionFlags switch
            {
                0 => true,
                // An unlocked flag in a disconnected/connecting session is not
                // proof that the interactive owner has returned after login.
                1 when prefix.ConnectionState == 0 => false,
                _ => null
            };
        }
        catch (Exception)
        {
            // Query failure is unknown, never treated as successful locking.
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero) WTSFreeMemory(buffer);
        }
    }

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", ExactSpelling = true,
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, uint sessionId, int informationClass,
        out IntPtr buffer, out uint bytesReturned);

    [DllImport("wtsapi32.dll", ExactSpelling = true)]
    private static extern void WTSFreeMemory(IntPtr buffer);
}
