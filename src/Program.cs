using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("UsKeyBridge")]
[assembly: AssemblyProduct("UsKeyBridge")]
[assembly: AssemblyDescription("日本語配列の Windows で US 配列の外付けキーボードを使うための常駐ツール")]
[assembly: AssemblyVersion("1.0.0.0")]

namespace UsKeyBridge
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (Mutex mutex = new Mutex(true, @"Local\UsKeyBridge_SingleInstance", out created))
            {
                if (!created)
                {
                    MessageBox.Show("UsKeyBridge は既に起動しています。\n通知領域 (タスクトレイ) の「US」アイコンを確認してください。",
                        "UsKeyBridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                {
                    MessageBox.Show("予期しないエラーが発生しました。\n" + e.Exception.Message, "UsKeyBridge",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                };

                // フックの処理が遅れて Windows にフックを外されないよう、優先度を少し上げる
                try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal; }
                catch (Exception) { }

                Application.Run(new TrayApp());
                GC.KeepAlive(mutex);
            }
        }
    }
}
