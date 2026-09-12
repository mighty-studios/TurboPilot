using System.IO;
using System.Windows;
using System.Windows.Interop;
using TurbolandTheme.Wpf.Controls;

namespace TurboPilot.Dialogs;

/// <summary>
/// Dialog for selecting a workspace folder for a new session.
/// Shown as a floating dialog window owned by the main window, so it sorts
/// above the WebView2 content and against other windows by OS rule.
/// Persists the last-used workspace path to a settings file between runs.
/// </summary>
public partial class ChooseFolderDialog : TurbolandFloatingDialog
{
	/// <summary>
	/// The selected workspace folder path, set when the user clicks OK.
	/// </summary>
	public string? WorkspacePath { get; private set; }

	public ChooseFolderDialog()
	{
		InitializeComponent();

		// Load the last-used workspace path from settings
		var lastPath = Settings.Load().LastWorkspacePath;
		if (!string.IsNullOrEmpty(lastPath))
		{
			textBoxWorkspacePath.Text = lastPath;
		}

		// Validate on text change to enable/disable OK button
		textBoxWorkspacePath.TextChanged += (_, _) => ValidatePath();
		ValidatePath();
	}

	private void OnBrowse(object sender, RoutedEventArgs e)
	{
		var folderDialog = new System.Windows.Forms.FolderBrowserDialog
		{
			Description = "Select the workspace folder for this session",
			UseDescriptionForTitle = true,
		};

		// Start in the last-used path if set
		if (!string.IsNullOrEmpty(textBoxWorkspacePath.Text))
		{
			folderDialog.InitialDirectory = textBoxWorkspacePath.Text;
		}

		// Own the native folder picker with this dialog's HWND so it stays
		// above the floating dialog instead of floating loose on the desktop.
		if (folderDialog.ShowDialog(new Win32Window(new WindowInteropHelper(this).Handle)) == System.Windows.Forms.DialogResult.OK)
		{
			textBoxWorkspacePath.Text = folderDialog.SelectedPath;
		}
	}

	private void OnOk(object sender, RoutedEventArgs e)
	{
		WorkspacePath = textBoxWorkspacePath.Text?.Trim();
		if (!string.IsNullOrEmpty(WorkspacePath) && Directory.Exists(WorkspacePath))
		{
			// Persist the workspace path for next run
			var settings = Settings.Load();
			settings.LastWorkspacePath = WorkspacePath;
			settings.Save();

			Close(true);
		}
	}

	private void OnCancel(object sender, RoutedEventArgs e)
	{
		Close(false);
	}

	private void ValidatePath()
	{
		var path = textBoxWorkspacePath.Text?.Trim();
		buttonOk.IsEnabled = !string.IsNullOrEmpty(path) && Directory.Exists(path);
	}

	/// <summary>
	/// Adapts a raw HWND to <see cref="System.Windows.Forms.IWin32Window"/> so the
	/// WinForms folder picker can be owned by this WPF window.
	/// </summary>
	private sealed class Win32Window(nint handle) : System.Windows.Forms.IWin32Window
	{
		public nint Handle { get; } = handle;
	}
}
