namespace AgentLock.Tests;

internal static partial class Program
{
    private static void AlreadyConfirmedRecoveryDoesNotRequest()
    {
        var backend = new FakeRecoveryBackend { Locked = _ => true };
        var outcome = RecoveryEngine.Run(backend, persistent: true);

        Expect(outcome == RecoveryOutcome.ConfirmedLocked, "实际锁屏应返回已确认");
        Expect(backend.ConfirmedCount == 1, "实际确认只能记录一次");
        Expect(backend.RequestCount == 0 && backend.OwnerChecks == 0,
            "已经锁屏时不应再询问解除保护或发送锁屏请求");
        Expect(backend.Delays.Count == 0 && backend.UnconfirmedAtAttempts.Count == 0,
            "已经锁屏不需要重试、等待或未确认标记");
    }

    private static void PersistentRecoveryWaitsForActualConfirmation()
    {
        var backend = new FakeRecoveryBackend
        {
            Locked = state => state.RequestCount >= 35 ? true : null,
            AcceptRequest = _ => false
        };
        var outcome = RecoveryEngine.Run(backend, persistent: true);

        Expect(outcome == RecoveryOutcome.ConfirmedLocked, "最终实际确认锁屏才可返回成功");
        Expect(backend.RequestCount == 35 && backend.LockChecks == 36,
            "锁屏状态未知且请求连续失败时，不能在第三十次之后提前停止");
        Expect(backend.Attempts.Count == 35 && backend.Attempts.All(attempt => !attempt.Accepted),
            "所有失败请求应保留为失败，不能伪造已获准记录");
        Expect(backend.Attempts.Select(attempt => attempt.Number).SequenceEqual(Enumerable.Range(1, 35)),
            "重试编号应连续超过三十次");
        Expect(backend.Delays.Take(29).All(delay => delay == 2000)
            && backend.Delays.Skip(29).SequenceEqual(Enumerable.Repeat(10000, 6)),
            "前二十九次等待两秒，从第三十次起等待十秒并继续重试");
        Expect(backend.UnconfirmedAtAttempts.SequenceEqual(new[] { 30 }),
            "第三十次失败只记录一次仍未确认标记，不终止恢复");
        Expect(backend.ConfirmedAtRequestCounts.SequenceEqual(new[] { 35 }),
            "不能在锁屏状态未知期间记录确认");
    }

    private static void AcceptedRequestDoesNotConfirmRecovery()
    {
        var backend = new FakeRecoveryBackend
        {
            Locked = state => state.RequestCount >= 3 ? true : state.RequestCount == 1 ? null : false,
            AcceptRequest = _ => true
        };
        var outcome = RecoveryEngine.Run(backend, persistent: true);

        Expect(outcome == RecoveryOutcome.ConfirmedLocked, "最后明确的已锁状态应确认恢复");
        Expect(backend.RequestCount == 3 && backend.Attempts.All(attempt => attempt.Accepted),
            "已接受的请求仍应重试，直到读取到实际已锁状态");
        Expect(backend.Delays.SequenceEqual(new[] { 2000, 2000, 2000 }),
            "请求已获准但状态尚未确认时仍需要等待再检查");
        Expect(backend.ConfirmedAtRequestCounts.SequenceEqual(new[] { 3 }),
            "异步请求返回 true 不能成为确认的依据");
    }

    private static void DisarmedOwnerStopsRecoveryBeforeRequest()
    {
        foreach (var state in new bool?[] { false, null })
        {
            var backend = new FakeRecoveryBackend { Locked = _ => state, Disarmed = _ => true };
            var outcome = RecoveryEngine.Run(backend, persistent: true);

            Expect(outcome == RecoveryOutcome.OwnerDisarmed, "新鲜主进程状态已解除保护时应停止恢复");
            Expect(backend.OwnerChecks == 1 && backend.RequestCount == 0,
                "主进程已解除保护后不能继续发送锁屏请求");
            Expect(backend.ConfirmedCount == 0 && backend.Delays.Count == 0,
                "解除保护既不等于锁屏确认，也不需要等待");
        }
    }

    private static void ResumedOwnerCanStopRecovery()
    {
        var backend = new FakeRecoveryBackend
        {
            Locked = _ => null,
            Disarmed = state => state.RequestCount >= 31,
            AcceptRequest = _ => false
        };
        var outcome = RecoveryEngine.Run(backend, persistent: true);

        Expect(outcome == RecoveryOutcome.OwnerDisarmed, "主进程恢复并解除保护后应终止重试");
        Expect(backend.RequestCount == 31 && backend.OwnerChecks == 32,
            "慢速重试阶段仍应检查解除保护，不能再发送第三十二次请求");
        Expect(backend.Delays.Count == 31 && backend.Delays[^1] == 10000,
            "解除保护前仍应执行慢速重试");
        Expect(backend.ConfirmedCount == 0 && backend.UnconfirmedAtAttempts.SequenceEqual(new[] { 30 }),
            "由主进程解除保护终止恢复时不能写已确认锁屏记录");
    }

    private static void NonPersistentRecoveryOnlyIssuesOneRequest()
    {
        foreach (var accepted in new[] { false, true })
        {
            var backend = new FakeRecoveryBackend { Locked = _ => null, AcceptRequest = _ => accepted };
            var outcome = RecoveryEngine.Run(backend, persistent: false);

            Expect(outcome == RecoveryOutcome.RequestIssued, "非持续路径只报告请求已发出");
            Expect(backend.LockChecks == 1 && backend.RequestCount == 1,
                "非持续路径必须恰好请求一次，不能开始确认循环");
            Expect(backend.Attempts.Single() == (1, accepted), "应记录请求的真实返回值");
            Expect(backend.ConfirmedCount == 0 && backend.Delays.Count == 0
                && backend.UnconfirmedAtAttempts.Count == 0,
                "一次请求不能被记录为确认，也不能触发等待或持久恢复标记");
        }
    }

    /// <summary>In-memory only: no OS locking, native calls, GUI, files, timers or real waits.</summary>
    private sealed class FakeRecoveryBackend : ILockRecoveryBackend
    {
        public Func<FakeRecoveryBackend, bool?> Locked { get; init; } = _ => null;
        public Func<FakeRecoveryBackend, bool> Disarmed { get; init; } = _ => false;
        public Func<FakeRecoveryBackend, bool> AcceptRequest { get; init; } = _ => false;
        public int LockChecks { get; private set; }
        public int OwnerChecks { get; private set; }
        public int RequestCount { get; private set; }
        public int ConfirmedCount => ConfirmedAtRequestCounts.Count;
        public List<(int Number, bool Accepted)> Attempts { get; } = [];
        public List<int> Delays { get; } = [];
        public List<int> ConfirmedAtRequestCounts { get; } = [];
        public List<int> UnconfirmedAtAttempts { get; } = [];

        public bool? IsLocked()
        {
            // A regressed non-terminating loop must fail the test without sleeping.
            if (++LockChecks > 80) throw new InvalidOperationException("假后端恢复循环超过预设边界");
            return Locked(this);
        }

        public bool OwnerDisarmed()
        {
            OwnerChecks++;
            return Disarmed(this);
        }

        public bool RequestLock()
        {
            RequestCount++;
            return AcceptRequest(this);
        }

        public void RecordAttempt(int attempt, bool accepted) => Attempts.Add((attempt, accepted));
        public void RecordConfirmed() => ConfirmedAtRequestCounts.Add(RequestCount);
        public void MarkUnconfirmed() => UnconfirmedAtAttempts.Add(Attempts.Count);
        public void Delay(int milliseconds) => Delays.Add(milliseconds);
    }
}
