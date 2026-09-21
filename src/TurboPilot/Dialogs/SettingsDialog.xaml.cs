using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using TurbolandTheme.Wpf.Controls;
using TurboPilot.Ai;

namespace TurboPilot.Dialogs;

/// <summary>
/// Session settings dialog: replaces the former New Session folder picker
/// with the full setup surface. The user picks the workspace folder and the
/// model hosting service (Copilot CLI or a BYOK OpenAI-compatible server),
/// queries the service for its models, and chooses the model, reasoning
/// effort and session mode. Permissions and Customization dialogs open from
/// here, and Begin Session drives the session lifecycle.
///
/// The dialog only queries services; actually starting an AI session is the
/// caller's job once Begin Session returns true. Cancel (or the close
/// box) persists nothing and changes no session state: like every other
/// dialog here, Cancel means no net change.
/// </summary>
public partial class SettingsDialog : TurbolandFloatingDialog
{
	/// <summary>True once the user clicked Begin Session with a valid setup.</summary>
	public bool BeginRequested { get; private set; }

	/// <summary>Selected workspace folder. Valid after Begin Session.</summary>
	public string? WorkspacePath { get; private set; }

	/// <summary>"CopilotCli" or "Byok". Valid after Begin Session.</summary>
	public string Provider { get; private set; } = "CopilotCli";

	/// <summary>Composed BYOK base URL (e.g. http://10.0.0.234:13305/api/v1).</summary>
	public string ByokEndpoint { get; private set; } = "";

	/// <summary>BYOK API key.</summary>
	public string ByokApiKey { get; private set; } = "";

	/// <summary>Selected model id.</summary>
	public string SelectedModel { get; private set; } = "";

	/// <summary>Selected reasoning effort (empty when not applicable).</summary>
	public string SelectedEffort { get; private set; } = "";

	/// <summary>Selected mode name.</summary>
	public string SelectedMode { get; private set; } = "Standard";

	/// <summary>
	/// Context window of the selected model in tokens, as advertised by the
	/// service at query time. 0 when the selection carries none.
	/// </summary>
	public int SelectedContextWindowTokens { get; private set; }

	public bool ApplyInstructions { get; private set; } = true;

	public bool PreloadSkills { get; private set; } = true;

	// The models returned by the last successful query, in display order.
	private IReadOnlyList<AvailableModel> _models = Array.Empty<AvailableModel>();

	// Cancels an in-flight query when the service changes or the dialog closes.
	private CancellationTokenSource? _queryCts;

	// Suppresses the SelectionChanged handlers while lists are rebuilt, so a
	// repopulation cannot fire a follow-up query or clobber the status line.
	private bool _suppressEvents;

	// The workspace whose permissions are in force, or null for app defaults.
	private readonly string? _activeWorkspacePath;

	// Ink used for query error messages.
	private static readonly Brush ErrorBrush =
		new SolidColorBrush(Color.FromRgb(220, 120, 120));

	private static readonly Brush DefaultStatusBrush =
		new SolidColorBrush(Color.FromRgb(148, 148, 148));

	public SettingsDialog(string? activeWorkspacePath, bool sessionActive)
	{
		_activeWorkspacePath = activeWorkspacePath;
		InitializeComponent();

		var settings = Settings.Load();

		textBoxWorkspacePath.Text = activeWorkspacePath
			?? settings.LastWorkspacePath
			?? "";

		radioByok.IsChecked = settings.ModelProvider == "Byok";
		radioCopilot.IsChecked = !radioByok.IsChecked;

		SplitEndpoint(settings.ByokEndpoint, out var host, out var port, out var path);
		textBoxHost.Text = host;
		textBoxPort.Text = port;
		textBoxPath.Text = path;
		textBoxApiKey.Text = settings.ByokApiKey;

		checkApplyInstructions.IsChecked = settings.ApplyInstructions;
		checkPreloadSkills.IsChecked = settings.PreloadSkills;

		PopulateModes(settings.SelectedMode);
		UpdateServiceFields();
		UpdateEndpointPreview();

		textBoxWorkspacePath.TextChanged += (_, _) => UpdateBeginEnabled();

		// Contact the configured service as soon as the dialog is up so the
		// model list is populated without an extra click.
		Loaded += (_, _) => _ = QueryServiceAsync();
	}

	// -- Service selection ----------------------------------------------------

	private bool IsByok => radioByok.IsChecked == true;

