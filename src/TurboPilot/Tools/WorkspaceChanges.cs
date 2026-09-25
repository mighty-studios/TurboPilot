using System.Diagnostics;
using System.IO;

namespace TurboPilot.Tools;

/// <summary>One file the agent touched, and how.</summary>
internal sealed record WorkspaceChange(string Path, string Kind)
{
	/// <summary>The letter the card shows, in the shape a diff uses.</summary>
	internal string Mark => Kind switch
	{
		"added" => "+",
		"deleted" => "-",
		_ => "~",
	};
}

/// <summary>
/// Where the workspace stood when a turn began, so what the turn did to
/// it can be worked out when the turn ends.
/// </summary>
internal sealed class ChangeAnchor(string workspace, string? commit, IReadOnlySet<string> untracked, IReadOnlyDictionary<string, long>? stamps)
{
	internal string Workspace { get; } = workspace;

	/// <summary>
	/// The commit object holding the tracked files as they were. Null
	/// when the workspace is not a repository, in which case the file
	/// stamps stand in and no diff is available.
	/// </summary>
	internal string? Commit { get; } = commit;

	internal IReadOnlySet<string> Untracked { get; } = untracked;
	internal IReadOnlyDictionary<string, long>? Stamps { get; } = stamps;
	internal bool HasDiffs => Commit is not null;
}

