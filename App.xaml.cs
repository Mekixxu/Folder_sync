using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using FolderSync.Core.Config;
using FolderSync.Core.Scheduler;
using FolderSync.UI.Localization;
using Serilog;

namespace FolderSync
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 1. 初始化 Serilog 日志记录器
            InitializeLogging();

            Log.Information("================================================");
            Log.Information("FolderSync Application Starting...");
            Log.Information("================================================");

            try
            {
                // 2. 加载显示与语言设置
                ApplyDisplaySettings();

                // 3. 启动 Quartz 定时任务调度引擎
                await SchedulerManager.Instance.StartAsync();
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Failed to start Quartz Scheduler.");
                MessageBox.Show($"Failed to initialize task scheduler: {ex.Message}", "Critical Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            Log.Information("FolderSync Application Exiting...");
            
            try
            {
                // 停止调度引擎
                await SchedulerManager.Instance.StopAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error occurred while stopping the scheduler.");
            }

            // 刷新并关闭日志流
            Log.CloseAndFlush();
            
            base.OnExit(e);
        }

        private void InitializeLogging()
        {
            // 确定日志文件存放目录 (当前运行目录下的 logs 文件夹)
            string logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            if (!Directory.Exists(logDirectory))
            {
                Directory.CreateDirectory(logDirectory);
            }

            // 运行日志文件：无固定前缀，使用时间戳+进程号，避免重名并便于追溯单次运行
            string runtimeLogFile = Path.Combine(
                logDirectory,
                $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Environment.ProcessId}.txt"
            );

            // 配置 Serilog
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                // 写入到控制台 (调试时有用)
                .WriteTo.Debug()
                // 写入到运行日志文件（每次启动一个新文件，避免并发冲突）
                .WriteTo.File(
                    runtimeLogFile,
                    retainedFileCountLimit: 30,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
                )
                .CreateLogger();
        }

        private void ApplyDisplaySettings()
        {
            var settings = new SettingsRepository().Load();
            LocalizationService.ApplyLanguage(settings.Language);

            if (settings.UiScale < 0.8) settings.UiScale = 0.8;
            if (settings.UiScale > 2.0) settings.UiScale = 2.0;

            Resources["AppZoomScale"] = settings.UiScale;

            try
            {
                Resources["AppFontFamily"] = new FontFamily(settings.FontFamily);
            }
            catch
            {
                Resources["AppFontFamily"] = new FontFamily("Microsoft YaHei UI");
            }
        }
    }
}
