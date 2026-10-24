using System.Text.Json;
using System.Text.Json.Nodes;
using Synapic.Indexer.Configuration;
using Synapic.Indexer.Contract.Configuration;

namespace Synapic.Indexer.Install;

/// <summary>
/// <c>--apply-config &lt;file&gt;</c>: merge a partial JSON configuration over the
/// current one, validate the result, and save it — or refuse and exit 2 with the
/// file untouched. The desktop app's Service tab is the caller: it cannot write
/// <c>%ProgramData%</c> unelevated, so it writes the patch to the user's temp
/// folder and this elevated command applies it, preserving every config member
/// this build does not know about (the same JSON-tree rule as
/// <c>--set-daminion-password</c>, D-11-07).
/// </summary>
public static class ApplyConfigCommand
{
    /// <summary>
    /// Merge, validate, save, then restart the service if it was running (the
    /// service reads its configuration once at startup). Exit codes: 0 applied,
    /// 1 the patch or config file could not be read or written, 2 validation
    /// refused the result.
    /// </summary>
    public static async Task<int> Run(
        IndexerConfigStore store,
        ServiceInstaller installer,
        string patchPath,
        TextWriter output,
        TextWriter error)
    {
        JsonObject patch;
        try
        {
            patch = JsonNode.Parse(await File.ReadAllTextAsync(patchPath).ConfigureAwait(false),
                    documentOptions: new JsonDocumentOptions
                    {
                        CommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true,
                    }) as JsonObject
                ?? throw new InvalidOperationException($"{patchPath} is not a JSON object.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            error.WriteLine($"Could not read the patch {patchPath}: {e.Message}");
            return 1;
        }

        JsonObject merged;
        try
        {
            merged = Merge(store.ReadTree(), patch);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            error.WriteLine($"Could not read {store.ConfigFilePath}: {e.Message}");
            return 1;
        }

        // Validate before saving: a broken configuration must never reach the
        // file a service start would read. The merged tree round-trips through
        // a temp file so the store's own reader (registry overrides included)
        // is what gets validated, not a second implementation.
        var tempPath = store.ConfigFilePath + ".apply.tmp";
        try
        {
            new IndexerConfigStore(configFilePathOverride: tempPath).SaveTree(merged);
            var load = new IndexerConfigStore(configFilePathOverride: tempPath).Load();
            var problems = load.Errors.Concat(IndexerConfigValidator.Validate(load.Config)).ToList();
            if (problems.Count > 0)
            {
                foreach (var problem in problems)
                    error.WriteLine($"Synapic Indexer configuration error - {problem}");
                error.WriteLine($"Nothing was saved; {store.ConfigFilePath} is unchanged.");
                return 2;
            }

            store.SaveTree(merged);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            error.WriteLine($"Could not write {store.ConfigFilePath}: {e.Message}");
            return 1;
        }
        finally
        {
            try { File.Delete(tempPath); } catch (IOException) { }
        }

        var restarted = await installer.RestartIfRunningAsync().ConfigureAwait(false);
        output.WriteLine(restarted
            ? $"Applied {patchPath} to {store.ConfigFilePath}; the service was restarted to pick it up."
            : $"Applied {patchPath} to {store.ConfigFilePath}; the service was not running, so it will read the change at its next start.");
        return 0;
    }

    /// <summary>
    /// Deep merge: objects merge member by member, any other value replaces.
    /// A patch names what it changes; everything else in the file survives.
    /// </summary>
    public static JsonObject Merge(JsonObject target, JsonObject patch)
    {
        foreach (var (key, value) in patch)
        {
            if (value is JsonObject patchObject && target[key] is JsonObject targetObject)
                Merge(targetObject, patchObject);
            else
                target[key] = value?.DeepClone();
        }
        return target;
    }
}
