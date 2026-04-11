using System;
using System.IO;
using System.Windows;
using FolderSync.Core.Scheduler;
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
                // 2. 启动 Quartz 定时任务调度引擎
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

            // 配置 Serilog
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                // 写入到控制台 (调试时有用)
                .WriteTo.Debug()
                // 写入到滚动文件，每天产生一个新文件，最多保留 30 天
                .WriteTo.File(
                    Path.Combine(logDirectory, "foldersync-.txt"), 
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
                )
                .CreateLogger();
        }
    }
}
