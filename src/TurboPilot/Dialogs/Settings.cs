using System.IO;
using System.Text.Json;

namespace TurboPilot.Dialogs;

/// <summary>
/// Application settings persisted to disk between runs.
/// Stored in the user's local application data folder.
/// </summary>
public sealed class Settings
{
	private const string SettingsFileName = "turbopilot-settings.json";

	private static string SettingsPath => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"TurboPilot",
		SettingsFileName);

	/// <summary>
	/// The last workspace folder path used by the user.
	/// </summary>
	public string? LastWorkspacePath { get; set; }

	/// <summary>
	/// List of folders searched for customization items (prompts,
	/// agents, skills, instructions and MCP servers). Order is not
	/// significant; duplicate short names coexist with suffixes.
	/// </summary>
	public List<string> CustomizationFolders { get; set; } = new();

	/// <summary>
	/// Loads settings from disk. Returns a default instance if the file
	/// doesn't exist or fails to parse.
	/// </summary>
	public static Settings Load()
	{
		try
		{
			if (!File.Exists(SettingsPath))
				return new Settings();

			var json = File.ReadAllText(SettingsPath);
			var settings = JsonSerializer.Deserialize<Settings>(json);
			return settings ?? new Settings();
		}
		catch
		{
			// Corrupted or unreadable settings file — return defaults
			return new Settings();
		}
	}

	/// <summary>
	/// Saves settings to disk, creating the directory structure if needed.
	/// </summary>
	public void Save()
	{
		var directory = Path.GetDirectoryName(SettingsPath);
		if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
		{
			Directory.CreateDirectory(directory);
		}

		var options = new JsonSerializerOptions
		{
			WriteIndented = true,
		};
		var json = JsonSerializer.Serialize(this, options);
		File.WriteAllText(SettingsPath, json);
	}
}
