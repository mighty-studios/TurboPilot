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
/// One repository a turn is measured against: its top folder, where
/// that sits in the workspace, and the commit object holding its files
/// as they were when the turn began.
/// </summary>
internal sealed record RepositoryAnchor(string Root, string Prefix, string Commit, IReadOnlySet<string> Untracked)
{
	/// <summary>
	/// A listed path as the repository names it. Listed paths are
	/// relative to the workspace, which is the repository's parent when
	/// the workspace is a folder holding checkouts.
	/// </summary>
	internal string Inner(string path) => path[Prefix.Length..];
}

/// <summary>
/// Where the workspace stood when a turn began, so what the turn did to
/// it can be worked out when the turn ends.
/// </summary>
internal sealed class ChangeAnchor(string workspace, IReadOnlyList<RepositoryAnchor> repositories, IReadOnlyDictionary<string, long>? stamps)
{
	internal string Workspace { get; } = workspace;

	/// <summary>
	/// The repositories whose history the turn is measured against: the
	/// workspace's own, or the ones found inside it.
	/// </summary>
	internal IReadOnlyList<RepositoryAnchor> Repositories { get; } = repositories;

	/// <summary>
	/// Size and write time for each file in no repository, which is all
	/// that can be known about a file with no history. Null when the
	/// workspace is itself a repository.
	/// </summary>
	internal IReadOnlyDictionary<string, long>? Stamps { get; } = stamps;

	/// <summary>
	/// The repository holding a listed path, or null when the file is in
	/// none. Repositories found inside a workspace never nest, because
	/// the search does not look inside one it has found, so the first
	/// match is the only one.
	/// </summary>
	internal RepositoryAnchor? RepositoryFor(string path) =>
		Repositories.FirstOrDefault(repository => path.StartsWith(repository.Prefix, StringComparison.OrdinalIgnoreCase));

	/// <summary>
	/// Whether an earlier copy of the file was kept, so it can be
	/// diffed, compared and put back.
	/// </summary>
	internal bool CanDiff(string path) => RepositoryFor(path) is not null;
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
///
/// A workspace that is not a repository is often the folder above one
/// or more checkouts, so the repositories inside it are found and each
/// is anchored the same way: a file in any of them keeps its history.
/// Only a file in no repository falls back to a size-and-time stamp,
/// which is enough to say that it changed, the part worth knowing.
/// </summary>
internal static class WorkspaceChanges
{
	internal const int MaxFiles = 20_000;
	internal const int MaxDiffLines = 400;

	/// <summary>
	/// Repositories anchored inside one workspace. Each costs a few git
	/// runs at every turn, so a folder of many checkouts is bounded, and
	/// the ones past the bound are watched by stamp.
	/// </summary>
	internal const int MaxRepositories = 16;
	private const int GitRunsAtOnce = 4;
	private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);
	private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
		{ ".git", ".vs", ".vscode", ".idea", "bin", "obj", "node_modules", "packages", "TestResults" };

	/// <summary>
	/// Runs a git command in a folder, the workspace or a repository
	/// inside it, or returns null.
	/// </summary>
	internal static Func<string, string, string?>? Runner { get; set; }

