using System.Security.Cryptography; // ProtectedData (DPAPI) via System.Security.Cryptography.ProtectedData
using System.Text;
using Microsoft.Win32;
using Synapic.Shared;

namespace Synapic.Avalonia.Services;

/// <summary>Per-user Daminion connection parameters (Step 1 convenience).</summary>
/// <summary>Daminion connection + Step 1 scope/filter/limit settings.</summary>
public sealed record DaminionConnectionParams(
    string ServerUrl,
    string Username,
    string Password,
    string CatalogId,

    // Scope + search
    string DaminionScope,
    string SearchTerm,

    // Saved search / shared collection (ids as strings, mirrored from the form)
    string SavedSearchId,
    string CollectionId,

    // Filters: status + untagged fields
    string StatusFilter,
    bool UntaggedKeywords,
    bool UntaggedCategories,
    bool UntaggedDescription,

    // Processing limits
    int MaxItems,
    int ResizeScale,
    bool UseThumbnailOverride);

/// <summary>
/// Persists the Daminion connection parameters to the Windows registry so the
/// Step 1 connection form comes back pre-filled on the next launch (spec §6.1
/// "remember the Daminion connection"): HKCU\Software\Synapic\Daminion.
///
/// Security notes:
/// - HKCU (per-user) — no elevation needed, survives app updates.
/// - The password is stored DPAPI-protected with <see
///   cref="ProtectedData"/> (CurrentUser scope): the ciphertext only
///   decrypts under the same Windows user, and is base64-encoded for
///   registry storage. Plain text is never written.
/// - On non-Windows platforms the store is a no-op (XDG config JSON remains
///   the persistence layer there), so the app runs unchanged on Linux/macOS.
/// </summary>
public sealed class DaminionConnectionStore
{
    private const string KeyPath = @"Software\Synapic\Daminion";
    private const string UrlValue = "ServerUrl";
    private const string UserValue = "Username";
    private const string PassValue = "PasswordEncrypted";
    private const string CatalogValue = "CatalogId";

    private const string ScopeValue = "DaminionScope";
    private const string SearchTermValue = "SearchTerm";
    private const string SavedSearchIdValue = "SavedSearchId";
    private const string CollectionIdValue = "CollectionId";
    private const string StatusFilterValue = "StatusFilter";
    private const string UntaggedKeywordsValue = "UntaggedKeywords";
    private const string UntaggedCategoriesValue = "UntaggedCategories";
    private const string UntaggedDescriptionValue = "UntaggedDescription";
    private const string MaxItemsValue = "MaxItems";
    private const string ResizeScaleValue = "ResizeScale";
    private const string UseThumbnailOverrideValue = "UseThumbnailOverride";

    private static readonly byte[] Entropy = "Synapic.Daminion.v1"u8.ToArray();

    private readonly string? _keyPathOverride;

    public DaminionConnectionStore(string? keyPathOverride = null)
    {
        _keyPathOverride = keyPathOverride;
    }

    /// <summary>Registry path reported in log lines (overridden path or default).</summary>
    private string KeyPathForLog => _keyPathOverride ?? KeyPath;

    /// <summary>Load the saved connection parameters; null when none saved.</summary>
    public DaminionConnectionParams? Load()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(EffectiveKeyPath);
            if (key is null) return null;

            var url = key.GetValue(UrlValue) as string;
            if (string.IsNullOrEmpty(url)) return null;

            var user = key.GetValue(UserValue) as string ?? "";
            var catalog = key.GetValue(CatalogValue) as string ?? "";

            string password = "";
            if (key.GetValue(PassValue) is byte[] blob && blob.Length > 0)
            {
                try
                {
                    var plain = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
                    password = Encoding.UTF8.GetString(plain);
                }
                catch (CryptographicException e)
                {
                    // Wrong user / corrupted blob: drop the secret, keep the rest.
                    SynapicLog.Warning(nameof(DaminionConnectionStore),
                        $"Saved password could not be decrypted ({e.Message}); re-enter it once");
                }
            }

