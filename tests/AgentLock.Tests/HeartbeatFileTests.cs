using System.Text.Json;

namespace AgentLock.Tests;

internal static partial class Program
{
    private static void HeartbeatReaderAllowsAtomicReplacement()
    {
        using var fixture = new CredentialFixture();
        var targetPath = Path.Combine(fixture.DirectoryPath, "heartbeat.json");
        var timestamp = new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc);
        var original = new GuardHeartbeat(12345, 111111, Armed: false, timestamp, RecoveryRequested: false);
        var replacement = new GuardHeartbeat(54321, 222222, Armed: true, timestamp.AddSeconds(1), RecoveryRequested: true);
        RuntimeState.WriteHeartbeatFile(targetPath, original);
        Expect(RuntimeState.ReadHeartbeat(targetPath) == original,
            "目标不存在时应创建首份完整的心跳记录");

        // Hold the production reader's real sharing mode across replacement.
        // These are inert JSON fixtures: no watchdog or production file is used.
        using var oldReader = RuntimeState.OpenHeartbeatReadStream(targetPath);
        RuntimeState.WriteHeartbeatFile(targetPath, replacement);

        var oldRecord = JsonSerializer.Deserialize<GuardHeartbeat>(oldReader);
        Expect(oldRecord == original, "替换时仍打开的旧读句柄应得到旧的完整 JSON 记录");
        var newRecord = RuntimeState.ReadHeartbeat(targetPath);
        Expect(newRecord == replacement, "新的心跳读取应得到替换后的完整新记录");
        Expect(oldRecord != newRecord, "不能把仍打开的旧文件句柄误当成已替换的新文件");
    }
}
