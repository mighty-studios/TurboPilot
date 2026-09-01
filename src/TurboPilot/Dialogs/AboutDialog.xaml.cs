using System.Reflection;
using System.Windows;
using TurbolandTheme.Wpf.Controls;

namespace TurboPilot.Dialogs;

/// <summary>
/// The About box: a message over a single OK button, shown on the main
/// window's in-client dialog host.
/// </summary>
public partial class AboutDialog : TurbolandDialog
{
	public AboutDialog()
	{
		InitializeComponent();
		Message.Text = BuildMessage();
	}

	private void OnOk(object sender, RoutedEventArgs e) => Close(true);

	private static string BuildMessage()
	{
		Version version = Assembly.GetExecutingAssembly().GetName().Version
			?? new Version(1, 0, 0);

		return string.Join(Environment.NewLine,
			"TURBO-PILOT",
			$"Version {version.Major}.{version.Minor}.{version.Build}",
			"",
			"A retro interface for working with LLM models.",
			"Remote services: Copilot CLI, OpenAI-compatible servers.",
			"Mediator AI: on-device model via Microsoft Foundry Local.",
			"",
			"Copyright (c) 2026 Mighty Studios");
	}
}
