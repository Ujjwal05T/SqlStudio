using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace SqlStudio.Services;

/// <summary>
/// Linux: uses the desktop keyring (GNOME Keyring / KWallet) through `secret-tool` when it is installed
/// and a keyring is running. Otherwise passwords go to an AES-GCM encrypted file in ~/.config/SqlStudio.
/// That file's key sits next to it (both mode 600), so it protects against other users and casual
/// reading, not against malware running as you.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class LinuxCredentialStore : ICredentialStore
{
    readonly string _service;
    readonly KeyFileStore _file;

    public LinuxCredentialStore(string service = "SqlStudio", string? directory = null)
    {
        _service = service;
        _file = new KeyFileStore(directory);
    }

    public string? Get(Guid profileId) => KeyringGet(profileId) ?? _file.Get(profileId);

    public void Set(Guid profileId, string password)
    {
        if (KeyringSet(profileId, password))
            _file.Delete(profileId); // don't leave an older copy behind
        else
            _file.Set(profileId, password);
    }

    public void Delete(Guid profileId)
    {
        RunSecretTool(null, "clear", "service", _service, "account", profileId.ToString());
        _file.Delete(profileId);
    }

    string? KeyringGet(Guid id)
    {
        var (code, output) = RunSecretTool(null, "lookup", "service", _service, "account", id.ToString());
        return code == 0 && output.Length > 0 ? output : null;
    }

    bool KeyringSet(Guid id, string password) =>
        RunSecretTool(password, "store", "--label=" + _service, "service", _service, "account", id.ToString()).Code == 0;

    // Secrets go over stdin so they never show up in the process list. A missing tool or keyring is just "no".
    static (int Code, string Output) RunSecretTool(string? stdin, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("secret-tool")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi)!;
            if (stdin is not null)
                process.StandardInput.Write(stdin);
            process.StandardInput.Close();

            var stderr = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            _ = stderr.Result;
            return (process.ExitCode, stdout);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, "");
        }
    }

    sealed class KeyFileStore : EncryptedFileCredentialStore
    {
        readonly string _keyPath;
        byte[]? _key;

        public KeyFileStore(string? directory)
            : base(directory is null ? null : System.IO.Path.Combine(directory, "credentials.json")) =>
            _keyPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, "credentials.key");

        byte[] Key()
        {
            if (_key is not null)
                return _key;
            if (File.Exists(_keyPath))
                return _key = File.ReadAllBytes(_keyPath);

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_keyPath)!);
            var key = RandomNumberGenerator.GetBytes(32);
            using (var stream = new FileStream(_keyPath, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            }))
                stream.Write(key);
            return _key = key;
        }

        // blob = nonce (12) | tag (16) | ciphertext
        protected override string Encrypt(string password)
        {
            var plain = Encoding.UTF8.GetBytes(password);
            var blob = new byte[12 + 16 + plain.Length];
            var nonce = blob.AsSpan(0, 12);
            RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(Key(), 16);
            aes.Encrypt(nonce, plain, blob.AsSpan(28), blob.AsSpan(12, 16));
            return Convert.ToBase64String(blob);
        }

        protected override string? Decrypt(string blob)
        {
            try
            {
                var data = Convert.FromBase64String(blob);
                var plain = new byte[data.Length - 28];
                using var aes = new AesGcm(Key(), 16);
                aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plain);
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException)
            {
                return null;
            }
        }
    }
}
