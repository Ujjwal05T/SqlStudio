using System.Diagnostics;
using System.Text;

namespace SqlStudio.Services;

public interface ICredentialStore
{
    string? Get(Guid profileId);
    void Set(Guid profileId, string password);
    void Delete(Guid profileId);
}

/// <summary>
/// Stores passwords in the macOS login Keychain through the `security` tool. Secrets are sent over
/// stdin (`security -i`) so they never appear in the process list.
/// </summary>
public sealed class MacKeychain : ICredentialStore
{
    readonly string _service;

    public MacKeychain(string service = "SqlStudio") => _service = service;

    public string? Get(Guid profileId)
    {
        var (code, output) = Run(null, "find-generic-password", "-s", _service, "-a", profileId.ToString(), "-w");
        if (code != 0)
            return null;
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(output.Trim()));
        }
        catch (FormatException)
        {
            return null; // not written by this app
        }
    }

    public void Set(Guid profileId, string password)
    {
        var command = $"add-generic-password -U -s {Quote(_service)} -a {Quote(profileId.ToString())} -w {Quote(Encode(password))}\n";
        var (code, output) = Run(command, "-i");
        if (code != 0 || output.Contains("security:", StringComparison.Ordinal))
            throw new InvalidOperationException("Could not save the password to the Keychain: " + output.Trim());
    }

    public void Delete(Guid profileId) =>
        Run(null, "delete-generic-password", "-s", _service, "-a", profileId.ToString());

    // `security -w` prints non-ASCII secrets as hex, so store base64 to get the same text back.
    static string Encode(string password) => Convert.ToBase64String(Encoding.UTF8.GetBytes(password));

    static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    static (int Code, string Output) Run(string? stdin, params string[] args)
    {
        var psi = new ProcessStartInfo("/usr/bin/security")
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
        return (process.ExitCode, stdout + stderr.Result);
    }
}
