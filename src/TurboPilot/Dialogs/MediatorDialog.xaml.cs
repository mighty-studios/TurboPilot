using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using TurbolandTheme.Wpf.Controls;
using TurboPilot.Mediation;

namespace TurboPilot.Dialogs;

public partial class MediatorDialog : TurbolandFloatingDialog
{
	private readonly MediatorConfiguration _configuration;
	private readonly ILocalModelRuntime _runtime;
	private readonly MediatorSettings _original;
	private CancellationTokenSource? _operation;
	private bool _closed;
	public MediatorSettings? Result { get; private set; }

	public MediatorDialog(MediatorConfiguration configuration, ILocalModelRuntime runtime)
	{
		_configuration = configuration;
		_runtime = runtime;
		_original = configuration.Load();
		InitializeComponent();
		checkEnabled.IsChecked = _original.Enabled;
		checkDebug.IsChecked = _original.DebugRaw;
		UpdateControls();
		Loaded += async (_, _) => await QueryAsync();
	}

	private async Task QueryAsync() => await RunAsync("Querying compatible local models...", async token =>
	{
		var alias = (comboModel.SelectedItem as LocalModelDescriptor)?.Alias ?? _original.ModelAlias;
		var models = await _runtime.ListModelsAsync(token);
		if (_closed) return;
		comboModel.ItemsSource = models;
		comboModel.SelectedItem = models.FirstOrDefault(model => model.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase));
		textStatus.Text = models.Count == 0 ? "No compatible text models are available."
			: comboModel.SelectedItem is null ? $"'{alias}' is unavailable. Choose a listed model or prepare acceleration."
			: $"{models.Count} compatible model(s).";
	});

	private async Task RunAsync(string status, Func<CancellationToken, Task> action)
	{
		if (_operation is not null) return;
		using var operation = new CancellationTokenSource();
		_operation = operation;
		textStatus.Text = status;
		UpdateControls();
		try { await action(operation.Token); }
		catch (OperationCanceledException) when (operation.IsCancellationRequested)
		{
			if (!_closed) textStatus.Text = "Operation canceled.";
		}
		catch (Exception ex)
		{
			if (!_closed) textStatus.Text = "[error] " + ex.Message;
		}
		finally
		{
			_operation = null;
			if (!_closed) UpdateControls();
		}
	}

	private IProgress<LocalRuntimeProgress> Progress() => new Progress<LocalRuntimeProgress>(progress =>
	{
		if (!_closed && _operation is not null)
			textStatus.Text = progress.Message + (progress.Percent is { } value ? $" {value:0}%" : "");
	});

	private async void OnQuery(object sender, RoutedEventArgs e) => await QueryAsync();

	private async void OnAcceleration(object sender, RoutedEventArgs e)
	{
		if (!YesNoDialog.Ask(this, "Download and register hardware acceleration support for this device?", "Mediator"))
			return;
		await RunAsync("Preparing hardware acceleration...", token => _runtime.PrepareAccelerationAsync(Progress(), token));
		if (!_closed) await QueryAsync();
	}

	private async void OnDownload(object sender, RoutedEventArgs e)
	{
		if (comboModel.SelectedItem is not LocalModelDescriptor model) return;
		var size = model.SizeMb is { } mb ? $" ({mb} MB)" : "";
		if (!YesNoDialog.Ask(this, $"Download '{model.Alias}'{size}? License: {model.License ?? "see model catalog"}.", "Mediator"))
			return;
		await RunAsync("Downloading model...", token => _runtime.DownloadAsync(model.Alias, Progress(), token));
		if (!_closed) await QueryAsync();
	}

	private void OnModelChanged(object sender, SelectionChangedEventArgs e)
	{
		if (textModelDetails is null) return;
		textModelDetails.Text = comboModel.SelectedItem is LocalModelDescriptor model
			? $"{model.Device} | Context: {(model.ContextTokens is { } context ? context.ToString("N0") : "not reported")} tokens"
				+ $" | License: {model.License ?? "see catalog"}\r\n"
				+ (model.Cached ? "Downloaded and ready to load." : "Download this model before enabling the Mediator.")
			: "Select a compatible model.";
		UpdateControls();
	}

	private void OnOptionChanged(object sender, RoutedEventArgs e)
	{
		if (buttonOk is not null) UpdateControls();
	}

	private void UpdateControls()
	{
		var busy = _operation is not null;
		var model = comboModel.SelectedItem as LocalModelDescriptor;
		comboModel.IsEnabled = !busy;
		buttonQuery.IsEnabled = !busy;
		buttonAcceleration.IsEnabled = !busy;
		buttonDownload.IsEnabled = !busy && model is { Cached: false };
		buttonStop.IsEnabled = busy;
		buttonOk.IsEnabled = !busy && (checkEnabled.IsChecked != true || model is { Cached: true });
	}

	private void OnStop(object sender, RoutedEventArgs e) => _operation?.Cancel();

	private void OnOpenFolder(object sender, RoutedEventArgs e)
	{
		try
		{
			Process.Start(new ProcessStartInfo { FileName = _configuration.DirectoryPath, UseShellExecute = true });
		}
		catch (Exception ex) { textStatus.Text = "[error] Cannot open configuration folder: " + ex.Message; }
	}

	private void OnOk(object sender, RoutedEventArgs e)
	{
		try
		{
			var result = _original with
			{
				Enabled = checkEnabled.IsChecked == true,
				ModelAlias = (comboModel.SelectedItem as LocalModelDescriptor)?.Alias ?? _original.ModelAlias,
				DebugRaw = checkDebug.IsChecked == true,
			};
			_configuration.Save(result);
			Result = result;
			Close(true);
		}
		catch (Exception ex) { textStatus.Text = "[error] Cannot save Mediator settings: " + ex.Message; }
	}

	private void OnCancel(object sender, RoutedEventArgs e) => Close(false);

	protected override void OnClosed(EventArgs e)
	{
		_closed = true;
		_operation?.Cancel();
		base.OnClosed(e);
	}
}
