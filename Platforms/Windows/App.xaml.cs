using Microsoft.UI.Xaml;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace TermHost.WinUI;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : MauiWinUIApplication
{
	/// <summary>
	/// Initializes the singleton application object.  This is the first line of authored code
	/// executed, and as such is the logical equivalent of main() or WinMain().
	/// </summary>
	public App()
	{
		// WebView2 keeps its data beside the executable by default, which an installed
		// copy may not be allowed to write to: keep it with the user's other app data.
		Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER",
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TermHost", "WebView2"));
		this.InitializeComponent();
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}

