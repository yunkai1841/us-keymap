using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace UsKeyBridge
{
    public sealed class KeyboardInfo
    {
        public IntPtr Handle;
        public string Path;
        public string Key;        // 設定の保存に使う識別子 (例: HID\VID_046D&PID_C31C&MI_00)
        public string Kind;       // PS/2, USB, Bluetooth, 仮想, その他
        public string Name;
        public string Id;         // VID/PID など
        public bool IsUs;
        public bool Overridden;   // 手動で設定済み

        public string Display
        {
            get
            {
                string s = (Name ?? "キーボード") + "  [" + Kind;
                if (!string.IsNullOrEmpty(Id)) s += " " + Id;
                return s + "]";
            }
        }
    }

    /// <summary>Raw Input のデバイスハンドルからキーボードの種類と US/JIS の区別を求める。</summary>
    public sealed class DeviceDirectory
    {
        readonly Settings settings;
        readonly Dictionary<IntPtr, KeyboardInfo> cache = new Dictionary<IntPtr, KeyboardInfo>();
        // 製品名はデバイスとの通信が必要で遅いことがあるので、表示時にだけ取得して保持する
        readonly Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public DeviceDirectory(Settings settings)
        {
            this.settings = settings;
        }

        public void Invalidate()
        {
            cache.Clear();
        }

        public bool IsUs(IntPtr handle)
        {
            KeyboardInfo info = Get(handle);
            return info != null && info.IsUs;
        }

        public KeyboardInfo Get(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return null;
            KeyboardInfo info;
            if (cache.TryGetValue(handle, out info)) return info;
            info = Build(handle);
            cache[handle] = info;
            return info;
        }

        /// <summary>接続中のキーボード一覧 (同じ識別子のものは 1 つにまとめる)。</summary>
        public List<KeyboardInfo> Enumerate()
        {
            List<KeyboardInfo> result = new List<KeyboardInfo>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            uint count = 0;
            uint cb = (uint)Marshal.SizeOf(typeof(Native.RAWINPUTDEVICELIST));
            if (Native.GetRawInputDeviceList(null, ref count, cb) != 0 || count == 0) return result;
            Native.RAWINPUTDEVICELIST[] list = new Native.RAWINPUTDEVICELIST[count + 8];
            count = (uint)list.Length;
            uint n = Native.GetRawInputDeviceList(list, ref count, cb);
            if (n == uint.MaxValue) return result;
            for (int i = 0; i < n; i++)
            {
                if (list[i].dwType != Native.RIM_TYPEKEYBOARD) continue;
                KeyboardInfo info = Get(list[i].hDevice);
                if (info == null || !seen.Add(info.Key)) continue;
                LoadName(info);
                result.Add(info);
            }
            return result;
        }

        KeyboardInfo Build(IntPtr handle)
        {
            string path = GetDeviceName(handle);
            if (path == null) return null;
            KeyboardInfo info = new KeyboardInfo();
            info.Handle = handle;
            info.Path = path;
            info.Key = MakeKey(path);
            info.Kind = DetectKind(info.Key);
            info.Id = DetectId(info.Key);
            bool ov;
            if (settings.DeviceOverrides.TryGetValue(info.Key, out ov))
            {
                info.IsUs = ov;
                info.Overridden = true;
            }
            else
            {
                info.IsUs = DefaultIsUs(info.Kind);
            }
            return info;
        }

        /// <summary>表示用の製品名を読み込む (フックの中からは呼ばないこと)。</summary>
        public KeyboardInfo LoadName(KeyboardInfo info)
        {
            if (info == null || info.Name != null) return info;
            string name;
            if (!names.TryGetValue(info.Path, out name))
            {
                name = DetectName(info.Path, info.Kind);
                names[info.Path] = name;
            }
            info.Name = name;
            return info;
        }

        /// <summary>手動設定が無い場合: USB/Bluetooth は外付け (US)、それ以外は内蔵 (JIS)。</summary>
        public static bool DefaultIsUs(string kind)
        {
            return kind == "USB" || kind == "Bluetooth";
        }

        static string GetDeviceName(IntPtr handle)
        {
            uint size = 0;
            Native.GetRawInputDeviceInfo(handle, Native.RIDI_DEVICENAME, IntPtr.Zero, ref size);
            if (size == 0) return null;
            IntPtr buf = Marshal.AllocHGlobal((int)(size + 1) * 2);
            try
            {
                uint r = Native.GetRawInputDeviceInfo(handle, Native.RIDI_DEVICENAME, buf, ref size);
                if (r == uint.MaxValue || r == 0) return null;
                string s = Marshal.PtrToStringUni(buf);
                if (s != null && s.StartsWith(@"\??\")) s = @"\\?\" + s.Substring(4);
                return s;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        /// <summary>\\?\HID#VID_046D&amp;PID_C31C&amp;MI_00#7&amp;...#{guid} → HID\VID_046D&amp;PID_C31C&amp;MI_00</summary>
        public static string MakeKey(string path)
        {
            string p = path;
            if (p.StartsWith(@"\\?\") || p.StartsWith(@"\??\")) p = p.Substring(4);
            string[] parts = p.Split('#');
            if (parts.Length >= 2) return (parts[0] + "\\" + parts[1]).ToUpperInvariant();
            return p.ToUpperInvariant();
        }

        public static string DetectKind(string key)
        {
            if (key.StartsWith("ACPI\\")) return "PS/2";
            if (key.StartsWith("ROOT\\") || key.Contains("RDP_KBD")) return "仮想";
            if (key.Contains("{00001124-") || key.Contains("{00001812-")) return "Bluetooth";
            if (key.Contains("VID_")) return "USB";
            return "その他";
        }

        static string DetectId(string key)
        {
            Match m = Regex.Match(key, @"VID[_&](?:0002|0001)?([0-9A-F]{4}).*?PID[_&]([0-9A-F]{4})");
            if (m.Success) return "VID_" + m.Groups[1].Value + " PID_" + m.Groups[2].Value;
            int i = key.IndexOf('\\');
            return i >= 0 ? key.Substring(i + 1) : key;
        }

        static string DetectName(string path, string kind)
        {
            if (kind == "PS/2") return "内蔵キーボード";
            if (kind == "仮想") return "仮想キーボード";
            string product = null, maker = null;
            try
            {
                using (SafeFileHandle h = Native.CreateFile(path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                    IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero))
                {
                    if (!h.IsInvalid)
                    {
                        product = ReadHidString(h, true);
                        maker = ReadHidString(h, false);
                    }
                }
            }
            catch (Exception)
            {
            }
            if (!string.IsNullOrEmpty(product))
            {
                if (!string.IsNullOrEmpty(maker) && product.IndexOf(maker, StringComparison.OrdinalIgnoreCase) < 0)
                    return maker + " " + product;
                return product;
            }
            return "キーボード";
        }

        static string ReadHidString(SafeFileHandle h, bool product)
        {
            byte[] buf = new byte[512];
            bool ok = product ? Native.HidD_GetProductString(h, buf, (uint)buf.Length)
                              : Native.HidD_GetManufacturerString(h, buf, (uint)buf.Length);
            if (!ok) return null;
            string s = Encoding.Unicode.GetString(buf);
            int z = s.IndexOf('\0');
            if (z >= 0) s = s.Substring(0, z);
            s = s.Trim();
            return s.Length == 0 ? null : s;
        }
    }
}
