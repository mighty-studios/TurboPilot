using System.IO;

namespace TurboPilot.Rendering;

// Finds workspace files by name so replies can link references such as MainWindow.xaml that are
// not relative to the workspace root. Build output and tool folders are skipped because their
// copies would make ordinary source names ambiguous. The index refreshes at most every few seconds.
internal sealed class WorkspaceFileIndex(string? root)
{
	private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
		{ ".git", ".vs", ".vscode", ".idea", "bin", "obj", "node_modules", "packages", "TestResults" };
	private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(10);
	private const int MaxFiles = 50_000;
	private readonly object _lock = new();
	private Dictionary<string, List<string>>? _byName;
	private DateTime _builtAt;

	public string? Find(string reference)
	{
		var relative = reference.Replace('/', '\\');
		if (relative.StartsWith(@".\", StringComparison.Ordinal))
			relative = relative[2..];
		if (string.IsNullOrWhiteSpace(root) || relative.Length == 0
			|| relative.Split('\\').Any(part => part is "" or "." or ".."))
			return null;
		List<string>? candidates;
		lock (_lock)
		{
			if (_byName is null || DateTime.UtcNow - _builtAt > RefreshInterval)
				Build();
			_byName!.TryGetValue(Path.GetFileName(relative), out candidates);
		}
		var matches = candidates?.Where(path => path.EndsWith("\\" + relative, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
		return matches is { Count: 1 } ? matches[0] : null;
	}

	private void Build()
	{
		var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
		var folders = new Stack<string>();
		if (Directory.Exists(root))
			folders.Push(Path.GetFullPath(root));
		var count = 0;
		while (folders.Count > 0 && count < MaxFiles)
		{
			var folder = folders.Pop();
			try
			{
				foreach (var file in Directory.EnumerateFiles(folder))
				{
					var name = Path.GetFileName(file);
					if (!index.TryGetValue(name, out var paths))
						index[name] = paths = [];
					paths.Add(file);
					if (++count >= MaxFiles)
						break;
				}
				foreach (var child in new DirectoryInfo(folder).EnumerateDirectories())
				{
					if (!SkippedFolders.Contains(child.Name) && !child.Attributes.HasFlag(FileAttributes.ReparsePoint))
						folders.Push(child.FullName);
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// Unreadable folders are left out; their files simply stay unlinked.
			}
		}
		_byName = index;
		_builtAt = DateTime.UtcNow;
	}
}
