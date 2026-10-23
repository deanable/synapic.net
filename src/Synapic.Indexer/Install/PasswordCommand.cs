using System.Text;
using Synapic.Indexer.Configuration;

namespace Synapic.Indexer.Install;

/// <summary>
/// <c>--set-daminion-password</c> (D-11-07): read the password without echoing it,
/// encrypt with DPAPI LocalMachine, write only that member of the config file.
/// The value never reaches an argument list, the Event Log or the service log.
/// </summary>
public static class PasswordCommand
{
    public static int Run(IndexerConfigStore store, Func<string?> readPassword, TextWriter output)
    {
        output.Write("Daminion password: ");
        var password = readPassword();
        output.WriteLine();

        if (string.IsNullOrEmpty(password))
        {
            output.WriteLine("No password entered; nothing was saved.");
            return 1;
        }

        if (!DpapiCredentialProtector.TryProtect(password, out var ciphertext, out var protectError))
        {
            output.WriteLine($"Not saved: {protectError}");
            return 1;
        }

        try
        {
            store.SaveDaminionPassword(ciphertext!);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            output.WriteLine($"Not saved: {store.ConfigFilePath} could not be written ({e.Message}).");
            return 1;
        }

        output.WriteLine($"Saved to {store.ConfigFilePath} (DPAPI LocalMachine). The service reads it at startup.");
        return 0;
    }

    /// <summary>
    /// Reads a line without echoing it. Backspace edits; Enter ends. Control keys
    /// are ignored rather than becoming invisible characters in the secret.
    /// </summary>
    public static string? ReadWithoutEcho()
    {
        var buffer = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) return buffer.ToString();
            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0) buffer.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar)) buffer.Append(key.KeyChar);
        }
    }
}
