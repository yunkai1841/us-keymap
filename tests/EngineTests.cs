// 変換エンジンの単体テスト。build.bat から実行される。
using System;
using System.Collections.Generic;
using UsKeyBridge;

class FakeSink : IKeySink
{
    public readonly List<KeyOut> All = new List<KeyOut>();
    public void Send(IList<KeyOut> keys) { All.AddRange(keys); }
    public string Take()
    {
        var parts = new List<string>();
        foreach (var k in All) parts.Add(k.ToString());
        All.Clear();
        return string.Join(" ", parts.ToArray());
    }
}

static class EngineTests
{
    static readonly IntPtr US = new IntPtr(1);
    static readonly IntPtr JIS = new IntPtr(2);
    static int failures, passes;
    static long now = 1000;

    // JIS 配列で各スキャンコードに付く VK (フックが受け取る値)
    static int Vk(int scan)
    {
        switch (scan)
        {
            case 0x2A: return 0xA0;
            case 0x36: return 0xA1;
            case 0x1D: return 0xA2;
            case 0x38: return 0xA4;
            case 0x1E: return 0x41; // A
            case 0x29: return 0xF3; // 半角/全角
            case 0x3A: return 0xF0; // 英数
            case 0x02: return 0x31;
            case 0x0B: return 0x30;
        }
        int v = KeyMap.JisVk(scan);
        return v != 0 ? v : 0xFF;
    }

    static HookKey D(int scan) { return new HookKey(Vk(scan), scan, false, false); }
    static HookKey U(int scan) { return new HookKey(Vk(scan), scan, false, true); }

    static Engine New(FakeSink s, BridgeMode mode, bool waitForRaw = false)
    {
        var e = new Engine(s);
        e.Mode = mode;
        e.WaitForRaw = waitForRaw;
        e.IsUsDevice = d => d == US;
        return e;
    }

    static void Eq(string name, object expected, object actual)
    {
        if (Equals(expected, actual)) { passes++; return; }
        failures++;
        Console.WriteLine("FAIL {0}\n  expected: [{1}]\n  actual:   [{2}]", name, expected, actual);
    }

    // 素通し = フックが false を返し、送出は無し
    static void Pass(string name, Engine e, FakeSink s, HookKey k)
    {
        bool block = e.OnHook(k, now);
        Eq(name + " (pass)", false, block);
        Eq(name + " (no output)", "", s.Take());
    }

    static void Block(string name, Engine e, FakeSink s, HookKey k, string expectedOut)
    {
        bool block = e.OnHook(k, now);
        Eq(name + " (block)", true, block);
        Eq(name + " (output)", expectedOut, s.Take());
    }

    static int Main()
    {
        AllUsMode();
        RawBeforeHook();
        GuessFromLastDevice();
        ExactBeatsGuess();
        RawAfterHook();
        JisKeyboardUnchanged();
        OrderingWhilePending();
        TimeoutFallsBackToJis();
        MissingRawDoesNotCascade();
        StaleRawIsConsumed();
        GraveAndAltGrave();
        CtrlUsesVirtualKey();
        CapsLock();
        ShiftChangesWhileHeld();
        RightShift();
        Quotes();
        UsLayoutForeground();
        ReleaseAllReleasesTargets();
        VkValues();

        Console.WriteLine("{0} passed, {1} failed", passes, failures);
        return failures == 0 ? 0 : 1;
    }

    static void AllUsMode()
    {
        var s = new FakeSink(); var e = New(s, BridgeMode.AllUs);
        Pass("AllUs shift down", e, s, D(0x2A));
        Block("AllUs shift+2 => @", e, s, D(0x03), "2A- 1A+ 2A+");
        Block("AllUs 2 up => @ up", e, s, U(0x03), "1A-");
        Pass("AllUs shift up", e, s, U(0x2A));
        Pass("AllUs letter", e, s, D(0x1E));
        Pass("AllUs letter up", e, s, U(0x1E));
        Pass("AllUs 2 unshifted", e, s, D(0x03));
        Pass("AllUs 2 unshifted up", e, s, U(0x03));
    }

    static void RawBeforeHook()
    {
        var s = new FakeSink(); var e = New(s, BridgeMode.PerDevice);
        Pass("A shift", e, s, D(0x2A));
        e.AddRaw(0x03, false, false, US, now);
        Block("A US shift+2", e, s, D(0x03), "2A- 1A+ 2A+");
        Block("A up", e, s, U(0x03), "1A-");
        Eq("A paired at hook", 1, e.Stats.PairedAtHook);
    }

