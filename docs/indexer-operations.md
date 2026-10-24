# Synapic Background Indexer — Operations Guide

The Synapic Indexer (`Synapic.Indexer.exe`) is a Windows service that tags images
without the desktop app: it scans folders (or a Daminion catalog) on a schedule
and/or watches them for new files, runs them through the same tagging engine as
the desktop, and writes the tags back to the files or catalog. Design rationale
lives in [windows-indexer.md](windows-indexer.md); this page is the how-to.

Who it is for: an administrator who installs the service once, points it at a
folder or catalog, and then only looks at logs and `--status`.

---

## Install

The service needs Windows 10/11 or Windows Server 2019+, the .NET 10 Desktop
Runtime (the installer provides it), and a sidecar (`synapic-inference.exe`) in
the same folder.

**Inno Setup installer (recommended).** Run `Synapic-Setup-win-x64.exe` from an
elevated prompt (or the normal GUI). It installs the app, the indexer and the CPU
sidecar, creates `%ProgramData%\Synapic`, registers the `SynapicIndexer` Event Log
source, and creates the service with automatic start (as `NT AUTHORITY\LocalService`)
and its recovery actions; it then runs `--install`, which tries to start it. On a
first install there is no configuration yet, so the service registers and stops
with a configuration error — the finish page says so, and the log names the key.
Write the configuration below, then start it:

```powershell
sc.exe start SynapicIndexer
```

An install over an existing configuration starts the service straight away. For
an unattended install:

```powershell
Start-Process -Wait -FilePath .\Synapic-Setup-win-x64.exe -ArgumentList '/VERYSILENT','/NORESTART'
```

**MSI.** The MSI ships the same files and registers the same service: its
install runs `Synapic.Indexer.exe --install` as a deferred, elevated custom
action, and its uninstall runs `--uninstall`. For an unattended install:

```powershell
Start-Process -Wait msiexec.exe -ArgumentList '/i','.\Synapic-win-x64.msi','/qn','/norestart'
```

The bundle (`Synapic-Setup-win-x64-msi.exe`) installs the .NET runtime first and
then runs this MSI. If a registration fails during the MSI install, the install
rolls back rather than leaving the app beside no service.

**From an unpacked payload** (development or a manual deployment): copy
`artifacts\win-x64` to the target and run:

```powershell
& .\Synapic.Indexer.exe --install
```

`--install` is idempotent: it creates the service if it is missing, always
refreshes the recovery actions (restart three times at 60 s, failure count reset
daily), ensures the Event Log source and the ProgramData tree with its ACLs, and
starts the service if it is not already running — which needs a valid
configuration, so on a fresh machine the start succeeds only after you write one.
It must be run elevated.
Close any desktop instance first only if you are replacing files by hand.

### Upgrade

Run the newer installer (it keeps the previous install directory and Add/Remove
Programs entry — same AppId), or `--install` again from the new payload. The
queue, configuration and logs under `%ProgramData%\Synapic` are not touched by an
upgrade. Stop the service first if you are replacing the binaries manually.

---

## Configure

Configuration is read at startup from two sources, in this order:

1. `HKLM\SOFTWARE\Synapic\Indexer` — machine defaults, one registry value per
   JSON path (`service.logLevel`, `schedule.intervalMinutes`, …). `REG_DWORD`
   works for switches, `REG_MULTI_SZ` (or a `;`-joined `REG_SZ`) for lists.
2. `%ProgramData%\Synapic\indexer-config.json` — applied on top, value by value.

The file wins for every key it contains; keys it omits keep the registry/default
value. Unknown keys are logged as warnings and ignored (a newer config does not
break an older build), and every problem is reported at startup with the dotted
path, e.g. `Synapic Indexer configuration error - schedule.intervalMinutes: must
be between 1 and 1440 minutes; was 0`. The service does not start with an invalid
configuration.

Defaults are the built-in values; the registry is empty on a fresh install.

### Example

