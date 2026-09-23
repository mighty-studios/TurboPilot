using System.IO;
using System.Text.Json;
using TurboPilot.Mediation;

namespace TurboPilot.Tests;

internal static class MediatorChecks
{
	public static async Task RunProcessingAsync()
	{
		using var workspace = new TestWorkspace();
		var configuration = new MediatorConfiguration(Path.Combine(workspace.Root, "mediator"));
		var options = new MediatorSettings { Enabled = true };
		var store = new MediationStore("mediator-fixture", workspace.Workspace, Path.Combine(workspace.Root, "worklogs"));
		var runtime = new FakeLocalRuntime();
		runtime.Generate = (system, input, _) =>
		{
			string output;
			if (system.Contains("# Reword a Prompt"))
				output = """{"prompt":"Fix `src\\main.cs` without changing 42.","meaningPreserved":true}""";
			else if (system.Contains("# Prepare Output"))
				output = """{"links":[],"headings":["Results"]}""";
			else if (system.Contains("# Monitor Output"))
				output = """{"warnings":[{"kind":"unsupported-claim","message":"This claim may need verification.","quote":"Everything is verified."}]}""";
			else
			{
				using var json = JsonDocument.Parse(input);
				var content = json.RootElement.GetProperty("entries")[0].GetProperty("Content").GetString()!;
				output = JsonSerializer.Serialize(new { summary = content.Contains("green") ? "Use green. Remove the old blue decision." : "Use blue. Work remains pending." });
			}
			return Task.FromResult(new LocalCompletion(output));
		};
		await using (var mediator = new MediatorService("mediator-fixture", workspace.Workspace, options, runtime, configuration, store))
		{
			var notices = new List<string>();
			mediator.NoticeReceived += notices.Add;
			var prompt = "Please, if you would be so kind, Fix `src\\main.cs` without changing 42. Thank you very much.";
			var prepared = await mediator.PreparePromptAsync(prompt);
			Check.True(prepared.Rewritten && prepared.PreparedTokens < prepared.OriginalTokens, "Measure fewer tokens before accepting a rewrite.");
			Check.True(prepared.Prompt.Contains("`src\\main.cs`") && prepared.Prompt.Contains("without") && prepared.Prompt.Contains("42"), "Preserve paths, literals, and negation.");
			runtime.Generate = (_, _, _) => Task.FromResult(new LocalCompletion("""{"prompt":"Fix the file.","meaningPreserved":true}"""));
			var rejected = await mediator.PreparePromptAsync(prompt);
			Check.Equal(prompt, rejected.Prompt, "Reject reductions that drop exact requirements");
			runtime.Generate = (_, _, _) => Task.FromResult(new LocalCompletion("""{"summary":"Use blue. Work remains pending."}"""));
			await mediator.RecordAsync("user", "Use blue.");
			Check.True((await mediator.GetSummaryAsync())!.Contains("blue"), "Update the summary after a prompt.");
			runtime.Generate = (_, _, _) => Task.FromResult(new LocalCompletion("""{"summary":"Use green. Work remains pending."}"""));
			await mediator.RecordAsync("user", "Correction: use green instead.");
			Check.True((await mediator.GetSummaryAsync())!.Contains("green") && !mediator.CurrentSummary!.Contains("blue"), "Replace stale summary decisions.");
			Check.True(File.ReadAllText(store.WorklogPath).Contains("Use blue.") && File.ReadAllText(store.WorklogPath).Contains("use green"), "Keep a verbatim history despite summary replacement.");
			var file = workspace.Write("workspace\\src\\main.cs", "content");
			var image = Path.Combine(workspace.Workspace, "preview.png");
			File.WriteAllBytes(image, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII="));
			runtime.Generate = (system, _, _) => Task.FromResult(new LocalCompletion(system.Contains("# Prepare Output")
				? """{"links":[],"headings":["Results"]}"""
				: """{"warnings":[{"kind":"unsupported-claim","message":"This claim may need verification.","quote":"Everything is verified."}]}"""));
			var output = "Results\r\n\r\nSee `src\\main.cs` and preview.png.\r\n\r\nEverything is verified.\r\n\r\n```csharp\r\nsrc\\main.cs\r\n```";
			var display = await mediator.PrepareOutputAsync(output);
			Check.True(display.Markdown.Contains("kp-path:" + Uri.EscapeDataString(file)), "Link a verified file reference.");
			Check.True(display.Markdown.Contains("[`src\\main.cs`]"), "Preserve single backslashes inside a code-formatted link label.");
			Check.True(display.Markdown.Contains("data:image/png;base64,"), "Embed a permitted local image.");
			Check.True(display.Markdown.Contains("## Results") && display.Markdown.Contains("```csharp\r\nsrc\\main.cs\r\n```"), "Prepare headings without modifying code.");
			Check.Equal(1, display.Warnings.Count, "Surface a grounded warning");
			Check.True(output.Contains(display.Warnings[0].Quote), "Ground every warning in the response.");
			Check.Equal(0, notices.Count, "Successful processing must not report errors");
		}
		await using (var recalled = new MediatorService("mediator-fixture", workspace.Workspace, options, runtime, configuration, store))
			Check.True(recalled.CurrentSummary!.Contains("green"), "Recall the durable summary across service instances.");
		await CheckFailurePathsAsync(workspace, configuration);
		CheckFormattingBoundaries(workspace);
		Console.WriteLine("PASS token reduction, preserved literals, local formatting, warnings, durable summaries, and fallback paths");
	}

	private static async Task CheckFailurePathsAsync(TestWorkspace workspace, MediatorConfiguration configuration)
	{
		var runtime = new FakeLocalRuntime { Generate = (_, _, _) => Task.FromResult(new LocalCompletion("{broken")) };
		var options = new MediatorSettings { Enabled = true, MaintainSummary = false, BeautifyOutput = false, MonitorOutput = false };
		var store = new MediationStore("failure-fixture", workspace.Workspace, Path.Combine(workspace.Root, "worklogs"));
		await using var mediator = new MediatorService("failure-fixture", workspace.Workspace, options, runtime, configuration, store);
		var notices = new List<string>();
		mediator.NoticeReceived += notices.Add;
		for (var index = 0; index < 3; index++)
			Check.Equal("Please keep this unchanged.", (await mediator.PreparePromptAsync("Please keep this unchanged.")).Prompt, "Fallback must preserve the prompt");
		Check.Equal("Mediator disabled", mediator.Status, "Disable after three consecutive failures");
		var calls = runtime.Requests.Count;
		await mediator.PreparePromptAsync("Another request.");
		Check.Equal(calls, runtime.Requests.Count, "Do not keep calling a disabled model");
		Check.Equal(3, notices.Count, "Report each local failure");
		await mediator.ConfigureAsync(options with { Enabled = false });
		await mediator.RecordAsync("user", "Still recorded when off.");
		Check.True(File.ReadAllText(store.WorklogPath).Contains("Still recorded when off"), "Always record the worklog.");
		await mediator.PreparePromptAsync("Off means pass-through.");
		Check.Equal(calls, runtime.Requests.Count, "Disabled mediation must not run inference");
		await mediator.ConfigureAsync(options);
		var repairAttempts = 0;
		runtime.Generate = (_, input, _) => Task.FromResult(new LocalCompletion(++repairAttempts == 1
			? """{"result":"wrong schema"}"""
			: """{"prompt":"Keep this unchanged.","meaningPreserved":true}"""));
		var repaired = await mediator.PreparePromptAsync("Please, if possible, keep this unchanged.");
		Check.Equal(2, repairAttempts, "Allow one bounded schema correction");
		Check.Equal("Keep this unchanged.", repaired.Prompt, "Validate the corrected result before use");
		runtime.LoadError = new LocalModelUnavailableException("Missing model.");
		await mediator.PreparePromptAsync("Model unavailable.");
		Check.Equal("Mediator offline", mediator.Status, "Offline state must be explicit");
		runtime.LoadError = null;
		await mediator.ConfigureAsync(options with { CallTimeoutSeconds = 1 });
		runtime.Generate = async (_, _, token) =>
		{
			await Task.Delay(Timeout.Infinite, token);
			return new LocalCompletion("");
		};
		var timedOut = await mediator.PreparePromptAsync("This must time out.");
		Check.Equal("This must time out.", timedOut.Prompt, "Timeout must preserve the prompt");
		using var cancellation = new CancellationTokenSource(50);
		await Check.ThrowsAsync<OperationCanceledException>(() => mediator.PreparePromptAsync("Cancel this.", cancellationToken: cancellation.Token));
		Check.True(!mediator.IsBusy, "Cancellation must release the busy state.");
	}

	private static void CheckFormattingBoundaries(TestWorkspace workspace)
	{
		var outside = workspace.Write("outside.png", "not an image");
		var notes = new List<string>();
		var text = $"[existing](https://example.com/src/main.cs) https://example.com/src/main.cs\r\n`src\\main.cs`\r\n{outside}\r\n~~~\r\nsrc\\main.cs\r\n~~~";
		var prepared = OutputFormatter.Apply(text, new([], []), workspace.Workspace, notes.Add);
		Check.True(prepared.Contains("[existing](https://example.com/src/main.cs)") && prepared.Contains("https://example.com/src/main.cs"), "Leave existing links and URLs intact.");
		Check.True(prepared.Contains("~~~\r\nsrc\\main.cs\r\n~~~"), "Leave alternate code fences intact.");
		Check.True(!prepared.Contains("data:") && !prepared.Contains(Uri.EscapeDataString(outside)), "Do not read or link files outside the allowed scope.");
		var repeated = new string('a', 110);
		Check.Equal(1, MediationText.FindRepetition(repeated + "\r\n\r\n" + repeated).Count, "Flag repeated substantial paragraphs");
	}

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
		var configuration = new MediatorConfiguration(Path.Combine(workspace.Root, "mediator"));
		var store = new MediationStore("native-fixture", workspace.Workspace, Path.Combine(workspace.Root, "worklogs"));
		var options = new MediatorSettings { Enabled = true, BeautifyOutput = false, MonitorOutput = false };
		await using var mediator = new MediatorService("native-fixture", workspace.Workspace, options, runtime, configuration, store);
		var notices = new List<string>();
		var diagnostics = new List<MediatorDiagnostic>();
		mediator.NoticeReceived += notices.Add;
		mediator.DiagnosticReceived += diagnostics.Add;
		var rewritten = await mediator.PreparePromptAsync(
			"Please, if you have a moment, summarize README.md in 3 bullet points without editing any files. Thank you very much.",
			cancellationToken: timeout.Token);
		Check.True(rewritten.Rewritten && rewritten.PreparedTokens < rewritten.OriginalTokens,
			"The default model must safely reduce the fixture prompt: " + string.Join(" | ", diagnostics.Select(item => item.Output + " " + item.Note)));
		await mediator.RecordAsync("user", "The current goal is to update README.md. Do not edit source files.", cancellationToken: timeout.Token);
		Check.True(!string.IsNullOrWhiteSpace(await mediator.GetSummaryAsync(timeout.Token)), "The default model must maintain a summary: " + string.Join(" | ", notices));
		workspace.Write("workspace\\README.md", "Fixture document.");
		await mediator.ConfigureAsync(options with { BeautifyOutput = true, MonitorOutput = true }, timeout.Token);
		var prepared = await mediator.PrepareOutputAsync("Results\r\n\r\nUpdated `README.md`.", timeout.Token);
		Check.True(prepared.Markdown.Contains("kp-path:"), "The default model must support the formatting contract: " + string.Join(" | ", notices));
		Check.Equal(0, notices.Count, "On-device processing must not silently fall back: " + string.Join(" | ", notices)
			+ " Responses: " + string.Join(" | ", diagnostics.Select(item => item.Output)));
		Console.WriteLine($"PASS default on-device prompt reduction ({rewritten.OriginalTokens} -> {rewritten.PreparedTokens} tokens), summary, formatting, and monitoring");
		await mediator.DisposeAsync();
		await using var provider = new LocalProvider();
		await using var chat = workspace.CreateChat();
		chat.MediatorFactory = (id, folder) =>
		{
			var service = new MediatorService(id, folder,
				options with { BeautifyOutput = true }, runtime, configuration,
				new MediationStore(id, folder, Path.Combine(workspace.Root, "worklogs")));
			service.DiagnosticReceived += diagnostics.Add;
			return service;
		};
		chat.MediatorNoticeReceived += notices.Add;
		await chat.StartAsync(new Ai.ChatSessionOptions
		{
			WorkspaceFolder = workspace.Workspace, Model = "test-model", UseByok = true,
			ByokEndpoint = provider.Endpoint, ContextWindowTokens = 32768,
		}, timeout.Token);
		provider.Replies.Enqueue(new LocalProvider.Reply("Results\r\n\r\nReviewed `README.md` without changing any files."));
		await RuntimeChecks.SendAndWaitAsync(chat,
			"Please, if you have a moment, summarize README.md in 3 bullet points without editing any files. Thank you very much.");
		Check.True(!provider.Requests.Last().GetRawText().Contains("if you have a moment"), "Forward the actual native reduction.");
		Check.True(chat.RenderedTranscript.Contains("kp-path:") && !chat.Transcript.Contains("kp-path:"), "Keep native prepared output separate from Raw. "
			+ string.Join(" | ", notices) + " Responses: " + string.Join(" | ", diagnostics.Select(item => item.Output)));
		Check.True(await chat.GetRestartSummaryAsync(timeout.Token) is not null, "Maintain restart context through the real native chat path.");
		Check.Equal(0, notices.Count, "Native chat must not hide local processing failures: " + string.Join(" | ", notices));
		Console.WriteLine("PASS integrated on-device mediation through a native SDK chat session");
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
