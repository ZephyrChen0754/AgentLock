using AgentLock.Security;

namespace AgentLock.Tests;

internal static partial class Program
{
    private static void NativeRecoveryBeginsOnlyWhenConfirmedUnlocked()
    {
        foreach (bool? state in new bool?[] { null, true })
        {
            var policy = new NativeRecoveryAuthorizationPolicy();
            Expect(!policy.TryBeginRecovery(state), "状态未知或已锁时不能开始新的忘密请求");
            Expect(!policy.RecoveryRequested && !policy.AwaitingNativeUnlock,
                "被拒绝的开始不能建立授权状态");
        }

        var unlocked = new NativeRecoveryAuthorizationPolicy();
        Expect(unlocked.TryBeginRecovery(false), "明确已解锁时可开始忘密请求");
        Expect(unlocked.RecoveryRequested && !unlocked.AwaitingNativeUnlock,
            "开始请求只能等待真实锁定，不能标记锁屏或认证成功");
    }

    private static void SpuriousNativeSignalsWithoutActualLockCannotAuthorize()
    {
        var policy = new NativeRecoveryAuthorizationPolicy();
        // A raw lock/unlock notification is only a wake-up. Its fresh native
        // query can still report false/null; those values must not authenticate.
        foreach (bool? state in new bool?[] { false, false, null, false, null })
        {
            Expect(!policy.ConfirmLock(state), "通知到达但实际未锁或未知不能确认锁定");
            Expect(!policy.TryConfirmUnlock(state), "没有实际锁观察时不能消费解锁授权");
            Expect(!policy.RecoveryRequested && !policy.AwaitingNativeUnlock,
                "伪通知或无效原生查询不能改变空闲授权状态");
        }
    }

    private static void NativeRecoveryRequestAloneNeverAuthorizesReset()
    {
        var policy = new NativeRecoveryAuthorizationPolicy();
        Expect(policy.TryBeginRecovery(false), "开始已解锁会话的请求");
        // RequestLock's accepted result has no policy entry point. Repeated
        // actual unlocked/unknown observations cannot substitute for locking.
        foreach (bool? state in new bool?[] { false, null, false, null, false })
        {
            Expect(!policy.ConfirmLock(state), "请求开始或接受不代表实际锁定");
            Expect(!policy.TryConfirmUnlock(state), "等待锁定时不能取得重设授权");
            Expect(policy.RecoveryRequested && !policy.AwaitingNativeUnlock,
                "未确认锁定时保持待处理请求，不能清为完成");
        }
    }

    private static void DuplicateRecoveryRequestsPreserveConfirmedLock()
    {
        var policy = new NativeRecoveryAuthorizationPolicy();
        Expect(policy.TryBeginRecovery(false), "第一次请求应建立等待");
        foreach (bool? state in new bool?[] { false, null, true })
            Expect(!policy.TryBeginRecovery(state), "已有请求时拒绝重复开始");
        Expect(policy.RecoveryRequested && !policy.AwaitingNativeUnlock,
            "重复请求不能变成实际锁定");

        Expect(policy.ConfirmLock(true), "确认实际锁定");
        foreach (bool? state in new bool?[] { false, null, true })
        {
            Expect(!policy.TryBeginRecovery(state), "等待原生解锁时不能重开请求");
            Expect(policy.RecoveryRequested && policy.AwaitingNativeUnlock,
                "重入不能擦掉已确认的锁定观察");
        }
        Expect(policy.TryConfirmUnlock(false), "真实解锁后仍可消费完整原生周期");
    }

    private static void LockedOrUnknownSessionNeverConfirmsUnlock()
    {
        var policy = new NativeRecoveryAuthorizationPolicy();
        Expect(policy.TryBeginRecovery(false) && policy.ConfirmLock(true), "已观察请求后的真实锁定");
        foreach (bool? state in new bool?[] { true, null, null, true })
        {
            Expect(!policy.TryConfirmUnlock(state), "仍锁定或查询失败不能确认解锁");
            Expect(policy.RecoveryRequested && policy.AwaitingNativeUnlock,
                "未知或锁定期间不得消费本次观察");
        }
        Expect(policy.TryConfirmUnlock(false), "只有实际已解锁才完成周期");
        Expect(!policy.RecoveryRequested && !policy.AwaitingNativeUnlock,
            "完成后清除本次请求和锁定观察");
    }

