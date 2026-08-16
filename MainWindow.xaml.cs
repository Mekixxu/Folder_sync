using System;
using System.Reflection;
using System.Windows;

namespace FolderSync;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += MainWindow_Closing;
        VersionTextBlock.Text = $"v{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)} Beta";
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (System.Windows.Application.Current is App app && app.HandleMainWindowClosing(this))
        {
            e.Cancel = true;
        }
    }
}
