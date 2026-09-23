using System.IO;
using System.Text.Json;
using TurboPilot.Customizations;
using TurboPilot.Storage;

namespace TurboPilot.Mediation;

public sealed class MediatorConfiguration
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
		RespectNullableAnnotations = true,
	};

	private static readonly string[] DocumentPaths =
	[
		"instructions\\mediator.instructions.md",
		"skills\\rewrite-prompt\\SKILL.md",
		"skills\\beautify-output\\SKILL.md",
		"skills\\update-summary\\SKILL.md",
		"skills\\monitor-output\\SKILL.md",
		"LICENSE.txt",
	];

	private readonly string _templateDirectory;
	public string DirectoryPath { get; }
	public string SettingsPath => Path.Combine(DirectoryPath, "settings.json");

	public MediatorConfiguration(string? directory = null, string? templateDirectory = null)
	{
		DirectoryPath = directory ?? Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TurboPilot", "mediator");
		_templateDirectory = templateDirectory ?? Path.Combine(AppContext.BaseDirectory, "Assets", "Mediator");
	}

	public MediatorSettings Load()
	{
		if (!File.Exists(SettingsPath))
			return new MediatorSettings();
		var settings = JsonSerializer.Deserialize<MediatorSettings>(File.ReadAllText(SettingsPath), JsonOptions)
			?? throw new InvalidDataException("Mediator settings are empty.");
		settings.Validate();
		return settings;
	}

	public void Save(MediatorSettings settings)
	{
		settings.Validate();
		AtomicFile.Write(SettingsPath, stream => JsonSerializer.Serialize(stream, settings, JsonOptions));
	}

	public void EnsureDocuments()
	{
		foreach (var relativePath in DocumentPaths)
		{
			var target = Path.Combine(DirectoryPath, relativePath);
			if (File.Exists(target))
				continue;
			AtomicFile.Write(target, output =>
			{
				using var input = File.OpenRead(Path.Combine(_templateDirectory, relativePath));
				input.CopyTo(output);
			}, overwrite: false);
		}
	}

	public string ReadInstructions(MediatorOperation operation)
	{
		var skill = operation switch
		{
			MediatorOperation.RewritePrompt => "rewrite-prompt",
			MediatorOperation.BeautifyOutput => "beautify-output",
			MediatorOperation.UpdateSummary => "update-summary",
			MediatorOperation.MonitorOutput => "monitor-output",
			_ => throw new ArgumentOutOfRangeException(nameof(operation)),
		};
		var general = FrontMatter.ReadDocument(Path.Combine(DirectoryPath, "instructions", "mediator.instructions.md")).Body;
		var specific = FrontMatter.ReadDocument(Path.Combine(DirectoryPath, "skills", skill, "SKILL.md")).Body;
		if (string.IsNullOrWhiteSpace(general) || string.IsNullOrWhiteSpace(specific))
			throw new InvalidDataException("Mediator instructions and the selected skill must not be empty.");
		return general.Trim() + "\r\n\r\n" + specific.Trim();
	}
}