    private static void NaturalNativeCycleGrantsOnlyOneConfirmation()
    {
        var policy = new NativeRecoveryAuthorizationPolicy();
        Expect(policy.ConfirmLock(true), "无忘密请求也可观察真实自然锁定");
        Expect(!policy.RecoveryRequested && policy.AwaitingNativeUnlock,
            "自然锁定不能伪造用户忘密请求或自动弹窗意图");
        Expect(policy.ConfirmLock(true), "重复的实际锁定采样可保留锁观察");
        Expect(policy.TryConfirmUnlock(false), "自然原生认证周期可确认一次");
        Expect(!policy.TryConfirmUnlock(false) && !policy.TryConfirmUnlock(null),
            "重复 / 延迟解锁通知不能复用已经消费的锁观察");
        Expect(!policy.RecoveryRequested && !policy.AwaitingNativeUnlock, "自然周期结束后回到空闲");
    }

    private static void NewRecoveryNeedsNewNativeLockAfterConsumption()
    {
        var policy = new NativeRecoveryAuthorizationPolicy();
        Expect(policy.TryBeginRecovery(false) && policy.ConfirmLock(true), "建立首个真实周期");
        var autoShowSetup = policy.RecoveryRequested;
        Expect(policy.TryConfirmUnlock(false), "真实解锁完成首轮");
        Expect(autoShowSetup && !policy.RecoveryRequested,
            "主控制可在消费前保存本次忘密意图；完成后请求清零");
        Expect(!policy.TryConfirmUnlock(false), "同一周期不能再次消费");

        Expect(policy.TryBeginRecovery(false), "完成后允许发起新的独立周期");
        Expect(!policy.TryConfirmUnlock(false), "上一周期的解锁不能授权新请求");
        Expect(policy.RecoveryRequested && !policy.AwaitingNativeUnlock,
            "新请求必须重新等待实际锁定");
        Expect(policy.ConfirmLock(true) && policy.TryConfirmUnlock(false), "新真实锁定 / 解锁可完成新周期");
    }

    private static void NativeRecoveryPolicyAndRealCredentialResetAreOneUse()
    {
        using var fixture = new CredentialFixture();
        fixture.Configure();
        File.WriteAllText(fixture.CredentialFile, "{ broken credential for native recovery test");
        var store = fixture.Store();
        var policy = new NativeRecoveryAuthorizationPolicy();

        Expect(!store.Verify(OriginalPassword).Success && !store.CanReplacePassword,
            "损坏凭证或失败验证不能自行授予重设权限");
        Expect(!policy.TryConfirmUnlock(false), "普通认证未启动原生恢复时不能自行建立锁解周期");
        ExpectThrows(() => store.SetPassword(NewPassword), "没有完整原生周期不能覆盖损坏凭证");
        Expect(policy.TryBeginRecovery(false), "用户明确请求原生恢复");
        Expect(!policy.ConfirmLock(null) && !policy.TryConfirmUnlock(false),
            "无效原生状态和仍解锁状态不能授权");
        Expect(!store.CanReplacePassword, "状态未确认期间实际凭证权限不变");

        Expect(policy.ConfirmLock(true) && policy.TryConfirmUnlock(false), "实际状态样本构成一次完整周期");
        store.AuthorizePasswordResetAfterNativeUnlock();
        store.RevokePasswordResetAuthorization();
        Expect(!store.CanReplacePassword, "重设取消契约撤销实际凭证权限");
        ExpectThrows(() => store.SetPassword(NewPassword), "撤销后不能继续使用权限");
        Expect(!policy.TryConfirmUnlock(false), "取消后的旧解锁样本不能复活已经消费的周期");

        Expect(policy.TryBeginRecovery(false) && policy.ConfirmLock(true) && policy.TryConfirmUnlock(false),
            "重新恢复必须再完成一个真实状态周期");
        store.AuthorizePasswordResetAfterNativeUnlock();
        store.SetPassword(NewPassword);
        Expect(!store.CanReplacePassword, "保存成功消耗实际密码重设权限");
        ExpectThrows(() => store.SetPassword(OriginalPassword), "成功重设的权限不能使用第二次");
        Expect(!policy.TryConfirmUnlock(false), "已消费的 native cycle 也不能再次授权");
        Expect(fixture.Store().Verify(NewPassword).Success, "只有这次新密码写入生效");
    }
}
