using System;
using System.Globalization;
using System.Linq;
using System.Windows;

namespace FolderSync.UI.Localization
{
    public static class LocalizationService
    {
        private const string ResourcePrefix = "UI/Localization/Strings.";

        public static void ApplyLanguage(string languageCode)
        {
            var app = Application.Current;
            if (app == null)
            {
                return;
            }

            var normalized = string.Equals(languageCode, "en-US", StringComparison.OrdinalIgnoreCase)
                ? "en-US"
                : "zh-CN";

            var culture = new CultureInfo(normalized);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;

            var dictionaries = app.Resources.MergedDictionaries;
            var existing = dictionaries.FirstOrDefault(d =>
                d.Source != null &&
                d.Source.OriginalString.Contains($"{ResourcePrefix}zh-CN.xaml", StringComparison.OrdinalIgnoreCase) ||
                d.Source != null &&
                d.Source.OriginalString.Contains($"{ResourcePrefix}en-US.xaml", StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                dictionaries.Remove(existing);
            }

            var uri = new Uri($"/FolderSync;component/{ResourcePrefix}{normalized}.xaml", UriKind.Relative);
            dictionaries.Add(new ResourceDictionary { Source = uri });
        }
    }
}
