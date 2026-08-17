using System;
using System.Globalization;
using System.Windows;

namespace FolderSync.UI.Services
{
    /// <summary>
    /// 统一的对话框消息服务：文案从本地化资源按 key 解析，支持 {0} 占位符格式化。
    /// </summary>
    public static class MessageDialogService
    {
        public static string GetString(string key, params object[] args)
        {
            var raw = Application.Current?.TryFindResource(key)?.ToString();
            if (raw == null)
            {
                return args.Length == 0 ? key : string.Format(CultureInfo.CurrentCulture, key, args);
            }

            return args.Length == 0 ? raw : string.Format(CultureInfo.CurrentCulture, raw, args);
        }

        public static void ShowInfo(string messageKey, string titleKey, params object[] args)
        {
            Show(messageKey, titleKey, MessageBoxImage.Information, args);
        }

        public static void ShowWarning(string messageKey, string titleKey, params object[] args)
        {
            Show(messageKey, titleKey, MessageBoxImage.Warning, args);
        }

        public static void ShowError(string messageKey, string titleKey, params object[] args)
        {
            Show(messageKey, titleKey, MessageBoxImage.Error, args);
        }

        public static bool Confirm(string messageKey, string titleKey, params object[] args)
        {
            var owner = Application.Current?.MainWindow;
            var message = GetString(messageKey, args);
            var title = GetString(titleKey);
            var result = owner == null
                ? MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning)
                : MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning);
            return result == MessageBoxResult.Yes;
        }

        private static void Show(string messageKey, string titleKey, MessageBoxImage icon, params object[] args)
        {
            var owner = Application.Current?.MainWindow;
            var message = GetString(messageKey, args);
            var title = GetString(titleKey);
            if (owner == null)
            {
                MessageBox.Show(message, title, MessageBoxButton.OK, icon);
                return;
            }

            MessageBox.Show(owner, message, title, MessageBoxButton.OK, icon);
        }
    }
}