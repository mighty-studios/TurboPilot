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
		var options = new MediatorSettings { Enabled = true, MinimumRewriteTokens = 0 };
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
				var content = json.RootElement.GetProperty("entries")[0].GetProperty("content").GetString()!;
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
		await CheckTracedTurnAsync(workspace, configuration);
		Console.WriteLine("PASS token reduction, preserved literals, local formatting, warnings, durable summaries, and fallback paths");
	}

	// Replays a live agentic turn whose reduction lost requirements, and the workload that turn produced.
	private static async Task CheckTracedTurnAsync(TestWorkspace workspace, MediatorConfiguration configuration)
	{
		const string tracePrompt = "read the attached readme to understand the project. Look over recent changes made over the last five git commits and make suggestions for next steps. Make no actual code changes yet. We will discuss those after you share your review of the last five commits and recommendations";
		const string traceRewrite = "Review the attached README to understand the TurboPilot project and summarize suggestions for next steps based on the last five git commits without making code changes yet.";
		var constraints = MediationText.GetProtectedText(tracePrompt);
		Check.True(constraints.Contains("no") && constraints.Contains("yet") && constraints.Contains("after"),
			"Protect negation and ordering words: " + string.Join(", ", constraints));
		Check.True(!MediationText.PreservesLiterals(tracePrompt, traceRewrite), "Reject a reduction that drops a prohibition and a request to wait.");
		Check.True(!MediationText.PreservesLiterals("Do not edit files.", "Edit files; note the result."), "A longer word must not stand in for a negation.");
		Check.True(MediationText.PreservesLiterals("Never edit files.", "Edit no files, never."), "Compare constraint words without case.");
		Check.True(!MediationText.PreservesLiterals("Keep 42 items.", "Keep 142 items."), "Compare numbers as whole tokens.");
		Check.True(MediationText.PreservesLiterals("Don't edit it, it's fine.", "Don't edit it."), "Apostrophes inside words are not quotes.");
		Check.True(MediationText.PreservesLiterals("Look in /src and then fix the bug.", "Fix the bug in /src."), "Unquoted paths end at whitespace.");

		var runtime = new FakeLocalRuntime();
		var store = new MediationStore("trace-fixture", workspace.Workspace, Path.Combine(workspace.Root, "worklogs"));
		await using var mediator = new MediatorService("trace-fixture", workspace.Workspace, new MediatorSettings { Enabled = true },
			runtime, configuration, store);
		var notices = new List<string>();
		mediator.NoticeReceived += notices.Add;
		Check.True(!mediator.ShouldRewrite(tracePrompt), "The traced prompt is below the default reword size.");
		var forwarded = await mediator.PreparePromptAsync("Please kindly summarize README.md. Thank you very much.");
		Check.True(!forwarded.Rewritten && runtime.Requests.Count == 0, "Forward short prompts without local processing.");

		await mediator.ConfigureAsync(new MediatorSettings { Enabled = true, MinimumRewriteTokens = 0 });
		runtime.Generate = (_, _, _) => Task.FromResult(new LocalCompletion(
			JsonSerializer.Serialize(new { prompt = traceRewrite, meaningPreserved = true })));
		var traced = await mediator.PreparePromptAsync(tracePrompt, [Path.Combine(workspace.Workspace, "README.md")]);
		Check.Equal(tracePrompt, traced.Prompt, "Send the original when a reduction loses a requirement");
		using (var json = JsonDocument.Parse(runtime.Requests.Last().Input))
		{
			Check.True(!json.RootElement.TryGetProperty("summary", out _), "Keep background context out of prompt reduction.");
			Check.Equal("README.md", json.RootElement.GetProperty("attachments")[0].GetString(), "Pass attachment names without folders");
		}

		runtime.Requests.Clear();
		runtime.Generate = (_, _, _) => Task.FromResult(new LocalCompletion("""{"summary":"Review the commits; make no code changes."}"""));
		mediator.Capture("user", "Review the commits. Don't change code.");
		mediator.Capture("answer", "Question: Which branch?\r\nAnswer: main");
		mediator.Capture("tool", "view succeeded\r\n" + new string('x', 5000));
		mediator.Capture("assistant", "It's done.");
		mediator.Capture("assistant", "Partial answer", interrupted: true);
		var summary = await mediator.GetSummaryAsync();
		Check.True(summary is not null && mediator.CurrentSummary == summary, "Bring the summary up to date past tool output.");
		Check.Equal(1, runtime.Requests.Count, "Summarize a turn's entries in one local call");
		using (var json = JsonDocument.Parse(runtime.Requests[0].Input))
		{
			var entries = json.RootElement.GetProperty("entries").EnumerateArray().ToList();
			Check.Equal("user,answer,assistant,assistant", string.Join(",", entries.Select(entry => entry.GetProperty("role").GetString())),
				"Summarize conversation entries without tool output");
			Check.True(!entries[0].TryGetProperty("interrupted", out _) && entries[3].GetProperty("interrupted").GetBoolean(),
				"Mark only interrupted entries.");
		}
		Check.True(runtime.Requests[0].Input.Contains("It's done.") && runtime.Requests[0].Input.Contains("Don't"), "Keep apostrophes readable for the local model.");

		runtime.Requests.Clear();
		var notes = workspace.Write("workspace\\notes.txt", "notes");
		var oversized = string.Join("\r\n\r\n", Enumerable.Range(0, 400).Select(index => $"Paragraph {index} mentions notes.txt and other details."));
		for (var attempt = 0; attempt < 3; attempt++)
		{
			var prepared = await mediator.PrepareOutputAsync(oversized);
			Check.True(prepared.Markdown.Contains("kp-path:" + Uri.EscapeDataString(notes)), "Link files in a response too large for the local model.");
		}
		Check.Equal(0, runtime.Requests.Count, "Skip oversized content instead of sending it to the local model");
		Check.True(mediator.Status == "Mediator ready" && notices.Count == 0,
			"Oversized content must not count as a local failure: " + mediator.Status + " " + string.Join(" | ", notices));

		runtime.Generate = (system, _, _) => Task.FromResult(new LocalCompletion(system.Contains("# Prepare Output")
			? """{"links":[],"headings":[]}""" : """{"warnings":[]}"""));
		mediator.Capture("user", "Check the build output.");
		mediator.Capture("tool", "powershell succeeded\r\n" + new string('y', 1400));
		mediator.Capture("answer", "Question: Continue?\r\nAnswer: yes");
		await mediator.PrepareOutputAsync("The build passed.");
		using (var json = JsonDocument.Parse(runtime.Requests.Single(request => request.System.Contains("# Monitor Output")).Input))
		{
			Check.Equal("Check the build output.", json.RootElement.GetProperty("prompt").GetString(), "Monitor against the latest request, not an answer");
			var evidence = json.RootElement.GetProperty("evidence").EnumerateArray().Select(item => item.GetString()!).ToList();
			Check.True(evidence.Count == 1 && evidence[0].Length <= 600 && evidence[0].StartsWith("powershell succeeded"),
				"Supply bounded evidence from the latest request's tools.");
			Check.Equal("Review the commits; make no code changes.", json.RootElement.GetProperty("summary").GetString(), "Give monitoring the latest summary");
		}

		runtime.Requests.Clear();
		runtime.Generate = (_, _, _) => Task.FromResult(new LocalCompletion(
			"""{"warnings":[{"kind":"repetition","message":"Repeated tool output.","quote":"powershell succeeded"}]}"""));
		var ungrounded = await mediator.PrepareOutputAsync("The build passed.");
		Check.True(ungrounded.Warnings.Count == 0 && runtime.Requests.Count == 1 && notices.Count == 0 && mediator.Status == "Mediator ready",
			"Discard a warning that quotes evidence instead of the response, without a correction or a failure.");
		Check.True(!OutputFormatter.HasHeadingCandidate(
			"**Assessment:** Stable.\r\n\r\n**Recommended next steps:**\r\n\r\n1. **Run checks**\r\n\r\n```powershell\r\nRun tests\r\n```\r\n\r\nNo code changes were made."),
			"Skip model formatting when no plain line can become a heading.");
		Check.True(OutputFormatter.HasHeadingCandidate("Results\r\n\r\nSee `README.md`."), "Offer standalone plain lines as heading candidates.");

		// Preparation that waited in the queue must use its own exchange, not a newer prompt.
		runtime.Requests.Clear();
		runtime.Generate = (_, _, _) => Task.FromResult(new LocalCompletion("""{"warnings":[]}"""));
		mediator.Capture("user", "First request.");
		mediator.Capture("tool", "view succeeded\r\nFIRST_EVIDENCE");
		var answered = mediator.Capture("assistant", "First answer.");
		mediator.Capture("user", "Second request.");
		mediator.Capture("tool", "view succeeded\r\nSECOND_EVIDENCE");
		await mediator.PrepareOutputAsync("First answer.", answered);
		using (var json = JsonDocument.Parse(runtime.Requests.Single().Input))
		{
			Check.Equal("First request.", json.RootElement.GetProperty("prompt").GetString(), "Monitor a queued answer against its own request");
			var evidence = string.Join(" ", json.RootElement.GetProperty("evidence").EnumerateArray().Select(item => item.GetString()));
			Check.True(evidence.Contains("FIRST_EVIDENCE") && !evidence.Contains("SECOND_EVIDENCE"), "Use only the answer's own tool evidence.");
		}

		runtime.Requests.Clear();
		var interim = mediator.FormatOutput("Checking notes.txt first.");
		Check.True(interim.Markdown.Contains("kp-path:") && runtime.Requests.Count == 0, "Link interim messages without local inference.");
		runtime.Generate = (_, _, _) => Task.FromResult(new LocalCompletion("{broken"));
		var failed = await mediator.PrepareOutputAsync("See notes.txt.");
		Check.True(failed.Markdown.Contains("kp-path:"), "Keep validated links when the local model fails.");
		await CheckSummaryPackingAsync(workspace);
		Console.WriteLine("PASS traced-turn literal checks, reword size, batched summaries, bounded evidence, and budget skips");
	}

	// Many short entries under a long summary must be packed by serialized size, never skipped.
	private static async Task CheckSummaryPackingAsync(TestWorkspace workspace)
	{
		var configuration = new MediatorConfiguration(Path.Combine(workspace.Root, "packing"));
		var runtime = new FakeLocalRuntime();
		var words = new List<string>();
		while (MediationText.CountTokens(string.Join(" ", words)) < 800)
			words.Add("fact" + words.Count);
		var longSummary = string.Join(" ", words);
		runtime.Generate = (_, _, _) => Task.FromResult(new LocalCompletion(JsonSerializer.Serialize(new { summary = longSummary })));
		var diagnostics = new List<MediatorDiagnostic>();
		var notices = new List<string>();
		var store = new MediationStore("packing-fixture", workspace.Workspace, Path.Combine(workspace.Root, "worklogs"));
		await using (var mediator = new MediatorService("packing-fixture", workspace.Workspace, new MediatorSettings { Enabled = true },
			runtime, configuration, store))
		{
			mediator.DiagnosticReceived += diagnostics.Add;
			mediator.NoticeReceived += notices.Add;
			mediator.Capture("user", "Start.");
			Check.True(await mediator.GetSummaryAsync() == longSummary, "Seed a long summary.");
			for (var index = 0; index < 86; index++)
				mediator.Capture("assistant", $"Narration step {index}: checked the build output and continued.");
			runtime.Requests.Clear();
			Check.True(await mediator.GetSummaryAsync() == longSummary, "Bring the summary up to date through many short entries.");
			Check.True(runtime.Requests.Count >= 2 && !diagnostics.Any(item => item.Note?.StartsWith("Skipped") == true) && notices.Count == 0,
				$"Pack entries into requests that fit: {runtime.Requests.Count} calls, notices: {string.Join(" | ", notices)}");
			var entries = runtime.Requests.Sum(request =>
			{
				using var json = JsonDocument.Parse(request.Input);
				return json.RootElement.GetProperty("entries").GetArrayLength();
			});
			Check.Equal(86, entries, "Summarize every entry exactly once");

			var answer = string.Join(" ", Enumerable.Range(0, 120).Select(index => $"step{index} without changes"));
			mediator.Capture("assistant", answer);
			runtime.Requests.Clear();
			await mediator.GetSummaryAsync();
			using (var json = JsonDocument.Parse(runtime.Requests.Single().Input))
				Check.Equal(answer, json.RootElement.GetProperty("entries").EnumerateArray().Single().GetProperty("content").GetString(),
					"Rejoin an entry's chunks within one request");
			Check.True(MediationText.SplitForContext(answer, 100).SkipLast(1).All(chunk => chunk.EndsWith(' ')), "Cut long text between words.");
		}

		var skill = Path.Combine(configuration.DirectoryPath, "skills", "update-summary", "SKILL.md");
		File.AppendAllText(skill, string.Join(" ", Enumerable.Range(0, 2600).Select(index => "rule" + index)));
		await using (var crowded = new MediatorService("packing-fixture", workspace.Workspace, new MediatorSettings { Enabled = true },
			runtime, configuration, store))
		{
			crowded.NoticeReceived += notices.Add;
			crowded.Capture("user", "One more request.");
			Check.True(await crowded.GetSummaryAsync() is null && notices.Any(notice => notice.Contains("too little room")),
				"Report a summary that cannot fit instead of stalling silently.");
		}
	}

	private static async Task CheckFailurePathsAsync(TestWorkspace workspace, MediatorConfiguration configuration)
	{
		var runtime = new FakeLocalRuntime { Generate = (_, _, _) => Task.FromResult(new LocalCompletion("{broken")) };
		var options = new MediatorSettings { Enabled = true, MaintainSummary = false, BeautifyOutput = false, MonitorOutput = false, MinimumRewriteTokens = 0 };
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
		CheckDefaultRefresh(workspace);
		Console.WriteLine("PASS Mediator defaults, editable instructions and skills, default refresh, and atomic settings");
	}

	private static void CheckDefaultRefresh(TestWorkspace workspace)
	{
		string[] documents =
		[
			"instructions\\mediator.instructions.md", "skills\\rewrite-prompt\\SKILL.md", "skills\\beautify-output\\SKILL.md",
			"skills\\update-summary\\SKILL.md", "skills\\monitor-output\\SKILL.md", "LICENSE.txt",
		];
		void WriteTemplates(string version)
		{
			foreach (var document in documents)
				workspace.Write(Path.Combine("templates", document), $"# Fixture {version}\r\n\r\nReturn JSON.\r\n\r\n## Gotchas\r\n\r\n- {document}\r\n");
		}
		var templates = Path.Combine(workspace.Root, "templates");
		var directory = Path.Combine(workspace.Root, "refresh");
		WriteTemplates("one");
		var configuration = new MediatorConfiguration(directory, templates);
		configuration.EnsureDocuments();
		Check.True(File.Exists(configuration.DefaultsManifestPath), "Track provisioned defaults.");
		var edited = Path.Combine(directory, "skills\\update-summary\\SKILL.md");
		File.AppendAllText(edited, "USER_EDIT_SENTINEL\r\n");
		WriteTemplates("two");
		configuration.EnsureDocuments();
		Check.True(File.ReadAllText(Path.Combine(directory, "skills\\rewrite-prompt\\SKILL.md")).Contains("Fixture two"), "Refresh unedited defaults.");
		Check.True(File.ReadAllText(edited).Contains("Fixture one") && File.ReadAllText(edited).Contains("USER_EDIT_SENTINEL"),
			"Never overwrite an edited document.");

		// Copies provisioned before tracking are refreshed only when they match a known earlier default.
		File.Delete(configuration.DefaultsManifestPath);
		var untracked = Path.Combine(directory, "skills\\monitor-output\\SKILL.md");
		var untrackedEdit = Path.Combine(directory, "skills\\beautify-output\\SKILL.md");
		File.WriteAllText(untracked, "earlier default\r\n");
		File.WriteAllText(untrackedEdit, "earlier edit\r\n");
		var earlier = MediatorConfiguration.HashContent(System.Text.Encoding.UTF8.GetBytes("earlier default\n"));
		WriteTemplates("three");
		new MediatorConfiguration(directory, templates, new Dictionary<string, string[]> { ["skills\\monitor-output\\SKILL.md"] = [earlier] })
			.EnsureDocuments();
		Check.True(File.ReadAllText(untracked).Contains("Fixture three"), "Refresh an untracked copy of a known earlier default.");
		Check.True(File.ReadAllText(untrackedEdit).Contains("earlier edit"), "Keep an untracked copy that matches no known default.");
		Check.True(File.ReadAllText(edited).Contains("USER_EDIT_SENTINEL"), "Keep edits when tracking is unavailable.");
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
		var options = new MediatorSettings { Enabled = true, BeautifyOutput = false, MonitorOutput = false, MinimumRewriteTokens = 0 };
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
		var prepared = await mediator.PrepareOutputAsync("Results\r\n\r\nUpdated `README.md`.", cancellationToken: timeout.Token);
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