	private void OnServiceChanged(object sender, RoutedEventArgs e)
	{
		UpdateServiceFields();
		UpdateEndpointPreview();
		if (IsLoaded) _ = QueryServiceAsync();
	}

	private void OnByokFieldChanged(object sender, TextChangedEventArgs e)
	{
		UpdateEndpointPreview();
	}

	private void UpdateServiceFields()
	{
		var byok = IsByok;
		foreach (var child in FindByokInputControls())
			child.IsEnabled = byok;
		textEndpointPreview.Visibility = byok ? Visibility.Visible : Visibility.Collapsed;
	}

	private IEnumerable<UIElement> FindByokInputControls()
	{
		yield return textBoxHost;
		yield return textBoxPort;
		yield return textBoxPath;
		yield return textBoxApiKey;
	}

	private void UpdateEndpointPreview()
	{
		if (!IsByok)
		{
			textEndpointPreview.Text = "";
			return;
		}
		if (TryBuildEndpoint(out var endpoint, out _))
		{
			textEndpointPreview.Text = "Endpoint: " + endpoint;
			textEndpointPreview.Foreground = DefaultStatusBrush;
		}
		else
		{
			textEndpointPreview.Text = "Enter a host and port to query the server.";
			textEndpointPreview.Foreground = ErrorBrush;
		}
	}

	// -- Querying -------------------------------------------------------------

	private async Task QueryServiceAsync()
	{
		// Only one query at a time: a service switch or a second click
		// abandons whatever is in flight.
		_queryCts?.Cancel();
		var cts = new CancellationTokenSource();
		_queryCts = cts;

		_models = Array.Empty<AvailableModel>();
		SetComboItems(comboModel, Array.Empty<string>());
		SetComboItems(comboEffort, Array.Empty<string>());
		textContextWindow.Text = "-";

		string? endpoint = null;
		if (IsByok)
		{
			if (!TryBuildEndpoint(out endpoint, out _))
			{
				SetStatus("Enter a host and port to query the server.", isError: true);
				return;
			}
		}

		buttonQuery.IsEnabled = false;
		SetStatus("Contacting service...", isError: false);

		try
		{
			var models = IsByok
				? await ModelService.QueryOpenAiCompatibleAsync(endpoint!, textBoxApiKey.Text, cts.Token)
				: await ModelService.QueryCopilotCliAsync(ValidatedWorkspacePath(), cts.Token);

			if (cts.IsCancellationRequested) return;

			_models = models;
			SetComboItems(comboModel, models.Select(m => m.DisplayLabel).ToList());

			if (models.Count == 0)
			{
				SetStatus("The service reported no models.", isError: true);
				UpdateBeginEnabled();
				return;
			}

			// Restore the persisted selection when it still exists.
			var saved = Settings.Load().SelectedModel;
			var index = Array.FindIndex(models.ToArray(),
				m => string.Equals(m.Id, saved, StringComparison.OrdinalIgnoreCase));
			comboModel.SelectedIndex = index >= 0 ? index : 0;

			SetStatus($"{models.Count} model(s) loaded.", isError: false);
		}
		catch (OperationCanceledException)
		{
			// Superseded by a newer query or the dialog closed.
		}
		catch (Exception ex)
		{
			SetStatus("Service unreachable: " + ex.Message, isError: true);
		}
		finally
		{
			buttonQuery.IsEnabled = true;
			UpdateBeginEnabled();
		}
	}

	private void OnQueryService(object sender, RoutedEventArgs e) => _ = QueryServiceAsync();

