using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TurbolandTheme.Wpf.Controls;

namespace TurboPilot.Dialogs;

/// <summary>
/// Dialog for adding file or folder attachments to the prompt.
/// Shows a ListBox of attached files/folders with add/remove support.
/// Shown as a floating dialog window owned by the main window, so it sorts
/// above the WebView2 content and against other windows by OS rule.
/// </summary>
internal sealed class AttachmentDialog : TurbolandFloatingDialog
{
	private ListBox? _list;
	private TextBox? _pathInput;
	private Button? _removeBtn;
	private readonly List<string> _addedPaths = new();

	/// <summary>The paths of all attached files/folders.</summary>
	public IReadOnlyList<string> AddedPaths => _addedPaths;

	public AttachmentDialog()
	{
		Title = "ATTACHMENTS";
	}

	public void Initialize()
	{
		Content = CreateContent();
	}

	private object CreateContent()
	{
		var rootGrid = new Grid();
		rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // List
		rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Path input
		rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Buttons

		// ListBox showing attached files/folders
		_list = new ListBox
		{
			Background = FindResource("Turboland.Brush.DesktopBackground") as Brush,
			Foreground = FindResource("Turboland.Brush.WindowForeground") as Brush,
			FontFamily = FindResource("Turboland.Font.Primary") as FontFamily,
			FontSize = 14,
			BorderBrush = FindResource("Turboland.Brush.ControlShadow") as Brush,
			HorizontalContentAlignment = HorizontalAlignment.Stretch,
		};

		_list.SelectionChanged += (_, _) => UpdateButtonStates();
		_list.MouseDoubleClick += (_, _) =>
		{
			if (_list.SelectedItem != null)
				AcceptSelected();
		};

		Grid.SetRow(_list, 0);
		rootGrid.Children.Add(_list);

		// Path input + Add button row
		var inputRow = new Grid();
		inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

		_pathInput = new TextBox
		{
			Background = FindResource("Turboland.Brush.DesktopBackground") as Brush,
			Foreground = FindResource("Turboland.Brush.WindowForeground") as Brush,
			FontFamily = FindResource("Turboland.Font.Primary") as FontFamily,
			FontSize = 14,
			BorderBrush = FindResource("Turboland.Brush.ControlShadow") as Brush,
			HorizontalContentAlignment = HorizontalAlignment.Left,
		};
		_pathInput.KeyDown += (_, e) =>
		{
			if (e.Key == Key.Enter)
				AddFromInput();
		};

		var addBtn = new Button
		{
			Content = "+ ADD",
			Width = 80,
			Height = 28,
			Margin = new Thickness(8, 0, 12, 0),
			Background = FindResource("Turboland.Brush.ButtonFace") as Brush,
			Foreground = FindResource("Turboland.Brush.ButtonForeground") as Brush,
			FontFamily = FindResource("Turboland.Font.Primary") as FontFamily,
			FontSize = 14,
			BorderBrush = FindResource("Turboland.Brush.ControlShadow") as Brush,
		};
		addBtn.Click += (_, _) => AddFromInput();

		Grid.SetColumn(_pathInput, 0);
		Grid.SetColumn(addBtn, 1);
		inputRow.Children.Add(_pathInput);
		inputRow.Children.Add(addBtn);

		Grid.SetRow(inputRow, 1);
		rootGrid.Children.Add(inputRow);

		// Button panel
		var buttonPanel = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Right,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(12),
			FlowDirection = FlowDirection.RightToLeft,
		};

		var cancelBtn = new Button
		{
			Content = "Cancel",
			Width = 80,
			Height = 28,
			Margin = new Thickness(0, 0, 8, 0),
			Background = FindResource("Turboland.Brush.ButtonFace") as Brush,
			Foreground = FindResource("Turboland.Brush.WindowForeground") as Brush,
			FontFamily = FindResource("Turboland.Font.Primary") as FontFamily,
			FontSize = 14,
			BorderBrush = FindResource("Turboland.Brush.ControlShadow") as Brush,
		};
		cancelBtn.Click += (_, _) => Close(false);

		var addAllBtn = new Button
		{
			Content = "Add All",
			Width = 100,
			Height = 28,
			Margin = new Thickness(0, 0, 8, 0),
			Background = FindResource("Turboland.Brush.ButtonFace") as Brush,
			Foreground = FindResource("Turboland.Brush.ButtonForeground") as Brush,
			FontFamily = FindResource("Turboland.Font.Primary") as FontFamily,
			FontSize = 14,
			BorderBrush = FindResource("Turboland.Brush.ControlShadow") as Brush,
		};
		addAllBtn.Click += (_, _) => AcceptAll();

		_removeBtn = new Button
		{
			Content = "Remove",
			Width = 90,
			Height = 28,
			Margin = new Thickness(0, 0, 8, 0),
			Background = FindResource("Turboland.Brush.ButtonFace") as Brush,
			Foreground = FindResource("Turboland.Brush.WindowForeground") as Brush,
			FontFamily = FindResource("Turboland.Font.Primary") as FontFamily,
			FontSize = 14,
			BorderBrush = FindResource("Turboland.Brush.ControlShadow") as Brush,
		};
		_removeBtn.Click += (_, _) => RemoveSelected();

		buttonPanel.Children.Add(cancelBtn);
		buttonPanel.Children.Add(_removeBtn);
		buttonPanel.Children.Add(addAllBtn);

		Grid.SetRow(buttonPanel, 2);
		rootGrid.Children.Add(buttonPanel);

		return rootGrid;
	}

	private void AddFromInput()
	{
		if (_pathInput == null) return;
		var path = _pathInput.Text?.Trim();
		if (string.IsNullOrEmpty(path)) return;

		if (!File.Exists(path) && !Directory.Exists(path))
		{
			MessageBox.Show(this, $"Path not found:\r\n{path}",
				"Attachment", MessageBoxButton.OK, MessageBoxImage.Warning);
			return;
		}

		if (_addedPaths.Contains(path)) return;

		_addedPaths.Add(path);
		RefreshList();
		_pathInput.Text = string.Empty;
		UpdateButtonStates();
	}

	private void RemoveSelected()
	{
		if (_list == null) return;
		if (_list.SelectedItem is string path)
		{
			_addedPaths.Remove(path);
			RefreshList();
			UpdateButtonStates();
		}
	}

	private void AcceptSelected()
	{
		if (_list == null) return;
		if (_list.SelectedItem is string selectedPath)
		{
			// Remove from dialog list but keep in the parent's attachment list
			_addedPaths.Remove(selectedPath);
			RefreshList();
			UpdateButtonStates();
			Close(true);
		}
	}

	private void AcceptAll()
	{
		Close(true);
	}

	private void RefreshList()
	{
		if (_list == null) return;
		_list.Items.Clear();
		foreach (var path in _addedPaths)
		{
			var type = File.Exists(path) ? "File" : "Folder";
			_list.Items.Add($"{type}: {path}");
		}
	}

	private void UpdateButtonStates()
	{
		if (_removeBtn == null || _list == null) return;
		bool hasSelection = _list.SelectedItems.Count > 0;
		_removeBtn.IsEnabled = hasSelection;
	}
}