            // Step 1 scope / filter / limit fields (all optional — unknown values
            // fall back to the form defaults, same as a brand-new first launch).
            var scope = key.GetValue(ScopeValue) as string ?? "all";
            var searchTerm = key.GetValue(SearchTermValue) as string ?? "";
            var savedSearchId = key.GetValue(SavedSearchIdValue) as string ?? "";
            var collectionId = key.GetValue(CollectionIdValue) as string ?? "";
            var statusFilter = key.GetValue(StatusFilterValue) as string ?? "all";

            var untaggedKeywords = key.GetValue(UntaggedKeywordsValue) is int ukw && ukw != 0;
            var untaggedCategories = key.GetValue(UntaggedCategoriesValue) is int ukc && ukc != 0;
            var untaggedDescription = key.GetValue(UntaggedDescriptionValue) is int uds && uds != 0;

            var maxItems = 100;
            var resizeScale = 100;
            var useThumbnailOverride = false;

            if (key.GetValue(MaxItemsValue) is int mi) maxItems = mi;
            if (key.GetValue(ResizeScaleValue) is int rs) resizeScale = rs;
            if (key.GetValue(UseThumbnailOverrideValue) is int uto && uto != 0)
                useThumbnailOverride = true;

            return new DaminionConnectionParams(
                url, user, password, catalog,
                scope, searchTerm, savedSearchId, collectionId,
                statusFilter, untaggedKeywords, untaggedCategories, untaggedDescription,
                maxItems, resizeScale, useThumbnailOverride);
        }
        catch (Exception e)
        {
            // Registry access issues must never block app startup.
            SynapicLog.Warning(nameof(DaminionConnectionStore),
                $"Failed to read connection params from registry: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Save the connection parameters. An empty password clears the stored
    /// secret (the remaining fields are still saved).
    /// </summary>
    public void Save(DaminionConnectionParams params_)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(EffectiveKeyPath, writable: true);
            key.SetValue(UrlValue, params_.ServerUrl, RegistryValueKind.String);
            key.SetValue(UserValue, params_.Username, RegistryValueKind.String);
            key.SetValue(CatalogValue, params_.CatalogId, RegistryValueKind.String);

            if (string.IsNullOrEmpty(params_.Password))
            {
                key.DeleteValue(PassValue, throwOnMissingValue: false);
            }
            else
            {
                var plain = Encoding.UTF8.GetBytes(params_.Password);
                var blob = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
                key.SetValue(PassValue, blob, RegistryValueKind.Binary);
            }

            // Step 1 scope / filter / limit fields.
            key.SetValue(ScopeValue, params_.DaminionScope, RegistryValueKind.String);
            key.SetValue(SearchTermValue, params_.SearchTerm, RegistryValueKind.String);
            key.SetValue(SavedSearchIdValue, params_.SavedSearchId, RegistryValueKind.String);
            key.SetValue(CollectionIdValue, params_.CollectionId, RegistryValueKind.String);
            key.SetValue(StatusFilterValue, params_.StatusFilter, RegistryValueKind.String);
            key.SetValue(UntaggedKeywordsValue, params_.UntaggedKeywords ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue(UntaggedCategoriesValue, params_.UntaggedCategories ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue(UntaggedDescriptionValue, params_.UntaggedDescription ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue(MaxItemsValue, params_.MaxItems, RegistryValueKind.DWord);
            key.SetValue(ResizeScaleValue, params_.ResizeScale, RegistryValueKind.DWord);
            key.SetValue(UseThumbnailOverrideValue, params_.UseThumbnailOverride ? 1 : 0, RegistryValueKind.DWord);

            SynapicLog.Info(nameof(DaminionConnectionStore),
                $"Saved Daminion connection + Step 1 params to registry ({KeyPathForLog}, {10} fields, password DPAPI-protected)");
        }
        catch (Exception e)
        {
            SynapicLog.Error(nameof(DaminionConnectionStore),
                $"Failed to save connection params to registry: {e.Message}");
        }
    }

    /// <summary>Clear every saved connection parameter (password included).</summary>
    public void Clear()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(EffectiveKeyPath, throwOnMissingSubKey: false);
            SynapicLog.Info(nameof(DaminionConnectionStore), $"Cleared registry key {KeyPathForLog}");
        }
        catch (Exception e)
        {
            SynapicLog.Warning(nameof(DaminionConnectionStore),
                $"Failed to clear registry key: {e.Message}");
        }
    }

    private string EffectiveKeyPath => _keyPathOverride ?? KeyPath;
}
