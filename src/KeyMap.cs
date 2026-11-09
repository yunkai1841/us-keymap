namespace UsKeyBridge
{
    /// <summary>出力キーを押すときに必要な Shift の状態。</summary>
    public enum ShiftReq
    {
        Keep = 0, // 現在の Shift 状態のまま
        On = 1,   // Shift を押した状態にする
        Off = 2   // Shift を離した状態にする
    }

    /// <summary>JIS 配列 (kbd106) 上で送出するキー。</summary>
    public sealed class Target
    {
        public readonly int Scan;
        public readonly int Vk;
        public readonly ShiftReq Shift;
        public readonly string Label;

        public Target(int scan, int vk, ShiftReq shift, string label)
        {
            Scan = scan;
            Vk = vk;
            Shift = shift;
            Label = label;
        }

        public override string ToString() { return Label; }
    }

    /// <summary>
    /// US 配列キーボードのスキャンコードを、OS が JIS 配列 (kbd106) のときに
    /// 同じ結果になるキーへ対応付ける表。
    /// </summary>
    public static class KeyMap
    {
        public const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_CAPITAL = 0x14;
        public const int VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3;
        public const int VK_LMENU = 0xA4, VK_RMENU = 0xA5, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

        public const int SC_LSHIFT = 0x2A, SC_RSHIFT = 0x36, SC_CAPSLOCK = 0x3A, SC_GRAVE = 0x29;

        // 文字として変換 (修飾キーなし / Shift のみ)
        static readonly Target[] charUnshifted = new Target[0x80];
        static readonly Target[] charShifted = new Target[0x80];
        // Ctrl / Alt / Win と同時押し: US 配列と同じ仮想キーコードになる JIS のキーへ
        static readonly Target[] vkMap = new Target[0x80];

        /// <summary>US 配列キーボードの CapsLock を本来の CapsLock として送る。</summary>
        public static readonly Target CapsLock = new Target(SC_CAPSLOCK, VK_CAPITAL, ShiftReq.Keep, "CapsLock");

        static KeyMap()
        {
            //   US キー  Shift なし                      Shift あり
            Char(0x29, J(0x1A, ShiftReq.On, "`"),       J(0x0D, ShiftReq.On, "~"));
            Char(0x03, null,                            J(0x1A, ShiftReq.Off, "@"));
            Char(0x07, null,                            J(0x0D, ShiftReq.Off, "^"));
            Char(0x08, null,                            J(0x07, ShiftReq.On, "&"));
            Char(0x09, null,                            J(0x28, ShiftReq.On, "*"));
            Char(0x0A, null,                            J(0x09, ShiftReq.On, "("));
            Char(0x0B, null,                            J(0x0A, ShiftReq.On, ")"));
            Char(0x0C, null,                            J(0x73, ShiftReq.On, "_"));
            Char(0x0D, J(0x0C, ShiftReq.On, "="),       J(0x27, ShiftReq.On, "+"));
            Char(0x1A, J(0x1B, ShiftReq.Off, "["),      J(0x1B, ShiftReq.On, "{"));
            Char(0x1B, J(0x2B, ShiftReq.Off, "]"),      J(0x2B, ShiftReq.On, "}"));
            Char(0x2B, J(0x7D, ShiftReq.Off, "\\"),     J(0x7D, ShiftReq.On, "|"));
            Char(0x27, null,                            J(0x28, ShiftReq.Off, ":"));
            Char(0x28, J(0x08, ShiftReq.On, "'"),       J(0x03, ShiftReq.On, "\""));

            // 同じ仮想キーコード (VK_OEM_x) を持つ JIS 側のキー
            Vk(0x29, 0x1A, "VK_OEM_3");    // `
            Vk(0x0D, 0x27, "VK_OEM_PLUS"); // =
            Vk(0x1A, 0x1B, "VK_OEM_4");    // [
            Vk(0x1B, 0x2B, "VK_OEM_6");    // ]
            Vk(0x2B, 0x7D, "VK_OEM_5");    // \
            Vk(0x27, 0x28, "VK_OEM_1");    // ;
            Vk(0x28, 0x0D, "VK_OEM_7");    // '
        }

        /// <summary>JIS 配列 (kbd106) でのスキャンコード→仮想キーコード (送出先のキーのみ)。</summary>
        public static int JisVk(int scan)
        {
            switch (scan)
            {
                case 0x03: return 0x32; // 2 "
                case 0x07: return 0x36; // 6 &
                case 0x08: return 0x37; // 7 '
                case 0x09: return 0x38; // 8 (
                case 0x0A: return 0x39; // 9 )
                case 0x0C: return 0xBD; // - =   VK_OEM_MINUS
                case 0x0D: return 0xDE; // ^ ~   VK_OEM_7
                case 0x1A: return 0xC0; // @ `   VK_OEM_3
                case 0x1B: return 0xDB; // [ {   VK_OEM_4
                case 0x27: return 0xBB; // ; +   VK_OEM_PLUS
                case 0x28: return 0xBA; // : *   VK_OEM_1
                case 0x2B: return 0xDD; // ] }   VK_OEM_6
                case 0x73: return 0xE2; // \ _   VK_OEM_102 (ろ)
                case 0x7D: return 0xDC; // \ |   VK_OEM_5 (￥)
                default: return 0;
            }
        }

        static Target J(int scan, ShiftReq shift, string label)
        {
            return new Target(scan, JisVk(scan), shift, label);
        }

        static void Char(int usScan, Target unshifted, Target shifted)
        {
            charUnshifted[usScan] = unshifted;
            charShifted[usScan] = shifted;
        }

        static void Vk(int usScan, int jisScan, string label)
        {
            vkMap[usScan] = new Target(jisScan, JisVk(jisScan), ShiftReq.Keep, label);
        }

        /// <summary>変換対象になりうるキーか (拡張キーは対象外)。CapsLock は Engine 側で扱う。</summary>
        public static bool IsSource(int scan)
        {
            if (scan <= 0 || scan >= 0x80) return false;
            return charUnshifted[scan] != null || charShifted[scan] != null || vkMap[scan] != null;
        }

        /// <summary>文字としての変換先。null は変換不要 (同じキーで同じ文字)。</summary>
        public static Target ByChar(int scan, bool shifted)
        {
            if (scan <= 0 || scan >= 0x80) return null;
            return shifted ? charShifted[scan] : charUnshifted[scan];
        }

        /// <summary>Ctrl/Alt/Win 併用時の変換先。null は変換不要。</summary>
        public static Target ByVk(int scan)
        {
            if (scan <= 0 || scan >= 0x80) return null;
            return vkMap[scan];
        }
    }
}