```json
{
  "version": 1,
  "service": {
    "autoStart": true,
    "logLevel": "Information",
    "maxConcurrentItems": 4,
    "idleSidecarTimeoutMinutes": 30
  },
  "schedule": {
    "mode": "IntervalAndWatch",
    "intervalMinutes": 15,
    "watchPaths": [ "D:\\Media\\Incoming" ],
    "watchRecursive": true
  },
  "engine": {
    "device": "cpu",
    "modelId": "LiquidAI/LFM2.5-VL-1.6B",
    "confidenceThreshold": 0.3,
    "probabilityMode": "both",
    "probabilityThreshold": 0.5,
    "userPrompt": null
  },
  "datasource": { "type": "local" },
  "tagging": {
    "writeCategory": true,
    "writeKeywords": true,
    "writeDescription": true,
    "overwriteExistingTags": false
  }
}
```

A Daminion datasource replaces the `datasource` block:

```json
"datasource": {
  "type": "daminion",
  "daminion": {
    "serverUrl": "http://daminion.example:8080",
    "catalogId": 1,
    "username": "indexer-bot",
    "encryptedPassword": "<written by --set-daminion-password>",
    "scope": "all",
    "untaggedFields": [ "keywords", "categories" ]
  }
}
```

### Keys

| Key | Type | Default | Accepted | Meaning |
|-----|------|---------|----------|---------|
| `version` | int | `1` | `1` | Schema version. Refuse to guess a future one. |
| `service.autoStart` | bool | `true` | `true`/`false` | Accepted for schema compatibility; this build always registers the service with automatic start (see Limitations). |
| `service.logLevel` | string | `Information` | `Verbose`, `Debug`, `Information`, `Warning`, `Error`, `Fatal` | Minimum level written to the rolling file. |
| `service.maxConcurrentItems` | int | `4` | 1–32 | Sidecar requests in flight; also sets the batch size (`×8`, max 64 rows). |
| `service.idleSidecarTimeoutMinutes` | int | `30` | 0–1440 | Stop the sidecar after this long without work; `0` never stops it. It restarts on the next batch. |
| `schedule.mode` | string | `IntervalAndWatch` | `Interval`, `Watch`, `IntervalAndWatch` | Interval scan, filesystem watcher, or both. A Daminion datasource cannot use `Watch`. |
| `schedule.intervalMinutes` | int | `15` | 1–1440 | Minutes between scans. |
| `schedule.watchPaths` | string[] | `[]` | absolute, existing paths | Folders to scan/watch. Required (and must exist) for `Watch`/`IntervalAndWatch` on a local datasource. |
| `schedule.watchRecursive` | bool | `true` | `true`/`false` | Recurse into subfolders when scanning and watching. |
| `engine.device` | string | `cpu` | `cpu`, `cuda`, `mps` | Device passed to the sidecar. `cuda` needs the CUDA sidecar bundle. |
| `engine.modelId` | string | (empty) | any model the sidecar can load | **Required.** For example `LiquidAI/LFM2.5-VL-1.6B` or `LiquidAI/LFM2.5-VL-450M`. |
| `engine.confidenceThreshold` | number | `0.3` | 0–1 | Minimum confidence for a returned label. |
| `engine.probabilityMode` | string | `both` | `both`, `threshold` | Whether probabilities are reported as `both` modes or thresholded. |
| `engine.probabilityThreshold` | number | `0.5` | 0–1 | Threshold used when `probabilityMode` is `threshold`. |
| `engine.userPrompt` | string/null | `null` | text or `null` | Overrides the sidecar's built-in tag instruction; `null` uses the built-in one. |
| `datasource.type` | string | `local` | `local`, `daminion` | Where work comes from. |
| `datasource.daminion.serverUrl` | string | (empty) | absolute http(s) URL | Daminion server base URL; required for a Daminion datasource. |
| `datasource.daminion.catalogId` | int | `0` | 0 or a catalog id | Catalog to work in; `0` means the server's default. |
| `datasource.daminion.username` | string | (empty) | any user | Account the service logs in with. |
| `datasource.daminion.encryptedPassword` | string | (empty) | DPAPI ciphertext | Written by `--set-daminion-password`; never edit by hand. |
| `datasource.daminion.scope` | string | `all` | `all`, `catalog` | Which catalog items to consider. Saved searches/collections need ids this build cannot store yet. |
| `datasource.daminion.untaggedFields` | string[] | `[]` | field names | Fields a record must be missing to count as untagged, e.g. `keywords`, `categories`. |
| `tagging.writeCategory` | bool | `true` | `true`/`false` | Write the model's category. At least one `tagging.write*` must be true. |
| `tagging.writeKeywords` | bool | `true` | `true`/`false` | Write the model's keywords. |
| `tagging.writeDescription` | bool | `true` | `true`/`false` | Write the model's description. |
| `tagging.overwriteExistingTags` | bool | `false` | `true`/`false` | `false`: a field that already has a value is left alone, and a file with every selected field filled is completed without calling the model. `true`: write the model's values (keywords still merge — see Limitations). |

