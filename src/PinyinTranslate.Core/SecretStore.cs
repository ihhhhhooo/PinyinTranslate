using System.Security.Cryptography;
using System.Text;

namespace PinyinTranslate.Core;

public static class SecretStore
{
    public static void Save(string path, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var bytes = Encoding.UTF8.GetBytes(key.Trim());
        try
        {
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            var fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            var temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    file.Write(encrypted);
                    file.Flush(true);
                }
                File.Move(temporaryPath, fullPath, true);
            }
            finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static string Load(string path)
    {
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
