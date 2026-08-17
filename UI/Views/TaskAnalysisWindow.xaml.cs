using System.ComponentModel;
using System.Windows;
using FolderSync.UI.ViewModels;

namespace FolderSync.UI.Views
{
    /// <summary>
    /// Interaction logic for TaskAnalysisWindow.xaml
    /// </summary>
    public partial class TaskAnalysisWindow : Window
    {
        public TaskAnalysisWindow()
        {
            InitializeComponent();
            Closing += OnWindowClosing;
        }

        private void OnWindowClosing(object? sender, CancelEventArgs e)
        {
            if (DataContext is not TaskAnalysisViewModel viewModel)
            {
                return;
            }

            viewModel.CancelPendingOperations();

            if (!viewModel.HasUnsavedChanges)
            {
                return;
            }

            var result = MessageBox.Show(
                this,
                "分析结果有未保存的手动修改，是否保存后再关闭？",
                "关闭确认",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                viewModel.SaveBeforeClose();
                e.Cancel = viewModel.HasUnsavedChanges;
            }
            else if (result == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
            }
        }
    }
}