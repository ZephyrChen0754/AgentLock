using System.Text;
using System.Text.Json;
using AgentLock.Native;
using AgentLock.Security;

namespace AgentLock.Tests;

internal static partial class Program
{
    private const string OriginalPassword = "测试Safe-Password-2026!";
    private const string NewPassword = "New-Password-2026!";

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        var checks = new (string Name, Action Check)[]
        {
            ("开放模式允许物理与合成事件", OpenInputPolicy),
            ("保护模式阻断物理事件并保留合成事件", GuardedInputPolicy),
            ("认证模式阻断合成与认证窗口外输入", AuthenticatingInputPolicy),
            ("无效输入模式拒绝放行", UnknownInputModeFailsClosed),
            ("认证模式拒绝离开密码框的普通快捷键", AuthenticationEscapeKeysAreBlocked),
            ("认证模式保留密码输入和编辑键", AuthenticationTypingRemainsAvailable),
            ("U 与所有非 F12 键不能提出认证请求", NonF12KeysNeverRequestAuthentication),
            ("实体新按下的 F12 可以提出认证请求", FreshPhysicalF12RequestsAuthentication),
            ("注入、连发、抬起或已持有快捷键不能提出认证请求", UnlockRequestRejectsInjectedRepeatedAndReleasedKeys),
            ("开放、认证或非法模式不响应保护快捷键", UnlockRequestRequiresGuardedMode),
            ("认证鼠标仅允许实际命中可见密码框且捕获安全的物理输入", AuthenticationPhysicalMouseRequiresActualTarget),
            ("认证鼠标拒绝所有合成输入", AuthenticationMouseRejectsInjectedInput),
            ("配对释放仅允许已获准的物理按键或按钮抬起", PairedReleasePolicy),
            ("已确认锁屏不重复发出请求", AlreadyConfirmedRecoveryDoesNotRequest),
            ("未知锁屏与连续请求失败超过三十次仍继续恢复", PersistentRecoveryWaitsForActualConfirmation),
            ("锁屏请求获准不等于已确认锁屏", AcceptedRequestDoesNotConfirmRecovery),
            ("主进程已解除保护时不再请求锁屏", DisarmedOwnerStopsRecoveryBeforeRequest),
            ("重试期间主进程解除保护会终止恢复", ResumedOwnerCanStopRecovery),
            ("非持续恢复只请求一次且不宣称确认", NonPersistentRecoveryOnlyIssuesOneRequest),
            ("忘密恢复仅在明确未锁定时开始", NativeRecoveryBeginsOnlyWhenConfirmedUnlocked),
            ("伪原生通知且实际未锁不能取得重设授权", SpuriousNativeSignalsWithoutActualLockCannotAuthorize),
            ("开始忘密请求不等于锁定或原生认证", NativeRecoveryRequestAloneNeverAuthorizesReset),
            ("重复忘密请求不擦除已确认锁定观察", DuplicateRecoveryRequestsPreserveConfirmedLock),
            ("锁定或原生查询未知时不能确认解锁", LockedOrUnknownSessionNeverConfirmsUnlock),
            ("自然原生锁解周期只能确认一次且不伪造忘密意图", NaturalNativeCycleGrantsOnlyOneConfirmation),
            ("消费后新恢复请求必须等待新锁解周期", NewRecoveryNeedsNewNativeLockAfterConsumption),
            ("原生周期与真实临时凭证重设权限单次使用且可撤销", NativeRecoveryPolicyAndRealCredentialResetAreOneUse),
            ("持有心跳读句柄时仍可原子替换并保留新旧完整记录", HeartbeatReaderAllowsAtomicReplacement),
            ("未配置凭证不能验证成功", UnconfiguredStoreFailsClosed),
            ("密码至少八字符且不能全部为空白", MinimumPasswordPolicy),
            ("首次设置保存后可跨实例验证", CredentialsPersist),
            ("正确密码通过，错误密码失败", PasswordVerification),
            ("未认证不能直接覆盖已有密码", ExistingPasswordNeedsAuthorization),
            ("验证成功后可以修改密码", SuccessfulVerificationAllowsChange),
            ("密码修改授权只可使用一次", PasswordReplacementAuthorizationIsOneUse),
            ("明确的原生认证授权可以恢复损坏凭证", NativeRecoveryReplacesCorruptCredentials),
            ("进入保护前可以撤销旧密码修改授权", PasswordReplacementAuthorizationCanBeRevoked),
            ("修改授权不跨实例保留", PasswordReplacementAuthorizationDoesNotPersist),
            ("第三次失败产生等待并跨实例保留", RateLimitPersists),
            ("等待期间正确密码也不能绕过限制", CorrectPasswordDoesNotBypassWait),
            ("等待中的尝试不延长计数与截止时间", AttemptsWhileWaitingDoNotAccumulate),
            ("凭证不包含明文且每次使用独立盐", NoPlaintextAndIndependentSalt),
            ("损坏 JSON 拒绝解锁与直接覆盖", CorruptCredentialsFailClosed),
            ("损坏编码凭证拒绝解锁", InvalidEncodedCredentialsFailClosed),
        };

