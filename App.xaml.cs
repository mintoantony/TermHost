using Microsoft.Extensions.DependencyInjection;

namespace TermHost;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var page = new MainPage();
		var window = new Window(page)
		{
			Title = "TermHost",
			// Recoloured by MainPage to match the selected theme.
			TitleBar = new TitleBar { Title = "TermHost", BackgroundColor = Color.FromArgb("#181825"), ForegroundColor = Color.FromArgb("#cdd6f4") },
		};
		window.Destroying += (_, _) => page.CloseAll();
		return window;
	}
}