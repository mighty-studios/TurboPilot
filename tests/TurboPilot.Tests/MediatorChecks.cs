using System.IO;
using System.Text.Json;
using TurboPilot.Mediation;

namespace TurboPilot.Tests;

internal static class MediatorChecks
{
	public static void RunFoundation()
	{
		using var workspace = new TestWorkspace();
		var configuration = new MediatorConfiguration(Path.Combine(workspace.Root, "mediator"));
		var defaults = configuration.Load();
		Check.True(!defaults.Enabled, "Keep the parked Mediator disabled by default.");
		Check.Equal("phi-3.5-mini", defaults.ModelAlias, "Prefer the requested local model");
		var configured = defaults with { Enabled = true, DebugRaw = true };
		configuration.Save(configured);
		Check.Equal(configured, configuration.Load(), "Persist all Mediator options");
		Check.Throws<InvalidOperationException>(() => configuration.Save(configured with { CallTimeoutSeconds = 0 }));
		Check.Equal(configured, configuration.Load(), "An invalid save must preserve the prior options");
		Check.True(!Directory.EnumerateFiles(configuration.DirectoryPath, "*.tmp", SearchOption.AllDirectories).Any(), "Do not leave partial configuration files.");
		File.WriteAllText(configuration.SettingsPath, """{"enabled":true,"modelAlias":"phi-3.5-mini","rewordPrompts":true,"beautifyOutput":true}""");
		Check.True(!configuration.Load().Enabled, "Settings from before the Mediator was parked load disabled.");
		File.WriteAllText(configuration.SettingsPath, """{"modelAlias":null}""");
		Check.Throws<JsonException>(() => configuration.Load());
		Console.WriteLine("PASS parked Mediator defaults, migration of retired options, and atomic settings");
	}

	public static async Task RunParkedAsync()
	{
		using var workspace = new TestWorkspace();
		var worklogs = Path.Combine(workspace.Root, "worklogs");
		var runtime = new FakeLocalRuntime { Generate = (_, input, _) => Task.FromResult(new LocalCompletion("echo: " + input)) };
		var store = new MediationStore("parked-fixture", workspace.Workspace, worklogs);
		await using (var parked = new MediatorService("parked-fixture", workspace.Workspace, new MediatorSettings(), runtime, store))
		{
			parked.Capture("user", "Not recorded while disabled.");
			Check.True(await parked.GenerateAsync("probe", "Reply.", "Hello.", 64) is null, "A disabled Mediator must not run inference.");
		}
		Check.True(!Directory.Exists(store.DirectoryPath), "A disabled Mediator must not write a worklog.");
		Check.Equal(0, runtime.Requests.Count, "A disabled Mediator must not call the local model");

		var options = new MediatorSettings { Enabled = true };
		await using (var mediator = new MediatorService("parked-fixture", workspace.Workspace, options, runtime, store))
		{
			var notices = new List<string>();
			var diagnostics = new List<MediatorDiagnostic>();
			mediator.NoticeReceived += notices.Add;
			mediator.DiagnosticReceived += diagnostics.Add;
			mediator.Capture("user", "Review README.md.");
			mediator.Capture("assistant", "It's reviewed.");
			Check.True(File.ReadAllText(store.WorklogPath).Contains("Review README.md.") && File.ReadAllText(store.WorklogPath).Contains("It's reviewed."),
				"Record prompts and replies while enabled.");
			Check.Equal("echo: Hello.", await mediator.GenerateAsync("probe", "Reply.", "Hello.", 64), "Return the local model's text");
			Check.True(diagnostics.Any(item => item.Purpose == "probe" && item.Output == "echo: Hello."), "Report the local exchange as a diagnostic.");
			var oversized = string.Join(" ", Enumerable.Range(0, 5000).Select(index => "word" + index));
			Check.True(await mediator.GenerateAsync("probe", "Reply.", oversized, 64) is null && runtime.Requests.Count == 1,
				"Skip requests too large for the local budget without calling the model.");
			Check.True(mediator.Status == "Mediator ready" && notices.Count == 0, "An oversized request is not a failure.");

			runtime.Generate = (_, _, _) => Task.FromException<LocalCompletion>(new InvalidOperationException("Fixture failure."));
			for (var attempt = 0; attempt < 3; attempt++)
				Check.True(await mediator.GenerateAsync("probe", "Reply.", "Fail.", 64) is null, "A failure returns no text.");
			Check.Equal("Mediator disabled", mediator.Status, "Disable after three consecutive failures");
			var calls = runtime.Requests.Count;
			await mediator.GenerateAsync("probe", "Reply.", "Another.", 64);
			Check.Equal(calls, runtime.Requests.Count, "Do not keep calling a disabled model");
			Check.Equal(3, notices.Count, "Report each local failure");

			await mediator.ConfigureAsync(options);
			runtime.LoadError = new LocalModelUnavailableException("Missing model.");
			await mediator.GenerateAsync("probe", "Reply.", "Unavailable.", 64);
			Check.Equal("Mediator offline", mediator.Status, "Offline state must be explicit");
			runtime.LoadError = null;
			await mediator.ConfigureAsync(options with { CallTimeoutSeconds = 1 });
			runtime.Generate = async (_, _, token) =>
			{
				await Task.Delay(Timeout.Infinite, token);
				return new LocalCompletion("");
			};
			Check.True(await mediator.GenerateAsync("probe", "Reply.", "Time out.", 64) is null, "A timeout returns no text.");
			using var cancellation = new CancellationTokenSource(50);
			await Check.ThrowsAsync<OperationCanceledException>(() => mediator.GenerateAsync("probe", "Reply.", "Cancel.", 64, cancellation.Token));
			Check.True(!mediator.IsBusy, "Cancellation must release the busy state.");
		}
		await using (var recalled = new MediatorService("parked-fixture", workspace.Workspace, options, runtime, store))
			Check.True(recalled.HasHistory, "Recall the worklog across service instances.");
		Console.WriteLine("PASS parked Mediator capture only while enabled, local generation, budget skips, and failure handling");
	}