    // 既定 (WaitForRaw=false): そのキーの Raw Input が無ければ、直前に使われたキーボードで判断する
    static void GuessFromLastDevice()
    {
        var s = new FakeSink(); var e = New(s, BridgeMode.PerDevice);
        // まだどのキーボードの Raw Input も無い → 日本語配列として素通し
        Pass("guess none [", e, s, D(0x1A));
        Pass("guess none [ up", e, s, U(0x1A));
        now += 300;
        // 外付け (US) で Shift → Shift の Raw Input から US と分かる
        e.AddRaw(0x2A, false, false, US, now);
        Pass("guess shift", e, s, D(0x2A));
        Block("guess US shift+2 => @ without waiting", e, s, D(0x03), "2A- 1A+ 2A+");
        Eq("guess nothing pending", false, e.HasPending);
        Block("guess up", e, s, U(0x03), "1A-");
        Pass("guess shift up", e, s, U(0x2A));
        now += 300;
        // 内蔵 (JIS) で文字を打った後の記号は変換しない
        e.AddRaw(0x1E, false, false, JIS, now);
        Pass("guess JIS a", e, s, D(0x1E));
        Pass("guess JIS a up", e, s, U(0x1E));
        Pass("guess JIS [ passes", e, s, D(0x1A));
        Pass("guess JIS [ up", e, s, U(0x1A));
        now += 300;
        // 外付けに持ち替えて文字を打てば、次の記号から US として変換
        e.AddRaw(0x1E, false, false, US, now);
        Pass("guess US a", e, s, D(0x1E));
        Pass("guess US a up", e, s, U(0x1E));
        Block("guess US [ translated", e, s, D(0x1A), "1B+");
        Block("guess US [ up", e, s, U(0x1A), "1B-");
        Eq("guess no timeouts", 0, e.Stats.Timeouts);
    }

    static void ExactBeatsGuess()
    {
        var s = new FakeSink(); var e = New(s, BridgeMode.PerDevice);
        e.AddRaw(0x1A, false, false, US, now);  // このキー自身の Raw Input (US)
        e.AddRaw(0x1E, false, false, JIS, now); // 直前のキーボードは JIS
        Block("exact beats guess", e, s, D(0x1A), "1B+");
        Block("exact up", e, s, U(0x1A), "1B-");
    }

    static void RawAfterHook()
    {
        var s = new FakeSink(); var e = New(s, BridgeMode.PerDevice, true);
        Pass("B shift", e, s, D(0x2A));
        Block("B US shift+2 pending", e, s, D(0x03), "");
        Eq("B pending", true, e.HasPending);
        e.AddRaw(0x03, false, false, US, now + 2);
        e.Process(now + 2);
        Eq("B emitted after raw", "2A- 1A+ 2A+", s.Take());
        Eq("B no pending", false, e.HasPending);
        Block("B up", e, s, U(0x03), "1A-");
        Eq("B paired later", 1, e.Stats.PairedLater);
    }

    static void JisKeyboardUnchanged()
    {
        var s = new FakeSink(); var e = New(s, BridgeMode.PerDevice, true);
        Pass("JIS shift", e, s, D(0x2A));
        e.AddRaw(0x03, false, false, JIS, now);
        Pass("JIS shift+2 (raw first)", e, s, D(0x03));
        Pass("JIS 2 up", e, s, U(0x03));
        Block("JIS shift+2 (raw later)", e, s, D(0x03), "");
        e.AddRaw(0x03, false, false, JIS, now);
        e.Process(now);
        Eq("JIS re-sent unchanged", "03+", s.Take());
        Pass("JIS 2 up (2)", e, s, U(0x03));
    }

    static void OrderingWhilePending()
    {
        var s = new FakeSink(); var e = New(s, BridgeMode.PerDevice, true);
        Block("ord [ pending", e, s, D(0x1A), "");
        // 次のキーが届く前に Raw Input をフック内で取り込んだ場合
        e.AddRaw(0x1A, false, false, US, now);
        Block("ord a after [", e, s, D(0x1E), "1B+ 1E+");
        Block("ord [ up", e, s, U(0x1A), "1B-");
        Pass("ord a up", e, s, U(0x1E));

        // Raw Input が届く前に次のキーが来た場合
        Block("ord2 [ pending", e, s, D(0x1A), "");
        Block("ord2 a queued", e, s, D(0x1E), "");
        Block("ord2 a up queued", e, s, U(0x1E), "");
        e.AddRaw(0x1A, false, false, US, now);
        e.Process(now);
        Eq("ord2 flushed in order", "1B+ 1E+ 1E-", s.Take());
        Block("ord2 [ up", e, s, U(0x1A), "1B-");
    }

