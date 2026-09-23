using System.IO;

namespace TurboPilot.Storage;

internal static class AtomicFile
{
	public static void Write(string path, Action<Stream> write, bool overwrite = true)
	{
		path = Path.GetFullPath(path);
		if (!overwrite && File.Exists(path)) return;
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
			{
				write(stream);
				stream.Flush(flushToDisk: true);
			}
			try { File.Move(temporaryPath, path, overwrite); }
			catch (IOException) when (!overwrite && File.Exists(path))
			{
				// Preserve a file another writer created before the rename.
			}
		}
		finally
		{
			if (File.Exists(temporaryPath))
				File.Delete(temporaryPath);
		}
	}
}