	/// <summary>
	/// Records the state of the workspace before a turn runs. Returns
	/// null when there is no workspace to watch.
	/// </summary>
	internal static ChangeAnchor? Begin(string? workspace)
	{
		if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace)) return null;
		workspace = Path.GetFullPath(workspace);

		if (Git(workspace, "rev-parse --is-inside-work-tree") is "true" && Anchor(workspace, workspace) is { } own)
			return new ChangeAnchor(workspace, [own], null);

		var found = new List<string>();
		var stamps = Stamps(workspace, Roots([]), found);
		found.Sort(StringComparer.OrdinalIgnoreCase);
		var repositories = found.AsParallel().AsOrdered().WithDegreeOfParallelism(GitRunsAtOnce)
			.Select(root => Anchor(workspace, root)).OfType<RepositoryAnchor>().ToList();
		// A folder that looked like a repository but has nothing to
		// anchor to, such as one before its first commit, is watched by
		// stamp like any other folder.
		if (repositories.Count < found.Count)
			stamps = Stamps(workspace, Roots(repositories), null);
		return new ChangeAnchor(workspace, repositories, stamps);
	}

	/// <summary>
	/// Records one repository as it stands. Null when there is no commit
	/// to diff from, as before the first one is made.
	/// </summary>
	private static RepositoryAnchor? Anchor(string workspace, string root)
	{
		// An empty result means the tree matched the index and the
		// head, so the head itself is the right thing to diff from.
		var stash = Git(root, "stash create");
		var commit = string.IsNullOrWhiteSpace(stash) ? Git(root, "rev-parse --verify -q HEAD") : stash;
		if (string.IsNullOrWhiteSpace(commit)) return null;
		var prefix = string.Equals(root, workspace, StringComparison.OrdinalIgnoreCase)
			? string.Empty
			: Path.GetRelativePath(workspace, root).Replace('\\', '/') + "/";
		return new RepositoryAnchor(root, prefix, commit.Trim(), Untracked(root));
	}

	/// <summary>
	/// What has changed since the anchor was taken, newest listing
	/// first. Deletions are included: a file the agent removed is the
	/// change a user most wants to be told about.
	/// </summary>
	internal static IReadOnlyList<WorkspaceChange> Since(ChangeAnchor? anchor)
	{
		if (anchor is null) return [];
		var changes = anchor.Stamps is null ? new List<WorkspaceChange>() : StampChanges(anchor);
		changes.AddRange(anchor.Repositories.AsParallel().AsOrdered().WithDegreeOfParallelism(GitRunsAtOnce)
			.SelectMany(GitChanges));
		return changes;
	}

	/// <summary>
	/// What changed in one repository, named from the workspace so the
	/// list reads the same whichever repository a file is in.
	/// </summary>
	private static List<WorkspaceChange> GitChanges(RepositoryAnchor repository)
	{
		var changes = new List<WorkspaceChange>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		// Git names a change from the top of the checkout, but reads a
		// path handed to it from the folder it runs in. A workspace
		// below the top would be listed paths it cannot act on, and
		// changes above it, so the list is kept relative to that folder.
		foreach (var line in Lines(Git(repository.Root, $"diff --name-status --relative {repository.Commit} --")))
		{
			var tab = line.IndexOf('\t');
			if (tab <= 0) continue;
			var path = line[(tab + 1)..].Trim();
			// A rename reports both names; the new one is what exists now.
			var lastTab = path.LastIndexOf('\t');
			if (lastTab >= 0) path = path[(lastTab + 1)..];
			if (path.Length == 0 || !seen.Add(path)) continue;
			changes.Add(new WorkspaceChange(repository.Prefix + path, line[0] switch
			{
				'A' => "added",
				'D' => "deleted",
				_ => "modified",
			}));
		}

		// A file created during the turn is untracked, so it is absent
		// from a commit object and has to be found by comparison.
		foreach (var path in Untracked(repository.Root))
			if (!repository.Untracked.Contains(path) && seen.Add(path))
				changes.Add(new WorkspaceChange(repository.Prefix + path, "added"));
		return changes;
	}

	private static List<WorkspaceChange> StampChanges(ChangeAnchor anchor)
	{
		var before = anchor.Stamps ?? new Dictionary<string, long>();
		var after = Stamps(anchor.Workspace, Roots(anchor.Repositories), null);
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
	/// file is in no repository, since there is nothing to compare it
	/// against.
	/// </summary>
	internal static string Diff(ChangeAnchor? anchor, string path)
	{
		if (anchor?.RepositoryFor(path) is not { } repository) return string.Empty;
		var inner = repository.Inner(path);
		var diff = Git(repository.Root, $"diff --no-color {repository.Commit} -- \"{inner}\"");
		// An untracked file has no counterpart in the commit object, so
		// git compares it against nothing and reports nothing.
		if (string.IsNullOrWhiteSpace(diff))
			diff = Git(repository.Root, $"diff --no-color --no-index -- /dev/null \"{inner}\"");
		return Bound(diff);
	}

	/// <summary>
	/// The command handing one file to the user's diff tool, run in the
	/// repository holding the file so git finds the earlier copy in that
	/// repository's history. Null when the file is in no repository.
	/// </summary>
	internal static ProcessStartInfo? DiffToolCommand(ChangeAnchor? anchor, string path) =>
		anchor?.RepositoryFor(path) is { } repository
			? new ProcessStartInfo
			{
				FileName = "git",
				Arguments = $"difftool --no-prompt {repository.Commit} -- \"{repository.Inner(path)}\"",
				WorkingDirectory = repository.Root,
				UseShellExecute = true,
			}
			: null;

	/// <summary>
	/// Opens the user's configured diff tool on one file. Whatever they
	/// already set up for `git difftool` is what opens, because a tool
	/// chosen once should not have to be chosen again here.
	/// </summary>
	internal static bool OpenDiffTool(ChangeAnchor? anchor, string path)
	{
		if (DiffToolCommand(anchor, path) is not { } command) return false;
		try
		{
			Process.Start(command)?.Dispose();
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
		if (anchor?.RepositoryFor(path) is not { } repository) return false;
		return Git(repository.Root, $"checkout {repository.Commit} -- \"{repository.Inner(path)}\"") is not null;
	}

	/// <summary>The full path of a listed change, for opening it.</summary>
	internal static string FullPath(ChangeAnchor anchor, string path) =>
		Path.GetFullPath(Path.Combine(anchor.Workspace, path.Replace('/', Path.DirectorySeparatorChar)));

	private static HashSet<string> Untracked(string root)
	{
		var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var line in Lines(Git(root, "ls-files --others --exclude-standard")))
			if (line.Trim() is { Length: > 0 } path)
				paths.Add(path);
		return paths;
	}

	private static HashSet<string> Roots(IEnumerable<RepositoryAnchor> repositories) =>
		new(repositories.Select(repository => repository.Root), StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Size and write time per file, bounded so a very large tree costs
	/// a fixed amount rather than an unbounded one. The repositories in
	/// <paramref name="anchored"/> are left out, since their history
	/// already answers for their files. When <paramref name="found"/> is
	/// given, the walk is also a search: a folder at the top of a
	/// repository is listed there and not walked into.
	/// </summary>
	private static Dictionary<string, long> Stamps(string workspace, IReadOnlySet<string> anchored, List<string>? found)
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
				{
					if (SkippedFolders.Contains(child.Name) || child.Attributes.HasFlag(FileAttributes.ReparsePoint)
						|| anchored.Contains(child.FullName))
						continue;
					// A submodule or an extra worktree marks its top with a
					// .git file rather than a folder, so either counts.
					if (found is { Count: < MaxRepositories } && Path.Exists(Path.Combine(child.FullName, ".git")))
						found.Add(child.FullName);
					else
						folders.Push(child.FullName);
				}
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
	private static string? Git(string folder, string arguments)
	{
		if (Runner is { } runner) return runner(folder, arguments);
		try
		{
			using var process = Process.Start(new ProcessStartInfo
			{
				FileName = "git",
				Arguments = arguments,
				WorkingDirectory = folder,
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
