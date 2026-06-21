using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace FolderSync.UI.Services
{
    /// <summary>
    /// 管理当前用户级别的 Windows 开机启动注册表项。
    /// </summary>
    public sealed class StartupRegistrationService
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "FolderSyncPro";

        public bool IsEnabled()
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var value = runKey?.GetValue(RunValueName) as string;
            return !string.IsNullOrWhiteSpace(value);
        }

        public void SetEnabled(bool enabled)
        {
            using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new InvalidOperationException("无法打开开机启动注册表项。");

            if (!enabled)
            {
                runKey.DeleteValue(RunValueName, throwOnMissingValue: false);
                return;
            }

            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                executablePath = Process.GetCurrentProcess().MainModule?.FileName;
            }

            if (string.IsNullOrWhiteSpace(executablePath))
            {
                throw new InvalidOperationException("无法确定当前程序路径，无法设置开机启动。");
            }

            runKey.SetValue(RunValueName, $"\"{executablePath}\"", RegistryValueKind.String);
        }
    }
}
