using System.Text;

namespace PBT.Server;

internal static class AtomicFile
{
    // durable: false skips the fsync. The replace is still atomic, only a power cut can lose the write.
    // backup: false skips the .bak copy, for stores with tens of thousands of files.
    public static void Write(string path, string content, bool durable = true, bool backup = true) =>
        Write(path, Encoding.UTF8.GetBytes(content), durable, backup);

    public static void Write(string path, byte[] bytes, bool durable = true, bool backup = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: durable);
            }
            if (!backup)
                File.Move(temporary, path, overwrite: true);
            else if (File.Exists(path))
                File.Replace(temporary, path, path + ".bak");
            else
                File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
