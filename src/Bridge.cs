using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace UsKeyBridge
{
    /// <summary>
    /// 低レベルキーボードフックと Raw Input を受け取り、Engine に渡して SendInput で送出する。
    /// すべて UI スレッドで動く (フックのコールバックもメッセージループ経由で呼ばれる)。
    /// </summary>
    public sealed class Bridge : NativeWindow, IKeySink, IDisposable
    {
        /// <summary>自分が送出したキーの目印 (dwExtraInfo)。</summary>
        static readonly IntPtr Magic = new IntPtr(0x55534B42);

        readonly Settings settings;
        readonly DeviceDirectory devices;
        readonly Engine engine;
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly Native.LowLevelKeyboardProc hookProc;
        readonly Timer pendingTimer;
        readonly Timer slowTimer;
        readonly StringBuilder logBuf = new StringBuilder();
        readonly Dictionary<IntPtr, bool> layoutCache = new Dictionary<IntPtr, bool>();

        IntPtr hook;
        IntPtr rawBuf;
        int rawBufSize;
        bool inHook;
        bool enabled;
        bool problemReported, problemPending;
        long lastHookMs, lastRawMs = -1, lastReinstallMs;

        public IntPtr LastDevice { get; private set; }
        public event EventHandler DetectionProblem;

        public Bridge(Settings settings, DeviceDirectory devices)
        {
            this.settings = settings;
            this.devices = devices;
            engine = new Engine(this);
            engine.IsUsDevice = devices.IsUs;
            engine.IsKeyDown = vk => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;
            engine.LayoutIsJis = ForegroundLayoutIsJis;
            engine.Trace = s => Log(s);
            hookProc = HookCallback;

            pendingTimer = new Timer();
            pendingTimer.Interval = 15;
            pendingTimer.Tick += delegate { OnPendingTimer(); };

            slowTimer = new Timer();
            slowTimer.Interval = 1000;
            slowTimer.Tick += delegate { OnSlowTimer(); };

            rawBufSize = 256;
            rawBuf = Marshal.AllocHGlobal(rawBufSize);
        }

        public Engine Engine { get { return engine; } }

        long Now { get { return clock.ElapsedMilliseconds; } }

        public void Start()
        {
            CreateParams cp = new CreateParams();
            cp.Caption = "UsKeyBridge";
            CreateHandle(cp);

            Native.RAWINPUTDEVICE[] rid = new Native.RAWINPUTDEVICE[1];
            rid[0].usUsagePage = 0x01; // Generic Desktop
            rid[0].usUsage = 0x06;     // Keyboard
            rid[0].dwFlags = Native.RIDEV_INPUTSINK | Native.RIDEV_DEVNOTIFY;
            rid[0].hwndTarget = Handle;
            if (!Native.RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf(typeof(Native.RAWINPUTDEVICE))))
                Log("RegisterRawInputDevices failed: " + Marshal.GetLastWin32Error());

            InstallHook();
            slowTimer.Start();
            ApplySettings();
        }

        public void ApplySettings()
        {
            engine.Mode = settings.Mode;
            engine.CapsLockFix = settings.CapsLockFix;
            if (enabled && !settings.Enabled) engine.ReleaseAll(Now);
            enabled = settings.Enabled;
            AfterEngine();
        }

        public void ResetStats()
        {
            engine.Stats.PairedAtHook = 0;
            engine.Stats.PairedLater = 0;
            engine.Stats.Timeouts = 0;
            engine.Stats.Translated = 0;
            engine.Stats.Deferred = 0;
            engine.Stats.RawSeen = 0;
            engine.Stats.Guessed = 0;
            problemReported = false;
        }

        // ---- フック ----

        void InstallHook()
        {
            if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
            hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, hookProc, Native.GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                Log("SetWindowsHookEx failed: " + err);
                MessageBox.Show("キーボードフックを設定できませんでした (エラー " + err + ")。", "UsKeyBridge",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            lastHookMs = Now;
        }

        IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && !inHook)
            {
                long now = Now;
                lastHookMs = now;
                Native.KBDLLHOOKSTRUCT k = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
                bool injected = (k.flags & Native.LLKHF_INJECTED) != 0;
                if (enabled && !injected)
                {
                    inHook = true;
                    try
                    {
                        DrainRawInput(now);
                        HookKey hk = new HookKey((int)k.vkCode, (int)k.scanCode,
                            (k.flags & Native.LLKHF_EXTENDED) != 0, (k.flags & Native.LLKHF_UP) != 0);
                        bool block = engine.OnHook(hk, now);
                        if (settings.Log)
                            Log(string.Format("hook vk={0:X2} sc={1:X2}{2} {3} -> {4}", hk.Vk, hk.Scan,
                                hk.Ext ? "e" : "", hk.Up ? "up" : "down", block ? "block" : "pass"));
                        AfterEngine();
                        if (block) return (IntPtr)1;
                    }
                    catch (Exception ex)
                    {
                        Log("hook error: " + ex);
                    }
                    finally
                    {
                        inHook = false;
                    }
                }
            }
            return Native.CallNextHookEx(hook, nCode, wParam, lParam);
        }

        /// <summary>フックより先に届いている WM_INPUT を取り込む (他のメッセージは処理しない)。</summary>
        void DrainRawInput(long now)
        {
            Native.MSG msg;
            uint flags = Native.PM_REMOVE | Native.PM_NOYIELD | (Native.QS_RAWINPUT << 16);
            for (int i = 0; i < 32; i++)
            {
                if (!Native.PeekMessage(out msg, Handle, Native.WM_INPUT, Native.WM_INPUT, flags)) break;
                HandleRaw(msg.lParam, now);
                Native.DefWindowProc(msg.hwnd, msg.message, msg.wParam, msg.lParam);
            }
        }

        // ---- Raw Input ----

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_INPUT)
            {
                long now = Now;
                HandleRaw(m.LParam, now);
                if (!inHook && enabled)
                {
                    engine.Process(now);
                    AfterEngine();
                }
            }
            else if (m.Msg == Native.WM_INPUT_DEVICE_CHANGE)
            {
                devices.Invalidate();
            }
            base.WndProc(ref m);
        }

        void HandleRaw(IntPtr hRaw, long now)
        {
            uint hdr = (uint)Marshal.SizeOf(typeof(Native.RAWINPUTHEADER));
            uint size = 0;
            if (Native.GetRawInputData(hRaw, Native.RID_INPUT, IntPtr.Zero, ref size, hdr) != 0 || size == 0) return;
            if (size > rawBufSize)
            {
                Marshal.FreeHGlobal(rawBuf);
                rawBufSize = (int)size;
                rawBuf = Marshal.AllocHGlobal(rawBufSize);
            }
            if (Native.GetRawInputData(hRaw, Native.RID_INPUT, rawBuf, ref size, hdr) != size) return;

            Native.RAWINPUTHEADER h = (Native.RAWINPUTHEADER)Marshal.PtrToStructure(rawBuf, typeof(Native.RAWINPUTHEADER));
            if (h.dwType != Native.RIM_TYPEKEYBOARD) return;
            Native.RAWKEYBOARD kb = (Native.RAWKEYBOARD)Marshal.PtrToStructure(
                new IntPtr(rawBuf.ToInt64() + hdr), typeof(Native.RAWKEYBOARD));
            if (kb.MakeCode == 0 || kb.MakeCode == 0xFF) return;

            bool up = (kb.Flags & Native.RI_KEY_BREAK) != 0;
            bool ext = (kb.Flags & (Native.RI_KEY_E0 | Native.RI_KEY_E1)) != 0;
            if (h.hDevice != IntPtr.Zero)
            {
                lastRawMs = now;
                if (!up) LastDevice = h.hDevice;
            }
            if (settings.Log)
                Log(string.Format("raw  mk={0:X2}{1} {2} dev={3:X}", kb.MakeCode, ext ? "e" : "", up ? "up" : "down",
                    h.hDevice.ToInt64()));
            if (enabled) engine.AddRaw(kb.MakeCode, ext, up, h.hDevice, now);
        }

        // ---- 送出 ----

        public void Send(IList<KeyOut> keys)
        {
            int n = keys.Count;
            if (n == 0) return;
            Native.INPUT[] inputs = new Native.INPUT[n];
            for (int i = 0; i < n; i++)
            {
                KeyOut k = keys[i];
                inputs[i].type = Native.INPUT_KEYBOARD;
                inputs[i].ki.wVk = (ushort)k.Vk;
                inputs[i].ki.wScan = (ushort)k.Scan;
                inputs[i].ki.dwFlags = (k.Ext ? Native.KEYEVENTF_EXTENDEDKEY : 0) | (k.Up ? Native.KEYEVENTF_KEYUP : 0);
                inputs[i].ki.dwExtraInfo = Magic;
            }
            uint sent = Native.SendInput((uint)n, inputs, Marshal.SizeOf(typeof(Native.INPUT)));
            if (settings.Log || sent != n)
            {
                StringBuilder sb = new StringBuilder("send");
                foreach (KeyOut k in keys) sb.Append(' ').Append(k.ToString());
                if (sent != n) sb.Append("  FAILED sent=" + sent + " err=" + Marshal.GetLastWin32Error());
                Log(sb.ToString());
            }
        }

        // ---- 前面ウィンドウのレイアウト ----

        bool ForegroundLayoutIsJis()
        {
            IntPtr fg = Native.GetForegroundWindow();
            uint tid = fg == IntPtr.Zero ? 0 : Native.GetWindowThreadProcessId(fg, IntPtr.Zero);
            IntPtr hkl = Native.GetKeyboardLayout(tid);
            bool jis;
            if (!layoutCache.TryGetValue(hkl, out jis))
            {
                jis = LayoutIsJis(hkl);
                layoutCache[hkl] = jis;
            }
            return jis;
        }

        /// <summary>そのレイアウトで「@」キー (スキャンコード 0x1A) が VK_OEM_3 なら JIS 配列。</summary>
        public static bool LayoutIsJis(IntPtr hkl)
        {
            return Native.MapVirtualKeyEx(0x1A, Native.MAPVK_VSC_TO_VK, hkl) == 0xC0;
        }

        public static string DescribeLayout(IntPtr hkl)
        {
            uint vk = Native.MapVirtualKeyEx(0x1A, Native.MAPVK_VSC_TO_VK, hkl);
            string name = vk == 0xC0 ? "日本語配列 (JIS)" : vk == 0xDB ? "英語配列 (US)" : "不明";
            return string.Format("{0}  (HKL {1:X8})", name, hkl.ToInt64() & 0xFFFFFFFF);
        }

        public static IntPtr ForegroundLayout()
        {
            IntPtr fg = Native.GetForegroundWindow();
            uint tid = fg == IntPtr.Zero ? 0 : Native.GetWindowThreadProcessId(fg, IntPtr.Zero);
            return Native.GetKeyboardLayout(tid);
        }

        // ---- タイマー ----

        void AfterEngine()
        {
            if (engine.HasPending) { if (!pendingTimer.Enabled) pendingTimer.Start(); }
            else if (pendingTimer.Enabled) pendingTimer.Stop();

            EngineStats s = engine.Stats;
            // 記号キーを何度も打っているのに Raw Input が 1 件も届かない → キーボードを判別できない
            if (!problemReported && engine.Mode == BridgeMode.PerDevice && s.RawSeen == 0 &&
                s.Guessed + s.PairedAtHook + s.Timeouts >= 5)
            {
                problemReported = true;
                problemPending = true; // 通知はフックの外 (OnSlowTimer) で出す
                Log("detection problem: no raw input");
            }
        }

        void OnPendingTimer()
        {
            if (inHook) return;
            engine.Process(Now);
            AfterEngine();
        }

        void OnSlowTimer()
        {
            FlushLog();
            if (problemPending)
            {
                problemPending = false;
                EventHandler h = DetectionProblem;
                if (h != null) h(this, EventArgs.Empty);
            }
            // フックが Windows に外されていないか (応答遅延で外されることがある)。
            // Raw Input は届いているのにフックが呼ばれていなければ付け直す。
            long now = Now;
            if (lastRawMs > 0 && lastRawMs - lastHookMs > 3000 && now - lastRawMs < 5000 &&
                now - lastReinstallMs > 10000 && !engine.HasPending)
            {
                lastReinstallMs = now;
                Log("reinstalling hook");
                InstallHook();
            }
        }

        // ---- ログ ----

        public static string LogDirectory
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsKeyBridge");
            }
        }

        public static string LogPath { get { return Path.Combine(LogDirectory, "debug.log"); } }

        void Log(string s)
        {
            if (!settings.Log) return;
            logBuf.Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append(' ').Append(s).Append("\r\n");
            if (logBuf.Length > 64 * 1024) FlushLog();
        }

        void FlushLog()
        {
            if (logBuf.Length == 0) return;
            try
            {
                Directory.CreateDirectory(LogDirectory);
                FileInfo fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 5 * 1024 * 1024) fi.Delete();
                File.AppendAllText(LogPath, logBuf.ToString(), Encoding.UTF8);
            }
            catch (Exception)
            {
            }
            logBuf.Length = 0;
        }

        public void Dispose()
        {
            pendingTimer.Stop();
            slowTimer.Stop();
            try { engine.ReleaseAll(Now); } catch (Exception) { }
            if (hook != IntPtr.Zero)
            {
                Native.UnhookWindowsHookEx(hook);
                hook = IntPtr.Zero;
            }
            FlushLog();
            DestroyHandle();
            if (rawBuf != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(rawBuf);
                rawBuf = IntPtr.Zero;
            }
        }
    }
}