	private void OnModelChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_suppressEvents) return;

		var index = comboModel.SelectedIndex;
		if (index < 0 || index >= _models.Count)
		{
			textContextWindow.Text = "-";
			SetComboItems(comboEffort, Array.Empty<string>());
			labelEffort.IsEnabled = false;
			comboEffort.IsEnabled = false;
			return;
		}

		var model = _models[index];
		textContextWindow.Text = model.ContextWindowTokens > 0
			? AvailableModel.FormatTokensShort(model.ContextWindowTokens) + " tokens"
			: "not reported";

		if (model.SupportsReasoningEffort && model.ReasoningEfforts.Count > 0)
		{
			SetComboItems(comboEffort, model.ReasoningEfforts);
			var saved = Settings.Load().SelectedEffort;
			var effortIndex = model.ReasoningEfforts.ToList()
				.FindIndex(e => string.Equals(e, saved, StringComparison.OrdinalIgnoreCase));
			comboEffort.SelectedIndex = effortIndex >= 0
				? effortIndex
				: Math.Max(0, model.ReasoningEfforts.ToList()
					.FindIndex(e => string.Equals(e, model.DefaultReasoningEffort, StringComparison.OrdinalIgnoreCase)));
			labelEffort.IsEnabled = true;
			comboEffort.IsEnabled = true;
		}
		else
		{
			SetComboItems(comboEffort, Array.Empty<string>());
			labelEffort.IsEnabled = false;
			comboEffort.IsEnabled = false;
		}
	}

	// -- Modes ----------------------------------------------------------------

	/// <summary>
	/// Fills the mode combo with the built-in modes followed by the enabled
	/// custom agents from the customization lists. Selects
	/// <paramref name="preferred"/> when present, otherwise Standard.
	/// </summary>
	private void PopulateModes(string preferred)
	{
		var modes = new List<string> { "Standard", "Plan", "Autopilot" };
		modes.AddRange(Customizations.CustomizationService.Current.Agents.Values
			.Where(a => a.Enabled)
			.Select(a => a.Name)
			.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));

		SetComboItems(comboMode, modes);
		comboMode.SelectedItem = modes.Contains(preferred, StringComparer.OrdinalIgnoreCase)
			? preferred
			: "Standard";
	}

	// -- Workspace ------------------------------------------------------------

	private void OnBrowse(object sender, RoutedEventArgs e)
	{
		var folderDialog = new System.Windows.Forms.FolderBrowserDialog
		{
			Description = "Select the workspace folder for this session",
			UseDescriptionForTitle = true,
		};

		var current = textBoxWorkspacePath.Text?.Trim();
		if (!string.IsNullOrEmpty(current) && Directory.Exists(current))
			folderDialog.InitialDirectory = current;

		// Own the native folder picker with this dialog's HWND so it stays
		// above the floating dialog instead of floating loose on the desktop.
		if (folderDialog.ShowDialog(new Win32Window(new WindowInteropHelper(this).Handle))
			== System.Windows.Forms.DialogResult.OK)
		{
			textBoxWorkspacePath.Text = folderDialog.SelectedPath;
		}
	}

	/// <summary>The typed workspace path when it exists on disk, else null.</summary>
	private string? ValidatedWorkspacePath()
	{
		var path = textBoxWorkspacePath.Text?.Trim();
		return !string.IsNullOrEmpty(path) && Directory.Exists(path) ? path : null;
	}

	private void UpdateBeginEnabled()
	{
		buttonBeginSession.IsEnabled = ValidatedWorkspacePath() != null;
	}

	// -- Sibling dialogs ------------------------------------------------------

	private void OnOpenPermissions(object sender, RoutedEventArgs e)
	{
		new PermissionsDialog(_activeWorkspacePath).ShowDialog(this);
	}

	private void OnOpenCustomization(object sender, RoutedEventArgs e)
	{
		// Keep the current combo pick: SelectedMode only carries a value
		// once Begin Session has run, so using it here would reset the
		// user's in-dialog choice back to Standard.
		var current = comboMode.SelectedItem as string ?? "Standard";
		new CustomizeDialog().ShowDialog(this);
		// The customization lists may have changed; refresh the agent modes.
		PopulateModes(current);
	}

	// -- Close paths ----------------------------------------------------------

	private void OnBeginSession(object sender, RoutedEventArgs e)
	{
		var workspace = ValidatedWorkspacePath();
		if (workspace == null)
		{
			SetStatus("Choose a workspace folder that exists.", isError: true);
			return;
		}

		WorkspacePath = workspace;
		Provider = IsByok ? "Byok" : "CopilotCli";
		if (IsByok && TryBuildEndpoint(out var endpoint, out _))
			ByokEndpoint = endpoint;
		ByokApiKey = textBoxApiKey.Text.Trim();

		var modelIndex = comboModel.SelectedIndex;
		SelectedModel = modelIndex >= 0 && modelIndex < _models.Count
			? _models[modelIndex].Id
			: "";
		SelectedContextWindowTokens = modelIndex >= 0 && modelIndex < _models.Count
			? _models[modelIndex].ContextWindowTokens
			: 0;
		SelectedEffort = comboEffort.SelectedItem as string ?? "";
		SelectedMode = comboMode.SelectedItem as string ?? "Standard";
		ApplyInstructions = checkApplyInstructions.IsChecked == true;
		PreloadSkills = checkPreloadSkills.IsChecked == true;

		Persist();

		BeginRequested = true;
		Close(true);
	}

	private void OnCancel(object sender, RoutedEventArgs e)
	{
		Close(false);
	}

	protected override void OnClosed(EventArgs e)
	{
		_queryCts?.Cancel();
		_queryCts?.Dispose();
		base.OnClosed(e);
	}

	private void Persist()
	{
		var settings = Settings.Load();
		settings.LastWorkspacePath = WorkspacePath;
		settings.ModelProvider = Provider;
		settings.ByokEndpoint = ByokEndpoint;
		settings.ByokApiKey = ByokApiKey;
		settings.SelectedModel = SelectedModel;
		settings.SelectedEffort = SelectedEffort;
		settings.SelectedMode = SelectedMode;
		settings.ApplyInstructions = ApplyInstructions;
		settings.PreloadSkills = PreloadSkills;
		settings.Save();
	}

	// -- Helpers --------------------------------------------------------------

	private void SetStatus(string message, bool isError)
	{
		textQueryStatus.Text = message;
		textQueryStatus.Foreground = isError ? ErrorBrush : DefaultStatusBrush;
	}

	/// <summary>
	/// Replaces a combo's items without firing its SelectionChanged handler,
	/// so a repopulation cannot trigger a follow-up query.
	/// </summary>
	private void SetComboItems(ComboBox combo, IEnumerable<string> items)
	{
		_suppressEvents = true;
		combo.ItemsSource = items.ToList();
		combo.SelectedIndex = -1;
		_suppressEvents = false;
	}

	/// <summary>
	/// Composes host, port, and path into an absolute base URL. Forgiving of
	/// a pasted scheme in the host field, an embedded host:port, or an IPv6
	/// literal in brackets. Defaults to http when no scheme is supplied.
	/// </summary>
	private bool TryBuildEndpoint(out string endpoint, out string error)
	{
		endpoint = "";
		error = "";

		var host = textBoxHost.Text.Trim();
		if (host.Length == 0)
		{
			error = "Enter a host name or IP address.";
			return false;
		}

		var scheme = "http";
		if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
		{
			scheme = "http";
			host = host.Substring(7);
		}
		else if (host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
		{
			scheme = "https";
			host = host.Substring(8);
		}

		var slash = host.IndexOf('/');
		if (slash >= 0)
			host = host.Substring(0, slash);

		var portText = textBoxPort.Text.Trim();

		if (host.StartsWith("[", StringComparison.Ordinal))
		{
			var end = host.IndexOf(']');
			if (end < 0)
			{
				error = "Invalid IPv6 address (missing ']').";
				return false;
			}
			var after = host.Substring(end + 1);
			if (after.StartsWith(":", StringComparison.Ordinal))
				portText = after.Substring(1);
			host = host.Substring(0, end + 1);
		}
		else
		{
			var colon = host.IndexOf(':');
			if (colon >= 0)
			{
				portText = host.Substring(colon + 1);
				host = host.Substring(0, colon);
			}
		}

		if (host.Length == 0)
		{
			error = "Enter a host name or IP address.";
			return false;
		}

		if (portText.Length == 0)
		{
			error = "Enter the server port.";
			return false;
		}

		if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
			|| port < 1 || port > 65535)
		{
			error = "Port must be a number between 1 and 65535.";
			return false;
		}

		var path = textBoxPath.Text.Trim();
		if (path.Length == 0) path = "/v1";
		if (!path.StartsWith("/", StringComparison.Ordinal)) path = "/" + path;
		path = path.TrimEnd('/');

		var candidate = scheme + "://" + host + ":" + port.ToString(CultureInfo.InvariantCulture) + path;
		if (!Uri.TryCreate(candidate, UriKind.Absolute, out _))
		{
			error = "Could not form a valid URL from the host, port, and path.";
			return false;
		}

		endpoint = candidate;
		return true;
	}

	/// <summary>
	/// Splits an existing base URL into host, port, and API path for
	/// pre-populating the fields. Leaves fields blank / defaulted when the
	/// value is missing or unparseable.
	/// </summary>
	private static void SplitEndpoint(string endpoint, out string host, out string port, out string path)
	{
		host = "";
		port = "";
		path = "/v1";
		if (string.IsNullOrWhiteSpace(endpoint))
			return;

		if (Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri))
		{
			host = uri.Host;
			port = uri.Port > 0 ? uri.Port.ToString(CultureInfo.InvariantCulture) : "";
			path = string.IsNullOrEmpty(uri.AbsolutePath) || uri.AbsolutePath == "/"
				? "/v1"
				: uri.AbsolutePath.TrimEnd('/');
		}
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
