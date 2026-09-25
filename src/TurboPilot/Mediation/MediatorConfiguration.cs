using System.IO;
using System.Text.Json;
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

	// Options of the retired prompt rewording, output formatting, summary, and monitoring work.
	// Settings that still carry them predate the parked Mediator, so they load disabled.
	private static readonly string[] RetiredOptions =
		["rewordPrompts", "beautifyOutput", "maintainSummary", "monitorOutput", "minimumRewriteTokens"];

	public string DirectoryPath { get; }
	public string SettingsPath => Path.Combine(DirectoryPath, "settings.json");

	public MediatorConfiguration(string? directory = null)
	{
		DirectoryPath = directory ?? Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TurboPilot", "mediator");
	}

	public MediatorSettings Load()
	{
		if (!File.Exists(SettingsPath))
			return new MediatorSettings();
		var json = File.ReadAllText(SettingsPath);
		var settings = JsonSerializer.Deserialize<MediatorSettings>(json, JsonOptions)
			?? throw new InvalidDataException("Mediator settings are empty.");
		settings.Validate();
		using var document = JsonDocument.Parse(json);
		var retired = document.RootElement.EnumerateObject()
			.Any(property => RetiredOptions.Contains(property.Name, StringComparer.OrdinalIgnoreCase));
		return retired ? settings with { Enabled = false } : settings;
	}

	public void Save(MediatorSettings settings)
	{
		settings.Validate();
		AtomicFile.Write(SettingsPath, stream => JsonSerializer.Serialize(stream, settings, JsonOptions));
	}
}
