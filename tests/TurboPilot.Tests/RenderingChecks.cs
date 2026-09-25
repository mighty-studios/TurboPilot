using System.IO;
using System.Text.Json;
using TurboPilot.Rendering;
using TurboPilot.Tools;

namespace TurboPilot.Tests;

internal static class RenderingChecks
{
	public static void Run()
	{
		using var workspace = new TestWorkspace();
		var readme = workspace.Write("workspace\\README.md", "readme");
		var deep = workspace.Write("workspace\\src\\app\\Deep.cs", "deep");
		workspace.Write("workspace\\src\\a\\dup.txt", "a");
		var second = workspace.Write("workspace\\src\\b\\dup.txt", "b");
		workspace.Write("workspace\\bin\\Debug\\Only.dll", "build output");
		var image = Path.Combine(workspace.Workspace, "preview.png");
		File.WriteAllBytes(image, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII="));
		var outside = workspace.Write("outside.png", "not an image");
		var index = new WorkspaceFileIndex(workspace.Workspace);
		var notes = new List<string>();
		string Format(string text) => OutputFormatter.Apply(text, workspace.Workspace, index, notes.Add);
		static string Link(string path) => "kp-path:" + Uri.EscapeDataString(path);

		var prepared = Format("See README.md, `src\\app\\Deep.cs`, and preview.png.");
		Check.True(prepared.Contains(Link(readme)) && prepared.Contains("[`src\\app\\Deep.cs`](" + Link(deep) + ")"),
			"Link workspace-relative references and keep code-formatted labels.");
		Check.True(prepared.Contains("data:image/png;base64,"), "Preview a referenced image.");
		Check.True(Format("Open Deep.cs next.").Contains(Link(deep)), "Link a bare name found once in the workspace.");
		Check.True(!Format("Compare the dup.txt files.").Contains("kp-path:"), "Leave an ambiguous name unlinked.");
		Check.True(Format("Edit b/dup.txt now.").Contains(Link(second)), "Use a partial path to choose between matches.");
		Check.True(!Format("Rebuild Only.dll first.").Contains("kp-path:"), "Ignore build output folders.");
		Check.True(!Format("Not ../README.md here.").Contains("kp-path:"), "Do not search upward for parent references.");
		var protectedText = $"[existing](https://example.com/README.md) https://example.com/README.md\r\n```\r\nREADME.md\r\n```\r\n~~~\r\nDeep.cs\r\n~~~\r\n{outside}";
		var kept = Format(protectedText);
		Check.True(kept.Contains("[existing](https://example.com/README.md)") && kept.Contains("```\r\nREADME.md\r\n```")
			&& kept.Contains("~~~\r\nDeep.cs\r\n~~~"), "Leave existing links, URLs, and code as written.");
		Check.True(!kept.Contains(Uri.EscapeDataString(outside)), "Do not link files outside the permitted scope.");
		Check.Equal(0, notes.Count, "Readable files must not produce notices");
		Console.WriteLine("PASS rule-based Rendered links, image previews, workspace name lookup, and protected regions");

		CheckNotices();
	}

	private static void CheckNotices()
	{
		var (questionText, questionRendered) = NoticeFormatter.Question(
			"Pick a **plan**", ["Ship it", "Keep planning"], allowFreeform: true);
		Check.True(questionText.StartsWith("Question: Pick a **plan**", StringComparison.Ordinal)
			&& questionText.Contains("1. Ship it  |  2. Keep planning")
			&& questionText.Contains("(Reply with an option number, its text, or your own answer and press Send.)"),
			"Keep the plain question line in the Raw tab.");
		Check.True(questionRendered.StartsWith("<div class=\"kp-card kp-question\">", StringComparison.Ordinal)
			&& questionRendered.EndsWith("</div>", StringComparison.Ordinal),
			"Frame a question as a card.");
		Check.True(questionRendered.Contains("</div>\r\n\r\nPick a **plan**\r\n\r\n"),
			"Leave the question body as markdown, set off by blank lines.");
		Check.True(questionRendered.Contains("<ol><li>Ship it</li><li>Keep planning</li></ol>"),
			"List the choices as an ordered list.");

		var (permissionText, permissionRendered) = NoticeFormatter.Permission("shell", "dir <C:\\a & b>");
		Check.True(permissionText.StartsWith("Permission requested - shell: dir <C:\\a & b>", StringComparison.Ordinal),
			"Keep the plain permission line in the Raw tab.");
		Check.True(permissionRendered.Contains("<code>dir &lt;C:\\a &amp; b&gt;</code>"),
			"Escape the requested operation and show it as code.");
		Check.True(!permissionRendered.Contains("<C:"), "A command can never be read as markup.");

		var (statusText, statusRendered) = NoticeFormatter.Status("error", "MCP <server> failed");
		Check.Equal("[error] MCP <server> failed", statusText, "Keep the plain status line in the Raw tab");
		Check.True(statusRendered.Contains("class=\"kp-status kp-status-error\"")
			&& statusRendered.Contains("<span class=\"kp-status-tag\">error</span>")
			&& statusRendered.Contains("MCP &lt;server&gt; failed"),
			"Tag and escape a status line.");

		var (bannerText, bannerRendered) = NoticeFormatter.Banner("Session 1 | model | mode");
		Check.Equal("--- Session 1 | model | mode ---", bannerText, "Keep the plain banner in the Raw tab");
		Check.Equal("<div class=\"kp-banner\">Session 1 | model | mode</div>", bannerRendered, "Frame a banner");
		Console.WriteLine("PASS notice cards, status lines, and banners with escaped content");

		CheckToolDetail();
	}

	/// <summary>
	/// Tool calls in the transcript. What matters is that the headline
	/// says what the tool was actually asked to do, that the detail is
	/// bounded no matter what the tool was handed, and that nothing a
	/// tool touched can be read as markup.
	/// </summary>
	private static void CheckToolDetail()
	{
		static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

		var shell = ToolDetail.Describe("powershell", Args("""{"command":"cd D:\\repo && git status"}"""), "git status");
		Check.Equal("git status", shell.Headline, "Prefer the command the shell driver will actually run");
		Check.True(shell.Body.Contains("command: cd D:\\repo && git status"),
			"The full arguments stay available behind the headline.");

		var read = ToolDetail.Describe("view", Args("""{"path":"src\\App.cs","view_range":[1,40]}"""), null);
		Check.Equal("src\\App.cs", read.Headline, "Fall back to the path when there is no command");
		Check.True(read.Body.Contains("view_range: 1, 40"), "An array argument reads as a list.");

		var edit = ToolDetail.Describe("edit",
			Args("""{"path":"a.cs","old_str":"var x = 1;","new_str":"var x = 2;"}"""), null);
		Check.True(edit.Body.Contains("- var x = 1;") && edit.Body.Contains("+ var x = 2;"),
			"An edit reads as the change it makes, not as two quoted blobs.");

		var wide = ToolDetail.Describe("powershell", Args($$"""{"command":"{{new string('x', 400)}}"}"""), null);
		Check.True(wide.Headline.Length <= ToolDetail.HeadlineBudget, "A headline stays on one line.");
		var tall = ToolDetail.Describe("view",
			Args($$"""{"note":"{{string.Join("\\n", Enumerable.Repeat("line", 500))}}"}"""), null);
		Check.True(tall.Body.Length <= ToolDetail.BodyBudget + 8, "The detail block is bounded.");

		var multiline = ToolDetail.Describe("powershell", Args("""{"command":"one\n  two"}"""), null);
		Check.Equal("one two", multiline.Headline, "A headline collapses the whitespace in its value");

		Check.Equal(string.Empty, ToolDetail.Describe("ask_user", null, null).Headline,
			"A tool called with nothing reports nothing rather than guessing");
		Check.Equal("Failed.", ToolDetail.Outcome(false, null, "ignored"),
			"A failure with no message still says it failed");
		Check.Equal("Boom", ToolDetail.Outcome(false, "Boom", null), "A failure reports its message");
		Check.Equal("done", ToolDetail.Outcome(true, null, "done"), "Success reports what the tool returned");

		var running = NoticeFormatter.Tool("powershell", "git status", "command: git status");
		Check.Equal("[tool] powershell  git status", running.Text, "Keep one plain tool line in the Raw tab");
		Check.True(running.Rendered.Contains("kp-tool-running") && running.Rendered.Contains("<details><summary>"),
			"A running tool is a card whose detail is shut until asked for.");
		Check.True(!running.Rendered.Contains("<details open"), "Tool detail must never open itself.");

		var failed = NoticeFormatter.Tool("powershell", "rm <x>", "command: rm <x>", "failed", "No such <file>");
		Check.True(failed.Rendered.Contains("kp-tool-failed")
			&& failed.Rendered.Contains("rm &lt;x&gt;")
			&& failed.Rendered.Contains("No such &lt;file&gt;"),
			"A failed tool is marked as such and cannot inject markup.");

		var bare = NoticeFormatter.Tool("ask_user", "", "", "ok", "");
		Check.Equal("[tool] ask_user", bare.Text, "A tool with nothing to show still names itself");
		Check.True(bare.Rendered.Contains("No detail was reported."),
			"An empty disclosure says so rather than opening onto nothing.");
		Console.WriteLine("PASS tool calls summarized, bounded, escaped, and shut by default");

		CheckChangeCards();
	}

	/// <summary>
	/// The Changes card and the diff it opens. What matters is that a
	/// path can never be read as markup or as a link to somewhere else,
	/// and that a very long list stays a summary.
	/// </summary>
	private static void CheckChangeCards()
	{
		var changes = new List<WorkspaceChange>
		{
			new("src/App.cs", "modified"),
			new("src/New <x>.cs", "added"),
			new("src/Old.cs", "deleted"),
		};
		var (text, rendered) = NoticeFormatter.Changes(changes);
		Check.Equal("Changes (3)\r\n~ src/App.cs\r\n+ src/New <x>.cs\r\n- src/Old.cs", text,
			"Keep a plain list of changed files in the Raw tab");
		Check.True(rendered.Contains("kp-change-modified") && rendered.Contains("kp-change-added")
			&& rendered.Contains("kp-change-deleted"), "Mark each file by what happened to it.");
		Check.True(rendered.Contains("New &lt;x&gt;.cs"), "A path can never be read as markup.");
		Check.True(rendered.Contains("kp-act:diff%7Csrc%2FApp.cs"),
			"Clicking a path asks for its diff, with the path encoded into the link.");
		Check.True(rendered.Contains("kp-act:revert%7Csrc%2FApp.cs") && rendered.Contains("kp-act:tool%7Csrc%2FApp.cs"),
			"Offer the diff tool and the undo beside each file.");
		Check.True(rendered.Contains("Review all 3 files"), "Offer the whole session from the card.");

		var many = Enumerable.Range(0, 40).Select(index => new WorkspaceChange("f" + index + ".cs", "modified")).ToList();
		var (manyText, manyRendered) = NoticeFormatter.Changes(many);
		Check.Equal(10, manyRendered.Split("kp-change-row kp-change-").Length - 1, "A long list is capped rather than dumped");
		Check.True(manyText.Contains("... 30 more") && manyRendered.Contains("Review all 40 files"),
			"A capped list says how much it is not showing.");

		var (diffText, diffRendered) = NoticeFormatter.Diff("src/App.cs",
			"@@ -1 +1 @@\n-var x = <1>;\n+var x = 2;\n unchanged");
		Check.True(diffText.StartsWith("--- src/App.cs ---", StringComparison.Ordinal), "Name the file in the Raw tab.");
		Check.True(diffRendered.Contains("kp-diff-hunk") && diffRendered.Contains("kp-diff-del")
			&& diffRendered.Contains("kp-diff-add") && diffRendered.Contains("kp-diff-ctx"),
			"Color a diff a line at a time rather than making the reader count leading characters.");
		Check.True(diffRendered.Contains("var x = &lt;1&gt;;"), "A diff can never be read as markup.");
		Check.True(NoticeFormatter.Diff("src/App.cs", "").Text.Contains("No change to show"),
			"An empty diff says so.");
		Console.WriteLine("PASS change cards listing, capping, linking, and escaping");
	}
}
