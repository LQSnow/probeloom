using Microsoft.UI.Xaml;

namespace ProbeLoom;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    public MainWindow? MainWindowInstance { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindowInstance = new MainWindow();
        MainWindowInstance.Activate();
    }
}
