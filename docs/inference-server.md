Here is the complete spec. I'll write it to the repo as `windows-indexer.md` as well.

# Synapic Windows Indexer Service — Component Specification

`windows-indexer.md` | **Version:** 1.0.0 | **Target OS:** Windows 10/11 & Windows Server 2019+

---

## 1. Executive Summary & Purpose

The **Synapic Windows Indexer Service** (`Synapic.Indexer.exe`) is a background system service that runs permanently on a Windows host as a Windows Service (`ServiceBase` / `Microsoft.Extensions.Hosting.WindowsServices`).

Unlike the main interactive desktop application (`Synapic.Main.exe`), which provides a user-driven wizard workflow, the Windows Indexer operates autonomously in the background without user intervention. It monitors configured local folders or Daminion catalogs, executes background image AI tagging (and optionally deduplication or upscaling checks) on a configurable schedule, writes metadata back to files or DAM catalogs, and maintains comprehensive diagnostic logs and state history.

### Key Goals
- **Autonomous Background Operation**: Runs as an unattended Windows Service under `NT AUTHORITY\LocalService` or a designated service account.
- **Scheduled & Watched Tagging**: Scans configured local folders and Daminion catalog scopes at fixed intervals or via filesystem watcher triggers (`FileSystemWatcher`).
- **Shared Engine Core**: Reuses Synapic.NET core processing services (`ProcessingOrchestrator`, `IInferenceSidecar`, `IMetadataWriter`, `DaminionApiClient`, `DedupService`) without duplicating business logic.
- **Headless Sidecar Management**: Spawns and manages a headless Python inference sidecar process (`synapic-inference.exe`) dedicated to background indexing tasks.
- **Resilient & Recoverable**: Handles unexpected sidecar crashes, network disconnections, system reboots, and catalog locks gracefully with retry backoffs and transaction checkpoints.

---

## 2. High-Level System Architecture

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                       WINDOWS HOST (System Background)                      │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  ┌───────────────────────────────────────────────────────────────────────┐  │
│  │               WINDOWS SERVICE (Synapic.Indexer.exe)                   │  │
│  │               [Microsoft.Extensions.Hosting / WindowsService]         │  │
│  │                                                                       │  │
│  │   ┌──────────────────────────────────────────────────────────────────┐ │
│  │   │  Indexer Scheduler / Cron      FileSystemWatcher Engine           │ │
│  │   └──────────────┬──────────────────────────────┬─────────────────────┘ │
│  │                  │                               │                      │
│  │                  ▼                               ▼                      │
│  │   ┌────────────────────────────────────────────────────────────────┐   │
│  │   │                    Indexing Work Orchestrator                   │   │
│  │   │          (Batching, Queueing, State Checkpointing)             │   │
│  │   └──────┬────────────────────────┬──────────────────────┬────────┘   │
│  │          │                        │                      │            │
│  │          ▼                        ▼                      ▼            │
│  │   ┌──────────────┐       ┌─────────────────┐    ┌─────────────────┐   │
│  │   │ Sidecar Host │       │ Metadata Writer │    │ Daminion Client │   │
│  │   │ Manager      │       │ (XMP/EXIF/IPTC) │    │ (Refit REST)    │   │
│  │   └──────┬───────┘       └─────────────────┘    └─────────────────┘   │
│  └──────────┼────────────────────────────────────────────────────────────┘  │
│             │                                                               │
│             │ HTTP / JSON (127.0.0.1:{port})                                │
│             ▼                                                               │
│  ┌───────────────────────────────────────────────────────────────────────┐  │
│  │               PYTHON SIDECAR (synapic-inference.exe)                  │  │
│  │               FastAPI + PyInstaller Bundled Executable                │  │
│  │               HF Vision Models (LFM2.5-VL / BLIP / CLIP)              │  │
│  └───────────────────────────────────────────────────────────────────────┘  │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

## 3. Architecture & Service Seams

The indexer is built following Synapic's deep module architecture, placing distinct responsibilities behind clear interfaces.

