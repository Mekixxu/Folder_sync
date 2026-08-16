using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using FolderSync.Core.Config;
using FolderSync.Core.Scheduler;
using FolderSync.Core.Sync;
using FolderSync.UI.Localization;
using FolderSync.UI.Services;
using Serilog;

namespace FolderSync
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : System.Windows.Application
    {
        private TrayIconService? _trayIconService;
        private bool _isExitRequested;
        private Mutex? _singleInstanceMutex;
        private bool _ownsSingleInstanceMutex;

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 0. 单实例互斥，防止双进程并发写坏 tasks.json / SQLite / 日志
            _singleInstanceMutex = new Mutex(true, @"Local\FolderSyncPro", out var isFirstInstance);
            _ownsSingleInstanceMutex = isFirstInstance;
            if (!isFirstInstance)
            {
                MessageBox.Show("FolderSync Pro 已在运行。", "FolderSync Pro", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(-1);
                return;
            }

            // 1. 初始化 Serilog 日志记录器
            InitializeLogging();
            RegisterGlobalExceptionHandlers();

            Log.Information("================================================");
            Log.Information("FolderSync Application Starting...");
            Log.Information("================================================");

            try
            {
                var startInTray = ShouldStartInTray(e.Args);

// 2. 加载显示与语言设置
                var settings = ApplyDisplaySettings();

                // 3. 按保留天数清理过期运行日志与任务报告
                CleanupOldLogs(settings);

                // 4. 启动 Quartz 定时任务调度引擎
                await SchedulerManager.Instance.StartAsync();

                // 5. 从 tasks.json 恢复全部定时任务注册
                await RestoreScheduledTasksAsync();

                // 6. 初始化托盘常驻与主窗口
                InitializeTrayIcon();
                MainWindow = new MainWindow();
                if (startInTray)
                {
                    HideMainWindowToTray(MainWindow);
                }
                else
                {
                    MainWindow.Show();
                }
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Failed to start Quartz Scheduler.");
                MessageBox.Show($"Failed to initialize task scheduler: {ex.Message}", "Critical Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(-1);
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

            _trayIconService?.Dispose();

            // 刷新并关闭日志流
            Log.CloseAndFlush();

            if (_ownsSingleInstanceMutex)
            {
                _singleInstanceMutex?.ReleaseMutex();
            }
            _singleInstanceMutex?.Dispose();

            base.OnExit(e);
        }

        private void InitializeLogging()
        {
            // 确定日志文件存放目录 (当前运行目录下的 log 文件夹)
            string logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log");
            if (!Directory.Exists(logDirectory))
            {
                Directory.CreateDirectory(logDirectory);
            }

            // 运行日志文件：无固定前缀，使用时间戳+进程号，避免重名并便于追溯单次运行
            string runtimeLogFile = Path.Combine(
                logDirectory,
                $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Environment.ProcessId}.log"
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

        private void RegisterGlobalExceptionHandlers()
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnTaskSchedulerUnobservedTaskException;
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Fatal(e.Exception, "Unhandled dispatcher exception.");

            MessageBox.Show(
                $"程序捕获到未处理异常：{e.Exception.Message}\n\n详细信息已写入 log 文件夹，请将最新 .log 文件提供出来。",
                "未处理异常",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            e.Handled = true;
        }

        private static void OnCurrentDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                Log.Fatal(ex, "Unhandled AppDomain exception. IsTerminating={IsTerminating}", e.IsTerminating);
            }
            else
            {
                Log.Fatal("Unhandled AppDomain exception. IsTerminating={IsTerminating}, ExceptionObject={ExceptionObject}", e.IsTerminating, e.ExceptionObject);
            }

            Log.CloseAndFlush();
        }

        private static void OnTaskSchedulerUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            Log.Fatal(e.Exception, "Unobserved task exception.");
            e.SetObserved();
        }

        private AppSettings ApplyDisplaySettings()
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

            return settings;
        }

        private static void CleanupOldLogs(AppSettings settings)
        {
            try
            {
                var logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log");
                if (!Directory.Exists(logDirectory))
                {
                    return;
                }

                var retentionDays = Math.Max(settings.LogRetentionDays, 1);
                var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

                foreach (var file in Directory.EnumerateFiles(logDirectory)
                             .Where(f => f.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
                                         || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        if (info.LastWriteTimeUtc < cutoff)
                        {
                            info.Delete();
                            Log.Information("Cleaned up expired log file {FileName}", info.Name);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Failed to delete expired log file {FilePath}", file);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to clean up old logs");
            }
        }

        private void InitializeTrayIcon()
        {
            _trayIconService = new TrayIconService(ShowMainWindowFromTray, ExitApplicationFromTray);
            _trayIconService.Hide();
        }

        private static async Task RestoreScheduledTasksAsync()
        {
            List<SyncTaskDefinition> tasks;
            try
            {
                tasks = new TaskRepository().LoadAll();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to load task definitions during scheduler restore. Scheduled jobs were not restored.");
                return;
            }

            foreach (var task in tasks.Where(t => !t.IsManualTrigger))
            {
                try
                {
                    var cron = SyncTaskFactory.ResolveCronExpression(task);
                    var executor = SyncTaskFactory.CreateExecutor(task);
                    await SchedulerManager.Instance.AddOrUpdateJobAsync(task.Id, task.TaskName, cron, executor);
                    Log.Information("Restored scheduled job {TaskName} ({TaskId}) with cron {Cron}", task.TaskName, task.Id, cron);
                }
                catch (Exception ex)
                {
                    // 单个任务损坏不能阻断整个应用启动
                    Log.Error(ex, "Failed to restore scheduled job {TaskName} ({TaskId})", task.TaskName, task.Id);
                }
            }
        }

        private static bool ShouldStartInTray(string[] args)
        {
            foreach (var arg in args)
            {
                if (string.Equals(arg, StartupRegistrationService.TrayStartupArgument, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public bool ShouldMinimizeToTray()
        {
            return new SettingsRepository().Load().MinimizeToTray;
        }

        public bool HandleMainWindowClosing(Window window)
        {
            if (_isExitRequested || !ShouldMinimizeToTray())
            {
                return false;
            }

            HideMainWindowToTray(window);
            return true;
        }

        public void RefreshTrayIconText()
        {
            _trayIconService?.RefreshText();
        }

        private void HideMainWindowToTray(Window window)
        {
            _trayIconService?.RefreshText();
            _trayIconService?.Show();
            window.Hide();
        }

        private void ShowMainWindowFromTray()
        {
            var window = MainWindow;
            if (window == null)
            {
                return;
            }

            if (!window.IsVisible)
            {
                window.Show();
            }

            window.WindowState = WindowState.Normal;
            window.ShowInTaskbar = true;
            window.Activate();
            _trayIconService?.Hide();
        }

        private void ExitApplicationFromTray()
        {
            _isExitRequested = true;
            _trayIconService?.Hide();
            MainWindow?.Close();
            Shutdown();
        }
    }
}
