using System.Linq;
using System.Windows;
using TurbolandTheme.Wpf.Controls;
using TurboPilot.Dialogs;

namespace TurboPilot;

public partial class MainWindow : TurbolandWindow
{
	public MainWindow()
	{
		InitializeComponent();
	}

	private void OnAbout(object sender, RoutedEventArgs e)
	{
		// Opening the same dialog twice should raise the existing one rather
		// than stack a duplicate on the host.
		AboutDialog? existing = Dialogs.Dialogs.OfType<AboutDialog>().FirstOrDefault();
		if (existing is not null)
		{
			Dialogs.BringToFront(existing);
			existing.FocusFirstControl();
			return;
		}

		Dialogs.Show(new AboutDialog());
	}
}
