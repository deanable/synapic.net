using System.Reflection;
using Synapic.Indexer.Configuration;
using Xunit;

namespace Synapic.Indexer.Tests;

/// <summary>
/// docs/indexer-operations.md is the operator's only path to a working service.
/// A configuration key that exists in code but not in that table is a support
/// call waiting to happen, so the table is checked against the model instead of
/// trusted.
/// </summary>
public sealed class ConfigDocumentationTests
{
    /// <summary>
    /// Keys that are deliberately undocumented. Empty on purpose: the gate
    /// requires the doc to describe every key the loader accepts.
    /// </summary>
    private static readonly HashSet<string> AllowedOmissions = new(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Every_configuration_key_is_documented_in_the_operations_guide()
    {
        var doc = File.ReadAllText(FindDoc());
        var missing = ConfigurationPaths()
            .Where(path => !AllowedOmissions.Contains(path))
            .Where(path => !doc.Contains(path, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(missing.Count == 0,
            "docs/indexer-operations.md is missing these configuration keys: " + string.Join(", ", missing));
    }

    [Fact]
    public void The_guide_names_every_failure_mode_from_the_specification()
    {
        var doc = File.ReadAllText(FindDoc());

        // One row per docs/windows-indexer.md §8 failure. Wording is the doc's,
        // the subject is the specification's.
        foreach (var subject in new[]
                 {
                     "Sidecar crashed", "File locked", "Daminion server unreachable",
                     "shutdown or reboot", "Corrupt or unsupported image",
                 })
        {
            Assert.True(doc.Contains(subject, StringComparison.OrdinalIgnoreCase),
                $"docs/indexer-operations.md has no troubleshooting row for \"{subject}\".");
        }
    }

    [Fact]
    public void The_guide_does_not_claim_the_msi_registers_the_service_or_that_uninstall_deletes_data()
    {
        var doc = File.ReadAllText(FindDoc());

        Assert.Contains("does not register the service", doc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("kept", doc, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("uninstall removes the data", doc, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The JSON paths the loader accepts, derived from the model's own nesting.</summary>
    private static IReadOnlyList<string> ConfigurationPaths()
    {
        var paths = new List<string> { "version" };
        Walk(typeof(ServiceSettings), "service", paths);
        Walk(typeof(ScheduleSettings), "schedule", paths);
        Walk(typeof(EngineSettings), "engine", paths);
        Walk(typeof(DatasourceSettings), "datasource", paths);
        Walk(typeof(DaminionSettings), "datasource.daminion", paths);
        Walk(typeof(TaggingSettings), "tagging", paths);
        return paths;
    }

    private static void Walk(Type type, string prefix, List<string> paths)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0) continue;

            var name = JsonName(property.Name);
            var path = $"{prefix}.{name}";

            if (IsSettingsSection(property.PropertyType))
            {
                Walk(property.PropertyType, path, paths);
                continue;
            }

            paths.Add(path);
        }
    }

    private static bool IsSettingsSection(Type type) =>
        type == typeof(ServiceSettings) || type == typeof(ScheduleSettings) ||
        type == typeof(EngineSettings) || type == typeof(DatasourceSettings) ||
        type == typeof(DaminionSettings) || type == typeof(TaggingSettings);

    /// <summary>System.Text.Json's web default: the property name with a lower-case first letter.</summary>
    private static string JsonName(string propertyName) =>
        char.ToLowerInvariant(propertyName[0]) + propertyName[1..];

    private static string FindDoc()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "indexer-operations.md");
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException(
            "docs/indexer-operations.md was not found above " + AppContext.BaseDirectory +
            "; this test needs the repository checkout, not just the build output.");
    }
}
