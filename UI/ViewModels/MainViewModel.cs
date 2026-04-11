using System.Windows.Input;

namespace FolderSync.UI.ViewModels
{
    /// <summary>
    /// 主窗口的 ViewModel，负责控制左侧导航和视图切换
    /// </summary>
    public class MainViewModel : ViewModelBase
    {
        private object? _currentView;

        public MainViewModel()
        {
            // 注册导航命令
            NavigateCommand = new RelayCommand(Navigate);
            
            // 默认显示 Dashboard
            Navigate("Dashboard");
        }

        public object? CurrentView
        {
            get => _currentView;
            set => SetProperty(ref _currentView, value);
        }

        public ICommand NavigateCommand { get; }

        private void Navigate(object? viewName)
        {
            if (viewName is string name)
            {
                switch (name)
                {
                    case "Dashboard":
                        CurrentView = new DashboardViewModel();
                        break;
                    case "Tasks":
                        CurrentView = new TasksViewModel(v => CurrentView = v);
                        break;
                    case "Logs":
                        // CurrentView = new LogsViewModel();
                        break;
                    case "Settings":
                        // CurrentView = new SettingsViewModel();
                        break;
                }
            }
        }
    }
}
