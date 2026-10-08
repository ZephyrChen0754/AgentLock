namespace AgentLock;

internal enum RecoveryOutcome { ConfirmedLocked, OwnerDisarmed, RequestIssued }

internal interface ILockRecoveryBackend
{
    bool? IsLocked();
    bool OwnerDisarmed();
    bool RequestLock();
    void RecordAttempt(int attempt, bool accepted);
    void RecordConfirmed();
    void MarkUnconfirmed();
    void Delay(int milliseconds);
}

/// <summary>Never equates an accepted asynchronous lock request with a confirmed lock.</summary>
internal static class RecoveryEngine
{
    internal static RecoveryOutcome Run(ILockRecoveryBackend backend, bool persistent)
    {
        for (int attempt = 1; ; attempt++)
        {
            if (backend.IsLocked() == true)
            {
                backend.RecordConfirmed();
                return RecoveryOutcome.ConfirmedLocked;
            }
            if (backend.OwnerDisarmed()) return RecoveryOutcome.OwnerDisarmed;
            backend.RecordAttempt(attempt, backend.RequestLock());
            if (!persistent) return RecoveryOutcome.RequestIssued;
            if (attempt == 30) backend.MarkUnconfirmed();
            // After a minute of unsuccessful requests, remain alive and retry less often.
            backend.Delay(attempt < 30 ? 2000 : 10000);
        }
    }
}