        var failures = 0;
        foreach (var (name, check) in checks)
        {
            try
            {
                check();
                Console.WriteLine($"PASS  {name}");
            }
            catch (Exception error)
            {
                failures++;
                Console.Error.WriteLine($"FAIL  {name}: {error.GetType().Name}: {error.Message}");
            }
        }

        Console.WriteLine($"\n结果：{checks.Length - failures}/{checks.Length} 项通过。");
        Console.WriteLine("仅测试纯输入策略、内存假后端恢复逻辑与独立临时目录中的凭证 / 心跳读写；没有安装输入钩子、全局拦截、发送输入、实际锁屏或访问生产文件。");
        return failures == 0 ? 0 : 1;
    }

    private static void OpenInputPolicy()
    {
        foreach (var injected in new[] { false, true })
        foreach (var allowedForeground in new[] { false, true })
            Expect(!InputGate.ShouldBlockInput(GateMode.Open, injected, allowedForeground),
                "开放模式不应阻断输入");
    }

    private static void GuardedInputPolicy()
    {
        foreach (var allowedForeground in new[] { false, true })
        {
            Expect(InputGate.ShouldBlockInput(GateMode.Guarded, false, allowedForeground),
                "保护模式应拒绝物理输入");
            Expect(!InputGate.ShouldBlockInput(GateMode.Guarded, true, allowedForeground),
                "保护模式应放行合成事件以供 Agent 使用");
        }
    }

    private static void AuthenticatingInputPolicy()
    {
        Expect(!InputGate.ShouldBlockInput(GateMode.Authenticating, false, true),
            "认证窗口内应能使用物理输入");
        Expect(InputGate.ShouldBlockInput(GateMode.Authenticating, false, false),
            "认证窗口外的物理输入应拒绝");
        foreach (var allowedForeground in new[] { false, true })
            Expect(InputGate.ShouldBlockInput(GateMode.Authenticating, true, allowedForeground),
                "认证期间应阻断 Agent 合成事件，避免代填或误操作认证窗口");
    }

    private static void UnknownInputModeFailsClosed()
    {
        foreach (var injected in new[] { false, true })
        foreach (var allowedForeground in new[] { false, true })
            Expect(InputGate.ShouldBlockInput((GateMode)int.MaxValue, injected, allowedForeground),
                "无效模式不应放行事件");
    }

    private static void AuthenticationEscapeKeysAreBlocked()
    {
        Expect(InputGate.ShouldBlockAuthenticationKey(0x5B, false, false, false), "左 Windows 键应被阻断");
        Expect(InputGate.ShouldBlockAuthenticationKey(0x5C, false, false, false), "右 Windows 键应被阻断");
        Expect(InputGate.ShouldBlockAuthenticationKey(0x52, false, false, true), "Win+R 应被阻断");
        Expect(InputGate.ShouldBlockAuthenticationKey(0x45, false, false, true), "Win+E 应被阻断");
        foreach (var key in new uint[] { 0x09, 0x1B, 0x73, 0x20 })
            Expect(InputGate.ShouldBlockAuthenticationKey(key, false, true, false),
                "Alt+Tab / Esc / F4 / Space 普通消息应被阻断");
        Expect(InputGate.ShouldBlockAuthenticationKey(0x1B, true, false, false), "Ctrl+Esc 普通消息应被阻断");
        Expect(InputGate.ShouldBlockAuthenticationKey(0x2E, true, true, false),
            "Ctrl+Alt+Del 的普通键消息应被阻断；此检查不保证拦截原生安全序列");
    }

    private static void AuthenticationTypingRemainsAvailable()
    {
        foreach (var key in new uint[] { 0x41, 0x31, 0x08, 0x0D, 0x09, 0x1B, 0x2E })
            Expect(!InputGate.ShouldBlockAuthenticationKey(key, false, false, false),
                "普通字母、数字、退格、回车、Tab、Esc 与 Delete 应供密码框使用");
        Expect(!InputGate.ShouldBlockAuthenticationKey(0x41, true, false, false), "Ctrl+A 应供密码框选择文字");
        Expect(!InputGate.ShouldBlockAuthenticationKey(0x56, true, false, false), "Ctrl+V 应供密码框粘贴");
    }

    private static void PairedReleasePolicy()
    {
        foreach (var mode in new[] { GateMode.Open, GateMode.Guarded, GateMode.Authenticating, (GateMode)int.MaxValue })
        foreach (var injected in new[] { false, true })
        foreach (var isRelease in new[] { false, true })
        foreach (var hadAllowedDown in new[] { false, true })
        {
            var allowed = InputGate.ShouldAllowPairedRelease(mode, injected, isRelease, hadAllowedDown);
            var expected = (mode == GateMode.Guarded || mode == GateMode.Authenticating)
                && !injected && isRelease && hadAllowedDown;
            Expect(allowed == expected,
                $"配对释放判定错误：mode={mode}, injected={injected}, release={isRelease}, admittedDown={hadAllowedDown}");
        }

        // This verifies the policy's required caller state. The hook's private
        // state transition and real physical input are not exercised here.
        foreach (var mode in new[] { GateMode.Guarded, GateMode.Authenticating })
        {
            Expect(InputGate.ShouldAllowPairedRelease(mode, false, true, true),
                "已获准的物理按下应允许其对应释放完成");
            Expect(!InputGate.ShouldAllowPairedRelease(mode, false, true, false),
                "配对资格已经消耗或从未存在时，后续释放不能得到例外放行");
        }
    }

    private static void UnconfiguredStoreFailsClosed()
    {
        using var fixture = new CredentialFixture();
        var store = fixture.Store();
        Expect(!store.IsConfigured, "空目录应显示未配置");
        Expect(!store.Verify(OriginalPassword).Success, "空凭证不能通过验证");
    }

    private static void MinimumPasswordPolicy()
    {
        using var fixture = new CredentialFixture();
        var store = fixture.Store();
        foreach (var invalidPassword in new[] { "", "short7!", "        ", "\t\r\n      " })
        {
            ExpectThrows(() => store.SetPassword(invalidPassword), "不合法的初始密码应被拒绝");
            Expect(!store.IsConfigured, "拒绝初始密码后不应留下配置");
        }

        store.SetPassword("12345678");
        Expect(store.Verify("12345678").Success, "八字符的有效密码应被接受");
    }

    private static void CredentialsPersist()
    {
        using var fixture = new CredentialFixture();
        fixture.Store().SetPassword(OriginalPassword);
        var reopened = fixture.Store();
        Expect(reopened.IsConfigured, "重建实例应读到已有配置");
        Expect(reopened.Verify(OriginalPassword).Success, "重建实例应验证原密码");
    }

    private static void PasswordVerification()
    {
        using var fixture = new CredentialFixture();
        var store = fixture.Configure();
        var wrong = store.Verify("Incorrect-Password!");
        Expect(!wrong.Success, "错误密码应拒绝");
        Expect(wrong.RetryAfter == TimeSpan.Zero, "第一次错误不应触发等待");
        Expect(!string.IsNullOrWhiteSpace(wrong.Message), "错误密码应有可供界面显示的信息");
        var right = store.Verify(OriginalPassword);
        Expect(right.Success, "正确密码应通过");
        Expect(right.RetryAfter == TimeSpan.Zero, "正确密码无剩余等待");
    }

    private static void ExistingPasswordNeedsAuthorization()
    {
        using var fixture = new CredentialFixture();
        fixture.Configure();
        var reopened = fixture.Store();
        ExpectThrows(() => reopened.SetPassword(NewPassword), "未认证不能改密码");
        Expect(reopened.Verify(OriginalPassword).Success, "被拒绝的修改不应覆盖原密码");
    }

    private static void SuccessfulVerificationAllowsChange()
    {
        using var fixture = new CredentialFixture();
        var store = fixture.Configure();
        Expect(store.Verify(OriginalPassword).Success, "先验证旧密码");
        store.SetPassword(NewPassword);
        var reopened = fixture.Store();
        Expect(!reopened.Verify(OriginalPassword).Success, "修改后旧密码应无效");
        Expect(reopened.Verify(NewPassword).Success, "修改后新密码应通过");
    }

    private static void PasswordReplacementAuthorizationIsOneUse()
    {
        using var fixture = new CredentialFixture();
        var store = fixture.Configure();
        Expect(!store.CanReplacePassword, "首次保存后不能自动获得修改授权");
        Expect(!store.Verify("Incorrect-Password!").Success, "错误密码不通过");
        Expect(!store.CanReplacePassword, "错误密码不能获得修改授权");
        Expect(store.Verify(OriginalPassword).Success, "正确密码通过");
        Expect(store.CanReplacePassword, "正确认证后获得一次修改授权");
        store.SetPassword(NewPassword);
        Expect(!store.CanReplacePassword, "成功修改应消耗授权");
        ExpectThrows(() => store.SetPassword(OriginalPassword), "不能用已经消耗的授权再次修改");
        Expect(fixture.Store().Verify(NewPassword).Success, "再次修改被拒绝后应保留新密码");
    }

    private static void NativeRecoveryReplacesCorruptCredentials()
    {
        using var fixture = new CredentialFixture();
        fixture.Configure();
        File.WriteAllText(fixture.CredentialFile, "{ incomplete credential data", Encoding.UTF8);
        var store = fixture.Store();
        Expect(!store.CanReplacePassword, "损坏记录不能自动授权恢复");
        ExpectThrows(() => store.SetPassword(NewPassword), "显式原生恢复授权之前不能覆盖");
        // This checks only the controller-to-store contract. It does not simulate or authenticate Windows unlock.
        store.AuthorizePasswordResetAfterNativeUnlock();
        Expect(store.CanReplacePassword, "控制器显式原生认证授权后允许恢复");
        store.SetPassword(NewPassword);
        Expect(!store.CanReplacePassword, "恢复保存后授权也应被消耗");
        Expect(fixture.Store().Verify(NewPassword).Success, "恢复后的新密码应有效");
    }

    private static void PasswordReplacementAuthorizationCanBeRevoked()
    {
        using var fixture = new CredentialFixture();
        var store = fixture.Configure();
        store.AuthorizePasswordResetAfterNativeUnlock();
        Expect(store.CanReplacePassword, "明确恢复授权应可见");
        store.RevokePasswordResetAuthorization();
        Expect(!store.CanReplacePassword, "可以撤销原生认证授权");
        ExpectThrows(() => store.SetPassword(NewPassword), "撤销后不能覆盖密码");
        Expect(store.Verify(OriginalPassword).Success, "旧密码仍然有效");
        Expect(store.CanReplacePassword, "普通密码验证也会授予修改权限");
        store.RevokePasswordResetAuthorization();
        Expect(!store.CanReplacePassword, "可以撤销普通验证授权");
        ExpectThrows(() => store.SetPassword(NewPassword), "普通验证授权撤销后不能覆盖");
    }

    private static void PasswordReplacementAuthorizationDoesNotPersist()
    {
        using var fixture = new CredentialFixture();
        var store = fixture.Configure();
        Expect(store.Verify(OriginalPassword).Success, "正确密码应通过");
        Expect(store.CanReplacePassword, "当前实例获得授权");
        var reopened = fixture.Store();
        Expect(!reopened.CanReplacePassword, "新实例不能继承旧实例修改授权");
        ExpectThrows(() => reopened.SetPassword(NewPassword), "新实例需要重新认证才可修改密码");
        Expect(reopened.Verify(OriginalPassword).Success, "未授权的修改不影响旧密码");
    }

    private static void RateLimitPersists()
    {
        using var fixture = new CredentialFixture();
        var store = fixture.Configure();
        var blocked = TriggerRateLimit(store);
        Expect(blocked.RetryAfter > TimeSpan.Zero, "第三次错误应产生等待");
        var reopened = fixture.Store();
        var stillBlocked = reopened.Verify("Another-Incorrect-Password!");
        Expect(!stillBlocked.Success && stillBlocked.RetryAfter > TimeSpan.Zero,
            "重建实例不能重置等待");
        Expect(stillBlocked.RetryAfter <= blocked.RetryAfter + TimeSpan.FromSeconds(1),
            "重新打开不应增加等待长度");
    }

    private static void CorrectPasswordDoesNotBypassWait()
    {
        using var fixture = new CredentialFixture();
        var store = fixture.Configure();
        TriggerRateLimit(store);
        var result = fixture.Store().Verify(OriginalPassword);
        Expect(!result.Success && result.RetryAfter > TimeSpan.Zero,
            "等待期间正确密码也应受到限流");
    }

    private static void AttemptsWhileWaitingDoNotAccumulate()
    {
        using var fixture = new CredentialFixture();
        var store = fixture.Configure();
        TriggerRateLimit(store);
        using var before = JsonDocument.Parse(File.ReadAllText(fixture.CredentialFile));
        var failuresBefore = before.RootElement.GetProperty("failedAttempts").GetInt32();
        var deadlineBefore = before.RootElement.GetProperty("retryNotBeforeUtc").GetString();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var result = store.Verify("Still-Incorrect!");
            Expect(!result.Success && result.RetryAfter > TimeSpan.Zero, "等待期间应保持拒绝");
        }

        using var after = JsonDocument.Parse(File.ReadAllText(fixture.CredentialFile));
        Expect(after.RootElement.GetProperty("failedAttempts").GetInt32() == failuresBefore,
            "等待中的尝试不应增加计数");
        Expect(after.RootElement.GetProperty("retryNotBeforeUtc").GetString() == deadlineBefore,
            "等待中的尝试不应延长截止时间");
    }

    private static void NoPlaintextAndIndependentSalt()
    {
        using var firstFixture = new CredentialFixture();
        using var secondFixture = new CredentialFixture();
        firstFixture.Configure();
        secondFixture.Configure();
        var firstText = File.ReadAllText(firstFixture.CredentialFile);
        var secondText = File.ReadAllText(secondFixture.CredentialFile);
        Expect(!firstText.Contains(OriginalPassword, StringComparison.Ordinal), "文件不能保存明文密码");
        using var first = JsonDocument.Parse(firstText);
        using var second = JsonDocument.Parse(secondText);
        Expect(first.RootElement.GetProperty("salt").GetString() != second.RootElement.GetProperty("salt").GetString(),
            "相同密码的独立配置必须有不同盐");
        Expect(first.RootElement.GetProperty("hash").GetString() != second.RootElement.GetProperty("hash").GetString(),
            "独立盐应产生不同密码哈希");
    }

    private static void CorruptCredentialsFailClosed()
    {
        using var fixture = new CredentialFixture();
        fixture.Configure();
        File.WriteAllText(fixture.CredentialFile, "{ incomplete credential data", Encoding.UTF8);
        var reopened = fixture.Store();
        Expect(reopened.IsConfigured, "损坏已有凭证不能被视为允许首次设置的空配置");
        Expect(!reopened.Verify(OriginalPassword).Success, "损坏 JSON 不允许验证成功");
        ExpectThrows(() => reopened.SetPassword(NewPassword), "损坏凭证不能直接覆盖");
        Expect(File.ReadAllText(fixture.CredentialFile).Contains("incomplete", StringComparison.Ordinal),
            "失败的覆盖不能改写损坏文件");
    }

    private static void InvalidEncodedCredentialsFailClosed()
    {
        using var fixture = new CredentialFixture();
        fixture.Configure();
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(fixture.CredentialFile))!;
        node["salt"] = "this-is-not-base64!";
        File.WriteAllText(fixture.CredentialFile, node.ToJsonString(), Encoding.UTF8);
        var reopened = fixture.Store();
        Expect(reopened.IsConfigured, "损坏编码不能被视为空配置");
        Expect(!reopened.Verify(OriginalPassword).Success, "非法盐编码不能解锁");
        ExpectThrows(() => reopened.SetPassword(NewPassword), "编码损坏不能直接重置密码");
    }

    private static VerificationResult TriggerRateLimit(PasswordStore store)
    {
        var first = store.Verify("Incorrect-1!");
        var second = store.Verify("Incorrect-2!");
        var third = store.Verify("Incorrect-3!");
        Expect(!first.Success && !second.Success && !third.Success, "错误密码不能通过");
        Expect(first.RetryAfter == TimeSpan.Zero && second.RetryAfter == TimeSpan.Zero,
            "前两次错误不等待");
        Expect(third.RetryAfter > TimeSpan.Zero, "第三次错误需要等待");
        return third;
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void ExpectThrows(Action action, string message)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private sealed class CredentialFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "AgentLock.Tests", Guid.NewGuid().ToString("N"));

        public CredentialFixture()
        {
            Directory.CreateDirectory(root);
        }

        public string CredentialFile => Path.Combine(root, "credential.json");

        public string DirectoryPath => root;

        public PasswordStore Store() => new(root);

        public PasswordStore Configure()
        {
            var store = Store();
            store.SetPassword(OriginalPassword);
            return store;
        }

        public void Dispose()
        {
            var permittedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AgentLock.Tests")) + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(root);
            if (!target.StartsWith(permittedRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("临时测试目录超出允许清理范围");
            }

            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
        }
    }
}
