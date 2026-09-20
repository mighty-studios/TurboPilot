using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using TurboPilot.Customizations;
using TurbolandTheme.Wpf.Controls;

namespace TurboPilot.Dialogs;

/// <summary>
/// Dialog for editing the ordered list of folders the application searches
/// for customization items (agents, skills and instructions). Order matters:
/// later entries override earlier ones. The edited list is persisted to
/// settings when the user clicks OK. Scanning the folders for items is not
/// yet implemented; the found-items and details panes are placeholders.
/// Shown as a floating dialog window owned by the main window, so it sorts
/// above the WebView2 content and against other windows by OS rule.
/// </summary>
public partial class CustomizeDialog : TurbolandFloatingDialog
{
	private readonly List<string> _folders;

	/// <summary>
	/// The edited list of search folders. Only meaningful after the dialog
	/// returns true.
	/// </summary>
	public IReadOnlyList<string> Folders => _folders;

	public CustomizeDialog()
	{
		InitializeComponent();

		_folders = Settings.Load().CustomizationFolders
			.Where(f => !string.IsNullOrWhiteSpace(f))
			.Select(f => f.Trim())
			.ToList();

		ReloadList();
		UpdateButtonStates();
		RefreshFoundItems();
	}

	protected override void OnInitialized(EventArgs e)
	{
		base.OnInitialized(e);

		// The floating dialog sizes to its content, which overrides any
		// Width/Height set in the designer. Capture the designer's size
		// as the minimum so the dialog never opens smaller than designed,
		// while still growing if the content ever needs more room.
		if (!double.IsNaN(Width))
			MinWidth = Width;
		if (!double.IsNaN(Height))
			MinHeight = Height;
		Width = double.NaN;
		Height = double.NaN;
	}

	// ------------------------------------------------------------------ list

	private void ReloadList()
	{
		int prevIndex = listBox.SelectedIndex;
		listBox.Items.Clear();
		foreach (var folder in _folders)
			listBox.Items.Add(folder);

		if (listBox.Items.Count > 0)
			listBox.SelectedIndex = Math.Clamp(prevIndex, 0, listBox.Items.Count - 1);

		UpdateButtonStates();
	}

	private void UpdateButtonStates()
	{
		int i = listBox.SelectedIndex;
		buttonDelete.IsEnabled = i >= 0;
		buttonMoveUp.IsEnabled = i > 0;
		buttonMoveDown.IsEnabled = i >= 0 && i < _folders.Count - 1;
	}

	private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
		=> UpdateButtonStates();

	// ------------------------------------------------------------------ editing

	private void OnAdd(object sender, RoutedEventArgs e)
	{
		var folderDialog = new System.Windows.Forms.FolderBrowserDialog
		{
			Description = "Select a folder to search for customization items",
			UseDescriptionForTitle = true,
		};

		// Own the native folder picker with this dialog's HWND so it stays
		// above the floating dialog instead of floating loose on the desktop.
		if (folderDialog.ShowDialog(new Win32Window(new WindowInteropHelper(this).Handle))
			!= System.Windows.Forms.DialogResult.OK)
			return;

		AddFolder(folderDialog.SelectedPath);
	}

	private void AddFolder(string path)
	{
		var trimmed = path.Trim();
		if (string.IsNullOrEmpty(trimmed))
			return;

		if (IsDuplicate(trimmed))
		{
			MessageBox.Show(this, "That folder is already in the list.",
				"Customization", MessageBoxButton.OK, MessageBoxImage.Information);
			return;
		}

		_folders.Add(trimmed);
		ReloadList();
		listBox.SelectedIndex = _folders.Count - 1;
	}

	private void OnDelete(object sender, RoutedEventArgs e)
	{
		int i = listBox.SelectedIndex;
		if (i < 0)
			return;

		var path = _folders[i];
		var answer = MessageBox.Show(this, $"Delete {path}, are you sure?",
			"Customization", MessageBoxButton.YesNo, MessageBoxImage.Question);
		if (answer != MessageBoxResult.Yes)
			return;

		_folders.RemoveAt(i);
		ReloadList();
		if (_folders.Count > 0)
			listBox.SelectedIndex = Math.Min(i, _folders.Count - 1);
	}

	private void OnMoveUp(object sender, RoutedEventArgs e) => MoveSelected(-1);

	private void OnMoveDown(object sender, RoutedEventArgs e) => MoveSelected(+1);

	private void MoveSelected(int delta)
	{
		int i = listBox.SelectedIndex;
		int j = i + delta;
		if (i < 0 || j < 0 || j >= _folders.Count)
			return;

		(_folders[i], _folders[j]) = (_folders[j], _folders[i]);
		ReloadList();
		listBox.SelectedIndex = j;
	}

	private bool IsDuplicate(string candidate) =>
		_folders.Any(f => string.Equals(f, candidate, StringComparison.OrdinalIgnoreCase));

	// ------------------------------------------------------------------ found items

