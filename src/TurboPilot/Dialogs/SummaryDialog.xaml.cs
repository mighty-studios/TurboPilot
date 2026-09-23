using System.Windows;
using TurbolandTheme.Wpf.Controls;

namespace TurboPilot.Dialogs;

public partial class SummaryDialog : TurbolandFloatingDialog
{
	public SummaryDialog(string summary)
	{
		InitializeComponent();
		textSummary.Text = summary;
	}

	private void OnCopy(object sender, RoutedEventArgs e)
	{
		try { Clipboard.SetText(textSummary.Text); }
		catch (System.Runtime.InteropServices.ExternalException ex)
		{
			MessageDialog.Ok(this, "Cannot copy the summary: " + ex.Message, "Mediator");
		}
	}

	private void OnClose(object sender, RoutedEventArgs e) => Close(true);
}
