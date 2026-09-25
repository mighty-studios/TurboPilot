using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TurboPilot.Ai;
using TurboPilot.Storage;

namespace TurboPilot.Sessions;

// Context carried into a replacement session: the previous model's hand-off summary plus the
// user's own requests, quoted exactly so their constraints cannot be summarized away.
public sealed record SummaryBootstrap(string SourceSessionId, string Workspace, string Summary)
{
	public IReadOnlyList<string> RecentRequests { get; init; } = [];
}

public sealed class SessionRecord
{
	public required string SessionId { get; init; }
	public required ChatSessionOptions Options { get; set; }
	public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
	public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
	public string Description { get; set; } = "";
	public List<string> Prompts { get; set; } = [];
	// Prompts also holds answers to model questions; Requests holds only the user's own prompts.
	public List<string> Requests { get; set; } = [];
	public int ContextUsedTokens { get; set; }
	public int ContextWindowTokens { get; set; }
	public long AicNano { get; set; }
	public bool UsesApiKey { get; set; }
	public SummaryBootstrap? Bootstrap { get; set; }
	public bool BootstrapPending { get; set; }

	[JsonIgnore]
	public string DisplayLabel => $"{UpdatedAt.ToLocalTime():g} | {Options.Model} | "
		+ (string.IsNullOrWhiteSpace(Description) ? SessionId : Description);
}

public sealed class SessionStore
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
		RespectNullableAnnotations = true,
	};

	private readonly string _directory;

	public SessionStore(string? directory = null)
	{
		_directory = directory ?? Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"TurboPilot", "sessions");
	}

	public SessionRecord Create(string sessionId, ChatSessionOptions options, string transcript)
	{
		var metadataPath = FilePath(sessionId, ".json");
		var transcriptPath = FilePath(sessionId, ".md");
		Directory.CreateDirectory(_directory);
		if (File.Exists(metadataPath) || File.Exists(transcriptPath))
			throw new IOException($"Session '{sessionId}' already has saved history.");

		var record = new SessionRecord
		{
			SessionId = sessionId,
			Options = options,
			UsesApiKey = options.UseByok && !string.IsNullOrWhiteSpace(options.ByokApiKey),
		};
		File.WriteAllText(transcriptPath, transcript);
		File.WriteAllText(FilePath(sessionId, ".rendered.md"), transcript);
		Save(record);
		return record;
	}

	public SessionRecord Load(string sessionId)
	{
		var record = JsonSerializer.Deserialize<SessionRecord>(
			File.ReadAllText(FilePath(sessionId, ".json")), JsonOptions)
			?? throw new InvalidDataException($"Session '{sessionId}' has no metadata.");
		if (record.SessionId != sessionId || record.Options is null || record.Prompts is null)
			throw new InvalidDataException($"Session '{sessionId}' has invalid metadata.");
		return record;
	}

	public IReadOnlyList<SessionRecord> List(out List<string> errors)
	{
		errors = [];
		var records = new List<SessionRecord>();
		if (!Directory.Exists(_directory))
			return records;

		foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
		{
			try
			{
				records.Add(Load(Path.GetFileNameWithoutExtension(file)));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
			{
				errors.Add($"Cannot read '{Path.GetFileName(file)}': {ex.Message}");
			}
		}
		return records.OrderByDescending(record => record.UpdatedAt).ToList();
	}

	public string ReadTranscript(string sessionId) => File.ReadAllText(FilePath(sessionId, ".md"));
	public string ReadRenderedTranscript(string sessionId)
	{
		var path = FilePath(sessionId, ".rendered.md");
		return File.Exists(path) ? File.ReadAllText(path) : ReadTranscript(sessionId);
	}

	public void AppendTranscript(string sessionId, string text) =>
		File.AppendAllText(FilePath(sessionId, ".md"), text);
	public void AppendRenderedTranscript(string sessionId, string text) =>
		File.AppendAllText(FilePath(sessionId, ".rendered.md"), text);

	public void WriteTranscript(string sessionId, string text) =>
		WriteText(FilePath(sessionId, ".md"), text);

	public void WriteRenderedTranscript(string sessionId, string text) =>
		WriteText(FilePath(sessionId, ".rendered.md"), text);

	private static void WriteText(string path, string text) =>
		WriteAtomic(path, stream =>
		{
			using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true);
			writer.Write(text);
		});

	public void Save(SessionRecord record)
	{
		var path = FilePath(record.SessionId, ".json");
		record.UpdatedAt = DateTimeOffset.UtcNow;
		WriteAtomic(path, stream => JsonSerializer.Serialize(stream, record, JsonOptions));
	}

	private static void WriteAtomic(string path, Action<Stream> write) => AtomicFile.Write(path, write);

	private string FilePath(string sessionId, string extension)
	{
		if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 160
			|| sessionId is "." or ".."
			|| !sessionId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))
			throw new ArgumentException("Invalid session ID.", nameof(sessionId));
		return Path.Combine(_directory, sessionId + extension);
	}
}
