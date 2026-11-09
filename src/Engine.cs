using System;
using System.Collections.Generic;

namespace UsKeyBridge
{
    /// <summary>SendInput で送出する 1 イベント。</summary>
    public struct KeyOut
    {
        public int Vk;
        public int Scan;
        public bool Ext;
        public bool Up;

        public KeyOut(int vk, int scan, bool ext, bool up)
        {
            Vk = vk;
            Scan = scan;
            Ext = ext;
            Up = up;
        }

        public override string ToString()
        {
            return string.Format("{0:X2}{1}", Scan, Up ? "-" : "+");
        }
    }

    /// <summary>低レベルフックで受け取ったキーイベント。</summary>
    public struct HookKey
    {
        public int Vk;
        public int Scan;
        public bool Ext;
        public bool Up;

        public HookKey(int vk, int scan, bool ext, bool up)
        {
            Vk = vk;
            Scan = scan;
            Ext = ext;
            Up = up;
        }
    }

    public interface IKeySink
    {
        /// <summary>キーを順に送出する。呼び出し後に list は再利用されるのでコピーすること。</summary>
        void Send(IList<KeyOut> keys);
    }

    public enum BridgeMode
    {
        PerDevice = 0, // キーボードごとに判別
        AllUs = 1      // すべてのキーボードを US 配列として扱う
    }

    public sealed class EngineStats
    {
        public int PairedAtHook;  // フック時点で Raw Input が届いていた
        public int PairedLater;   // フックの後で Raw Input が届いた
        public int Timeouts;      // Raw Input が届かず判別できなかった
        public int Translated;    // 変換して送出した
        public int Deferred;      // いったん保留して後から送出した
        public int RawSeen;       // 受け取った Raw Input (物理キーボードからのもの)
        public int Guessed;       // 直前に使われたキーボードで判断した
    }

    /// <summary>
    /// 変換エンジン (Win32 には依存しない)。
    /// 低レベルフックのイベントと Raw Input のイベントを順序どおりに対応付けて入力元の
    /// キーボードを特定し、US 配列キーボードからのキーだけを JIS 配列用に置き換える。
    /// どちらが先に届いてもよいように、判別が必要なキーはいったん止めて (保留)、
    /// 判別できたところで順番を保ったまま送出し直す。
    /// </summary>
    public sealed class Engine
    {
        public BridgeMode Mode = BridgeMode.PerDevice;
        public bool CapsLockFix = true;
        /// <summary>
        /// true: Raw Input が届くまでキーを保留して正確に判別する (フックで止めたキーにも Raw Input が届く環境向け)。
        /// false: 保留せず、届いていなければ直前に使われたキーボードで判断する (既定)。
        /// </summary>
        public bool WaitForRaw = false;
        /// <summary>直前に Raw Input を受け取ったキーボード。</summary>
        public IntPtr LastDevice { get; private set; }
        /// <summary>Raw Input の到着を待つ最大時間。超えたら JIS 配列として素通しする。</summary>
        public int DeviceWaitMs = 150;
        /// <summary>対応付け待ちのデータを捨てるまでの時間。</summary>
        public int PairMaxAgeMs = 1000;

        /// <summary>デバイスハンドルが US 配列キーボードか。</summary>
        public Func<IntPtr, bool> IsUsDevice;
        /// <summary>現在のキー状態 (GetAsyncKeyState)。null なら同期しない。</summary>
        public Func<int, bool> IsKeyDown;
        /// <summary>前面ウィンドウのキーボードレイアウトが JIS か。null なら常に true。</summary>
        public Func<bool> LayoutIsJis;
        public Action<string> Trace;

        public readonly EngineStats Stats = new EngineStats();

        sealed class Pending
        {
            public HookKey K;
            public long T;
            public bool Live;   // いまフックの中で処理中 (素通しできる)
            public bool Paired;
            public bool Done;   // 送出済み
            public IntPtr Device;
        }

        sealed class PairItem
        {
            public int Scan;
            public long T;
            public Pending P;
        }

