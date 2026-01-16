using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using MessageArchive.ViewModels;

namespace MessageArchive;

public partial class MainWindow : Window
{
    private MainViewModel ViewModel => (MainViewModel)DataContext;
    
    public MainWindow()
    {
        InitializeComponent();
        Closed += (s, e) => ViewModel.Dispose();
        
        ViewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.HighlightedMessage) && ViewModel.HighlightedMessage != null)
            {
                MessageList.ScrollIntoView(ViewModel.HighlightedMessage);
            }
        };
    }
    
    private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var aboutWindow = new AboutWindow();
        aboutWindow.Owner = this;
        aboutWindow.ShowDialog();
    }
    
    private void StarOnGitHub_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "This will open the Message Archive GitHub repository in your default browser.\n\nWould you like to continue?",
            "Open GitHub",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
            
        if (result == MessageBoxResult.Yes)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/PrathameshWalunj/message-archive",
                UseShellExecute = true
            });
        }
    }
    
    private void ReportProblem_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "How would you like to report the problem?\n\nYes = Open GitHub Issues (recommended)\nNo = Send Email",
            "Report a Problem",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
            
        if (result == MessageBoxResult.Yes)
        {
            // Open GitHub Issues
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/PrathameshWalunj/message-archive/issues/new",
                UseShellExecute = true
            });
        }
        else if (result == MessageBoxResult.No)
        {
            // Open email
            var subject = Uri.EscapeDataString("Message Archive - Problem Report");
            var body = Uri.EscapeDataString($"Please describe the issue:\n\n\n\nSystem: {Environment.OSVersion.VersionString}\nApp: 1.0.0");
            Process.Start(new ProcessStartInfo
            {
                FileName = $"mailto:support.messagearchive@proton.me?subject={subject}&body={body}",
                UseShellExecute = true
            });
        }
    }
    
    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu != null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.IsOpen = true;
        }
    }
}
