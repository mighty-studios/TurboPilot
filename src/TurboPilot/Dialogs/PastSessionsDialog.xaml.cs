using System.Windows;
using System.Windows.Controls;
using TurbolandTheme.Wpf.Controls;
using TurboPilot.Sessions;

namespace TurboPilot.Dialogs;

public partial class PastSessionsDialog : TurbolandFloatingDialog
{
	private readonly IReadOnlyList<SessionRecord> _sessions;

	public SessionRecord? SelectedSession { get; private set; }
	public bool ResumeRequested { get; private set; }

	public PastSessionsDialog(IReadOnlyList<SessionRecord> sessions)
	{
		_sessions = sessions;
		InitializeComponent();
		RefreshList();
	}

	private void OnSearchChanged(object sender, TextChangedEventArgs e)
	{
		if (listSessions is not null)
			RefreshList();
	}

	private void RefreshList()
	{
		var search = textSearch.Text.Trim();
		listSessions.ItemsSource = _sessions.Where(session =>
			session.SessionId.Contains(search, StringComparison.OrdinalIgnoreCase)
			|| session.Summary.Contains(search, StringComparison.OrdinalIgnoreCase)
			|| session.Description.Contains(search, StringComparison.OrdinalIgnoreCase)
			|| (session.Options.WorkspaceFolder?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
			.ToList();
		listSessions.SelectedIndex = listSessions.Items.Count > 0 ? 0 : -1;
		UpdateSelection();
	}

	private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelection();

	/// <summary>
	/// The details pane carries what the row left out. The model and the
	/// settings live here rather than in the row because they are the
	/// same across most sessions, so they tell one row from another
	/// least; they still matter once a row has been picked.
	/// </summary>
	private void UpdateSelection()
	{
		var selected = listSessions.SelectedItem as SessionRecord;
		buttonView.IsEnabled = selected is not null;
		buttonResume.IsEnabled = selected is not null;
		if (selected is null)
		{
			textDetails.Text = "No matching sessions.";
			return;
		}

		var lines = new List<string>
		{
			$"ID: {selected.SessionId}",
			$"Workspace: {selected.Options.WorkspaceFolder}",
			$"Model: {(string.IsNullOrWhiteSpace(selected.Options.Model) ? "default" : selected.Options.Model)}",
			$"Mode: {selected.Options.Mode} | Service: {(selected.Options.UseByok ? selected.Options.ByokEndpoint : "Cloud")}",
		};
		// The row already shows the summary clipped. The opening prompt is
		// only worth the space when there is no summary to have shown.
		if (string.IsNullOrWhiteSpace(selected.Summary) && !string.IsNullOrWhiteSpace(selected.Description))
			lines.Add($"Opening prompt: {selected.Description}");
		else if (!string.IsNullOrWhiteSpace(selected.Summary))
			lines.Add($"Summary: {selected.Summary}");

		textDetails.Text = string.Join("\r\n", lines);
	}

	private void OnView(object sender, RoutedEventArgs e) => Choose(resume: false);
	private void OnResume(object sender, RoutedEventArgs e) => Choose(resume: true);
	private void OnCancel(object sender, RoutedEventArgs e) => Close(false);

	private void Choose(bool resume)
	{
		if (listSessions.SelectedItem is not SessionRecord selected)
			return;
		SelectedSession = selected;
		ResumeRequested = resume;
		Close(true);
	}
}
