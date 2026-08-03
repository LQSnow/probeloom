using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace ProbeLoom;

public sealed partial class MainWindow : Window
{
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new SizeInt32(1440, 900));
        AppWindow.Closing += AppWindow_Closing;
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 1040;
            presenter.PreferredMinimumHeight = 680;
        }

        RootFrame.Navigate(typeof(MainPage));
    }

    public void UpdateProjectTitle(string? projectName, bool isDirty)
    {
        TitleProjectText.Text = string.IsNullOrWhiteSpace(projectName) ? "未打开项目" : projectName;
        TitleDirtyText.Visibility = isDirty ? Visibility.Visible : Visibility.Collapsed;
        Title = string.IsNullOrWhiteSpace(projectName)
            ? "ProbeLoom"
            : $"{projectName}{(isDirty ? " *" : string.Empty)} — ProbeLoom";
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose || RootFrame.Content is not MainPage page)
        {
            return;
        }

        args.Cancel = true;
        if (await page.ConfirmCloseAsync())
        {
            _allowClose = true;
            Close();
        }
    }
}