    static void TimeoutFallsBackToJis()
    {
        var s = new FakeSink(); var e = New(s, BridgeMode.PerDevice, true);
        Block("to [ pending", e, s, D(0x1A), "");
        e.Process(now + 50);
        Eq("to still waiting", "", s.Take());
        e.Process(now + 200);
        Eq("to sent as original", "1A+", s.Take());
        Eq("to count", 1, e.Stats.Timeouts);
        Pass("to up", e, s, U(0x1A));
    }

    static void MissingRawDoesNotCascade()
    {
        long saved = now;
        var s = new FakeSink(); var e = New(s, BridgeMode.PerDevice, true);
        Block("miss [ pending", e, s, D(0x1A), "");
        e.Process(now + 200);
        Eq("miss timed out", "1A+", s.Take());
        now += 210;
        Pass("miss up", e, s, U(0x1A));
        // Raw Input が来なかった古い項目に、次のキーの Raw Input を取られないこと
        now += 90;
        e.AddRaw(0x1A, false, false, US, now);
        Block("miss next [ translated", e, s, D(0x1A), "1B+");
        Block("miss next up", e, s, U(0x1A), "1B-");
        now = saved;
    }

    static void StaleRawIsConsumed()
    {
        var s = new FakeSink(); var e = New(s, BridgeMode.PerDevice);
        // US キーボードで Shift なしの 2 (変換不要なので判別を待たずに素通し)
        Pass("stale 2 unshifted", e, s, D(0x03));
        Pass("stale 2 up", e, s, U(0x03));
        e.AddRaw(0x03, false, false, US, now); // 遅れて届いた Raw Input
        e.Process(now);
        Eq("stale nothing sent", "", s.Take());
        // 続いて JIS キーボードで Shift+2。古い US の Raw Input と取り違えないこと
        Pass("stale shift", e, s, D(0x2A));
        e.AddRaw(0x03, false, false, JIS, now);
        Pass("stale JIS shift+2 passes", e, s, D(0x03));
        Pass("stale JIS 2 up", e, s, U(0x03));
    }

    static void GraveAndAltGrave()
    {
        var s = new FakeSink(); var e = New(s, BridgeMode.AllUs);
        Block("` => shift+@", e, s, D(0x29), "2A+ 1A+ 2A-");
        Block("` up", e, s, U(0x29), "1A-");
        Pass("alt down", e, s, D(0x38));
        Pass("alt+` => 半角/全角", e, s, D(0x29));
        Pass("alt+` up", e, s, U(0x29));
        Pass("alt up", e, s, U(0x38));
        Pass("shift", e, s, D(0x2A));
        Block("~ => shift+^", e, s, D(0x29), "0D+");
        Block("~ up", e, s, U(0x29), "0D-");
        Pass("shift up", e, s, U(0x2A));
    }

    static void CtrlUsesVirtualKey()
    {
        var s = new FakeSink(); var e = New(s, BridgeMode.AllUs);
        Pass("ctrl", e, s, D(0x1D));
        Block("ctrl+= => VK_OEM_PLUS key", e, s, D(0x0D), "27+");
        Block("ctrl+= up", e, s, U(0x0D), "27-");
        Block("ctrl+` => VK_OEM_3 key", e, s, D(0x29), "1A+");
        Block("ctrl+` up", e, s, U(0x29), "1A-");
        Pass("ctrl+- unchanged", e, s, D(0x0C));
        Pass("ctrl+- up", e, s, U(0x0C));
        Pass("ctrl+shift+2 unchanged", e, s, D(0x03));
        Pass("ctrl+2 up", e, s, U(0x03));
        Pass("ctrl up", e, s, U(0x1D));
    }

    static void CapsLock()
    {
        var s = new FakeSink(); var e = New(s, BridgeMode.AllUs);
        Block("caps => VK_CAPITAL", e, s, D(0x3A), "3A+");
        Eq("caps vk", 0x14, LastVk(e, s, U(0x3A)));
        e.CapsLockFix = false;
        Pass("caps fix off", e, s, D(0x3A));
        Pass("caps fix off up", e, s, U(0x3A));
    }