---

## Daminion credentials

The password is never stored in plain text. From an elevated **console** on the
service machine:

```powershell
& "C:\Program Files\Synapic\Synapic.Indexer.exe" --set-daminion-password
```

Type the password at the prompt (it is not echoed). It is encrypted with DPAPI at
**LocalMachine** scope and written to
`%ProgramData%\Synapic\indexer-config.json` as
`datasource.daminion.encryptedPassword`, preserving every other key in the file.
The service reads it at startup, so restart the service after changing it.

Re-run the command after the Daminion account's password changes; the service
cannot ask for a new one. Any local administrator can decrypt the value by
design — the ProgramData ACL limits who can read the file, and the machine is the
trust boundary.

---

## Service account

The default account is `NT AUTHORITY\LocalService` (least privilege, no password
to manage). It can read local folders it is granted access to; a folder under
another user's profile normally cannot be read by it.

For UNC shares or a domain catalog, run the service under a dedicated domain
account (elevated):

```powershell
sc.exe config SynapicIndexer obj= "DOMAIN\svc-synapic" password= "<account password>"
```

That account needs:

- **read/write** on every watch folder (the service writes metadata next to the
  image), including network shares reached over SMB;
- **modify** on `%ProgramData%\Synapic` (queue, logs, config) — `--install` grants
  `SYSTEM` and `LocalService`; add your account deliberately:
  `icacls "C:\ProgramData\Synapic" /grant "DOMAIN\svc-synapic:(OI)(CI)M" /T`;
- **log on as a service** (the `sc.exe config` above sets it; Group Policy may
  also need the account listed).

---

## Operating

```powershell
sc.exe query SynapicIndexer          # state
sc.exe start SynapicIndexer          # start (elevated)
sc.exe stop SynapicIndexer           # stop (elevated); the service budget is 10 s
& "C:\Program Files\Synapic\Synapic.Indexer.exe" --console   # foreground, Ctrl+C to stop
```

`--console` runs the same host without the SCM (useful for a first run or a
debug session) and stops the sidecar on Ctrl+C within the same budget.

`--status` is read-only: it opens the queue without starting the host, creating
directories, or writing anything.

```text
> Synapic.Indexer.exe --status
queue: C:\ProgramData\Synapic\state\indexer.db
Pending 2
InFlight 1
Completed 128
TransientFailure 1
Failed 0
Recent failures:
  41  local|d:\media\incoming\broken.jpg  the image could not be decoded: cannot identify image file
```

`--status --failed` lists up to 50 failed and retrying items with their key,
attempt count and the first 160 characters of the last error — the view to use
when the five-line summary is not enough.

```text
> Synapic.Indexer.exe --status --failed
queue: C:\ProgramData\Synapic\state\indexer.db
Failures (newest first, up to 50):
  41  local|d:\media\incoming\broken.jpg  attempts=1  the image could not be decoded: cannot identify image file
```

### Changing the configuration: `--apply-config` (elevated)

`--apply-config <file>` merges a partial JSON file over the current
configuration (members this build does not know survive), validates the merged
result before anything is saved — a refusal exits 2 and leaves the file
unchanged — writes it, and restarts the service if it was running, because the
service reads its configuration once at startup.

```text
> Synapic.Indexer.exe --apply-config "%TEMP%\synapic-apply-config.json"
Applied \Temp\synapic-apply-config.json to C:\ProgramData\Synapic\indexer-config.json; the service was restarted to pick it up.
```

