using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TurboPilot.Permissions;

/// <summary>
/// The permissions owned by one scope: the folder grants that let file
/// actions reach outside a workspace, and the set of session operations
/// pre-approved so they run without asking.
/// </summary>
public sealed class PermissionScope
{
	/// <summary>
	/// Folder grants belonging to this scope.
	/// </summary>
	public List<PermissionEntry> Folders { get; set; } = new();

	/// <summary>
	/// Operation kinds pre-approved in this scope. Null means the scope has
	/// no opinion of its own and inherits; an empty list means every
	/// operation was deliberately turned off.
	/// </summary>
	public List<string>? Operations { get; set; }
}

/// <summary>
/// The persisted permission settings: a set of application defaults that
/// every session starts from, plus a scope owned by each workspace folder.
/// </summary>
public sealed class PermissionSettings
{
	/// <summary>
	/// The scope that applies when nothing else does.
	/// </summary>
	public PermissionScope Defaults { get; set; } = new();

	/// <summary>
	/// Per-workspace scopes, keyed by <see cref="PermissionService.MakeKey"/>.
	/// </summary>
	public Dictionary<string, PermissionScope> Workspaces { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The shape of a permission options file written and read by the dialog's
/// Save Options and Load Options buttons. Only the settings travel in these
/// files: the scope they land in is decided when they are loaded. A missing
/// section is left alone on load, so a file can carry just folder grants or
/// just operation toggles.
/// </summary>
public sealed class PermissionOptionsFile
{
	public List<PermissionEntry>? Folders { get; set; }

	public List<string>? Operations { get; set; }
}

/// <summary>
/// Holds the permissions granted to a session: the folders outside the
/// workspace its files may be read or written, and the operations it may
/// perform without asking.
///
/// Two scopes are stored. The application defaults are the starting point
/// for every session. A workspace can add folder grants of its own, and can
/// take over the operation toggles; until it does, it inherits them.
///
/// The two kinds of setting combine differently, because they mean different
/// things. Folder grants are additive: the defaults apply everywhere and a
/// workspace only adds to them, so a shared library never has to be repeated
/// per project. Operation toggles are a mode: a workspace either inherits
/// the defaults or owns its own list, so a project that must never run shell
/// commands can switch that off without touching anyone else.
///
/// The dialog edits one scope at a time: the active workspace when a session
/// is running, the application defaults when none is. Enforcement consults
/// both, through <see cref="IsAllowed"/> and
/// <see cref="IsOperationAllowed"/>.
///
/// Paths inside the workspace itself need no entry and are always allowed.
/// </summary>
public static class PermissionService
{
	private const string SettingsFileName = "permissions.json";

	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	private static string SettingsPath => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"TurboPilot",
		SettingsFileName);

	/// <summary>
	/// The most recently loaded (or saved) permission settings.
	/// </summary>
	public static PermissionSettings Current { get; private set; } = Load();

	/// <summary>
	/// The key a workspace's scope is stored under. Paths that differ only
	/// by case, trailing separators or slash direction share a key, because
	/// they name the same folder.
	/// </summary>
	public static string MakeKey(string workspaceFolder)
	{
		string anchor = PermissionMatcher.AnchorFolder(workspaceFolder);
		return anchor.TrimEnd('\\').ToLowerInvariant();
	}

	/// <summary>
	/// Re-reads the store from disk. Called when the file may have changed
	/// outside the running process.
	/// </summary>
	public static PermissionSettings Reload()
	{
		Current = Load();
		return Current;
	}

	// ------------------------------------------------------------------ folders

	/// <summary>
	/// The folder grants belonging to one scope: the workspace's own list,
	/// or the application defaults when no workspace is active. The result
	/// is a copy, safe to edit as a working list.
	/// </summary>
	public static IReadOnlyList<PermissionEntry> EntriesFor(string? workspaceFolder)
	{
		PermissionScope? scope = WorkspaceScope(workspaceFolder);
		List<PermissionEntry> stored = scope?.Folders
			?? (string.IsNullOrWhiteSpace(workspaceFolder) ? Current.Defaults.Folders : []);

		return stored.Select(e => e.Clone()).ToList();
	}

	/// <summary>
	/// Replaces one scope's folder grants and persists the store.
	/// </summary>
	public static void SetEntries(string? workspaceFolder, IEnumerable<PermissionEntry> entries)
	{
		var cleaned = entries
			.Where(e => !string.IsNullOrWhiteSpace(e.FolderPath))
			.Select(e => e.Clone())
			.ToList();

		if (string.IsNullOrWhiteSpace(workspaceFolder))
		{
			Current.Defaults.Folders = cleaned;
		}
		else
		{
			WorkspaceScope(workspaceFolder, create: true)!.Folders = cleaned;
			Prune(MakeKey(workspaceFolder));
		}

		Save();
	}

	/// <summary>
	/// Every folder grant in force for a workspace: the application defaults
	/// followed by the workspace's own.
	/// </summary>
	public static IReadOnlyList<PermissionEntry> EffectiveEntries(string? workspaceFolder)
	{
		var all = new List<PermissionEntry>(Current.Defaults.Folders);
		PermissionScope? scope = WorkspaceScope(workspaceFolder);
		if (scope is not null)
			all.AddRange(scope.Folders);

		return all;
	}

	// ------------------------------------------------------------------ operations

	/// <summary>
	/// The operation kinds pre-approved in one scope. A workspace with no
	/// toggles of its own reports the application defaults.
	/// </summary>
	public static IReadOnlyList<string> OperationsFor(string? workspaceFolder) =>
		[.. ApprovedOperations(workspaceFolder)];

	/// <summary>
	/// True when the scope states its own operation toggles rather than
	/// inheriting the application defaults. The defaults have nothing to
	/// inherit from, so they always own theirs.
	/// </summary>
	public static bool OwnsOperations(string? workspaceFolder) =>
		string.IsNullOrWhiteSpace(workspaceFolder)
			|| WorkspaceScope(workspaceFolder)?.Operations is not null;

	/// <summary>
	/// Replaces a scope's pre-approved operations and persists the store.
	/// An empty list is a real answer: every operation asks first.
	/// </summary>
	public static void SetOperations(string? workspaceFolder, IEnumerable<string> approvedKinds)
	{
		var cleaned = approvedKinds
			.Where(k => !string.IsNullOrWhiteSpace(k))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();

		if (string.IsNullOrWhiteSpace(workspaceFolder))
		{
			Current.Defaults.Operations = cleaned;
		}
		else
		{
			WorkspaceScope(workspaceFolder, create: true)!.Operations = cleaned;
		}

		Save();
	}

	/// <summary>
	/// Drops a workspace's own operation toggles so it inherits the
	/// application defaults again.
	/// </summary>
	public static void ClearOperations(string? workspaceFolder)
	{
		if (string.IsNullOrWhiteSpace(workspaceFolder))
			return;

		PermissionScope? scope = WorkspaceScope(workspaceFolder);
		if (scope is null)
			return;

		scope.Operations = null;
		Prune(MakeKey(workspaceFolder));
		Save();
	}

	/// <summary>
	/// True when <paramref name="kind"/> may run in a session without
	/// asking. Unknown kinds are never pre-approved.
	/// </summary>
	public static bool IsOperationAllowed(string kind, string? workspaceFolder)
	{
		if (string.IsNullOrWhiteSpace(kind) || !PermissionOperations.IsKnown(kind))
			return false;

		return ApprovedOperations(workspaceFolder)
			.Any(k => string.Equals(k, kind, StringComparison.OrdinalIgnoreCase));
	}

	// ------------------------------------------------------------------ checks

	/// <summary>
	/// True when <paramref name="access"/> to <paramref name="targetPath"/>
	/// is permitted. Anything inside the workspace is; anything outside it
	/// needs a matching grant in the defaults or the workspace list.
	/// </summary>
	public static bool IsAllowed(string targetPath, PermissionAccess access, string? workspaceFolder)
	{
		if (string.IsNullOrWhiteSpace(targetPath))
			return false;

		if (!string.IsNullOrWhiteSpace(workspaceFolder)
			&& PermissionMatcher.IsUnder(targetPath, workspaceFolder))
		{
			return true;
		}

		foreach (PermissionEntry entry in EffectiveEntries(workspaceFolder))
		{
			if (entry.Access.Covers(access) && PermissionMatcher.IsMatch(entry.FolderPath, targetPath))
				return true;
		}

		return false;
	}

	// ------------------------------------------------------------------ snapshots

	/// <summary>
	/// The permissions actually in force for a workspace, flattened into
	/// one scope: every folder grant that applied, defaults included, and
	/// the operations that were pre-approved. Taken when a session
	/// connects so the session can be resumed on the same terms.
	/// </summary>
	public static PermissionScope Snapshot(string? workspaceFolder) => new()
	{
		Folders = EffectiveEntries(workspaceFolder).Select(e => e.Clone()).ToList(),
		Operations = [.. ApprovedOperations(workspaceFolder)],
	};

	/// <summary>
	/// Gives a workspace back the permissions a snapshot recorded, as its
	/// own scope. Returns false when the workspace already has them, so a
	/// resume that changes nothing says nothing.
	/// </summary>
	public static bool Restore(string? workspaceFolder, PermissionScope? snapshot)
	{
		if (string.IsNullOrWhiteSpace(workspaceFolder) || snapshot is null)
			return false;
		// An empty snapshot is either a session saved before snapshots
		// existed or one that genuinely had nothing; neither is worth
		// revoking a live grant over.
		if (snapshot.Folders.Count == 0 && (snapshot.Operations is null || snapshot.Operations.Count == 0))
			return false;
		if (Matches(workspaceFolder, snapshot))
			return false;

		SetEntries(workspaceFolder, snapshot.Folders);
		SetOperations(workspaceFolder, snapshot.Operations ?? []);
		return true;
	}

	private static bool Matches(string workspaceFolder, PermissionScope snapshot)
	{
		var folders = EffectiveEntries(workspaceFolder);
		if (folders.Count != snapshot.Folders.Count)
			return false;
		for (var index = 0; index < folders.Count; index++)
		{
			if (!string.Equals(folders[index].FolderPath, snapshot.Folders[index].FolderPath, StringComparison.OrdinalIgnoreCase)
				|| folders[index].Access != snapshot.Folders[index].Access)
				return false;
		}
		var operations = ApprovedOperations(workspaceFolder);
		var wanted = snapshot.Operations ?? [];
		return operations.Count == wanted.Count
			&& !operations.Except(wanted, StringComparer.OrdinalIgnoreCase).Any();
	}

	// ------------------------------------------------------------------ files

	/// <summary>
	/// Writes one scope's settings to a file the user chose. Throws to the
	/// caller so it can report the failure.
	/// </summary>
	public static void SaveOptions(string filePath, IEnumerable<PermissionEntry> entries,
		IEnumerable<string> approvedKinds)
	{
		var file = new PermissionOptionsFile
		{
			Folders = entries
				.Where(e => !string.IsNullOrWhiteSpace(e.FolderPath))
				.Select(e => e.Clone())
				.ToList(),
			Operations = approvedKinds
				.Where(k => !string.IsNullOrWhiteSpace(k))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList(),
		};

		File.WriteAllText(filePath, JsonSerializer.Serialize(file, SerializerOptions));
	}

	/// <summary>
	/// Reads settings from a file written by <see cref="SaveOptions"/>.
	/// A section the file does not carry comes back null, meaning "leave
	/// what is there alone". Throws to the caller so it can report the
	/// failure.
	/// </summary>
	public static PermissionOptionsFile LoadOptions(string filePath)
	{
		var file = JsonSerializer.Deserialize<PermissionOptionsFile>(
			File.ReadAllText(filePath), SerializerOptions) ?? new PermissionOptionsFile();

		file.Folders = file.Folders?
			.Where(e => !string.IsNullOrWhiteSpace(e.FolderPath))
			.Select(e => e.Clone())
			.ToList();

		file.Operations = file.Operations?
			.Where(k => !string.IsNullOrWhiteSpace(k))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();

		return file;
	}

	// ------------------------------------------------------------------ store

	/// <summary>
	/// The stored scope of a workspace, or null when the workspace has none
	/// and inherits everything. Passing <c>create</c> adds an empty scope
	/// when one is needed.
	/// </summary>
	private static PermissionScope? WorkspaceScope(string? workspaceFolder, bool create = false)
	{
		if (string.IsNullOrWhiteSpace(workspaceFolder))
			return null;

		string key = MakeKey(workspaceFolder);
		if (!Current.Workspaces.TryGetValue(key, out PermissionScope? scope))
		{
			if (!create)
				return null;

			scope = new PermissionScope();
			Current.Workspaces[key] = scope;
		}

		return scope;
	}

	/// <summary>
	/// The operation kinds a scope runs with: its own list when it has one,
	/// the application defaults otherwise. Never null, because a scope that
	/// has said nothing still has an answer: nothing is pre-approved.
	/// </summary>
	private static List<string> ApprovedOperations(string? workspaceFolder)
	{
		List<string>? own = string.IsNullOrWhiteSpace(workspaceFolder)
			? Current.Defaults.Operations
			: WorkspaceScope(workspaceFolder)?.Operations;

		return own ?? Current.Defaults.Operations ?? new List<string>();
	}

	/// <summary>
	/// Removes a workspace record that no longer says anything, so the store
	/// does not fill up with empty scopes that silently stop inheriting.
	/// </summary>
	private static void Prune(string key)
	{
		if (Current.Workspaces.TryGetValue(key, out PermissionScope? scope)
			&& scope.Folders.Count == 0
			&& scope.Operations is null)
		{
			Current.Workspaces.Remove(key);
		}
	}

	/// <summary>
	/// Writes the store to disk. Failures are swallowed: the in-memory
	/// settings stay authoritative for the running session.
	/// </summary>
	public static void Save()
	{
		try
		{
			string? directory = Path.GetDirectoryName(SettingsPath);
			if (!string.IsNullOrEmpty(directory))
				Directory.CreateDirectory(directory);

			File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Current, SerializerOptions));
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}

