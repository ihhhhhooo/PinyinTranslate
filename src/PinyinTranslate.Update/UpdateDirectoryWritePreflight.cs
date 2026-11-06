namespace PinyinTranslate.Update;

internal interface IUpdateDirectoryAccess
{
    bool DirectoryExists(string path);
    Stream CreateNew(string path);
    void Delete(string path);
}

/// <summary>Checks real create/write/delete permission, without changing ACLs or trusting attributes.</summary>
internal static class UpdateDirectoryWritePreflight
{
    internal static void EnsureWritable(IEnumerable<string?> directories, IUpdateDirectoryAccess? access = null,
        Func<string>? newFileName = null)
    {
        access ??= new DirectoryAccess();
        newFileName ??= () => ".pinyintranslate-update-write-" + Guid.NewGuid().ToString("N") + ".tmp";
        foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory) || !access.DirectoryExists(directory))
                throw new IOException("UpdateDirectoryUnavailable");
            string name = newFileName();
            if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new IOException("InvalidUpdateProbeName");
            string path = Path.Combine(directory, name);
            bool owned = false;
            try
            {
                var stream = access.CreateNew(path); // A collision must never grant cleanup ownership.
                owned = true;
                using (stream) { stream.WriteByte(1); stream.Flush(); }
            }
            finally { if (owned) access.Delete(path); }
        }
    }
    private sealed class DirectoryAccess : IUpdateDirectoryAccess
    {
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public Stream CreateNew(string path) => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        public void Delete(string path) => File.Delete(path);
    }
}
