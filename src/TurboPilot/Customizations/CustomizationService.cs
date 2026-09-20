using System.IO;
using System.Text.Json;
using TurboPilot.Dialogs;

namespace TurboPilot.Customizations;

/// <summary>
/// Collects prompts, skills and instructions from the customization search
/// roots and keeps the resulting lists in memory and on disk. GitHub
/// Copilot custom agent definitions (agents/) are deliberately not
/// collected; this tool does not use them.
///
/// Search order, following the GitHub Copilot convention plus our own:
///   1. The personal folder, %USERPROFILE%\.copilot
///   2. The user-defined customization folders, in their configured order
///   3. The workspace .github folder
///
/// Within each root the standard layout is scanned:
///   prompts/*.md, skills/[name]/SKILL.md and instructions/*.instructions.md
/// (top-level files only; a skill is its whole folder, keyed by its SKILL.md).
///
/// A rescan builds fresh lists but transfers the enabled flag from the
/// previous lists for items found at the same file path, so user toggles
/// survive reordering and rescans. The lists persist to disk between runs.
/// Callers rescan when a session starts or when the customization folder
/// list changes.
/// </summary>
public static class CustomizationService
{
	private const string LibraryFileName = "customizations.json";

	private const string InstructionsSuffix = ".instructions.md";

	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
	};

	private static string LibraryPath => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"TurboPilot",
		LibraryFileName);

	/// <summary>
	/// The most recently scanned (or loaded from disk) lists.
	/// </summary>
	public static CustomizationLibrary Current { get; private set; } = Load();

	/// <summary>
	/// Rebuilds all three lists from the search roots, transfers the
	/// enabled flag from the previous lists for items at the same path,
	/// persists the result and makes it current.
	/// </summary>
	/// <param name="workspaceFolder">
	/// The session workspace whose .github folder is searched last.
	/// Null or missing folders are skipped.
	/// </param>
	public static CustomizationLibrary Rescan(string? workspaceFolder)
	{
		CustomizationLibrary previous = Current;
		CustomizationLibrary library = Collect(workspaceFolder);

		TransferEnabled(previous.Prompts, library.Prompts);
		TransferEnabled(previous.Skills, library.Skills);
		TransferEnabled(previous.Instructions, library.Instructions);

		Save(library);
		Current = library;
		return library;
	}

	// ------------------------------------------------------------------ scan

	private static CustomizationLibrary Collect(string? workspaceFolder)
	{
		var library = new CustomizationLibrary();
		var usedPromptNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var usedSkillNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var usedInstructionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (string root in GetSearchRoots(workspaceFolder))
		{
			CollectPrompts(root, library, usedPromptNames);
			CollectSkills(root, library, usedSkillNames);
			CollectInstructions(root, library, usedInstructionNames);
		}

		return library;
	}

	private static IEnumerable<string> GetSearchRoots(string? workspaceFolder)
	{
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		string personal = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
			".copilot");
		if (AddRoot(seen, personal))
			yield return personal;

		foreach (string folder in Settings.Load().CustomizationFolders)
		{
			string trimmed = folder.Trim();
			if (AddRoot(seen, trimmed))
				yield return trimmed;
		}

		if (!string.IsNullOrWhiteSpace(workspaceFolder))
		{
			string github = Path.Combine(workspaceFolder.Trim(), ".github");
			if (AddRoot(seen, github))
				yield return github;
		}
	}

	private static bool AddRoot(HashSet<string> seen, string path) =>
		!string.IsNullOrWhiteSpace(path) && Directory.Exists(path) && seen.Add(path);

	private static void CollectPrompts(string root, CustomizationLibrary library, HashSet<string> usedNames)
	{
		foreach (string file in EnumerateFiles(Path.Combine(root, "prompts"), "*.md"))
		{
			string name = MakeUniqueName(Path.GetFileNameWithoutExtension(file), usedNames);
			library.Prompts[file] = new CustomizationItem { FilePath = file, Name = name };
		}
	}

	private static void CollectSkills(string root, CustomizationLibrary library, HashSet<string> usedNames)
	{
		string skillsDir = Path.Combine(root, "skills");
		if (!Directory.Exists(skillsDir))
			return;

		foreach (string folder in Directory.GetDirectories(skillsDir)
			.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
		{
			string skillFile = Path.Combine(folder, "SKILL.md");
			if (!File.Exists(skillFile))
				continue;

			string name = MakeUniqueName(Path.GetFileName(folder), usedNames);
			library.Skills[skillFile] = new CustomizationItem { FilePath = skillFile, Name = name };
		}
	}

	private static void CollectInstructions(string root, CustomizationLibrary library, HashSet<string> usedNames)
	{
		foreach (string file in EnumerateFiles(Path.Combine(root, "instructions"), "*" + InstructionsSuffix))
		{
			string stem = Path.GetFileName(file);
			stem = stem[..^InstructionsSuffix.Length];
			string name = MakeUniqueName(stem, usedNames);
			library.Instructions[file] = new CustomizationItem { FilePath = file, Name = name };
		}
	}

	private static IEnumerable<string> EnumerateFiles(string directory, string searchPattern) =>
		Directory.Exists(directory)
			? Directory.GetFiles(directory, searchPattern, SearchOption.TopDirectoryOnly)
				.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
			: [];

	/// <summary>
	/// Returns a name not yet in <paramref name="usedNames"/>, appending an
	/// incrementing suffix on collision (base, base_2, base_3, ...).
	/// The chosen name is added to the set.
	/// </summary>
	private static string MakeUniqueName(string baseName, HashSet<string> usedNames)
	{
		if (usedNames.Add(baseName))
			return baseName;

		int suffix = 2;
		while (!usedNames.Add($"{baseName}_{suffix}"))
			suffix++;

		return $"{baseName}_{suffix}";
	}

	// ------------------------------------------------------------------ state

	private static void TransferEnabled(
		Dictionary<string, CustomizationItem> previous,
		Dictionary<string, CustomizationItem> current)
	{
		foreach (var (path, item) in current)
		{
			if (previous.TryGetValue(path, out var old))
				item.Enabled = old.Enabled;
		}
	}

	/// <summary>
	/// Writes the library to disk. Failures are swallowed: the in-memory
	/// lists stay authoritative for the running session.
	/// </summary>
	public static void Save(CustomizationLibrary library)
	{
		try
		{
			string? directory = Path.GetDirectoryName(LibraryPath);
			if (!string.IsNullOrEmpty(directory))
				Directory.CreateDirectory(directory);

			File.WriteAllText(LibraryPath, JsonSerializer.Serialize(library, SerializerOptions));
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}

	/// <summary>
	/// Loads the persisted library. Returns empty lists when the file is
	/// missing or unreadable.
	/// </summary>
	public static CustomizationLibrary Load()
	{
		try
		{
			if (!File.Exists(LibraryPath))
				return new CustomizationLibrary();

			var library = JsonSerializer.Deserialize<CustomizationLibrary>(
				File.ReadAllText(LibraryPath), SerializerOptions);
			return RebuildKeys(library ?? new CustomizationLibrary());
		}
		catch (IOException)
		{
			return new CustomizationLibrary();
		}
		catch (UnauthorizedAccessException)
		{
			return new CustomizationLibrary();
		}
		catch (JsonException)
		{
			return new CustomizationLibrary();
		}
	}

	// Deserialized dictionaries get the default ordinal comparer; rebuild
	// them so path lookups stay case-insensitive like the scanner's.
	private static CustomizationLibrary RebuildKeys(CustomizationLibrary library)
	{
		library.Prompts = new(library.Prompts ?? new(), CustomizationLibrary.PathComparer);
		library.Skills = new(library.Skills ?? new(), CustomizationLibrary.PathComparer);
		library.Instructions = new(library.Instructions ?? new(), CustomizationLibrary.PathComparer);
		return library;
	}
}
