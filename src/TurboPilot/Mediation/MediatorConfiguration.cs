using System.IO;
using System.Security.Cryptography;
using System.Text;
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

	// Content hashes of defaults provisioned before copies were tracked in the defaults manifest.
	// A copy still matching one of these was never edited, so it can take a newer default.
	private static readonly IReadOnlyDictionary<string, string[]> UntrackedDefaults = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
	{
		["instructions\\mediator.instructions.md"] = ["EF50D24268D3393C278F15B0CC2B9B5894A8D2D872DDE349B8928E1DAEB6AEE9"],
		["skills\\rewrite-prompt\\SKILL.md"] =
		[
			"392131778C3F45A36FE14CFF63BE89C4282B8655F5F5AEE13FFFF801F73A5642",
			"2629E2E7E0CF77DE389EBD03E0E8E8AA7B9BFD78E497450202BDBAC69822DD4B",
		],
		["skills\\beautify-output\\SKILL.md"] =
		[
			"3D515B26757C1248E52CBC0EDFC8E26A8FE5A7528A25E83327711250D51706E6",
			"D171A2D28F7C5DC6A99EB156B0CD0A84C41AA70156BAF10A4CB135B980607290",
		],
		["skills\\update-summary\\SKILL.md"] =
		[
			"AFD01565C2B148B6785F18065381603F5D70F4B033553A590F00200B1469E8B4",
			"17F44BC523E0C830DC9084848910222D7AB4FC3B9F27118EE0BC517514DAB4F9",
		],
		["skills\\monitor-output\\SKILL.md"] =
		[
			"4D024862AADC5BDEB8A531C3D93007671281C69F715B26169F7BAF74F53A3CBB",
			"FE5FEFA25247EC1A1A5F05036E492B6EB824D024AB5A6DCC168F9BBB0020AFCC",
		],
		["LICENSE.txt"] = ["CDF5ECA42DDBD339BE7E3811ABF1DADA97C9A7A9D880B35FD23A406B49425C7D"],
	};

	private readonly string _templateDirectory;
	private readonly IReadOnlyDictionary<string, string[]> _untrackedDefaults;
	private readonly object _documentsLock = new();
	public string DirectoryPath { get; }
	public string SettingsPath => Path.Combine(DirectoryPath, "settings.json");
	public string DefaultsManifestPath => Path.Combine(DirectoryPath, "defaults.json");

	public MediatorConfiguration(string? directory = null, string? templateDirectory = null,
		IReadOnlyDictionary<string, string[]>? untrackedDefaults = null)
	{
		DirectoryPath = directory ?? Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TurboPilot", "mediator");
		_templateDirectory = templateDirectory ?? Path.Combine(AppContext.BaseDirectory, "Assets", "Mediator");
		_untrackedDefaults = untrackedDefaults ?? UntrackedDefaults;
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

	// Missing documents are provisioned, and copies that still match the default they were provisioned
	// from take newer defaults. Edited copies are never overwritten.
	public void EnsureDocuments()
	{
		lock (_documentsLock)
		{
			var manifest = ReadDefaultsManifest();
			var changed = false;
			foreach (var relativePath in DocumentPaths)
			{
				var template = File.ReadAllBytes(Path.Combine(_templateDirectory, relativePath));
				var templateHash = HashContent(template);
				var target = Path.Combine(DirectoryPath, relativePath);
				manifest.TryGetValue(relativePath, out var provisioned);
				if (File.Exists(target))
				{
					var current = HashContent(File.ReadAllBytes(target));
					var unedited = provisioned is not null
						? current == provisioned
						: _untrackedDefaults.TryGetValue(relativePath, out var earlier) && earlier.Contains(current, StringComparer.Ordinal);
					if (current == templateHash || !unedited)
					{
						if (current == templateHash && provisioned != templateHash)
						{
							manifest[relativePath] = templateHash;
							changed = true;
						}
						continue;
					}
				}
				AtomicFile.Write(target, output => output.Write(template));
				manifest[relativePath] = templateHash;
				changed = true;
			}
			if (changed)
				AtomicFile.Write(DefaultsManifestPath, stream => JsonSerializer.Serialize(stream, manifest, JsonOptions));
		}
	}

	private Dictionary<string, string> ReadDefaultsManifest()
	{
		try
		{
			if (File.Exists(DefaultsManifestPath)
				&& JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(DefaultsManifestPath), JsonOptions) is { } manifest)
				return new Dictionary<string, string>(manifest, StringComparer.OrdinalIgnoreCase);
		}
		catch (JsonException)
		{
			// An unreadable manifest only loses refresh tracking; untracked copies are then left as they are.
		}
		return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
	}

	// Line endings are normalized so a checkout's line-ending conversion does not look like an edit.
	internal static string HashContent(byte[] content) => Convert.ToHexString(SHA256.HashData(
		Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(content).ReplaceLineEndings("\n"))));

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
