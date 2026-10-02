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
		var window = new Window(page) { Title = "TermHost" };
		window.Destroying += (_, _) => page.CloseAll();
		return window;
	}
}