This is also what **Synapic.ServiceConsole.exe** (the service's own UI, v1.4)
runs when you save watch folders: the console (running unelevated) writes the
patch to your temp folder, then launches this elevated command and waits for
it, so the GUI never writes ProgramData directly and one validation
implementation serves both the CLI and the GUI. Before exiting, the command
writes `<patch>.result.json` — the exit code, whether the service was
restarted, and the messages — and the console reads it after the process
exits, so a save either visibly happened or visibly did not (a declined UAC
prompt or a refused configuration is reported, never silently dropped).

### Logs

| Where | What |
|-------|------|
| `%ProgramData%\Synapic\Logs\indexer-YYYYMMDD.log` | Everything at `service.logLevel` and above, one file per day, 30 kept. |
| Windows Application log, source `SynapicIndexer` | `Warning` and above, when the source exists. Created by `--install` / the installer. |

Each batch writes one line with its id and counts, e.g.
`batch 7: claimed=4 completed=3 transient=1 failed=0 preserved=0 elapsed=12.4s`,
so a queue row can be tied to the log without logging file contents.

---

## Troubleshooting

The five failure modes the service is designed to survive. In every row the item
is recorded in the queue, so `--status` / `--status --failed` shows what happened.

| Symptom | What you see | What the service does | What you do |
|---------|--------------|----------------------|-------------|
| **Sidecar crashed / out of memory** | `The inference sidecar restarted after a crash.` or `did not restart`; rows `TransientFailure` | Restarts `synapic-inference.exe` with 1 s/2 s/4 s backoff, puts the affected rows back and retries once; if it cannot restart, rows retry in 5 minutes and the service stays up | Check free memory/GPU and the sidecar line in the log; the next interval retries. A model that needs more memory than the machine has will keep failing — choose a smaller `engine.modelId`. |
| **File locked by another program** | `the file is in use: …`; row `TransientFailure` due in 60 s | Retries after 60 s; other files in the batch continue | Close the program holding the file. Nothing else is needed; the row is retried automatically. |
| **Daminion server unreachable** | `Catalog unavailable; the run is paused until …`; rows go back to `Pending` | Pauses the run for 5 minutes, then tries again; no item is dropped | Check the server URL, network and credentials. Work resumes on its own when the server answers. A wrong password fails the batch and logs a Fatal line — fix it with `--set-daminion-password` and restart. |
| **Service/OS shutdown or reboot mid-batch** | After start: `Recovered N InFlight job(s) left by the previous run; they are Pending again.` | The queue is the checkpoint: claimed rows return to `Pending` and are processed again | Nothing. Start the service (it starts automatically after a reboot). |
| **Corrupt or unsupported image** | `the image could not be decoded: …`; row `Failed` | Marks it permanently `Failed` on the first attempt with the error stored, and continues the batch | Look at it with `--status --failed`. Repair or replace the file; a changed file is enqueued again automatically. |

If the service does not start at all, the reason is in the last log file and in
the Application log; a configuration error names the exact key, and an install
problem names the `sc.exe` exit code.

---

## Uninstall

Elevated:

```powershell
& "C:\Program Files\Synapic\Synapic.Indexer.exe" --uninstall
```

or uninstall "Synapic" from Add/Remove Programs (its uninstaller runs the same
command before removing the files). This stops the service, waits up to 10 s for
it to reach STOPPED, deletes the service, and leaves `%ProgramData%\Synapic`
intact — the queue database, logs and configuration are **kept** so an uninstall
does not destroy an index or its log trail. It also leaves the Event Log source.
To remove the data deliberately, after uninstalling:

```powershell
Remove-Item -Recurse -Force "C:\ProgramData\Synapic"
```

The desktop app's uninstaller separately removes the per-user model cache
(`%LOCALAPPDATA%\Synapic\models`), so the next install downloads weights again.

---

## Limitations in v1.1

- **The MSI needs elevation at install time** (as any perMachine package does),
  because registering the service, the Event Log source and the ProgramData ACLs
  all require administrator rights. A non-elevated `msiexec` fails with the
  Windows Installer elevation prompt or exit code 1603, not a partial install.
- **Keywords merge; they are never deleted.** `tagging.overwriteExistingTags:
  true` means "write the model's values", not "remove what is already there".
  Category and description are replaced (the writer does not merge those).
- **Tagging only.** Background deduplication and upscaling are not part of the
  service in v1.1.
- **`service.autoStart` is accepted but ignored** — the service is always
  registered for automatic start. Use `sc.exe config SynapicIndexer start= demand`
  if manual start is required.
- **Daminion scope is `all` or `catalog`.** Saved-search and collection scopes
  need id fields the configuration cannot hold yet.
- **No UI.** Logs, the Event Log, `--status` and `%ProgramData%\Synapic\state`
  are the whole observability surface.