        struct RawItem
        {
            public int Scan;
            public IntPtr Device;
            public long T;
        }

        sealed class Held
        {
            public bool Original; // 元のキーをそのまま押している
            public int OrigVk;
            public Target T;      // 変換先を押している
        }

        readonly IKeySink sink;
        readonly List<Pending> queue = new List<Pending>();
        readonly List<PairItem> pairs = new List<PairItem>();
        readonly List<RawItem> raws = new List<RawItem>();
        readonly Held[] held = new Held[0x80];
        readonly List<KeyOut> outs = new List<KeyOut>();

        bool lShift, rShift, lCtrl, rCtrl, lAlt, rAlt, lWin, rWin;
        long lastSendMs = -100000;

        public Engine(IKeySink sink)
        {
            this.sink = sink;
        }

        public bool HasPending { get { return queue.Count > 0; } }

        /// <summary>入力元の判別が必要なキーか。</summary>
        public bool Tracked(int scan, bool ext)
        {
            if (ext || scan <= 0 || scan >= 0x80) return false;
            if (scan == KeyMap.SC_CAPSLOCK) return CapsLockFix;
            return KeyMap.IsSource(scan);
        }

        /// <summary>Raw Input を登録する (送出はしない)。フックの中からも呼べる。</summary>
        public void AddRaw(int scan, bool ext, bool up, IntPtr device, long now)
        {
            if (device == IntPtr.Zero) return; // SendInput によるもの
            LastDevice = device;
            Stats.RawSeen++;
            if (up || !Tracked(scan, ext)) return;
            RawItem r;
            r.Scan = scan;
            r.Device = device;
            r.T = now;
            raws.Add(r);
            if (raws.Count > 64) raws.RemoveAt(0);
        }

        /// <summary>
        /// 低レベルフックのイベントを処理する。
        /// 戻り値 true: 元のイベントを止める (必要なものは Send 済み/保留中)。false: そのまま通す。
        /// </summary>
        public bool OnHook(HookKey k, long now)
        {
            // 直前に送出したキーがまだ OS のキー状態に反映されていない可能性があるので、少し間が空いたときだけ同期
            if (queue.Count == 0 && now - lastSendMs > 50) Resync();
            Pair(now);

            Pending p = new Pending();
            p.K = k;
            p.T = now;
            p.Live = true;
            if (!k.Up && Tracked(k.Scan, k.Ext))
            {
                PairItem pi = new PairItem();
                pi.Scan = k.Scan;
                pi.T = now;
                pi.P = p;
                pairs.Add(pi);
                Pair(now);
            }
            queue.Add(p);

            bool livePassed = Flush(now, false);
            p.Live = false;
            if (!livePassed && queue.Count > 0 && queue[queue.Count - 1] == p) Stats.Deferred++;
            return !livePassed;
        }

        /// <summary>Raw Input の到着後やタイマーで呼ぶ。判別できた保留キーを送出する。</summary>
        public void Process(long now)
        {
            Pair(now);
            Flush(now, false);
        }

        /// <summary>保留中のキーをすべて送出し、変換して押しっぱなしのキーを離す (無効化時など)。</summary>
        public void ReleaseAll(long now)
        {
            Flush(now, true);
            outs.Clear();
            for (int i = 0; i < held.Length; i++)
            {
                Held h = held[i];
                if (h != null && !h.Original) outs.Add(new KeyOut(h.T.Vk, h.T.Scan, false, true));
                held[i] = null;
            }
            if (outs.Count > 0) { sink.Send(outs); lastSendMs = now; }
            outs.Clear();
            queue.Clear();
            pairs.Clear();
            raws.Clear();
        }

        // ---- 対応付け ----

