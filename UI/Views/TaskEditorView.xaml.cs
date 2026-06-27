using System.Windows.Controls;
using FolderSync.UI.ViewModels;

namespace FolderSync.UI.Views
{
    /// <summary>
    /// Interaction logic for TaskEditorView.xaml
    /// </summary>
    public partial class TaskEditorView : System.Windows.Controls.UserControl
    {
        public TaskEditorView()
        {
            InitializeComponent();
            DataContextChanged += TaskEditorView_DataContextChanged;
        }

        private void TaskEditorView_DataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is TaskEditorViewModel oldVm)
            {
                oldVm.PropertyChanged -= ViewModel_PropertyChanged;
            }

            if (e.NewValue is TaskEditorViewModel newVm)
            {
                newVm.PropertyChanged += ViewModel_PropertyChanged;
                SyncPasswordBoxes(newVm);
            }
        }

        private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (sender is not TaskEditorViewModel vm)
            {
                return;
            }

            if (e.PropertyName is nameof(TaskEditorViewModel.SourceFtpPassword) or nameof(TaskEditorViewModel.DestFtpPassword))
            {
                SyncPasswordBoxes(vm);
            }
        }

        private void SyncPasswordBoxes(TaskEditorViewModel vm)
        {
            if (SourceFtpPasswordBox.Password != vm.SourceFtpPassword)
            {
                SourceFtpPasswordBox.Password = vm.SourceFtpPassword ?? string.Empty;
            }

            if (DestFtpPasswordBox.Password != vm.DestFtpPassword)
            {
                DestFtpPasswordBox.Password = vm.DestFtpPassword ?? string.Empty;
            }
        }

        private void SourceFtpPasswordBox_OnPasswordChanged(object sender, System.Windows.RoutedEventArgs e)
        {
            if (DataContext is TaskEditorViewModel vm && vm.SourceFtpPassword != SourceFtpPasswordBox.Password)
            {
                vm.SourceFtpPassword = SourceFtpPasswordBox.Password;
            }
        }

        private void DestFtpPasswordBox_OnPasswordChanged(object sender, System.Windows.RoutedEventArgs e)
        {
            if (DataContext is TaskEditorViewModel vm && vm.DestFtpPassword != DestFtpPasswordBox.Password)
            {
                vm.DestFtpPassword = DestFtpPasswordBox.Password;
            }
        }
    }
}