	/// <summary>
	/// Fills the per-type tabs from the collected library. With the
	/// Only Show Enabled filter on, disabled items are left out of the
	/// lists; the tab headers always report the full counts.
	/// </summary>
	private void RefreshFoundItems()
	{
		var library = CustomizationService.Current;
		bool onlyEnabled = radioOnlyEnabled.IsChecked == true;
		PopulateTab(listPrompts, tabPrompts, "Prompts", library.Prompts, onlyEnabled);
		PopulateTab(listSkills, tabSkills, "Skills", library.Skills, onlyEnabled);
		PopulateTab(listInstructions, tabInstructions, "Instructions", library.Instructions, onlyEnabled);
		textBoxDetails.Text = string.Empty;
	}

	private static void PopulateTab(ListBox list, TabItem tab, string label,
		IReadOnlyDictionary<string, CustomizationItem> items, bool onlyEnabled)
	{
		list.ItemsSource = items.Values
			.Where(i => !onlyEnabled || i.Enabled)
			.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
			.ToList();
		tab.Header = $"{label} ({items.Count})";
	}

	/// <summary>
	/// A checkbox in one of the found-items lists was toggled. The TwoWay
	/// binding already wrote the flag back to the item; persist so the
	/// choice survives the session. The lists are deliberately not rebuilt
	/// here: the row the user just clicked must not vanish under the filter.
	/// </summary>
	private void OnItemToggled(object sender, RoutedEventArgs e)
	{
		if (e.OriginalSource is not CheckBox checkBox
			|| checkBox.DataContext is not CustomizationItem item)
			return;

		// Write the flag explicitly rather than trusting the binding's
		// update order relative to this event.
		item.Enabled = checkBox.IsChecked == true;
		CustomizationService.Save(CustomizationService.Current);

		// Keep the details pane honest if the toggled item is selected.
		if (listPrompts.SelectedItem == item || listSkills.SelectedItem == item
			|| listInstructions.SelectedItem == item)
		{
			ShowDetails(item);
		}
	}

	private void OnOnlyEnabledChanged(object sender, RoutedEventArgs e) => RefreshFoundItems();

	/// <summary>
	/// A row in one of the found-items lists was selected: show its
	/// details. Selection never changes the enabled flag; only the
	/// checkmark does, through OnItemToggled.
	/// </summary>
	private void OnFoundItemSelected(object sender, SelectionChangedEventArgs e)
	{
		if (sender is not ListBox list)
			return;

		if (list.SelectedItem is not CustomizationItem item)
		{
			textBoxDetails.Text = string.Empty;
			return;
		}

		ShowDetails(item);
	}

	private void ShowDetails(CustomizationItem item)
	{
		var lines = new List<string>
		{
			$"Type: {TypeNameFor(item)}",
			$"Name: {item.Name}",
			$"Path: {item.FilePath}",
			$"State: {(item.Enabled ? "Enabled" : "Disabled")}",
		};

		AppendFrontMatter(lines, item.FilePath);

		textBoxDetails.Text = string.Join(Environment.NewLine, lines);
	}

	/// <summary>
	/// Adds the item's description and remaining front matter fields to
	/// the details lines. Sections are omitted when the file has no
	/// front matter or cannot be read.
	/// </summary>
	private static void AppendFrontMatter(List<string> lines, string filePath)
	{
		var frontMatter = FrontMatter.Read(filePath);
		if (frontMatter.Fields.Count == 0)
			return;

		string? description = frontMatter.Get("description");
		if (!string.IsNullOrWhiteSpace(description))
		{
			lines.Add(string.Empty);
			lines.Add($"Description: {description}");
		}

		var others = frontMatter.Fields
			.Where(f => !string.Equals(f.Key, "description", StringComparison.OrdinalIgnoreCase))
			.ToList();
		if (others.Count > 0)
		{
			lines.Add(string.Empty);
			lines.Add("Metadata:");
			foreach (var (key, value) in others)
				lines.Add($"  {key}: {value}");
		}
	}

	private string TypeNameFor(CustomizationItem item)
	{
		var library = CustomizationService.Current;
		if (library.Prompts.ContainsKey(item.FilePath)) return "Prompt";
		if (library.Skills.ContainsKey(item.FilePath)) return "Skill";
		if (library.Instructions.ContainsKey(item.FilePath)) return "Instruction";
		return "Item";
	}

	// ------------------------------------------------------------------ results

	private void OnOk(object sender, RoutedEventArgs e)
	{
		var settings = Settings.Load();
		settings.CustomizationFolders = new List<string>(_folders);
		settings.Save();

		// The search roots changed: rebuild the customization lists so the
		// next session (and the Items Found tree) sees the new folders.
		CustomizationService.Rescan(settings.LastWorkspacePath);

		Close(true);
	}

	private void OnCancel(object sender, RoutedEventArgs e) => Close(false);

	/// <summary>
	/// Adapts a raw HWND to <see cref="System.Windows.Forms.IWin32Window"/> so the
	/// WinForms folder picker can be owned by this WPF window.
	/// </summary>
	private sealed class Win32Window(nint handle) : System.Windows.Forms.IWin32Window
	{
		public nint Handle { get; } = handle;
	}
}
