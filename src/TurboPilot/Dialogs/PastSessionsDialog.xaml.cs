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
			|| session.Description.Contains(search, StringComparison.OrdinalIgnoreCase)
			|| (session.Options.WorkspaceFolder?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
			.ToList();
		listSessions.SelectedIndex = listSessions.Items.Count > 0 ? 0 : -1;
		UpdateSelection();
	}

	private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelection();

	private void UpdateSelection()
	{
		var selected = listSessions.SelectedItem as SessionRecord;
		buttonView.IsEnabled = selected is not null;
		buttonResume.IsEnabled = selected is not null;
		textDetails.Text = selected is null ? "No matching sessions."
			: $"ID: {selected.SessionId}\r\nWorkspace: {selected.Options.WorkspaceFolder}\r\n"
				+ $"Mode: {selected.Options.Mode} | Service: {(selected.Options.UseByok ? selected.Options.ByokEndpoint : "Cloud")}";
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