    static int LastVk(Engine e, FakeSink s, HookKey k)
    {
        e.OnHook(k, now);
        int vk = s.All.Count > 0 ? s.All[s.All.Count - 1].Vk : -1;
        s.Take();
        return vk;
    }

    static void ShiftChangesWhileHeld()
    {
        var s = new FakeSink(); var e = New(s, BridgeMode.AllUs);
        Pass("held 2", e, s, D(0x03));
        Pass("held shift", e, s, D(0x2A));
        Block("held repeat 2 with shift", e, s, D(0x03), "03- 2A- 1A+ 2A+");
        Block("held repeat again", e, s, D(0x03), "2A- 1A+ 2A+");
        Block("held 2 up", e, s, U(0x03), "1A-");
        Pass("held shift up", e, s, U(0x2A));
    }

    static void RightShift()
    {
        var s = new FakeSink(); var e = New(s, BridgeMode.AllUs);
        Pass("rshift", e, s, new HookKey(0xA1, 0x36, false, false));
        Block("rshift+; => :", e, s, D(0x27), "36- 28+ 36+");
        Block("rshift+; up", e, s, U(0x27), "28-");
        Pass("rshift up", e, s, new HookKey(0xA1, 0x36, false, true));
    }

    static void Quotes()
    {
        var s = new FakeSink(); var e = New(s, BridgeMode.AllUs);
        Block("' => shift+7", e, s, D(0x28), "2A+ 08+ 2A-");
        Block("' up", e, s, U(0x28), "08-");
        Block("\\ => ¥ key", e, s, D(0x2B), "7D+");
        Block("\\ up", e, s, U(0x2B), "7D-");
        Block("[ => [ key", e, s, D(0x1A), "1B+");
        Block("[ up", e, s, U(0x1A), "1B-");
        Block("= => shift+-", e, s, D(0x0D), "2A+ 0C+ 2A-");
        Block("= up", e, s, U(0x0D), "0C-");
        Pass("shift", e, s, D(0x2A));
        Block("\" => shift+2", e, s, D(0x28), "03+");
        Block("\" up", e, s, U(0x28), "03-");
        Block("_ => shift+ろ", e, s, D(0x0C), "73+");
        Block("_ up", e, s, U(0x0C), "73-");
        Block("+ => shift+;", e, s, D(0x0D), "27+");
        Block("+ up", e, s, U(0x0D), "27-");
        Block("* => shift+:", e, s, D(0x09), "28+");
        Block("* up", e, s, U(0x09), "28-");
        Block("( => shift+8", e, s, D(0x0A), "09+");
        Block("( up", e, s, U(0x0A), "09-");
        Block(") => shift+9", e, s, D(0x0B), "0A+");
        Block(") up", e, s, U(0x0B), "0A-");
        Block("^ => ^ key", e, s, D(0x07), "2A- 0D+ 2A+");
        Block("^ up", e, s, U(0x07), "0D-");
        Block("& => shift+6", e, s, D(0x08), "07+");
        Block("& up", e, s, U(0x08), "07-");
        Pass("shift up", e, s, U(0x2A));
    }

    static void UsLayoutForeground()
    {
        var s = new FakeSink(); var e = New(s, BridgeMode.AllUs);
        e.LayoutIsJis = () => false;
        Pass("us-layout ` passes", e, s, D(0x29));
        Pass("us-layout ` up", e, s, U(0x29));
    }

    static void ReleaseAllReleasesTargets()
    {
        var s = new FakeSink(); var e = New(s, BridgeMode.AllUs);
        Block("rel [", e, s, D(0x1A), "1B+");
        e.ReleaseAll(now);
        Eq("rel releases target", "1B-", s.Take());
        Pass("rel then up passes", e, s, U(0x1A));
    }

    static void VkValues()
    {
        var s = new FakeSink(); var e = New(s, BridgeMode.AllUs);
        e.OnHook(D(0x2B), now);
        Eq("vk of ¥ key", 0xDC, s.All[0].Vk);
        s.Take();
        e.OnHook(U(0x2B), now);
        s.Take();
        e.OnHook(D(0x29), now);
        Eq("vk of @ key", 0xC0, s.All[1].Vk);
        Eq("vk of shift", 0xA0, s.All[0].Vk);
        s.Take();
    }
}
