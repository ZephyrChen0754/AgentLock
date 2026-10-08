using AgentLock.Native;

namespace AgentLock.Tests;

internal static partial class Program
{
    private const uint KeyF12 = 0x7B;

    private static void NonF12KeysNeverRequestAuthentication()
    {
        for (uint key = 0; key < 256; key++)
        {
            if (key == KeyF12) continue;
            Expect(!RequestShortcut(key), $"非 F12 键不能提出认证请求：vk={key:X}");
        }

        Expect(!RequestShortcut(uint.MaxValue), "非法虚拟键不能提出认证请求");
        // The final shortcut policy has no modifier parameters. U is rejected
        // regardless of Ctrl/Alt state; it is not an alternate entry anymore.
    }

    private static void FreshPhysicalF12RequestsAuthentication()
    {
        Expect(RequestShortcut(KeyF12), "保护中的实体首次按下 F12 应提出认证请求");
        // This is a request policy only. No password acceptance, UI dispatch or
        // Windows keyboard delivery is exercised by these pure assertions.
    }

    private static void UnlockRequestRejectsInjectedRepeatedAndReleasedKeys()
    {
        foreach (var injected in new[] { false, true })
        foreach (var isDown in new[] { false, true })
        foreach (var wasAlreadyDown in new[] { false, true })
        foreach (var requestKeyHeld in new[] { false, true })
        {
            if (!injected && isDown && !wasAlreadyDown && !requestKeyHeld) continue;
            Expect(!InputGate.ShouldRequestUnlock(GateMode.Guarded, injected, KeyF12, isDown,
                    wasAlreadyDown, requestKeyHeld),
                $"不应响应非首次物理按下：injected={injected}, down={isDown}, repeat={wasAlreadyDown}, held={requestKeyHeld}");
        }
    }

    private static void UnlockRequestRequiresGuardedMode()
    {
        foreach (var mode in new[] { GateMode.Open, GateMode.Authenticating, (GateMode)int.MaxValue })
            Expect(!InputGate.ShouldRequestUnlock(mode, injected: false, KeyF12, isDown: true,
                    wasAlreadyDown: false, requestKeyHeld: false),
                "保护外或非法模式不应将快捷键视为认证请求");
    }

    private static void AuthenticationPhysicalMouseRequiresActualTarget()
    {
        foreach (var visible in new[] { false, true })
        foreach (var inside in new[] { false, true })
        foreach (var hitsAllowed in new[] { false, true })
        foreach (var captureAllowed in new[] { false, true })
        {
            var blocked = InputGate.ShouldBlockAuthenticationMouse(injected: false, visible, inside,
                hitsAllowed, captureAllowed);
            if (visible && inside && hitsAllowed && captureAllowed)
                Expect(!blocked,
                    "可见密码框内实际命中自身且捕获安全的物理点击应可抢回焦点，不依赖当前前台应用");
            else
                Expect(blocked,
                    $"框外、被其他窗口覆盖、不可见或捕获不明应拒绝：visible={visible}, inside={inside}, hit={hitsAllowed}, capture={captureAllowed}");
        }

        // The helper consumes facts already checked by native window/capture
        // APIs. These tests do not inspect real windows or move/click the mouse.
    }

    private static void AuthenticationMouseRejectsInjectedInput()
    {
        foreach (var visible in new[] { false, true })
        foreach (var inside in new[] { false, true })
        foreach (var hitsAllowed in new[] { false, true })
        foreach (var captureAllowed in new[] { false, true })
            Expect(InputGate.ShouldBlockAuthenticationMouse(injected: true, visible, inside,
                    hitsAllowed, captureAllowed),
                "即使命中可见密码框，Agent 合成鼠标也不能在认证期间操作密码界面");
    }

    private static bool RequestShortcut(uint key)
        => InputGate.ShouldRequestUnlock(GateMode.Guarded, injected: false, key, isDown: true,
            wasAlreadyDown: false, requestKeyHeld: false);
}