### 3.1 Module Breakdown

| Component / Service | Namespace / Project | Primary Responsibility |
|---|---|---|
| `IndexerWorkerService` | `Synapic.Indexer.Services` | Windows BackgroundService entry point; orchestrates lifecycle, timer triggers, and graceful shutdown. |
| `IndexerConfigStore` | `Synapic.Indexer.Configuration` | Reads/writes Indexer configuration from registry (`HKLM\SOFTWARE\Synapic\Indexer`) or `indexer-config.json`. |
| `IndexingQueueRepository` | `Synapic.Indexer.State` | SQLite / JSON persistent queue tracking pending, in-flight, failed, and completed indexing jobs. |
| `IInferenceSidecar` | `Synapic.Main.Services` (Shared) | Manages sidecar startup (`synapic-inference.exe`), port discovery via `%TEMP%\synapic_port_{pid}.txt`, and HTTP client routing. |
| `ProcessingOrchestrator` | `Synapic.Main.Services.Processing` | Shared batch execution kernel (`WorkflowRunner`): handles parallel sidecar requests (`SemaphoreSlim(4)`), progress tracking, and error isolation. |
| `IMetadataWriter` | `Synapic.Main.Services` | Lossless XMP / IPTC / EXIF writer for JPEG, PNG, TIFF files. |
| `DaminionApiClient` | `Synapic.Main.Services.Daminion` | Paged catalog fetch, metadata check-in, thumbnail generation via REST API. |

---

## 4. Work Detection Strategies

The Indexer supports two distinct modes of operation for locating un-indexed or newly added images:

### 4.1 Scheduled Batch Scans (Poll Mode)
- **Frequency**: Configurable interval (default: every 15 minutes; range 1 min to 24 hours).
- **Local Scope**: Performs a recursive folder walk searching for supported extensions (`.jpg`, `.jpeg`, `.png`, `.tif`, `.tiff`). Compares modification dates and hashes against the `IndexingQueueRepository`.
- **Daminion Scope**: Queries the Daminion catalog REST API for items matching configured untagged filters (e.g., items where `Keywords` or `Categories` are empty, or matching specific saved searches/collections).

### 4.2 FileSystemWatcher Triggers (Real-time Local Watch Mode)
- Watches configured root directory paths for `OnCreated`, `OnChanged`, or `OnRenamed` events.
- **Debounce Buffer**: Accumulates incoming filesystem events over a sliding window (default: 5 seconds) before queueing them to prevent premature processing of files currently being written to disk.
- **File Access Probe**: Verifies exclusive read lock on newly detected files (`FileShare.None` test) before handing them to the sidecar.

---

## 5. Configuration & Storage Specification

### 5.1 Registry Configuration Path
The Windows Indexer reads system-wide settings from the Windows Registry:
`HKLM\SOFTWARE\Synapic\Indexer`

An optional JSON override file is supported at `%ProgramData%\Synapic\indexer-config.json`.

### 5.2 Configuration Schema (`indexer-config.json`)

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
    "watchPaths": [
      "D:\\Media\\Incoming",
      "\\\\NAS\\Photos\\Archive"
    ],
    "watchRecursive": true
  },
  "engine": {
    "device": "cuda",
    "modelId": "LiquidAI/LFM2.5-VL-1.6B",
    "confidenceThreshold": 0.3,
    "probabilityMode": "both",
    "probabilityThreshold": 0.5,
    "userPrompt": null
  },
  "datasource": {
    "type": "daminion",
    "daminion": {
      "serverUrl": "http://daminion-server:8080",
      "catalogId": 1,
      "username": "indexer-bot",
      "encryptedPassword": "AQAAANCMnd8BFdERjHoB...",
      "scope": "all",
      "untaggedFields": ["keywords", "categories"]
    }
  },
  "tagging": {
    "writeCategory": true,
    "writeKeywords": true,
    "writeDescription": true,
    "overwriteExistingTags": false
  }
}
```

---

## 6. Execution Flow & Lifecycle

```
[Service Start] ──► Read Registry / Config ──► Initialize SQLite Job DB
                         │
                         ▼
             Spawn Headless Sidecar (`synapic-inference.exe --port=0`)
                         │
                         ▼
             Poll /health until {"status": "ready"}
                         │
        ┌────────────────┴────────────────┐
        ▼                                 ▼
