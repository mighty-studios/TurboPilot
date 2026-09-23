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
		Check.True(!defaults.Enabled, "Keep existing chat unchanged until mediation is enabled.");
		Check.Equal("phi-3.5-mini", defaults.ModelAlias, "Prefer the requested local model");
		configuration.EnsureDocuments();
		foreach (var operation in Enum.GetValues<MediatorOperation>())
		{
			var instructions = configuration.ReadInstructions(operation);
			Check.True(instructions.Contains("JSON") && instructions.Contains("Gotchas"), "Provision the operation's editable instructions.");
			Check.True(!instructions.StartsWith("---"), "Strip front matter before generation.");
		}
		var generalPath = Path.Combine(configuration.DirectoryPath, "instructions", "mediator.instructions.md");
		File.AppendAllText(generalPath, "\r\nUSER_EDIT_SENTINEL\r\n");
		configuration.EnsureDocuments();
		Check.True(configuration.ReadInstructions(MediatorOperation.RewritePrompt).Contains("USER_EDIT_SENTINEL"), "Never overwrite edited instructions.");
		var configured = defaults with { Enabled = true, DebugRaw = true, BeautifyOutput = false };
		configuration.Save(configured);
		Check.Equal(configured, configuration.Load(), "Persist all Mediator options");
		Check.Throws<InvalidOperationException>(() => configuration.Save(configured with { CallTimeoutSeconds = 0 }));
		Check.Equal(configured, configuration.Load(), "An invalid save must preserve the prior options");
		Check.True(!Directory.EnumerateFiles(configuration.DirectoryPath, "*.tmp", SearchOption.AllDirectories).Any(), "Do not leave partial configuration files.");
		File.WriteAllText(configuration.SettingsPath, """{"modelAlias":null}""");
		Check.Throws<JsonException>(() => configuration.Load());
		Console.WriteLine("PASS Mediator defaults, editable instructions and skills, and atomic settings");
	}

	public static async Task RunNativeAsync(bool download)
	{
		await using var runtime = new FoundryModelRuntime();
		using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(download ? 30 : 3));
		var models = await runtime.ListModelsAsync(timeout.Token);
		Check.True(models.Count > 0, "The embedded runtime must return compatible text models.");
		var preferred = models.FirstOrDefault(model => model.Alias == MediatorSettings.DefaultModelAlias);
		Console.WriteLine($"PASS embedded catalog: {models.Count} compatible models; preferred model: {preferred?.DisplayLabel ?? "not available"}");
		if (!download) return;
		var model = preferred ?? throw new InvalidOperationException("The requested default model is not available.");
		if (!model.Cached)
		{
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
		await runtime.UnloadAsync(timeout.Token);
		Console.WriteLine("PASS default on-device model download, loading, generation, and unloading");
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

	public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