	/// <summary>
	/// Reads the store from disk. Returns empty settings when the file is
	/// missing or unreadable.
	/// </summary>
	public static PermissionSettings Load()
	{
		try
		{
			if (!File.Exists(SettingsPath))
				return new PermissionSettings();

			var settings = JsonSerializer.Deserialize<PermissionSettings>(
				File.ReadAllText(SettingsPath), SerializerOptions);
			return Rebuild(settings ?? new PermissionSettings());
		}
		catch (IOException)
		{
			return new PermissionSettings();
		}
		catch (UnauthorizedAccessException)
		{
			return new PermissionSettings();
		}
		catch (JsonException)
		{
			return new PermissionSettings();
		}
	}

	// Deserialized dictionaries get the default ordinal comparer; rebuild
	// them so workspace lookups stay case-insensitive like the keys are.
	private static PermissionSettings Rebuild(PermissionSettings settings)
	{
		settings.Defaults ??= new PermissionScope();
		settings.Defaults.Folders ??= new List<PermissionEntry>();

		settings.Workspaces = new Dictionary<string, PermissionScope>(
			settings.Workspaces ?? new Dictionary<string, PermissionScope>(),
			StringComparer.OrdinalIgnoreCase);

		foreach (string key in settings.Workspaces.Keys.ToList())
		{
			if (settings.Workspaces[key] is null)
				settings.Workspaces[key] = new PermissionScope();
			else
				settings.Workspaces[key].Folders ??= new List<PermissionEntry>();
		}

		return settings;
	}
}