[Timer Event Triggered]      [FileSystemWatcher Triggered]
        │                                 │
        └────────────────┬────────────────┘
                         │
                         ▼
             Scan Scope / Query Untagged Items
                         │
                         ▼
            Filter Out Previously Processed Items
                         │
                         ▼
          Enqueue Batch in Job Database
                         │
                         ▼
       Execute Batch via `ProcessingOrchestrator`
         ├── Send /tag request to Sidecar (Max 4 parallel)
         ├── Write Metadata (Local File XMP / Daminion REST)
         └── Mark Job Completed in DB
                         │
                         ▼
        Sleep Until Next Interval / Watcher Event
```

---

## 7. Windows Service Deployment & Lifecycle Management

### 7.1 Service Installation Commands (PowerShell / Admin Prompt)

To install as a native Windows Service:

```bash
# Create the Windows Service
sc.exe create SynapicIndexer binPath= "C:\Program Files\Synapic\Synapic.Indexer.exe" start= auto displayname= "Synapic Background Image Indexer"

# Set recovery options (Restart service on failure)
sc.exe failure SynapicIndexer reset= 86400 actions= restart/60000/restart/60000/restart/60000

# Start the service
sc.exe start SynapicIndexer
```

### 7.2 Service Account Requirements
- Default service account: `NT AUTHORITY\LocalService` or `NT AUTHORITY\NetworkService`.
- For network share access (UNC paths like `\\NAS\Photos`) or Daminion API domain authentication, run under a dedicated Managed Service Account (gMSA) or domain user account with read/write privileges to target image directories.

---

## 8. Failure Modes, Resiliency, & Logging

| Failure Scenario | Impact | Self-Healing / Resiliency Strategy |
|---|---|---|
| **Python Sidecar Crash / OOM** | Background inference fails | `WatchProcessExit` detects process termination; restarts `synapic-inference.exe` with exponential backoff (max 3 retries per batch). |
| **File Lock Contention** | Cannot read image or write EXIF | Item is marked `TransientFailure` in SQLite queue; re-queued for retry after 60 seconds. |
| **Daminion Server Unreachable** | Cannot fetch or write tags | Pauses current run; retries Daminion API connection every 5 minutes until server responds. |
| **System Shutdown / Reboot** | Mid-batch interruption | SQLite job queue tracks state per item. On service startup, unacknowledged jobs in `InFlight` state are automatically re-queued. |
| **Corrupt Image File** | Sidecar or decoder error | Item is permanently marked `Failed` in DB with error detail logged; batch continues processing remaining queue. |

### 8.1 Logging Infrastructure
- Logging uses **Serilog** configured with the `Serilog.Sinks.EventLog` and `Serilog.Sinks.File` sinks.
- Event Log Source: `SynapicIndexer` (Windows Application Log).
- File Log Path: `%ProgramData%\Synapic\Logs\indexer-YYYYMMDD.log` with rolling daily archives and 30-day retention.

---

## 9. Verification & Acceptance Criteria

1. **Service Registration**: `Synapic.Indexer.exe` successfully installs and starts via `sc.exe` without requiring an active user desktop session.
2. **Unattended Execution**: Tags new images automatically upon addition to watched directory without user interaction or desktop UI presence.
3. **Parity**: Generated categories, keywords, and descriptions match those produced by `Synapic.Main.exe` using identical model parameters.
4. **Clean Shutdown**: Responds to `sc.exe stop` or system shutdown within 10 seconds, gracefully terminating `synapic-inference.exe` without orphaned background processes.
5. **No Code Written**: This specification document (`windows-indexer.md`) establishes the complete blueprint for future implementation.

---

That's the spec. I'll now write it to the repo.