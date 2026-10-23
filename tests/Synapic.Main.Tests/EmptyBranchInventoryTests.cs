using System.Text.RegularExpressions;
using Xunit;

namespace Synapic.Main.Tests;

/// <summary>
/// <c>docs/ui-empty-branches.md</c> is the finite form of <c>docs/ui-design.md</c> §10 criterion 6:
/// every reachable empty-result and report branch, with the behavioral test that names it. A row
/// naming a test nobody can run is worse than no row, because it reads as evidence — so the table is
/// resolved against the test sources instead of trusted.
///
/// What fails here: a citation in any table's <c>Named by</c> column naming a missing test — the
/// per-mode tables and the summary tables (busy + Stop, the gap-closing list) alike — a row marked Named
/// or Partial with no test at all, an unknown status, a duplicated row id, and a table that has shrunk to
/// nothing (which would make the criterion vacuously true). What does not: a <c>Gap</c> (no test yet) or
/// a <c>Shadowed</c> row (copy a later branch overwrites, so there is nothing to test). Both are tallied
/// in the document itself.
/// </summary>
public sealed class EmptyBranchInventoryTests
{
    private static readonly HashSet<string> AllowedStatuses =
        new(StringComparer.Ordinal) { "Named", "Partial", "Gap", "Shadowed" };

    /// <summary>An inventory row: <c>| T-01 | … | … | Named | `TestClass.Method` |</c>.</summary>
    private static readonly Regex RowStart = new(@"^\|\s*([TRDUS]-\d+)\s*\|", RegexOptions.Compiled);

    /// <summary>A citation inside the "Named by" cell.</summary>
    private static readonly Regex Citation =
        new(@"([A-Z][A-Za-z0-9]*Tests)\.([A-Za-z0-9_]+)", RegexOptions.Compiled);

    /// <summary>
    /// The inventory cannot legitimately shrink to a handful of rows: the branches it lists are the
    /// shipped UI's, not a sample of them. This floor exists so an emptied or truncated document fails
    /// instead of passing every other check by having nothing to check.
    /// </summary>
    private const int MinimumRows = 40;

