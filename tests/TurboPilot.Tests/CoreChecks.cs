using System.IO;
using System.Text;
using GitHub.Copilot;
using TurboPilot.Ai;
using TurboPilot.Commands;
using TurboPilot.Customizations;
using TurboPilot.Dialogs;
using TurboPilot.Permissions;
using TurboPilot.Rendering;
using TurboPilot.Sessions;
using TurboPilot.Tools;

namespace TurboPilot.Tests;

internal static class CoreChecks
{
	public static async Task RunAsync()
	{
		CheckConfiguration();
		CheckApplicationInstructions();
		CheckExternalTools();
		await CheckCliUpdateAsync();
		CheckWorkspaceReadme();
		CheckShortText();
		CheckSlashCommands();
		CheckSessionDetails();
		CheckWorkspaceChanges();
		CheckSessionSnapshot();
		CheckStorage();
		await CheckQuestionsAsync();
		await CheckEventsAsync();
		CheckHistoryAndStatus();
		Console.WriteLine("PASS configuration, history storage, questions, streaming events, usage, prompt navigation, and external tools");
	}

	/// <summary>
	/// What a turn changed on disk, against a real repository. The list
	/// has to come from the workspace rather than from what the agent
	/// said it did, and it has to be right about a file that was
	/// already modified before the turn began.
	/// </summary>
	private static void CheckWorkspaceChanges()
	{
		using var workspace = new TestWorkspace();
		var root = workspace.Workspace;
		if (Git(root, "init -q -b main") is null)
		{
			Console.WriteLine("SKIP workspace changes: git is not available");
			return;
		}
		Git(root, "config user.email fixture@example.invalid");
		Git(root, "config user.name Fixture");
		workspace.Write("workspace\\kept.txt", "one\n");
		workspace.Write("workspace\\edited.txt", "before\n");
		workspace.Write("workspace\\removed.txt", "gone soon\n");
		Git(root, "add -A");
		Git(root, "commit -q -m fixture");

		// A file dirtied before the turn must not be reported by it,
		// and its later edit must be reported against that dirty state.
		File.WriteAllText(Path.Combine(root, "kept.txt"), "one\ntwo\n");
		var anchor = WorkspaceChanges.Begin(root);
		Check.True(anchor is { HasDiffs: true }, "A repository workspace must anchor against a commit.");

		File.WriteAllText(Path.Combine(root, "edited.txt"), "after\n");
		File.WriteAllText(Path.Combine(root, "created.txt"), "new\n");
		File.Delete(Path.Combine(root, "removed.txt"));

		var changes = WorkspaceChanges.Since(anchor);
		var byPath = changes.ToDictionary(change => change.Path, change => change.Kind);
		Check.True(!byPath.ContainsKey("kept.txt"),
			"A file already modified before the turn is not the turn's doing: " + string.Join(", ", byPath.Keys));
		Check.Equal("modified", byPath.GetValueOrDefault("edited.txt"), "Report an edited file");
		Check.Equal("added", byPath.GetValueOrDefault("created.txt"), "Report a created file");
		Check.Equal("deleted", byPath.GetValueOrDefault("removed.txt"), "Report a deleted file");

		var diff = WorkspaceChanges.Diff(anchor, "edited.txt");
		Check.True(diff.Contains("-before") && diff.Contains("+after"),
			"A diff must show what the change actually was: " + diff);

		Check.True(WorkspaceChanges.Revert(anchor, "edited.txt"), "Reverting a tracked file must succeed.");
		Check.Equal("before\n", File.ReadAllText(Path.Combine(root, "edited.txt")).Replace("\r\n", "\n"),
			"Reverting puts the file back as it was when the turn began");
		Check.Equal(0, WorkspaceChanges.Since(anchor).Count(change => change.Path == "edited.txt"),
			"A reverted file is no longer a change");

		// Outside a repository there is nothing to diff against, but
		// the list of files is still worth having.
		using var plain = new TestWorkspace();
		plain.Write("workspace\\a.txt", "a");
		var loose = WorkspaceChanges.Begin(plain.Workspace);
		Check.True(loose is { HasDiffs: false }, "A workspace without Git anchors on file stamps instead.");
		File.WriteAllText(Path.Combine(plain.Workspace, "b.txt"), "b");
		Check.True(WorkspaceChanges.Since(loose).Any(change => change.Path == "b.txt" && change.Kind == "added"),
			"A workspace without Git still reports which files changed.");
		Check.Equal(string.Empty, WorkspaceChanges.Diff(loose, "b.txt"),
			"A workspace without Git offers no diff rather than a wrong one");
		Check.True(!WorkspaceChanges.Revert(loose, "b.txt"), "Nothing can be put back without an earlier copy.");

		Check.Equal(0, WorkspaceChanges.Since(null).Count, "No anchor means nothing to report");
		Check.True(WorkspaceChanges.Begin(Path.Combine(workspace.Root, "absent")) is null,
			"A workspace that is not there cannot be watched.");
		Console.WriteLine("PASS workspace changes anchored per turn, diffed, and reverted");

		static string? Git(string workspace, string arguments)
		{
			try
			{
				using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
				{
					FileName = "git",
					Arguments = arguments,
					WorkingDirectory = workspace,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false,
					CreateNoWindow = true,
				});
				if (process is null) return null;
				var output = process.StandardOutput.ReadToEnd();
				process.StandardError.ReadToEnd();
				process.WaitForExit(20_000);
				return output;
			}
			catch (Exception)
			{
				return null;
			}
		}
	}

	/// <summary>
	/// Typed commands. The risk worth checking is not that a command	/// fails to run, but that an ordinary message is mistaken for one
	/// and never reaches the model.
	/// </summary>
	private static void CheckSlashCommands()
	{
		Check.True(SlashCommands.Parse("/help") is { } help && help.Command.Name == "/help",
			"A bare command must be recognized.");
		Check.True(SlashCommands.Parse("  /HELP  ") is not null, "Commands ignore case and surrounding space.");
		Check.True(SlashCommands.Parse("/attach notes.txt") is { } attach && attach.Argument == "notes.txt",
			"Everything after the command is its argument.");

		Check.True(SlashCommands.Parse("/nonesuch") is null, "An unknown command is an ordinary message.");
		Check.True(SlashCommands.Parse("Look at /help in the docs") is null,
			"A message that mentions a command is not a command.");
		Check.True(SlashCommands.Parse("/help\r\nand then explain it") is null,
			"A command is one line, never the opening of a longer message.");
		Check.True(SlashCommands.Parse("") is null && SlashCommands.Parse(null) is null,
			"An empty prompt is not a command.");

		Check.True(SlashCommands.Suggest("/").Count == SlashCommands.All.Count, "A lone slash offers everything.");
		Check.Equal(1, SlashCommands.Suggest("/pa").Count, "Offer only what matches what is typed");
		Check.Equal("/past", SlashCommands.Suggest("/pa")[0].Name, "Complete the typed prefix");
		Check.Equal(0, SlashCommands.Suggest("/help").Count, "A command typed in full has nothing left to complete");
		Check.Equal(0, SlashCommands.Suggest("/attach notes.txt").Count,
			"Stop offering commands once an argument is being typed");
		Check.Equal(0, SlashCommands.Suggest("explain /help").Count, "Never offer commands mid-sentence");

		var listed = SlashCommands.HelpText();
		foreach (var command in SlashCommands.All)
		{
			Check.True(listed.Contains(command.Name, StringComparison.Ordinal),
				"The command list must name every command: " + command.Name);
			Check.True(command.Name.StartsWith(SlashCommands.Prefix, StringComparison.Ordinal)
				&& command.Name.Length > 1, "Every command needs a name after its prefix.");
			Check.True(SlashCommands.Parse(command.Name) is not null,
				"Every listed command must parse: " + command.Name);
		}

		var listing = NoticeFormatter.Listing("Commands", "/help  <b>");
		Check.True(listing.Rendered.Contains("&lt;b&gt;", StringComparison.Ordinal)
			&& listing.Rendered.Contains("kp-listing-body", StringComparison.Ordinal),
			"A listing keeps its columns in a pre block and escapes what it shows.");
	}

	/// <summary>
	/// What a session reports it is running with. The point of the
	/// listing is to find the one item that should not be there, so the
	/// check is that items are named and that an off switch is stated
	/// rather than shown as an empty list.
	/// </summary>
	private static void CheckSessionDetails()
	{
		var library = new CustomizationLibrary();
		library.Instructions[@"C:\kit\a.instructions.md"] =
			new CustomizationItem { FilePath = @"C:\kit\a.instructions.md", Name = "house-style", Enabled = true };
		library.Skills[@"C:\kit\skills\pdf\SKILL.md"] =
			new CustomizationItem { FilePath = @"C:\kit\skills\pdf\SKILL.md", Name = "pdf", Enabled = true };
		library.Skills[@"C:\kit\skills\off\SKILL.md"] =
			new CustomizationItem { FilePath = @"C:\kit\skills\off\SKILL.md", Name = "unused", Enabled = false };
		library.Agents[@"C:\kit\agents\duck.md"] =
			new CustomizationItem { FilePath = @"C:\kit\agents\duck.md", Name = "duck", Enabled = true };

		var options = new ChatSessionOptions
		{
			WorkspaceFolder = @"C:\work",
			Model = "a-model",
			Mode = "duck",
			Customizations = library,
		};

		var text = SessionDetails.Describe(options, "session-123", @"C:\app\turbopilot.instructions.md");
		Check.True(text.Contains("session-123") && text.Contains(@"C:\work"),
			"The listing must name the session and the workspace");
		Check.True(text.Contains("house-style") && text.Contains(@"C:\kit\a.instructions.md"),
			"An instruction must be named with the file it came from");
		Check.True(text.Contains("pdf") && !text.Contains("unused"),
			"Only what is enabled is in force, so only that is listed");
		Check.True(text.Contains("duck  (in use)"),
			"The agent the mode selected must say so; that is the one that changes the answers");
		Check.True(text.Contains(@"C:\app\turbopilot.instructions.md"),
			"The presentation instructions are part of what the session runs with");
		Check.True(text.Contains("None are enabled."), "An empty list says so rather than showing nothing");

		var off = SessionDetails.Describe(options with { ApplyInstructions = false, PreloadSkills = false },
			null, null);
		Check.True(off.Contains("Apply Instructions is off") && off.Contains("Preload Skills is off"),
			"A switch that is off must be stated, not shown as an empty list");
		Check.True(off.Contains("none") && off.Contains("not loaded"),
			"A session that has not started still describes itself");

		var autopilot = SessionDetails.Describe(options with { Mode = "Autopilot" }, null, null);
		Check.True(autopilot.Contains("Autopilot approves every request"),
			"The mode that grants everything must say so where permissions are read");
	}

	private static void CheckConfiguration()
	{		using var workspace = new TestWorkspace();
		var library = workspace.CreateLibrary();
		var serverFile = workspace.Write("servers.mcp.json", """
			{"servers":{"enabled":{"type":"http","url":"http://127.0.0.1:5000/mcp","headers":{"Authorization":"fixture-value"}},"no-tools":{"type":"http","url":"http://127.0.0.1:5000/mcp","tools":[]},"disabled":{"type":"unsupported"}}}
			""");
		library.McpServers["enabled"] = new() { Name = "enabled", Element = "enabled", FilePath = serverFile };
		library.McpServers["disabled"] = new() { Name = "disabled", Element = "disabled", FilePath = serverFile, Enabled = false };
		var options = new ChatSessionOptions
		{
			WorkspaceFolder = workspace.Workspace,
			Model = "test-model",
			ReasoningEffort = "high",
			Mode = "writer.agent",
			Customizations = library,
		};
		var config = new SessionConfig();
		SessionConfiguration.Apply(config, options, workspace.ApplicationInstructionsPath);
		var system = config.SystemMessage!.Content!;
		Check.True(system.Contains("ENABLED_INSTRUCTION_SENTINEL") && system.Contains("ENABLED_SKILL_SENTINEL"), "Enabled content must be preloaded.");
		Check.True(!system.Contains("DISABLED_"), "Disabled content must not be preloaded.");
		Check.True(system.Contains("**/*.cs"), "Instruction file scope must survive.");
		Check.Equal(false, config.EnableConfigDiscovery, "Automatic discovery must not undo the user's choices");
		Check.Equal(true, config.SkipCustomInstructions, "Unselected instructions must stay disabled");
		Check.Equal(false, config.EnableOnDemandInstructionDiscovery, "On-demand loading must honor instruction choices");
		Check.Equal(1, config.SkillDirectories!.Count, "Only enabled skill paths may be registered");
		Check.True(config.SkillDirectories[0].EndsWith("enabled-skill"), "Pass the selected skill folder, not its siblings.");
		Check.Equal("writer.agent", config.Agent, "Activate the selected agent");
		Check.Equal(1, config.CustomAgents!.Count, "Exclude disabled agents");
		Check.Equal("Writes fixture responses", config.CustomAgents[0].Description, "Parse multiline YAML");
		Check.Equal(0, config.CustomAgents[0].Tools!.Count, "Preserve an explicit empty tool allowlist");
		Check.Equal(1, config.CustomAgents[0].Skills!.Count, "Agents cannot preload disabled skills");
		Check.Equal("high", config.ReasoningEffort, "Apply reasoning effort");
		Check.Equal(1, config.McpServers!.Count, "Only enabled servers may be started");
		Check.Equal("*", config.McpServers["enabled"].Tools!.Single(), "Register all tools explicitly when no server filter is supplied");
		Check.Equal(0, McpConfig.ReadServerConfiguration(serverFile, "no-tools").Tools!.Count, "Preserve an explicit empty server tool filter");
		Check.True(!system.Contains("fixture-value"), "Server credentials must not enter the system prompt.");

		var resume = new ResumeSessionConfig();
		SessionConfiguration.Apply(resume, options with { ApplyInstructions = false, PreloadSkills = false }, workspace.ApplicationInstructionsPath);
		Check.True(!resume.SystemMessage!.Content!.Contains("_SENTINEL"), "Both loading switches must apply to resumed sessions.");
		Check.Equal(false, resume.EnableSkills, "Preload off must disable automatic skill loading");
		Check.Equal(0, resume.SkillDirectories!.Count, "Preload off must not register skill folders");
		Check.Equal(0, resume.CustomAgents![0].Skills!.Count, "Preload off must also apply to custom agents");
		Check.Throws<InvalidOperationException>(() => SessionConfiguration.Apply(new SessionConfig(), options with { Mode = "disabled-agent" }, workspace.ApplicationInstructionsPath));
		Check.Throws<InvalidOperationException>(() => SessionConfiguration.Apply(new SessionConfig(), options with { UseByok = true }, workspace.ApplicationInstructionsPath));

		var provider = new SessionConfig();
		SessionConfiguration.Apply(provider, options with
		{
			UseByok = true, ByokEndpoint = "http://127.0.0.1:5001/v1", ByokApiKey = "fixture-key", ContextWindowTokens = 32768,
		}, workspace.ApplicationInstructionsPath);
		Check.Equal("http://127.0.0.1:5001/v1", provider.Provider!.BaseUrl, "Use the selected endpoint");
		Check.Equal("fixture-key", provider.Provider.ApiKey, "Use the selected credential only for the provider");
		Check.True(provider.Provider.MaxPromptTokens == 32768, "Apply the selected context limit.");
		var malformed = workspace.Write("malformed.md", "---\nname: [broken\n---\nbody");
		Check.Throws<YamlDotNet.Core.YamlException>(() => FrontMatter.ReadDocument(malformed));
	}

	private static void CheckApplicationInstructions()
	{
		using var workspace = new TestWorkspace();
		var path = workspace.ApplicationInstructionsPath;
		var options = new ChatSessionOptions { Customizations = workspace.CreateLibrary() };
		var config = new SessionConfig();
		SessionConfiguration.Apply(config, options, path);
		var initial = File.ReadAllBytes(path);
		var content = config.SystemMessage!.Content!;
		Check.Equal(SystemMessageMode.Append, config.SystemMessage.Mode, "Append instead of replacing the runtime instructions");
		Check.True(content.Contains("Markdown") && content.Contains("mermaid") && content.Contains("![")
			&& content.Contains("kp-path:") && content.Contains("https://"), "Provision presentation guidance for all supported visuals");
		Check.True(content.IndexOf("TurboPilot application instructions") > content.IndexOf("ENABLED_INSTRUCTION_SENTINEL"),
			"Append app guidance after enabled instructions.");
		Check.True(!content.Contains("description: 'Presentation"), "Do not send instruction front matter.");
		SessionConfiguration.Apply(new SessionConfig(), options, path);
		Check.True(initial.SequenceEqual(File.ReadAllBytes(path)), "Do not rewrite an existing file.");

		File.WriteAllText(path, "USER_EDITED_APP_GUIDANCE");
		var resume = new ResumeSessionConfig();
		SessionConfiguration.Apply(resume, options with { ApplyInstructions = false, PreloadSkills = false }, path);
		Check.True(resume.SystemMessage!.Content!.Contains("USER_EDITED_APP_GUIDANCE")
			&& !resume.SystemMessage.Content.Contains("ENABLED_INSTRUCTION_SENTINEL"), "Reload user edits on resume, independently of workspace instruction loading.");
		SessionConfiguration.Apply(config, options, path);
		Check.True(config.SystemMessage!.Content!.Contains("USER_EDITED_APP_GUIDANCE"), "Reload edits for new sessions too.");
		File.WriteAllText(path, "");
		SessionConfiguration.Apply(config, options, path);
		Check.Equal("", File.ReadAllText(path), "An intentionally empty file must not restore the defaults.");
		File.WriteAllText(path, "---\ndescription: [broken\n---\nbody");
		Check.Throws<InvalidOperationException>(() => SessionConfiguration.Apply(config, options, path));
		using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
			Check.Throws<InvalidOperationException>(() => SessionConfiguration.Apply(config, options, path));
		File.Delete(path);
		SessionConfiguration.Apply(config, options, path);
		Check.True(initial.SequenceEqual(File.ReadAllBytes(path)), "Recreate missing defaults.");
	}

	private static void CheckExternalTools()
	{
		using var workspace = new TestWorkspace();
		var path = workspace.ScriptsPath;
		var scripts = ExternalTools.EnsureScripts(path);
		Check.Equal(path, scripts, "Use the supplied helper path");
		var initial = File.ReadAllBytes(path);
		Check.True(Encoding.UTF8.GetString(initial).Contains("function current_root"), "Provision the bundled helper functions.");
		File.WriteAllText(path, "function fixture_helper {}");
		ExternalTools.EnsureScripts(path);
		Check.Equal("function fixture_helper {}", File.ReadAllText(path), "Never overwrite an edited helper file");
		File.Delete(path);
		ExternalTools.EnsureScripts(path);
		Check.True(initial.SequenceEqual(File.ReadAllBytes(path)), "Recreate a deleted helper file from the defaults.");

		var quoted = Path.Combine(workspace.Root, "scripts", "o'brien.ps1");
		File.Copy(path, quoted);
		var shell = ExternalTools.BuildPowerShell(workspace.Workspace, quoted);
		Check.Equal($"-NoExit -Command \". '{quoted.Replace("'", "''")}'\"", shell.Arguments, "Dot the helper file, escaping quotes");
		Check.Equal(workspace.Workspace, shell.WorkingDirectory, "Open the shell in the workspace");
		Check.True(shell.UseShellExecute, "Let the shell own its own window.");
		Check.True(shell.FileName.EndsWith("pwsh.exe", StringComparison.OrdinalIgnoreCase)
			|| shell.FileName == "powershell.exe", "Prefer PowerShell 7 and fall back to Windows PowerShell.");
		Check.Equal("-NoExit", ExternalTools.BuildPowerShell(workspace.Workspace, null).Arguments,
			"Open a plain shell when there are no helper functions");
		Check.Equal("-NoExit", ExternalTools.BuildPowerShell(workspace.Workspace, Path.Combine(workspace.Root, "gone.ps1")).Arguments,
			"Open a plain shell when the helper file is missing");

		var explorer = ExternalTools.BuildExplorer(workspace.Workspace);
		Check.Equal("explorer.exe", explorer.FileName, "Open File Explorer");
		Check.Equal($"\"{workspace.Workspace}\"", explorer.Arguments, "Open Explorer on the workspace");
		var editor = ExternalTools.BuildVsCode(workspace.Workspace);
		Check.Equal("code", editor.FileName, "Resolve the VS Code launcher on PATH");
		Check.Equal($"\"{workspace.Workspace}\"", editor.Arguments, "Open VS Code on the workspace");
		Check.True(editor.UseShellExecute, "A PATH shim needs the shell to resolve it.");
	}

	private static void CheckWorkspaceReadme()
	{
		using var workspace = new TestWorkspace();
		Check.True(WorkspaceReadme.Find(workspace.Workspace) is null, "A workspace without a readme must offer nothing.");
		Check.True(WorkspaceReadme.Find(null) is null, "No workspace means no readme.");
		Check.True(WorkspaceReadme.Find(Path.Combine(workspace.Root, "gone")) is null, "A missing folder must not throw.");
		workspace.Write("workspace\\docs\\README.md", "nested");
		Check.True(WorkspaceReadme.Find(workspace.Workspace) is null, "Only the workspace root describes the project.");

		var text = workspace.Write("workspace\\README.txt", "plain");
		Check.Equal(text, WorkspaceReadme.Find(workspace.Workspace), "Fall back to a plain-text readme");
		var markdown = workspace.Write("workspace\\README.md", "markdown");
		Check.Equal(markdown, WorkspaceReadme.Find(workspace.Workspace), "Prefer the markdown readme");
		Check.True(WorkspaceReadme.Find(workspace.Workspace.ToUpperInvariant())?.EndsWith("README.md", StringComparison.Ordinal) == true,
			"Match the workspace path as Windows does, without regard to case.");

		Check.True(WorkspaceReadme.Question(markdown).Contains("README.md"), "Name the file being offered.");
		var prompt = WorkspaceReadme.Prompt(markdown);
		Check.True(prompt.Contains("README.md") && prompt.Contains("wait for my instructions")
			&& prompt.Contains("do not start any work yet"), "Ask only for orientation, then a stop.");
	}

	/// <summary>
	/// The shortening behind the session badge and the Past Sessions
	/// rows. Model IDs and descriptions are what drive a dialog past the
	/// width it was given, so the guarantee is a hard ceiling, not a
	/// tidier average.
	/// </summary>
	private static void CheckShortText()
	{
		Check.Equal("short", ShortText.Clip("  short  ", 20), "Leave text within budget alone, trimmed");
		Check.Equal("", ShortText.Clip("anything", 0), "A budget of nothing yields nothing");
		Check.Equal(10, ShortText.Clip(new string('x', 400), 10).Length, "Never exceed the budget, ellipsis included");
		Check.True(ShortText.Clip(new string('x', 400), 10).EndsWith("...", StringComparison.Ordinal), "Mark text that was cut.");
		Check.Equal("ab", ShortText.Clip("abcdef", 2), "A budget too small for an ellipsis just cuts");

		Check.Equal("qwen3-coder-30b:4",
			ShortText.Model("Qwen3-Coder-30B-A3B-Instruct-GGUF/qwen3-coder-30b:4"),
			"Keep the segment that distinguishes one model from another");
		Check.Equal("gpt-5", ShortText.Model("gpt-5"), "Leave an unqualified model alone");
		Check.Equal("publisher/", ShortText.Model("publisher/", 34), "A trailing separator is not a segment break");
		Check.Equal(34, ShortText.Model("vendor/" + new string('m', 200)).Length, "Clip the segment too");

		var record = new SessionRecord
		{
			SessionId = "workspace-" + new string('9', 60),
			Options = new ChatSessionOptions { Model = "registry.example.com/org/" + new string('m', 120) },
			Description = new string('d', 400),
		};
		Check.True(record.DisplayLabel.Length < 140, "A Past Sessions row must stay a predictable width.");
		record.Description = "";
		Check.True(record.DisplayLabel.Length < 90 && record.DisplayLabel.Contains("workspace-"),
			"A row without a description still names its session.");
	}

	/// <summary>
	/// The settings a session carries: the customization lists it ran
	/// with and the permissions in force, saved with the record and given
	/// back on resume. Without these a resumed session silently adopts
	/// whatever is configured at the moment it is reopened.
	/// </summary>
	private static void CheckSessionSnapshot()
	{
		using var workspace = new TestWorkspace();
		var library = new CustomizationLibrary();
		Check.True(!library.HasItems, "An empty library has nothing to restore.");
		var instruction = workspace.Write("instructions\\house.instructions.md", "House rules.");
		library.Instructions[instruction] = new() { Name = "house", FilePath = instruction, Enabled = false };
		Check.True(library.HasItems, "A library with an item has something to restore.");

		var options = new ChatSessionOptions { WorkspaceFolder = workspace.Workspace, Model = "test-model" };
		var record = workspace.Store.Create("snapshot-1", options, "");
		record.Customizations = library.Clone();
		record.Permissions = new PermissionScope
		{
			Folders = [new PermissionEntry { FolderPath = workspace.Root, Access = PermissionAccess.Read }],
			Operations = ["shell"],
		};
		workspace.Store.Save(record);

		var loaded = workspace.Store.Load("snapshot-1");
		Check.Equal(1, loaded.Customizations.Instructions.Count, "Save the customization lists with the session");
		Check.True(!loaded.Customizations.Instructions[instruction].Enabled, "Restore the enabled state, not just the item.");
		Check.Equal(workspace.Root, loaded.Permissions.Folders.Single().FolderPath, "Save the folder grants with the session");
		Check.Equal("shell", loaded.Permissions.Operations!.Single(), "Save the approved operations with the session");

		var restored = MainWindow.RestoreSessionOptions(loaded, new Settings());
		Check.Equal(1, restored.Customizations.Instructions.Count, "Resume on the session's own lists");
		var bare = workspace.Store.Create("snapshot-2", options, "");
		Check.True(!MainWindow.RestoreSessionOptions(bare, new Settings()).Customizations.HasItems,
			"A session saved without a snapshot must fall back to a fresh scan.");
	}

	/// <summary>
	/// The Copilot CLI update notice. It has to be right about being
	/// behind, because an update prompt for a current tool trains the
	/// user to ignore it, and it has to stay quiet about everything it
	/// cannot determine.
	/// </summary>
	private static async Task CheckCliUpdateAsync()
	{
		Check.True(CliUpdate.IsNewer("1.0.37", "1.0.41"), "A later patch is newer.");
		Check.True(CliUpdate.IsNewer("v1.0.37", "v1.1.0"), "A later minor is newer.");
		Check.True(CliUpdate.IsNewer("1.9.0", "2.0.0"), "A later major is newer.");
		Check.True(!CliUpdate.IsNewer("1.0.41", "1.0.41"), "The same version is not newer.");
		Check.True(!CliUpdate.IsNewer("1.0", "1.0.0"), "A missing part counts as zero.");
		Check.True(!CliUpdate.IsNewer("1.0.41", "1.0.9"), "A lower patch is not newer, digit by digit.");
		Check.True(!CliUpdate.IsNewer("1.0.41", "not-a-version"), "An unparseable tag must never claim to be newer.");
		Check.True(!CliUpdate.IsNewer("", "1.0.41"), "An unknown current version must never claim to be behind.");

		Check.Equal("Copilot CLI v1.0.37 -> v1.0.41 available. Open a Copilot terminal and run /update",
			CliUpdate.Notice("1.0.37", "v1.0.41"), "Name both versions and how to update");
		Check.True(CliUpdate.Notice("1.0.41", "1.0.41") is null, "Say nothing when the CLI is current.");

		CliUpdate.ResetForTesting();
		var lookups = 0;
		Task<string?> Latest(CancellationToken _) { lookups++; return Task.FromResult<string?>("v1.0.41"); }
		Check.True(await CliUpdate.CheckAsync("1.0.37", Latest) is not null, "Report a CLI that is behind.");
		Check.True(await CliUpdate.CheckAsync("1.0.37", Latest) is null, "Check at most once per run.");
		Check.Equal(1, lookups, "Ask GitHub once per run, not once per session");

		CliUpdate.ResetForTesting();
		Check.True(await CliUpdate.CheckAsync(null, Latest) is null, "Say nothing when the CLI reports no version.");
		Check.Equal(1, lookups, "An unknown version must not cost a lookup");
		CliUpdate.ResetForTesting();
		Check.True(await CliUpdate.CheckAsync("1.0.37", _ => Task.FromResult<string?>(null)) is null,
			"A failed lookup is silent, not an error.");
	}

	private static void CheckStorage()
	{
		using var workspace = new TestWorkspace();
		var options = new ChatSessionOptions
		{
			WorkspaceFolder = workspace.Workspace, Model = "test-model", Mode = "Plan",
			UseByok = true, ByokEndpoint = "http://127.0.0.1:5001/v1", ByokApiKey = "DO_NOT_PERSIST_FIXTURE_KEY",
		};
		var record = workspace.Store.Create("saved-123", options, "**You:** first\r\n");
		workspace.Store.AppendTranscript(record.SessionId, "streamed answer\r\n");
		record.Prompts.Add("first");
		record.AicNano = 13_000_000_000;
		record.ContextUsedTokens = 20480;
		record.ContextWindowTokens = 131072;
		workspace.Store.Save(record);
		var loaded = workspace.Store.Load(record.SessionId);
		Check.Equal(options.Mode, loaded.Options.Mode, "Restore the mode");
		Check.Equal("", loaded.Options.ByokApiKey, "Do not restore keys from transcript metadata");
		Check.Equal("first", loaded.Prompts.Single(), "Restore prompt history");
		Check.Equal(13_000_000_000L, loaded.AicNano, "Restore credit usage");
		Check.True(loaded.UsesApiKey, "Remember when resuming needs credentials without storing the credentials.");
		Check.Equal("**You:** first\r\nstreamed answer\r\n", workspace.Store.ReadTranscript(record.SessionId), "Restore exactly the saved transcript");
		Check.True(!File.ReadAllText(Path.Combine(workspace.HistoryDirectory, "saved-123.json")).Contains("DO_NOT_PERSIST"), "Do not persist provider credentials.");
		Check.True(!Directory.EnumerateFiles(workspace.HistoryDirectory, "*.tmp").Any(), "Atomic saves must not leave temporary files.");
		workspace.Write("history\\broken.json", "{broken");
		Check.Equal(1, workspace.Store.List(out var errors).Count, "A corrupt entry must not hide healthy sessions");
		Check.Equal(1, errors.Count, "Report corrupt history explicitly");
		workspace.Write("history\\null.json", """{"sessionId":"null","options":{},"description":null}""");
		Check.Equal(1, workspace.Store.List(out errors).Count, "Reject invalid null fields before opening a dialog");
		Check.Equal(2, errors.Count, "Report invalid metadata shapes");
		Check.Throws<ArgumentException>(() => workspace.Store.ReadTranscript("..\\escape"));
		Check.Throws<IOException>(() => workspace.Store.Create(record.SessionId, options, "replacement"));

		var settings = new Settings { ByokEndpoint = options.ByokEndpoint, ByokApiKey = "current-fixture-key" };
		Check.Equal("current-fixture-key", MainWindow.RestoreSessionOptions(loaded, settings).ByokApiKey, "Resume with the current matching credential");
		settings.ByokEndpoint = "http://127.0.0.1:5002/v1";
		Check.Throws<InvalidOperationException>(() => MainWindow.RestoreSessionOptions(loaded, settings));
		var publicEndpoint = new SessionRecord { SessionId = "public", Options = options, UsesApiKey = false };
		Check.Equal("", MainWindow.RestoreSessionOptions(publicEndpoint, settings).ByokApiKey, "Never reuse a credential belonging to another endpoint");
	}

	private static async Task CheckQuestionsAsync()
	{
		await using var chat = new ChatService();
		var first = chat.HandleUserInputRequestAsync(
			new UserInputRequest { Question = "First?", Choices = ["red", "blue"], AllowFreeform = false }, new());
		var second = chat.HandleUserInputRequestAsync(
			new UserInputRequest { Question = "Second?", Choices = ["small", "large"], AllowFreeform = true }, new());
		Check.True(chat.HasPendingQuestion, "Questions must remain pending until answered.");
		Check.True(!chat.Transcript.Contains("Second?"), "Present only the oldest unanswered question.");
		Check.Throws<InvalidOperationException>(() => chat.TryAnswerPending("invalid"));
		Check.True(!first.IsCompleted && !second.IsCompleted, "An invalid answer must not release a question.");
		Check.True(chat.TryAnswerPending("2"), "Answer the first question.");
		Check.Equal("blue", (await first).Answer, "Resolve numbered choices to their text");
		Check.Equal(false, (await first).WasFreeform, "A numbered choice is not freeform");
		Check.True(chat.Transcript.Contains("Second?"), "Show the next queued question.");
		chat.TryAnswerPending("custom size");
		Check.Equal("custom size", (await second).Answer, "Accept permitted freeform input");
		Check.Equal(true, (await second).WasFreeform, "Mark freeform answers correctly");
		Check.True(!chat.HasPendingQuestion, "Clear answered questions immediately.");
		Check.Equal("yes", ChatService.ResolveAnswer("1", ["yes", "no"], false, true).Answer, "Numbered permission approval");
		Check.Equal("no", ChatService.ResolveAnswer("deny", ["yes", "no"], false, true).Answer, "Permission denial alias");
		Check.Throws<InvalidOperationException>(() => ChatService.ResolveAnswer("maybe", ["yes", "no"], false, true));
		var plan = chat.HandleExitPlanModeRequestAsync(new ExitPlanModeRequest
		{
			Summary = "Implement the plan?", Actions = ["interactive", "autopilot"],
		}, new());
		chat.TryAnswerPending("1");
		Check.True((await plan).Approved, "Confirm a plan in chat.");
		Check.Equal("interactive", (await plan).SelectedAction, "Return the selected plan action");
		var stay = chat.HandleExitPlanModeRequestAsync(new ExitPlanModeRequest
		{
			Summary = "Keep planning?", Actions = ["interactive", "autopilot"],
		}, new());
		chat.TryAnswerPending("3");
		Check.True(!(await stay).Approved, "Allow remaining in Plan without executing.");
		var canceled = chat.HandleUserInputRequestAsync(new UserInputRequest { Question = "Canceled?" }, new());
		await chat.DisposeAsync();
		Check.Equal("(user canceled)", (await canceled).Answer, "Disposal must release question handlers");
	}

	private static async Task CheckEventsAsync()
	{
		await using var chat = new ChatService();
		var displayed = new StringBuilder();
		var errors = new List<string>();
		chat.TranscriptReceived += text => displayed.Append(text);
		chat.ErrorReceived += errors.Add;
		chat.HandleSessionEvent(SessionEvent.FromJson("""{"type":"assistant.turn_start","data":{"turnId":"turn-1"}}"""));
		chat.HandleSessionEvent(SessionEvent.FromJson("""{"type":"assistant.message_delta","data":{"messageId":"m1","deltaContent":"streamed"}}"""));
		chat.HandleSessionEvent(SessionEvent.FromJson("""{"type":"assistant.message","data":{"messageId":"m1","content":"streamed"}}"""));
		chat.HandleSessionEvent(SessionEvent.FromJson("""{"type":"assistant.message","data":{"messageId":"m2","content":"final only"}}"""));
		chat.HandleSessionEvent(SessionEvent.FromJson("""{"type":"assistant.message","agentId":"child","data":{"messageId":"m3","content":"do not duplicate subagent output"}}"""));
		Check.Equal("streamed\r\n\r\nfinal only\r\n\r\n", displayed.ToString(), "Deduplicate by message, not by turn");
		chat.HandleSessionEvent(SessionEvent.FromJson("""{"type":"assistant.usage","data":{"model":"fixture","inputTokens":20480,"copilotUsage":{"totalNanoAiu":13000000000}}}"""));
		chat.HandleSessionEvent(SessionEvent.FromJson("""{"type":"assistant.usage","agentId":"child","data":{"model":"fixture","initiator":"sub-agent","inputTokens":7,"copilotUsage":{"totalNanoAiu":1000000000}}}"""));
		Check.Equal(20480, chat.ContextUsedTokens, "Subagent usage must not replace the main context meter");
		Check.Equal(14d, chat.AicUsed, "Accumulate credits from all calls");
		chat.HandleSessionEvent(SessionEvent.FromJson("""{"type":"session.usage_info","data":{"currentTokens":21504,"tokenLimit":131072,"messagesLength":3}}"""));
		Check.Equal(21504, chat.ContextUsedTokens, "Use runtime context snapshots");
		Check.Equal(131072, chat.ContextWindowTokens, "Use the runtime context limit");
		chat.HandleSessionEvent(SessionEvent.FromJson("""{"type":"session.idle","data":{}}"""));
		Check.True(!chat.IsWorking, "Idle must end the working state.");
		chat.HandleSessionEvent(SessionEvent.FromJson("""{"type":"session.mcp_server_status_changed","data":{"serverName":"fixture","status":"failed","error":"Connection refused"}}"""));
		chat.HandleSessionEvent(SessionEvent.FromJson("""{"type":"session.mcp_servers_loaded","data":{"servers":[{"name":"fixture","status":"failed","error":"Connection refused"}]}}"""));
		Check.Equal(1, chat.Transcript.Split("Connection refused").Length - 1, "Surface server failures without duplicate notices");
		Check.Equal(0, errors.Count, "All event fixtures must be processed");
	}

	private static void CheckHistoryAndStatus()
	{
		var history = new PromptHistory();
		history.Add("first");
		history.Add("second");
		Check.Equal("second", history.NavigateBack("draft"), "Recall newest prompt");
		Check.Equal("first", history.NavigateBack("second"), "Recall older prompt");
		Check.True(!history.CanGoBack, "Disable the oldest boundary.");
		Check.Equal("second", history.NavigateForward(), "Move toward newer prompts");
		Check.Equal("draft", history.NavigateForward(), "Restore the unsent draft");
		Check.True(!history.CanGoForward, "Disable the draft boundary.");
		Check.Equal("Working.. 20/128K AiC=13", MainWindow.FormatStatus("Working..", 20480, 131072, 13, true), "Compact cloud status");
		Check.Equal("Ready.. 20/128K", MainWindow.FormatStatus("Ready..", 20480, 131072, 13, false), "Hide credits for other providers");
		Check.Equal("Waiting..", MainWindow.FormatStatus("Waiting..", 0, 0, 0, false), "Do not invent an unknown context limit");
		CheckTaskProgress();
	}

	/// <summary>
	/// Where the agent is in its plan. The status line is the only place
	/// progress can go in a narrow window, so the fragment has to be both
	/// short and correct about which step is current.
	/// </summary>
	private static void CheckTaskProgress()
	{
		Check.True(TaskProgress.From([]) is null, "No plan means no progress to report.");
		Check.True(TaskProgress.From([new("Only step", "pending")]) is null,
			"One step is not a plan worth a position.");

		var steps = new PlanStep[]
		{
			new("Reading the code", "done"),
			new("Writing tests", "in_progress"),
			new("Updating docs", "pending"),
		};
		var progress = TaskProgress.From(steps)!;
		Check.Equal(2, progress.Position, "The running step is the position");
		Check.Equal(3, progress.Total, "Count every step");
		Check.Equal("[2/3] Writing tests", progress.StatusFragment, "Say which step, out of how many, and its name");
		Check.Equal("Working.. [2/3] Writing tests 20/128K",
			MainWindow.FormatStatus("Working..", 20480, 131072, 0, false, progress),
			"Put progress before the numbers, where it is read");

		// Nothing marked running: the first unfinished step is where the
		// work stands, whether it is merely pending or actually blocked.
		Check.Equal(2, TaskProgress.From([new("A", "done"), new("B", "pending"), new("C", "pending")])!.Position,
			"Fall back to the first unfinished step");
		Check.Equal(2, TaskProgress.From([new("A", "done"), new("B", "blocked"), new("C", "pending")])!.Position,
			"A blocked step is still where the work stands");

		var finished = TaskProgress.From([new("A", "done"), new("B", "done")])!;
		Check.True(finished.Complete, "Recognize a finished plan.");
		Check.Equal("", finished.StatusFragment, "A finished plan is not where the user is");
		Check.Equal("Ready.. 20/128K", MainWindow.FormatStatus("Ready..", 20480, 131072, 0, false, finished),
			"Leave the status line alone once the plan is done");

		Check.True(TaskProgress.From([new("A", "done"), new(new string('t', 200), "in_progress")])!
			.StatusFragment.Length < 44, "A long step title must not push the status line out of the window.");

		var (text, rendered) = NoticeFormatter.Checklist(steps);
		Check.Equal("Plan:\r\n[x] Reading the code\r\n[>] Writing tests\r\n[ ] Updating docs", text,
			"Raw shows the plan as an ASCII checklist");
		Check.True(rendered.Contains("kp-plan-done") && rendered.Contains("kp-plan-running")
			&& rendered.Contains("kp-plan-pending"), "Rendered must mark each step with its state.");
		Check.True(NoticeFormatter.Checklist([new("<b>not markup</b>", "pending")]).Rendered
			.Contains("&lt;b&gt;not markup&lt;/b&gt;"), "A step title must never be read as markup.");
	}
}
