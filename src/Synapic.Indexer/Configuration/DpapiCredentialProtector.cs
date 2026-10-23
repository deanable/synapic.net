using System.Security.Cryptography;
using System.Text;

namespace Synapic.Indexer.Configuration;

/// <summary>
/// Protects the Daminion password with DPAPI's machine scope: the service runs
/// as a service account, so a user-scoped secret would be unreadable to it
/// (D-11-07). The trade-off is stated where the decision is recorded — any local
/// administrator can decrypt it — and is bounded by the ProgramData file's ACLs.
///
/// Nothing here logs: the plaintext, the ciphertext and the exceptions' data are
/// all kept out of the log pipeline on purpose.
/// </summary>
public static class DpapiCredentialProtector
{
    private const DataProtectionScope Scope = DataProtectionScope.LocalMachine;

    public static bool TryProtect(string plaintext, out string? ciphertextBase64, out string? error)
    {
        ciphertextBase64 = null;
        error = null;
        try
        {
            var protectedBytes = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plaintext), optionalEntropy: null, Scope);
            ciphertextBase64 = Convert.ToBase64String(protectedBytes);
            return true;
        }
        catch (CryptographicException e)
        {
            error = $"DPAPI could not protect the password ({e.GetType().Name})";
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            error = "DPAPI is only available on Windows";
            return false;
        }
    }

    public static bool TryUnprotect(string ciphertextBase64, out string? plaintext, out string? error)
    {
        plaintext = null;
        error = null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(ciphertextBase64), optionalEntropy: null, Scope);
            plaintext = Encoding.UTF8.GetString(bytes);
            return true;
        }
        catch (FormatException)
        {
            error = "the stored password is not valid base64";
            return false;
        }
        catch (CryptographicException e)
        {
            error = $"DPAPI could not decrypt the stored password ({e.GetType().Name}); re-run --set-daminion-password";
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            error = "DPAPI is only available on Windows";
            return false;
        }
    }
}
