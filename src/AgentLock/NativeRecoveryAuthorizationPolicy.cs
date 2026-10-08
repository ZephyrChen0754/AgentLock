namespace AgentLock;

/// <summary>
/// Tracks a confirmed native lock/unlock cycle on the controller's UI thread.
/// Inputs must come from a fresh query of the current Windows session, never
/// from an accepted lock request or an unverified WTS notification.
/// This class neither queries Windows nor grants PasswordStore permissions.
/// </summary>
internal sealed class NativeRecoveryAuthorizationPolicy
{
    internal bool RecoveryRequested { get; private set; }
    internal bool AwaitingNativeUnlock { get; private set; }

    internal bool TryBeginRecovery(bool? actualLocked)
    {
        if (actualLocked is not false || RecoveryRequested || AwaitingNativeUnlock)
            return false;

        RecoveryRequested = true;
        return true;
    }

    internal bool ConfirmLock(bool? actualLocked)
    {
        if (actualLocked is not true)
            return false;

        AwaitingNativeUnlock = true;
        return true;
    }

    internal bool TryConfirmUnlock(bool? actualLocked)
    {
        if (!AwaitingNativeUnlock || actualLocked is not false)
            return false;

        AwaitingNativeUnlock = false;
        RecoveryRequested = false;
        return true;
    }
}
