using Synapic.Core.Services.Daminion;
using Synapic.Core.Services.Processing;
using Synapic.Indexer.Configuration;

namespace Synapic.Indexer.Pipeline;

/// <summary>
/// Builds the catalog selection the desktop builds: one place for credential
/// decryption, scope normalization and the untagged filter, used by both the
/// detector and the executor so the scan and the write-back ask the same catalog
/// the same question.
/// </summary>
public static class DaminionSelectionBuilder
{
    public static DatasourceSelection Build(IndexerConfig config)
    {
        var daminion = config.Datasource.Daminion;

        if (string.IsNullOrWhiteSpace(daminion.EncryptedPassword))
        {
            throw new IndexerConfigurationException(
                "datasource.daminion.encryptedPassword",
                "is required; run Synapic.Indexer.exe --set-daminion-password as an administrator");
        }

        if (!DpapiCredentialProtector.TryUnprotect(daminion.EncryptedPassword, out var password, out var error)
            || string.IsNullOrEmpty(password))
        {
            throw new IndexerConfigurationException(
                "datasource.daminion.encryptedPassword",
                error ?? "could not be decrypted; re-run --set-daminion-password");
        }

        var scope = NormalizeScope(daminion.Scope);
        if (scope is "saved_search" or "collection")
        {
            // The config has no saved-search/collection id field, so pretending
            // to support these scopes would silently query the whole catalog.
            throw new IndexerConfigurationException(
                "datasource.daminion.scope",
                $"is '{daminion.Scope}', but this build can only query the whole catalog; " +
                "use 'all' (saved-search and collection ids are not configurable yet)");
        }

        var client = new DaminionApiClient(daminion.ServerUrl, daminion.Username, password);
        return new DatasourceSelection
        {
            IsDaminion = true,
            DaminionClient = client,
            Scope = scope,
            UntaggedFields = daminion.UntaggedFields.ToArray(),
            StatusFilter = "all",
            MaxItems = 0,
        };
    }

    /// <summary>The config's words mapped to the values the client understands.</summary>
    public static string NormalizeScope(string? scope) =>
        (scope ?? "").Trim().ToLowerInvariant() switch
        {
            "catalog" or "" => "all",
            "saved-search" or "saved_search" => "saved_search",
            "collection" => "collection",
            "search" => "search",
            var other => other,
        };
}
