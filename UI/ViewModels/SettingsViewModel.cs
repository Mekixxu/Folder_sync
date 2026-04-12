using System.Windows.Input;
using System.Windows;
using System.Collections.ObjectModel;
using System.Windows.Media;
using FolderSync.Core.Config;
using FolderSync.UI.Localization;

namespace FolderSync.UI.ViewModels
{
    /// <summary>
    /// 系统设置页面 ViewModel（基础占位，后续可扩展）
    /// </summary>
    public class SettingsViewModel : ViewModelBase
    {
        private readonly SettingsRepository _settingsRepository = new();

        public ObservableCollection<LanguageOption> LanguageOptions { get; } = new()
        {
            new LanguageOption("zh-CN", "简体中文"),
            new LanguageOption("en-US", "English")
        };

        public ObservableCollection<string> FontOptions { get; } = new()
        {
            "Microsoft YaHei UI",
            "Segoe UI",
            "Arial"
        };

        private bool _startWithWindows;
        public bool StartWithWindows
        {
            get => _startWithWindows;
            set => SetProperty(ref _startWithWindows, value);
        }

        private bool _minimizeToTray = true;
        public bool MinimizeToTray
        {
            get => _minimizeToTray;
            set => SetProperty(ref _minimizeToTray, value);
        }

        private int _logRetentionDays = 30;
        public int LogRetentionDays
        {
            get => _logRetentionDays;
            set => SetProperty(ref _logRetentionDays, value);
        }

        private string _selectedLanguage = "zh-CN";
        public string SelectedLanguage
        {
            get => _selectedLanguage;
            set => SetProperty(ref _selectedLanguage, value);
        }

        private string _selectedFontFamily = "Microsoft YaHei UI";
        public string SelectedFontFamily
        {
            get => _selectedFontFamily;
            set => SetProperty(ref _selectedFontFamily, value);
        }

        private double _uiScale = 1.0;
        public double UiScale
        {
            get => _uiScale;
            set => SetProperty(ref _uiScale, value);
        }

        public ICommand SaveSettingsCommand { get; }

        public SettingsViewModel()
        {
            SaveSettingsCommand = new RelayCommand(_ => SaveSettings());
            LoadSettings();
        }

        private void LoadSettings()
        {
            var settings = _settingsRepository.Load();
            StartWithWindows = settings.StartWithWindows;
            MinimizeToTray = settings.MinimizeToTray;
            LogRetentionDays = settings.LogRetentionDays;
            SelectedLanguage = string.IsNullOrWhiteSpace(settings.Language) ? "zh-CN" : settings.Language;
            SelectedFontFamily = string.IsNullOrWhiteSpace(settings.FontFamily) ? "Microsoft YaHei UI" : settings.FontFamily;
            UiScale = settings.UiScale <= 0 ? 1.0 : settings.UiScale;
        }

        private void SaveSettings()
        {
            _settingsRepository.Save(new AppSettings
            {
                StartWithWindows = StartWithWindows,
                MinimizeToTray = MinimizeToTray,
                LogRetentionDays = LogRetentionDays < 1 ? 1 : LogRetentionDays,
                Language = SelectedLanguage,
                FontFamily = SelectedFontFamily,
                UiScale = UiScale < 0.8 ? 0.8 : (UiScale > 2.0 ? 2.0 : UiScale)
            });

            LocalizationService.ApplyLanguage(SelectedLanguage);
            Application.Current.Resources["AppZoomScale"] = UiScale < 0.8 ? 0.8 : (UiScale > 2.0 ? 2.0 : UiScale);
            Application.Current.Resources["AppFontFamily"] = new FontFamily(SelectedFontFamily);

            MessageBox.Show(
                Application.Current.TryFindResource("Settings.Saved")?.ToString() ?? "设置已保存。",
                Application.Current.TryFindResource("Settings.Tip")?.ToString() ?? "提示",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    public record LanguageOption(string Code, string DisplayName);
}