        void Pair(long now)
        {
            // 送出済みで待ち時間も過ぎたもの (Raw Input が来なかった) は、後続の対応付けを妨げないよう捨てる
            while (pairs.Count > 0)
            {
                long age = now - pairs[0].T;
                if (age > PairMaxAgeMs || (pairs[0].P.Done && age > DeviceWaitMs)) pairs.RemoveAt(0);
                else break;
            }
            // 古い Raw Input が別のキーボードの同じキーと取り違えられないよう捨てる
            while (raws.Count > 0 && now - raws[0].T > DeviceWaitMs + 100) raws.RemoveAt(0);

            while (pairs.Count > 0 && raws.Count > 0)
            {
                PairItem h = pairs[0];
                int idx = -1;
                for (int i = 0; i < raws.Count; i++)
                {
                    if (raws[i].Scan == h.Scan) { idx = i; break; }
                }
                if (idx < 0) break; // この Raw Input はまだ届いていない

                RawItem r = raws[idx];
                raws.RemoveRange(0, idx + 1); // 先に並んでいた分は対応するフックが無い (他アプリが止めた等)
                pairs.RemoveAt(0);
                h.P.Paired = true;
                h.P.Device = r.Device;
                if (h.P.Live) Stats.PairedAtHook++; else Stats.PairedLater++;
            }
        }

        // ---- 送出 ----

        bool Flush(long now, bool force)
        {
            outs.Clear();
            bool livePassed = false;
            while (queue.Count > 0)
            {
                if (!TryEmit(queue[0], now, force, ref livePassed)) break;
                queue[0].Done = true;
                queue.RemoveAt(0);
            }
            if (outs.Count > 0) { sink.Send(outs); lastSendMs = now; }
            outs.Clear();
            return livePassed;
        }

        bool TryEmit(Pending p, long now, bool force, ref bool livePassed)
        {
            HookKey k = p.K;

            if (k.Up)
            {
                Held h = (!k.Ext && k.Scan > 0 && k.Scan < 0x80) ? held[k.Scan] : null;
                if (h != null) held[k.Scan] = null;
                if (h != null && !h.Original)
                {
                    outs.Add(new KeyOut(h.T.Vk, h.T.Scan, false, true));
                }
                else
                {
                    EmitOriginal(p, ref livePassed);
                }
                return true;
            }

            if (!Tracked(k.Scan, k.Ext))
            {
                EmitOriginal(p, ref livePassed);
                return true;
            }

            Target t = Translation(k.Scan);
            bool isUs;
            if (t == null) isUs = false; // どの配列でも同じ結果なので元のまま
            else if (Mode == BridgeMode.AllUs) isUs = true;
            else if (p.Paired) isUs = IsUsDevice != null && IsUsDevice(p.Device);
            else if (WaitForRaw && !force && now - p.T < DeviceWaitMs) return false; // Raw Input 待ち
            else
            {
                // このキーの Raw Input が無い → 直前に使われたキーボードで判断する
                if (WaitForRaw && !force)
                {
                    Stats.Timeouts++;
                    Log("timeout: scan=" + k.Scan.ToString("X2"));
                }
                isUs = LastDevice != IntPtr.Zero && IsUsDevice != null && IsUsDevice(LastDevice);
                Stats.Guessed++;
            }

            Held prev = held[k.Scan];
            if (!isUs)
            {
                if (prev != null && !prev.Original) outs.Add(new KeyOut(prev.T.Vk, prev.T.Scan, false, true));
                Held h = new Held();
                h.Original = true;
                h.OrigVk = k.Vk;
                held[k.Scan] = h;
                EmitOriginal(p, ref livePassed);
                return true;
            }

            // US 配列キーボード: 変換して送出
            if (prev != null)
            {
                if (prev.Original) outs.Add(new KeyOut(prev.OrigVk, k.Scan, false, true));
                else if (prev.T != t) outs.Add(new KeyOut(prev.T.Vk, prev.T.Scan, false, true));
            }
            EmitTargetDown(t);
            Held nh = new Held();
            nh.T = t;
            held[k.Scan] = nh;
            Stats.Translated++;
            Log("translate: scan=" + k.Scan.ToString("X2") + " -> " + t.Label);
            return true;
        }

