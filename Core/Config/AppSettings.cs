namespace FolderSync.Core.Config
{
    public class AppSettings
    {
        public bool StartWithWindows { get; set; }
        public bool MinimizeToTray { get; set; } = true;
        public int LogRetentionDays { get; set; } = 30;
        public string Language { get; set; } = "zh-CN";
        public string FontFamily { get; set; } = "Microsoft YaHei UI";
        public double UiScale { get; set; } = 1.0;
    }
}
