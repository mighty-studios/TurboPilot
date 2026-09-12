using System.Windows;
using Theme = TurbolandTheme.Wpf.TurbolandTheme;

namespace TurboPilot;

public partial class App : System.Windows.Application
{
	protected override void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);

		Theme.Apply(this, TurbolandTheme.Core.ThemeMode.Authentic);

		var window = new MainWindow();
		Theme.ApplyTo(window);
		window.Show();
	}
}
