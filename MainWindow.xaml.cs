using System;
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
        StateChanged += MainWindow_StateChanged;
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (System.Windows.Application.Current is App app && app.HandleMainWindowClosing(this))
        {
            e.Cancel = true;
        }
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (System.Windows.Application.Current is App app)
        {
            app.HandleMainWindowStateChanged(this);
        }
    }
}
