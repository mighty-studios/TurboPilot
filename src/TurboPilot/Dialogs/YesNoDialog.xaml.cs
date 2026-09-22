using System.Windows;
using TurbolandTheme.Wpf.Controls;

namespace TurboPilot.Dialogs;

/// <summary>
/// A question with two answers, shown modally over an owner window. Yes is
/// the default button and No the cancel button, so Enter and Escape answer
/// it from the keyboard. Nothing is treated as a Yes unless the user says
/// so: pressing No, pressing Escape, and closing the dialog by its frame
/// box all come back false.
/// </summary>
public partial class YesNoDialog : TurbolandFloatingDialog
{
	public YesNoDialog()
	{
		InitializeComponent();
	}

	/// <summary>
	/// Asks <paramref name="message"/> over <paramref name="owner"/> and
	/// waits for the answer. True only when the user chose Yes.
	/// </summary>
	public static bool Ask(Window owner, string message, string title = "Confirm")
	{
		var dialog = new YesNoDialog
		{
			Title = title,
			Message = { Text = message }
		};

		return dialog.ShowDialog(owner) == true;
	}

	private void OnYes(object sender, RoutedEventArgs e) => Close(true);

	private void OnNo(object sender, RoutedEventArgs e) => Close(false);
}
