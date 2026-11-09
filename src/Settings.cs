using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Microsoft.Win32;

namespace UsKeyBridge
{
    /// <summary>設定 (HKCU\Software\UsKeyBridge に保存)。</summary>
    public sealed class Settings
    {
        const string RootKey = @"Software\UsKeyBridge";
        const string DevicesKey = RootKey + @"\Devices";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "UsKeyBridge";

        public bool Enabled = true;
        public BridgeMode Mode = BridgeMode.PerDevice;
        public bool CapsLockFix = true;
        public bool Log = false;

        /// <summary>デバイスキー → US 配列なら true。登録が無いデバイスは自動判定。</summary>
        public readonly Dictionary<string, bool> DeviceOverrides =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public void Load()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RootKey))
                {
                    if (k != null)
                    {
                        Enabled = ReadInt(k, "Enabled", 1) != 0;
                        Mode = ReadInt(k, "Mode", 0) == 1 ? BridgeMode.AllUs : BridgeMode.PerDevice;
                        CapsLockFix = ReadInt(k, "CapsLockFix", 1) != 0;
                        Log = ReadInt(k, "Log", 0) != 0;
                    }
                }
                DeviceOverrides.Clear();
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(DevicesKey))
                {
                    if (k != null)
                    {
                        foreach (string name in k.GetValueNames())
                            DeviceOverrides[name] = ReadInt(k, name, 0) != 0;
                    }
                }
            }
            catch (Exception)
            {
                // 読めなければ既定値のまま
            }
        }

        public void Save()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RootKey))
                {
                    k.SetValue("Enabled", Enabled ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("Mode", (int)Mode, RegistryValueKind.DWord);
                    k.SetValue("CapsLockFix", CapsLockFix ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("Log", Log ? 1 : 0, RegistryValueKind.DWord);
                }
                Registry.CurrentUser.DeleteSubKeyTree(DevicesKey, false);
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(DevicesKey))
                {
                    foreach (KeyValuePair<string, bool> kv in DeviceOverrides)
                        k.SetValue(kv.Key, kv.Value ? 1 : 0, RegistryValueKind.DWord);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("設定を保存できませんでした。\n" + ex.Message, "UsKeyBridge",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        static int ReadInt(RegistryKey k, string name, int def)
        {
            object v = k.GetValue(name);
            return v is int ? (int)v : def;
        }

        /// <summary>Windows 起動時の自動実行 (HKCU\...\Run)。</summary>
        public static bool StartupEnabled
        {
            get
            {
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                    {
                        string v = k == null ? null : k.GetValue(RunValue) as string;
                        return v != null && v.IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                }
                catch (Exception) { return false; }
            }
            set
            {
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                    {
                        if (value) k.SetValue(RunValue, "\"" + Application.ExecutablePath + "\"");
                        else k.DeleteValue(RunValue, false);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show("自動起動の設定を変更できませんでした。\n" + ex.Message, "UsKeyBridge",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }
    }
}
