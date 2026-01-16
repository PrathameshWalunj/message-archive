using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace MessageArchive;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
    }
    
    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = e.Uri.AbsoluteUri,
            UseShellExecute = true
        });
        e.Handled = true;
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
    
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