        Target Translation(int scan)
        {
            if (LayoutIsJis != null && !LayoutIsJis()) return null; // 前面アプリが US 配列 (英語入力など)

            bool shifted = lShift || rShift;
            bool ctrl = lCtrl || rCtrl;
            bool alt = lAlt || rAlt;
            bool win = lWin || rWin;

            if (scan == KeyMap.SC_CAPSLOCK) return (CapsLockFix && !shifted) ? KeyMap.CapsLock : null;
            if (scan == KeyMap.SC_GRAVE && alt && !ctrl && !win) return null; // Alt+` → Alt+半角/全角 (IME 切替)
            if (ctrl || alt || win) return KeyMap.ByVk(scan);
            return KeyMap.ByChar(scan, shifted);
        }

        void EmitOriginal(Pending p, ref bool livePassed)
        {
            UpdateModifiers(p.K);
            if (p.Live && outs.Count == 0) livePassed = true; // フックの戻り値で素通しさせる
            else outs.Add(new KeyOut(p.K.Vk, p.K.Scan, p.K.Ext, p.K.Up));
        }

        void EmitTargetDown(Target t)
        {
            bool shifted = lShift || rShift;
            KeyOut down = new KeyOut(t.Vk, t.Scan, false, false);
            if (t.Shift == ShiftReq.On && !shifted)
            {
                outs.Add(new KeyOut(KeyMap.VK_LSHIFT, KeyMap.SC_LSHIFT, false, false));
                outs.Add(down);
                outs.Add(new KeyOut(KeyMap.VK_LSHIFT, KeyMap.SC_LSHIFT, false, true));
            }
            else if (t.Shift == ShiftReq.Off && shifted)
            {
                if (lShift) outs.Add(new KeyOut(KeyMap.VK_LSHIFT, KeyMap.SC_LSHIFT, false, true));
                if (rShift) outs.Add(new KeyOut(KeyMap.VK_RSHIFT, KeyMap.SC_RSHIFT, false, true));
                outs.Add(down);
                if (lShift) outs.Add(new KeyOut(KeyMap.VK_LSHIFT, KeyMap.SC_LSHIFT, false, false));
                if (rShift) outs.Add(new KeyOut(KeyMap.VK_RSHIFT, KeyMap.SC_RSHIFT, false, false));
            }
            else
            {
                outs.Add(down);
            }
        }

        // ---- 修飾キーの状態 ----

        void Resync()
        {
            if (IsKeyDown == null) return;
            lShift = IsKeyDown(KeyMap.VK_LSHIFT);
            rShift = IsKeyDown(KeyMap.VK_RSHIFT);
            lCtrl = IsKeyDown(KeyMap.VK_LCONTROL);
            rCtrl = IsKeyDown(KeyMap.VK_RCONTROL);
            lAlt = IsKeyDown(KeyMap.VK_LMENU);
            rAlt = IsKeyDown(KeyMap.VK_RMENU);
            lWin = IsKeyDown(KeyMap.VK_LWIN);
            rWin = IsKeyDown(KeyMap.VK_RWIN);
        }

        void UpdateModifiers(HookKey k)
        {
            bool down = !k.Up;
            switch (k.Vk)
            {
                case KeyMap.VK_LSHIFT: lShift = down; break;
                case KeyMap.VK_RSHIFT: rShift = down; break;
                case KeyMap.VK_SHIFT: if (k.Scan == KeyMap.SC_RSHIFT) rShift = down; else lShift = down; break;
                case KeyMap.VK_LCONTROL: lCtrl = down; break;
                case KeyMap.VK_RCONTROL: rCtrl = down; break;
                case KeyMap.VK_CONTROL: if (k.Ext) rCtrl = down; else lCtrl = down; break;
                case KeyMap.VK_LMENU: lAlt = down; break;
                case KeyMap.VK_RMENU: rAlt = down; break;
                case KeyMap.VK_MENU: if (k.Ext) rAlt = down; else lAlt = down; break;
                case KeyMap.VK_LWIN: lWin = down; break;
                case KeyMap.VK_RWIN: rWin = down; break;
            }
        }

        void Log(string s)
        {
            if (Trace != null) Trace(s);
        }
    }
}