    [Fact]
    public void Every_inventory_row_names_a_test_that_exists()
    {
        var testsDirectory = FindDirectory("tests", "Synapic.Main.Tests");
        var violations = Check(File.ReadAllText(FindDoc()),
            (className, method) => TestExists(testsDirectory, className, method));

        Assert.True(violations.Count == 0,
            "docs/ui-empty-branches.md claims tests that are not there:" +
            Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", violations));
    }

    [Fact]
    public void The_inventory_still_covers_the_branches_it_was_built_from()
    {
        var rows = File.ReadAllLines(FindDoc()).Count(line => RowStart.IsMatch(line));

        Assert.True(rows >= MinimumRows,
            $"docs/ui-empty-branches.md holds {rows} row(s); §10 criterion 6 is only checkable while the " +
            $"inventory carries the branches it enumerated (at least {MinimumRows}).");
    }

    [Fact]
    public void Every_row_status_is_one_the_inventory_uses()
    {
        var doc = File.ReadAllText(FindDoc());
        var statuses = Rows(doc).Select(cells => cells[4].Trim()).ToList();

        Assert.All(statuses, status => Assert.Contains(status, AllowedStatuses));
    }

    [Fact]
    public void The_check_rejects_a_missing_test_an_unbacked_row_and_an_unknown_status()
    {
        // The guard above is only worth having if it can fail. This is the same check run over a
        // table whose tests do not exist, so the failure path is pinned rather than assumed.
        const string document = """
                                | Id | Branch | Where | Status | Named by |
                                |----|--------|-------|--------|----------|
                                | T-90 | renamed away | `A.cs:1` | Named | `GoneTests.No_longer_exists` |
                                | T-91 | says nothing | `A.cs:2` | Named | — |
                                | T-92 | an honest gap | `A.cs:3` | Gap | — |
                                | T-93 | odd status | `A.cs:4` | Probably | `GoneTests.Whatever` |
                                """;

        var violations = Check(document, (_, _) => false);

        Assert.Contains(violations, v => v.Contains("T-90") && v.Contains("GoneTests.No_longer_exists"));
        Assert.Contains(violations, v => v.Contains("T-91"));
        Assert.Contains(violations, v => v.Contains("T-93") && v.Contains("Probably"));
        Assert.DoesNotContain(violations, v => v.Contains("T-92"));
    }

    [Fact]
    public void The_busy_and_stop_table_is_part_of_the_check()
    {
        // That table has no row ids, so the check once read only the per-mode tables and never saw it —
        // and a citation inside it had drifted undetected. Its claims must reach the citation check the
        // same way a T-01 row does; this fails them all on purpose, so the busy rows are seen reaching it.
        var violations = Check(File.ReadAllText(FindDoc()), (_, _) => false);

        Assert.Contains(violations, v =>
            v.Contains("One pass in any operation locks the others out") &&
            v.Contains("OperationGatingTests.A_pass_in_any_operation_locks_every_other_one_out"));
    }

    /// <summary>
    /// The whole check, so the real document and the failing sample run the same code. Every table that
    /// carries a <c>Named by</c> column owes citations that resolve — not just the per-mode rows, which
    /// additionally answer for their id, status and shape. Tables without that column (the exclusions,
    /// the tally) promise nothing and are skipped.
    /// </summary>
    private static List<string> Check(string markdown, Func<string, string, bool> testExists)
    {
        var violations = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var namedByColumn = -1;

        foreach (var line in markdown.Split('\n'))
        {
            if (!line.StartsWith('|'))
            {
                namedByColumn = -1;
                continue;
            }

            var cells = line.Split('|');

            // A header row names its columns; remember where this table keeps its evidence.
            var header = Array.FindIndex(cells, cell => cell.Trim() == "Named by");
            if (header >= 0)
            {
                namedByColumn = header;
                continue;
            }

            if (namedByColumn < 0 || IsSeparatorRow(cells)) continue;

            var rowStart = RowStart.Match(line);
            var owesTest = false;

            if (rowStart.Success)
            {
                var id = rowStart.Groups[1].Value;
                if (!seen.Add(id)) violations.Add($"row {id} appears more than once");

                if (cells.Length != 7)
                {
                    violations.Add($"row {id} is not a five-column table row (found {cells.Length - 2})");
                    continue;
                }

                var status = cells[4].Trim();
                if (!AllowedStatuses.Contains(status))
                    violations.Add($"row {id} has status \"{status}\", which is not Named, Partial, Gap or Shadowed");

                // Gap means no test exists; Shadowed means the copy is unreachable so none is owed. Named
                // and Partial are the two statuses that claim a test backs the row.
                owesTest = status is "Named" or "Partial";
            }
            else if (cells.Length <= namedByColumn)
            {
                continue;
            }

            var citations = Citation.Matches(cells[namedByColumn])
                .Select(m => (Class: m.Groups[1].Value, Method: m.Groups[2].Value))
                .ToList();

            if (owesTest && citations.Count == 0)
                violations.Add($"{RowLabel(rowStart, cells)} is marked Named but names no test");

            foreach (var citation in citations)
            {
                if (!testExists(citation.Class, citation.Method))
                    violations.Add($"{RowLabel(rowStart, cells)} names {citation.Class}.{citation.Method}, " +
                                   "which does not exist in tests/Synapic.Main.Tests");
            }
        }

        return violations;
    }

    /// <summary>Per-mode rows are named by id; summary-table rows by the claim they make.</summary>
    private static string RowLabel(Match rowStart, string[] cells) =>
        rowStart.Success ? $"row {rowStart.Groups[1].Value}" : $"table row \"{cells[1].Trim().Trim('`')}\"";

    private static bool IsSeparatorRow(string[] cells) =>
        cells.Skip(1).Take(cells.Length - 2)
            .All(cell => Regex.IsMatch(cell.Trim(), @"^:?-{2,}:?$"));

    private static IEnumerable<string[]> Rows(string markdown) =>
        markdown.Split('\n')
            .Where(line => RowStart.IsMatch(line))
            .Select(line => line.Split('|'))
            .Where(cells => cells.Length == 7);

    /// <summary>A test is present when its class file exists and declares the method.</summary>
    private static bool TestExists(string testsDirectory, string className, string method)
    {
        var file = Path.Combine(testsDirectory, className + ".cs");
        if (!File.Exists(file)) return false;

        var declaration = new Regex(@"\b" + Regex.Escape(method) + @"\s*\(");
        return declaration.IsMatch(File.ReadAllText(file));
    }

    private static string FindDoc() => FindFile("docs", "ui-empty-branches.md");

    private static string FindDirectory(params string[] parts) => Find(parts, directory: true);

    private static string FindFile(params string[] parts) => Find(parts, directory: false);

    private static string Find(string[] parts, bool directory)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && current is not null; i++, current = current.Parent)
        {
            var candidate = Path.Combine(new[] { current.FullName }.Concat(parts).ToArray());
            if (directory ? Directory.Exists(candidate) : File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException(
            $"Could not find {Path.Combine(parts)} above {AppContext.BaseDirectory}; " +
            "this check needs the repository checkout, not just the build output.");
    }
}
