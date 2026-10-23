# 08-02 SUMMARY — The operator manual and the test that keeps it true

**Status:** Complete
**Requirements:** OPS-04

## What was built

| File | Role |
|------|------|
| [docs/indexer-operations.md](../../../docs/indexer-operations.md) | 318 lines: install (Inno, MSI, unpacked payload), upgrade, both configuration sources and precedence, the full key table with defaults and ranges, a complete example, Daminion credentials, service account, operating commands, the five §8 failure modes, uninstall and limitations |
| [ConfigDocumentationTests.cs](../../../tests/Synapic.Indexer.Tests/ConfigDocumentationTests.cs) | Reflects over `IndexerConfig` and its nested types, builds every camelCase JSON path and fails listing any path missing from the operations doc |
| [README.md](../../../README.md), [docs/README.md](../../../docs/README.md) | Indexer line pointing at the operations doc (and the spec for rationale); index row in the docs table |
| [docs/packaging.md](../../../docs/packaging.md) | Payload layout gains `Synapic.Indexer.exe` / `Synapic.Core.dll`, the publish order and the installer's install step |

The doc is written against the code, not the spec: ranges come from `IndexerConfigValidator`
(`intervalMinutes` 1–1440, thresholds 0–1, the three schedule modes, the four datasource scopes), commands come
from `ServiceCommandLine` and `StatusCommand`, and log lines are quoted from the real sink format.

## Evidence

```
dotnet test tests/Synapic.Indexer.Tests -c Release --nologo --filter "FullyQualifiedName~ConfigDocumentationTests"
Passed!  - Failed: 0, Passed: 3, Skipped: 0, Total: 3
```

The test reflects over the model rather than a hand-kept list, so a new key in `IndexerConfig` fails the run with
its JSON path named until the doc gains a row. The key table was extended until every path passed; no assertion
was skipped and the skip list is empty at the gate.

Links (both files exist, both resolve):

```
README.md:344:│   ├── indexer-operations.md       # Background indexer: install, configure, operate, troubleshoot
docs/README.md:20:| [`indexer-operations.md`](indexer-operations.md) | **Operators start here.** …
docs/packaging.md:117:troubleshooting) live in [indexer-operations.md](indexer-operations.md).
```

## Claims checked against the acceptance run

| Claim | Verified by |
|-------|-------------|
| Uninstall removes the service and keeps data | 08-03: `sc query` 1060, `%ProgramData%\Synapic` intact, queue still readable |
| The MSI does not register the service | Docs and `[UninstallRun]`/`.iss` inspection; MSI not built here (stated in 08-01) |
| `--install` needs elevation and is idempotent | 08-01's fake-`IScRunner` tests plus the real install/uninstall round trip |
| Service runs as `NT AUTHORITY\LocalService` | `sc qc` after install #2 (08-01) |
| First install stops until a configuration exists | 08-03 log: two configuration errors, exit 2, `sc start` 1053 |

## Stated limits

- The troubleshooting table covers the spec's §8 five failure modes; a live Daminion outage is covered by
  `ResilienceTests` (fake network), not by a server this machine can reach, and the doc says so where it matters.
- The doc's `sc.exe config … obj=/password=` example for a domain account was not executed; it is the standard
  SCM form, and the default account path is what the acceptance exercised.