	public static async Task RunNativeAsync(bool download)
	{
		await using var runtime = new FoundryModelRuntime();
		using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(download ? 30 : 3));
		var models = await runtime.ListModelsAsync(timeout.Token);
		Check.True(models.Count > 0, "The embedded runtime must return compatible text models.");
		var preferred = models.FirstOrDefault(model => model.Alias == MediatorSettings.DefaultModelAlias);
		Console.WriteLine($"PASS embedded catalog: {models.Count} compatible models; preferred model: "
			+ (preferred is null ? "not available" : $"{preferred.Name} ({preferred.Alias}) | {preferred.Device}{(preferred.Cached ? " | downloaded" : "")}"));
		var model = preferred ?? throw new InvalidOperationException("The requested default model is not available.");
		if (!model.Cached)
		{
			if (!download) return;
			var free = new DriveInfo(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))!).AvailableFreeSpace;
			var required = ((long)(model.SizeMb ?? 4096) * 2 + 1024) * 1024 * 1024;
			Check.True(free > required, "Insufficient free space for the default model download.");
			var last = -1;
			await runtime.DownloadAsync(model.Alias, new Progress<LocalRuntimeProgress>(progress =>
			{
				var percent = (int)(progress.Percent ?? 0);
				if (percent / 5 == last) return;
				last = percent / 5;
				Console.WriteLine($"{progress.Message}: {percent}%");
			}), timeout.Token);
		}
		await runtime.LoadAsync(model.Alias, timeout.Token);
		var result = await runtime.GenerateAsync(
			"Return only the JSON object {\"ready\":true}.",
			"Confirm readiness.", 64, timeout.Token);
		Check.True(result.Text.Contains("true", StringComparison.OrdinalIgnoreCase), "Complete an on-device request.");
		using (var canceled = new CancellationTokenSource(50))
			await Check.ThrowsAsync<OperationCanceledException>(() => runtime.GenerateAsync(
				"Write a long JSON array of integers.", "List every integer from 1 to 1000.", 2000, canceled.Token));
		await runtime.UnloadAsync(timeout.Token);
		Console.WriteLine("PASS default on-device model download, loading, generation, and unloading");
		using var workspace = new TestWorkspace();
		var store = new MediationStore("native-fixture", workspace.Workspace, Path.Combine(workspace.Root, "worklogs"));
		await using var mediator = new MediatorService("native-fixture", workspace.Workspace, new MediatorSettings { Enabled = true }, runtime, store);
		var notices = new List<string>();
		mediator.NoticeReceived += notices.Add;
		var reply = await mediator.GenerateAsync("readiness", "Return only the JSON object {\"ready\":true}.", "Confirm readiness.", 64, timeout.Token);
		Check.True(reply?.Contains("true", StringComparison.OrdinalIgnoreCase) == true, "Run the on-device model through the parked Mediator: " + string.Join(" | ", notices));
		Console.WriteLine("PASS parked Mediator on-device generation");
	}
}
internal sealed class FakeLocalRuntime : ILocalModelRuntime
{
	public IReadOnlyList<LocalModelDescriptor> Models { get; set; } =
	[
		new(MediatorSettings.DefaultModelAlias, "Phi 3.5 Mini", "CPU", 2000, true, 4096, "MIT"),
		new("fixture-small", "Fixture Small", "GPU", 500, false, 8192, "MIT"),
	];
	public Func<string, string, CancellationToken, Task<LocalCompletion>>? Generate { get; set; }
	public List<(string System, string Input, int Limit)> Requests { get; } = [];
	public int Loads { get; private set; }
	public int Unloads { get; private set; }
	public int DisposeCalls { get; private set; }
	public Func<ValueTask>? DisposeAction { get; set; }
	public Exception? LoadError { get; set; }

	public Task<IReadOnlyList<LocalModelDescriptor>> ListModelsAsync(CancellationToken cancellationToken = default) =>
		Task.FromResult(Models);

	public Task PrepareAccelerationAsync(IProgress<LocalRuntimeProgress>? progress = null, CancellationToken cancellationToken = default) =>
		Task.CompletedTask;

	public Task DownloadAsync(string alias, IProgress<LocalRuntimeProgress>? progress = null, CancellationToken cancellationToken = default)
	{
		Models = Models.Select(model => model.Alias == alias ? model with { Cached = true } : model).ToList();
		progress?.Report(new("Downloaded", 100));
		return Task.CompletedTask;
	}

	public Task LoadAsync(string alias, CancellationToken cancellationToken = default)
	{
		Loads++;
		return LoadError is not null ? Task.FromException(LoadError) : Task.CompletedTask;
	}

	public Task<LocalCompletion> GenerateAsync(string system, string input, int maxOutputTokens, CancellationToken cancellationToken = default)
	{
		Requests.Add((system, input, maxOutputTokens));
		return Generate?.Invoke(system, input, cancellationToken)
			?? Task.FromException<LocalCompletion>(new InvalidOperationException("No local response was scripted."));
	}

	public Task UnloadAsync(CancellationToken cancellationToken = default)
	{
		Unloads++;
		return Task.CompletedTask;
	}

	public ValueTask DisposeAsync()
	{
		DisposeCalls++;
		return DisposeAction?.Invoke() ?? ValueTask.CompletedTask;
	}
}