/// <summary>
/// What a turn did to the files on disk.
///
/// The list is taken from the workspace itself rather than from the
/// tools the agent reported running. A tool can write a file the user
/// was never told about, and a reported edit can fail; only the disk
/// knows what actually happened.
///
/// In a repository the anchor is a commit object built by `git stash
/// create`, which records the working tree without altering it or the
/// index. Diffing against that gives exactly what the turn changed,
/// even for a file that was already modified before the turn began.
/// Outside a repository there is nothing to diff against, so the anchor
/// falls back to a size-and-time stamp per file: enough to say which
/// files changed, which is the part worth knowing.
/// </summary>
internal static class WorkspaceChanges
{
	internal const int MaxFiles = 20_000;
	internal const int MaxDiffLines = 400;
	private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);
	private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
		{ ".git", ".vs", ".vscode", ".idea", "bin", "obj", "node_modules", "packages", "TestResults" };

	/// <summary>Runs a git command in the workspace, or returns null.</summary>
	internal static Func<string, string, string?>? Runner { get; set; }

	/// <summary>
	/// Records the state of the workspace before a turn runs. Returns
	/// null when there is no workspace to watch.
	/// </summary>
	internal static ChangeAnchor? Begin(string? workspace)
	{
		if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace)) return null;
		workspace = Path.GetFullPath(workspace);

		if (Git(workspace, "rev-parse --is-inside-work-tree") is "true")
		{
			// An empty result means the tree matched the index and the
			// head, so the head itself is the right thing to diff from.
			var stash = Git(workspace, "stash create");
			var commit = string.IsNullOrWhiteSpace(stash) ? Git(workspace, "rev-parse HEAD") : stash;
			if (!string.IsNullOrWhiteSpace(commit))
				return new ChangeAnchor(workspace, commit.Trim(), Untracked(workspace), null);
		}
		return new ChangeAnchor(workspace, null, new HashSet<string>(StringComparer.OrdinalIgnoreCase), Stamps(workspace));
	}

	/// <summary>
	/// What has changed since the anchor was taken, newest listing
	/// first. Deletions are included: a file the agent removed is the
	/// change a user most wants to be told about.
	/// </summary>
	internal static IReadOnlyList<WorkspaceChange> Since(ChangeAnchor? anchor)
	{
		if (anchor is null) return [];
		return anchor.Commit is null ? StampChanges(anchor) : GitChanges(anchor);
	}

	private static IReadOnlyList<WorkspaceChange> GitChanges(ChangeAnchor anchor)
	{
		var changes = new List<WorkspaceChange>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var line in Lines(Git(anchor.Workspace, $"diff --name-status {anchor.Commit} --")))
		{
			var tab = line.IndexOf('\t');
			if (tab <= 0) continue;
			var path = line[(tab + 1)..].Trim();
			// A rename reports both names; the new one is what exists now.
			var lastTab = path.LastIndexOf('\t');
			if (lastTab >= 0) path = path[(lastTab + 1)..];
			if (path.Length == 0 || !seen.Add(path)) continue;
			changes.Add(new WorkspaceChange(path, line[0] switch
			{
				'A' => "added",
				'D' => "deleted",
				_ => "modified",
			}));
		}

		// A file created during the turn is untracked, so it is absent
		// from a commit object and has to be found by comparison.
		foreach (var path in Untracked(anchor.Workspace))
			if (!anchor.Untracked.Contains(path) && seen.Add(path))
				changes.Add(new WorkspaceChange(path, "added"));
		return changes;
	}

	private static IReadOnlyList<WorkspaceChange> StampChanges(ChangeAnchor anchor)
	{
		var before = anchor.Stamps ?? new Dictionary<string, long>();
		var after = Stamps(anchor.Workspace);
		var changes = new List<WorkspaceChange>();
		foreach (var (path, stamp) in after)
		{
			if (!before.TryGetValue(path, out var was))
				changes.Add(new WorkspaceChange(path, "added"));
			else if (was != stamp)
				changes.Add(new WorkspaceChange(path, "modified"));
		}
		foreach (var path in before.Keys)
			if (!after.ContainsKey(path))
				changes.Add(new WorkspaceChange(path, "deleted"));
		return changes;
	}

	/// <summary>
	/// The change made to one file, as a unified diff. Empty when the
	/// workspace is not a repository, since there is nothing to compare
	/// against.
	/// </summary>
	internal static string Diff(ChangeAnchor? anchor, string path)
	{
		if (anchor?.Commit is null) return string.Empty;
		var diff = Git(anchor.Workspace, $"diff --no-color {anchor.Commit} -- \"{path}\"");
		// An untracked file has no counterpart in the commit object, so
		// git compares it against nothing and reports nothing.
		if (string.IsNullOrWhiteSpace(diff))
			diff = Git(anchor.Workspace, $"diff --no-color --no-index -- /dev/null \"{path}\"");
		return Bound(diff);
	}

	/// <summary>
	/// Opens the user's configured diff tool on one file. Whatever they
	/// already set up for `git difftool` is what opens, because a tool
	/// chosen once should not have to be chosen again here.
	/// </summary>
	internal static bool OpenDiffTool(ChangeAnchor? anchor, string path)
	{
		if (anchor?.Commit is null) return false;
		try
		{
			Process.Start(new ProcessStartInfo
			{
				FileName = "git",
				Arguments = $"difftool --no-prompt {anchor.Commit} -- \"{path}\"",
				WorkingDirectory = anchor.Workspace,
				UseShellExecute = true,
			})?.Dispose();
			return true;
		}
		catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
		{
			return false;
		}
	}

	/// <summary>Puts one file back as it was when the turn began.</summary>
	internal static bool Revert(ChangeAnchor? anchor, string path)
	{
		if (anchor?.Commit is null) return false;
		return Git(anchor.Workspace, $"checkout {anchor.Commit} -- \"{path}\"") is not null;
	}

	/// <summary>The full path of a listed change, for opening it.</summary>
	internal static string FullPath(ChangeAnchor anchor, string path) =>
		Path.GetFullPath(Path.Combine(anchor.Workspace, path.Replace('/', Path.DirectorySeparatorChar)));

	private static HashSet<string> Untracked(string workspace)
	{
		var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var line in Lines(Git(workspace, "ls-files --others --exclude-standard")))
			if (line.Trim() is { Length: > 0 } path)
				paths.Add(path);
		return paths;
	}

	/// <summary>
	/// Size and write time per file, bounded so a very large tree costs
	/// a fixed amount rather than an unbounded one.
	/// </summary>
	private static Dictionary<string, long> Stamps(string workspace)
	{
		var stamps = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
		var folders = new Stack<string>();
		folders.Push(workspace);
		while (folders.Count > 0 && stamps.Count < MaxFiles)
		{
			var folder = folders.Pop();
			try
			{
				foreach (var file in new DirectoryInfo(folder).EnumerateFiles())
				{
					stamps[Path.GetRelativePath(workspace, file.FullName).Replace('\\', '/')] =
						file.Length ^ file.LastWriteTimeUtc.Ticks;
					if (stamps.Count >= MaxFiles) break;
				}
				foreach (var child in new DirectoryInfo(folder).EnumerateDirectories())
					if (!SkippedFolders.Contains(child.Name) && !child.Attributes.HasFlag(FileAttributes.ReparsePoint))
						folders.Push(child.FullName);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// An unreadable folder simply reports no changes.
			}
		}
		return stamps;
	}

	private static string Bound(string? diff)
	{
		if (string.IsNullOrWhiteSpace(diff)) return string.Empty;
		var lines = diff.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
		return lines.Length <= MaxDiffLines
			? string.Join("\r\n", lines)
			: string.Join("\r\n", lines[..MaxDiffLines]) + "\r\n... " + (lines.Length - MaxDiffLines) + " more lines";
	}

	private static string[] Lines(string? text) =>
		string.IsNullOrWhiteSpace(text) ? [] : text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

	/// <summary>
	/// Runs one git command and returns its output, or null when git is
	/// missing or the command failed. Nothing here is important enough
	/// to interrupt a session over.
	/// </summary>
	private static string? Git(string workspace, string arguments)
	{
		if (Runner is { } runner) return runner(workspace, arguments);
		try
		{
			using var process = Process.Start(new ProcessStartInfo
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
			if (!process.WaitForExit(Patience))
			{
				process.Kill(entireProcessTree: true);
				return null;
			}
			// A diff reports a difference with exit code 1, which is not
			// a failure, so output is preferred over the code.
			return output.Length > 0 || process.ExitCode == 0 ? output.TrimEnd('\r', '\n') : null;
		}
		catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
		{
			return null;
		}
	}
}
