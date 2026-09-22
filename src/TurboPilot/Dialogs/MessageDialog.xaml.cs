using System.Windows;
using TurbolandTheme.Wpf.Controls;

namespace TurboPilot.Dialogs;

/// <summary>
/// A statement the user acknowledges: a line of text over an OK button,
/// shown modally over an owner window. Escape and the dialog's close box
/// dismiss it the same way, because there is no decision in it to make.
/// </summary>
public partial class MessageDialog : TurbolandFloatingDialog
{
	public MessageDialog()
	{
		InitializeComponent();
	}

	/// <summary>
	/// Shows <paramref name="message"/> over <paramref name="owner"/> and
	/// waits for the user to acknowledge it.
	/// </summary>
	public static void Ok(Window owner, string message, string title = "TurboPilot")
	{
		var dialog = new MessageDialog
		{
			Title = title,
			Message = { Text = message }
		};

		dialog.ShowDialog(owner);
	}

	private void OnOk(object sender, RoutedEventArgs e) => Close(true);
}
