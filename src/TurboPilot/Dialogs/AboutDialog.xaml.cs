using System.Diagnostics;
using System.Reflection;
using System.Windows;
using TurbolandTheme.Wpf.Controls;

namespace TurboPilot.Dialogs;

/// <summary>
/// The About box: a message over a Website and an OK button, shown as a
/// floating dialog window owned by the main window.
/// </summary>
public partial class AboutDialog : TurbolandFloatingDialog
{
	public AboutDialog()
	{
		InitializeComponent();
		Message.Text = BuildMessage();
	}

	/// <summary>
	/// The URL the Website button opens in the default browser. Assign before
	/// showing the dialog; an empty or invalid value leaves the button inert.
	/// </summary>
	public string WebsiteUrl { get; set; } = "https://github.com/mighty-studios/TurboPilot";

	private void OnOk(object sender, RoutedEventArgs e) => Close(true);

	private void OnWebsite(object sender, RoutedEventArgs e)
	{
		if (!Uri.TryCreate(WebsiteUrl, UriKind.Absolute, out Uri? uri))
			return;

		Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
	}

	private static string BuildMessage()
	{
		Version version = Assembly.GetExecutingAssembly().GetName().Version
			?? new Version(1, 0, 0);

		return string.Join(Environment.NewLine,
			"TURBO-PILOT",
			$"Version {version.Major}.{version.Minor}.{version.Build}",
			"",
			"An open-source retro interface for working with LLM models.",
			"",
			"TurbolandWPF UI Theme includes the font 'Px437 IBM VGA 9x16'",
			"by VileR, https://int10h.org/oldschool-pc-fonts/",
			"used under license [CC BY-SA 4.0]",
			"",
			"Copyright (c) 2026 Mighty Studios",
			"www.mightystudios.com",
			"https://github.com/mighty-studios/TurboPilot",
			""
			);
	}
}
