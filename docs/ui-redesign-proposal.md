# Synapic UI Redesign Proposal

**Document Version:** 1.0
**Date:** 2026-10-07
**Status:** Draft — For Discussion Only

---

## Executive Summary

The current Synapic UI is feature-complete but suffers from **information overload** and **poor mental model** for users. The application combines three distinct workflows (Tagging, Deduplication, Upscaling) into a single linear wizard with scattered controls, making it difficult to understand what the app does and how to achieve their goals.

This proposal reorganizes the UI into a **centralized dashboard with distinct mode separation**, improving discoverability, reducing cognitive load, and providing a clearer path from source config to workflow execution.

---

## 1. Current State Analysis

### 1.1 Current Layout Structure

```
┌─────────────────────────────────────────────────────────────────┐
│ Toolbar (Row 0):                                                  │
│ ┌───────────────┬──────────┬─────────────┬───────┬────────────┐ │
│ │ Start Server  │ Stop     │ Build Ctrl  │ ● ●   │ Status     │ │
│ └───────────────┴──────────┴─────────────┴───────┴────────────┘ │
├─────────────────────────────────────────────────────────────────┤
│ Sidecar Setup Panel (Row 1):                                      │
│ ┌─────────────────────────────────────────────────────────────┐ │
│ │ Inference sidecar required... [Build/Download buttons]       │ │
│ └─────────────────────────────────────────────────────────────┘ │
├─────────────────────────────────────────────────────────────────┤
│ Wizard Nav (Row 2):                                               │
│ ┌─────────────────────────────────────────────────────────────┐ │
│ │ 1·Datasource 2·Engine 3·Process 4·Results  🧹  ✨  ↺ Home     │ │
│ └─────────────────────────────────────────────────────────────┘ │
├─────────────────────────────────────────────────────────────────┤
│ Main Content (Row 3):                                             │
│ ┌─────────────────────────────────────────────────────────────┐ │
│ │ Home Screen: Source selection + Route cards                 │ │
│ │ ┌─────────────────────────────────────────────────────────┐ │
│ │ │ 📂 Datasource Panel   |   🏷️ Tagging  🧹 Dedup  ✨ Upscale │ │
│ │ └─────────────────────────────────────────────────────────┘ │
│ │ StepViews: Sidebar, Step2Engine, Step3Process, Step4Results │ │
│ └─────────────────────────────────────────────────────────────┘ │
├─────────────────────────────────────────────────────────────────┤
│ Log Panel (Row 4):                                                │
│ ┌─────────────────────────────────────────────────────────────┐ │
│ │ Live log...                                                │ │
│ └─────────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────────┘
```

### 1.2 Identified Problems

| Category | Issue | Impact |
|----------|-------|--------|
| **Toolbar Clutter** | Start/Stop/Build/Status/Progress/AppName in one row | 5-6 elements fighting for attention |
| **Scattered Settings** | Engine settings split across Step2, home screen, settings reference | Inconsistent configuration location |
| **Repetition** | Each workflow repeats Step1 (datasource selection) | Users confused why they re-select source |
| **Hidden State** | Device warnings buried in toolbar, stale server notice | Problems go unnoticed |
| **Linear Wizard Fatigue** | 4-step wizard + 2 dedicated flows | Long, tedious path to completion |
| **Discussions** | All settings visible simultaneously | Decision paralysis, cognitive overload |

### 1.3 Information Density Analysis

```
Screen Area Usage by Component:
- Server Toolbar: ~360px (14% of height)
- Sidecar Panel: ~280px (11% of height)
- Nav Bar: ~80px (3% of height)
- Home Screen: ~400px (16% of height)
- Each Wizard Step: ~250px minimum (10% of height)
- Log Panel: ~160px (6% of height)
-----------------------------------
Total: First-time user content footprint: ~1540px minimum
```

---

## 2. Proposed New Design

### 2.1 High-Level Architecture

```
┌─────────────────────────────────────────────────────────────────────────┐
│ Header (Source Selector)                                             │
│ ┌───────────────────────────────────────────────────────────────────┐ │
│ │ Source: 📂 Local Folder ▼ | 🔌 Dominion Server ▼     [Settings] │ │
│ └───────────────────────────────────────────────────────────────────┘ │
├─────────────────────────────────────────────────────────────────────────┤
│ Main Dashboard                                                         │
│ ┌───────────────┬─────────────────────────────────────────────────┐  │
│ │ Navigation    │ Content Area                                   │  │
│ │ Sidebar       │                                               │  │
│ │               │ ┌─────────────────────────────────────────────┐ │  │
│ │ · Home        │ │ Mode-specific Content                       │ │  │
│ │ · Tagging     │ │ - Empty state / Welcome                    │ │  │
│ │ · Dedup       │ │ - Workflow panel                           │ │  │
│ │ · Upscale     │ │ - Results / Actions                        │ │  │
│ │               │ │ - Settings (collapsible)                  │ │  │
│ │               │ └─────────────────────────────────────────────┘ │  │
│ └───────────────┴─────────────────────────────────────────────────┘  │
├─────────────────────────────────────────────────────────────────────────┤
│ Footer (Server Status & Actions)                                      │
│ ┌───────────────────────────────────────────────────────────────────┐ │
│ │ ● Running | 2,847 images | [Start/Stop]  📍 settings  ❓ Help    │ │
│ └───────────────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────────────────┘
```

### 2.2 Component Specification

#### 2.2.1 Header: Source Selector

```
┌───────────────────────────────────────────────────────────────────────┐
│  🔌 Connect to Dominion Server          📂 Browse Local Folder    │
│  ┌────────────────────────────────────────────────────────────────┐   │
│  │ Server URL: [            ]  User: [      ] Pass: [******]     │   │
│  │          [Connect] [Invalidate]                                      │   │
│  │ Connected as: dam.local  |  Catalog: My Catalog    [Reconnect]  │   │
│  └────────────────────────────────────────────────────────────────┘   │
|# Local Panel (when first tab selected)                                │
│  ┌────────────────────────────────────────────────────────────────┐   │
│  │ Folder: [C:\Pictures           ]              [Browse…]         │   │
│  │ ☑ Recursive scan                                                                    │
│  └────────────────────────────────────────────────────────────────┘   │
├───────────────────────────────────────────────────────────────────────┤
│ [📁 Source Config] ── [🏷️ Tagging] ── [🧹 Dedup] ── [✨ Upscale]     │
└───────────────────────────────────────────────────────────────────────┘
```

**Changes:**
- Dedicated header rows for clear source selector role
- Forms clearly visible on selection change
- Toggle between Local/Daminion modes
- Connection status clearly displayed

#### 2.2.2 Sidebar Navigation

```
┌─────────────────────────────────────┐
│ Home                                │
│   🏠 Dashboard                      │
│                                     │
│ Tagging Flow                       │
│   🏷️  Overview                    │
│   └── 📁 Source Config            │
│   └── 🔧 Engine Settings          │
│   └── ⏳ Process Images           │
│   └── 📊 Results                  │
│                                     │
│                                   │
│ Dedup Flow                       │
│   🧹  Overview                    │
│   └── 📁 Source Config            │
│   └── 🔧 Scan Settings            │
│   └── 📋 Review Duplicates        │
│   └── ✂️ Apply Actions            │
│                                     │
│                                   │
│ Upscale Flow                       │
│   ✨  Overview                    │
│   └── 📁 Source Config            │
│   └── 🔧 Upscale Settings         │
│   └── ⚡ Run Enhancement          │
│   └── 📥 Output Files             │
│                                     │
└─────────────────────────────────────┘
```

**Changes:**
- Consistent navigation across all modes
- Source Config is shared, not repeated
- Mode-specific settings collapsible

#### 2.2.3 Settings Dialog (Pop-up)

```
┌─────────────────────────────────────────────────────────────────────────┐
│  🎛️ Engine Settings           [×]                                      │
├─────────────────────────────────────────────────────────────────────────┤
│ ┌──────────────┬──────────────────────────────────────────────────────┐ │
│ │ Model        │ 📊 nlp-ai/NL2                                   [✓] │ │
│ │              │ Download model...                              │   │ │
│ ├──────────────┼──────────────────────────────────────────────────────┤ │
│ │ Device       │ ○ CPU  ● CUDA  ○ MPS                                 │ │
│ │              │ Selects appropriate inference backend            │ │
│ ├──────────────┼──────────────────────────────────────────────────────┤ │
│ │ Tag Fields   │ ☑ Keywords  ☑ Categories  ☑ Description            │ │
│ ├──────────────┼──────────────────────────────────────────────────────┤ │
│ │ Thresholds   │ Confidence: [0.75]    Prob: [0.60]                 │ │
│ │              │ Candidates: "cat, dog, animal"                     │ │
│ ├──────────────┼──────────────────────────────────────────────────────┤ │
│ │ Prompts      │ [Save Preset ▼]  [Custom Prompt ▼]                │ │
│ │              │────────────────────────────────────────────────────│ │
│ │              │ Every image should return a JSON object with       │ │
│ │              │ description, category, and keywords fields.        │ │
│ ├──────────────┼──────────────────────────────────────────────────────┤ │
│ │             [Apply]    [Reset]                                   │ │
│ └──────────────┴──────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────────────────┘
```

**Changes:**
- All settings in one modal with clear sections
- Preview of effect visible before Apply
- Saves to session, persisted to config.json
- Action buttons prominently placed at bottom

---

## 3. Redesign Map by Area

### 3.1 Source Selection Redesign

**Current:**
- Source panel only appears inside Home Screen and Step1Datasource
- Must click through first screen to see it again
- Settings scattered across multiple views

**New:**
```
Source Section (Persistent Header)
├── Local Folder
│   ├── Folder path input
│   ├── Recursive checkbox
│   └── Record count display
└── Dominion Server
    ├── Server URL input
    ├── Credentials
    ├── Connection status
    ├── Catalog selection
    ├── Scope/filters
    └── Record count display
```

**Benefits:**
- Clear separation of configuration vs execution
- Source config is always accessible and consistent
- Record count is visible on all pages

### 3.2 Wizard Steps → Mode Views

**Eliminate the 4-step linear wizard for Tagging.**

Replace with: **Tagging Mode View**

```
Tagging Mode View
┌─────────────────────────────────────────────────────────────────────────┐
│ [Source Config] ── [Config Settings] ── [Apply]                          │
│                                                                         │
│  ┌─────────────────────────────────────────────────────────────────┐   │
│  │ 📁 Images Ready                                                   │   │
│  │ 2,847 files from Daminion catalog                                │   │
│  │    [Resync]                                                     │   │
│  ├─────────────────────────────────────────────────────────────────┤   │
│  │ 🎯 Processing                                                   │   │
│  │    • Complete: 1,247  • Pending: 1,600  • Failed: 0              │   │
│  └─────────────────────────────────────────────────────────────────┘   │
│                                                                         │
│  [Process Images]  [View Results]  [Export CSV]                          │
└─────────────────────────────────────────────────────────────────────────┘
```

### 3.3 Sidebar Driven Navigation

Entire application is divided into three **discrete modes**:

| Mode | Page | Content |
|------|------|---------|
| **Tagging** | Home | Source config, processing status, actions |
| | Engine Config | Model, device, thresholds, prompts |
| | Process | Progress tracker, batch controls |
| | Results | Tag list, export, retry |
| **Dedup** | Home | Source config, scan settings |
| | Scan | Progress, duplicate counts |
| | Review | Duplicate groups with images |
| | Action | Keep/Action selection, apply |
| **Upscale** | Home | Source config, upscale settings |
| | Process | Progress tracker |
| | Output | Completed files |

### 3.4 Server Controls Consolidation

**Current:**
- Toolbar with 5+ controls
- Status indicators scattered
- Device warnings hidden

**New:**
```
Footer Panel (Compact, Always Visible)
┌─────────────────────────────────────────────────────────────────────────┐
│ ▶️ Inference Server Running  ● Green    |  2,847 images | ⚙️ Settings │
│    CPU only (GPU not available) - Build CUDA variant                  │
├─────────────────────────────────────────────────────────────────────────┤
│ [Start Server] [Stop Server] [Build/Download] [Help]                   │
└─────────────────────────────────────────────────────────────────────────┘
```

**Benefits:**
- Server state prominent at bottom of screen
- Actions grouped logically
- Warnings visible and actionable

---

## 4. Functional Preservation

All existing functionality must be preserved:

### 4.1 Source Configuration
- ✅ Local folder with recursive scan
- ✅ Daminion connection with catalog scope
- ✅ Filters (status, untagged fields)
- ✅ Max items/pagination controls

### 4.2 Tagging Workflow
- ✅ Model selection (local models, Hugging Face, manual ID)
- ✅ Device selection (CPU/CUDA/MPS)
- ✅ Task type (multimodal)
- ✅ Tag fields (Keywords/Categories/Description)
- ✅ Confidence & probability thresholds
- ✅ Candidate labels
- ✅ System/User prompts with presets
- ✅ Processing limits (max items, thumbnail override)
- ✅ Export CSV
- ✅ Retry failed
- ✅ Verify Daminion writes

### 4.3 Deduplication Workflow
- ✅ Local folder scan
- ✅ Daminion catalog scan
- ✅ Algorithm selection (hamming/exact)
- ✅ Threshold control
- ✅ Server hash matching options
- ✅ Auto-select rules (oldest/newest/smallest/largest)
- ✅ Action selection (tag, move, delete)
- ✅ Duplicate group review with thumbnails

### 4.4 Upscaling Workflow
- ✅ Feature enhancement upscaling
- ✅ Quality/realistic/balanced presets
- ✅ Scale factors
- ✅ Precision (float32/float16)
- ✅ Output format (PNG/JPEG)
- ✅ Quality/SNR sliders
- ✅ Sharpen controls
- ✅ Overwrite options

### 4.5 Sidebar/Inference
- ✅ Start/Stop/Build server controls
- ✅ Download progress
- ✅ Sidecar variants (CPU/CUDA)
- ✅ Health polling
- ✅ Device fallback notice
- ✅ Log view

---

## 5. Accessibility & UX Improvements

### 5.1 Keyboard Navigation
- [ ] F1 opens context-sensitive help (same as now)
- [ ] Ctrl+S saves configuration (implied requirement)
- [ ] Esc closes modals/panels
- [ ] Tab cycles through focusable elements with visible focus ring
- [ ] Alt+Home returns to home screen

### 5.2 Visual Hierarchy
1. **Primary Action**: Process Tagging / Scan / Run Upscale
2. **Secondary Actions**: Start Server / Stop Server
3. **Tertiary Actions**: Export, View Settings
4. **Informational**: Status indicators, record counts, help

### 5.3 Consistent Patterns
- All inputs follow the same: Label + Control [+ Tooltip/Help]
- All collapsible sections expand/collapse on click without animation (to avoid motion trigger)
- All settings dialogs follow: Title + Sections + Footer Buttons

### 5.4 Responsive Considerations
- Sidebar compresses to icons-only on narrower screens
- Settings dialog is modal on all sizes (no tab navigation within main content)

---

## 6. Implementation Recommendations

### 6.1 File Structure Refactor

```
src/Synapic.Avalonia/Views/
├── MainLayout.axaml.cs              # Shell wrapper
│   ├── HeaderPanel.axaml           # Source selector
│   ├── MainContentPanel.axaml      # Grid with sidebar + content
│   ├── FooterPanel.axaml           # Server status
│   └── SettingsDialog.axaml.cs     # Settings dialog container
│
├── Dashboard/
│   ├── DashboardHome.axaml         # Mode overview page
│   └── DashboardHomeViewModel.cs   # Mode selection state
│
├── Tagging/
│   ├── TaggingConfig.axaml         # Engine settings dialog
│   ├── TaggingConfigViewModel.cs
│   ├── TaggingProcess.axaml        # Progress & action buttons
│   ├── TaggingProcessViewModel.cs
│   └── TaggingResults.axaml        # Results table
│
├── Dedup/
│   ├── DedupConfig.axaml           # Scan settings dialog
│   ├── DedupConfigViewModel.cs
│   ├── DedupProcess.axaml          # Scan progress
│   ├── DedupProcessViewModel.cs
│   └── DedupReview.axaml           # Duplicate groups
│
└── Upscale/
    ├── UpscaleConfig.axaml         # Upscale settings dialog
    ├── UpscaleConfigViewModel.cs
    ├── UpscaleProcess.axaml        # Progress
    └── UpscaleProcessViewModel.cs
```

### 6.2 ViewModel Consolidation

Current independent viewmodels for each wizard step should be refactored:

```
src/Synapic.Avalonia/ViewModels/
├── Steps/
│   ├── Step1DatasourceViewModel.cs        # → Datasource/SharedDatasourceViewModel.cs
│   ├── Step2EngineViewModel.cs            # → Tagging/TaggingConfigViewModel.cs
│   ├── Step3ProcessViewModel.cs           # = Tagging/TaggingProcessViewModel.cs
│   ├── Step4ResultsViewModel.cs           # = Tagging/TaggingResultsViewModel.cs
│   ├── StepDedupViewModel.cs              # → Dedup/DedupConfigViewModel.cs
│   └── StepUpscaleViewModel.cs            # → Upscale/UpscaleConfigViewModel.cs
```

### 6.3 Migration Path

**Phase 1: Layout Restructure** (No functional changes)
1. Create new MainLayout.axaml with Header + Sidebar + Content
2. Port existing pages into new layout
3. Test navigation and state preservation

**Phase 2: Component Extraction**
1. Create SettingsDialog.axaml and move all settings controls
2. Create separate viewmodels for each mode
3. Test settings persistence

**Phase 3: Mode Separation**
1. Eliminate wizard flow, use sidebar-driven navigation
2. Remove duplicate source configuration panels
3. Streamline status indicators

**Phase 4: Polish**
1. Refine visual hierarchy
2. Add animations/transitions
3. Accessibility improvements

### 6.4 Color Scheme Recommendations

| Element | Current | Proposed |
|---------|---------|----------|
| Primary Action | (varies) | #2563EB (blue - standard CTA) |
| Server Running | Green | #16A34A (green-600) |
| Server Stopped | Red | #DC2626 (red-600) |
| Server Error | Orange Red | #EA580C (orange-600) |
| Selected Tab | (none) | #2563EB with indicator line |
| Sidebar | Gray bg | #F3F4F6 (gray-100) |
| Border | #00000020 | #E5E7EB (gray-200) |
| Text Primary | #1F2937 | #111827 (gray-900) |
| Text Secondary | #6B7280 | #374151 (gray-700) |

---

## 7. Breaking Changes & Migration

### 7.1 Navigation Model Change

**Before:** Linear wizard navigation (`Step 1 → Step 2 → Step 3 → Step 4`)
**After:** Mode-based navigation (Tagging/Dedup/Upscale mode → Settings → Process → Results)

**Migration impact:**
- Session state needs to track current mode, not current step
- WizardViewModel should be refactored into separate mode viewmodels

### 7.2 Configuration Location Change

**Before:** Engine settings scattered across Step2Engine and help docs
**After:** Settings dialog modal for each mode

**Migration impact:**
- EngineSettingsStore read/write remains unchanged
- New `IEngineSettingsViewModel` exposes all settings for the dialog binding

### 7.3 Source Selection Visibility

**Before:** Only accessible from first screen or by clicking "Home"
**After:** Always visible in header for all modes

**Migration impact:**
- Session.Datasource should be read automatically from header selector
- Any viewmodel that needs source data queries the session directly

### 7.4 Back/Next Navigation

**Before:** Wizard.BackCommand, Wizard.NextCommand
**After:** Sidebar navigation or explicit navigation buttons

**Migration impact:**
- WizardViewModel.BackCommand enabled/disabled based on navigation rules
- WizardViewModel.NextCommand replaced by action buttons (Process, Scan, Apply, Run)

---

## 8. Future Considerations

### 8.1 Additional Workflows

This redesign lays groundwork for:
- Batch/Dashboard view for multiple datasets
- Preset saving/reloading for common configurations
- Dark mode theming support
- Keyboard-centric workflows for power users

### 8.2 Analytics & Usage Tracking

With mode-based navigation, we could track:
- Which mode is most commonly used
- Time spent in configuration vs execution
- Common configuration patterns (model + device combinations)
- Bottlenecks in user flow

### 8.3 Progressive Disclosure

For advanced power users:
- Settings → Show all options (includes embedding, API keys, etc.)
- Settings → Show defaults (# of checkboxes expanded)
- Settings → Show essential only (matches current/visible for non-power users)

---

## 9. Questions & Decisions Needed

1. **Source Config Persistency:**
   - Should loading the app default to the last selected mode (Tagging/Dedup/Upscale)?
   - OR default to Dashboard/Home for maximum clarity?

2. **Wasit Bubble:**
   - Should there be a persistent notification when settings differ from defaults?
   - OR just show visual indication and save on explicit Apply?

3. **Batch Processing:**
   - Can the three workflows be combined into a single "Batch Mode" that supports multiple datasets?
   - OR keep them separate for simplicity?

4. **Settings History:**
   - Should we support multiple saved configurations per mode?
   - OR just one saved configuration that persists across sessions?

5. **Workspace State:**
   - Can users save their workspace (mode + configuration + session state)?
   - OR just save configuration and reset session on load?

---

## 10. Conclusion

This redesign addresses the core issue of **UI clutter** by:

1. **Separating concerns:** Source config is always accessible, not hidden in wizard steps
2. **Reducing repetition:** Source configuration appears once, not per workflow
3. **Clearing decision paralysis:** Settings in modal dialogs with preview, not scattered on multiple screens
4. **Improving mental model:** Three distinct modes makes the app's purpose obvious at all times
5. **Consolidating server controls:** Status and actions grouped at footer for clarity

The changes are **backward-compatible in functionality** — all existing features are preserved and just reorganized — but give users a clearer path to accomplish their goals with less visual noise.

---

**Next Steps:**
1. Review this proposal with stakeholders
2. Decide on remaining TODOs (questions from Section 9)
3. Approve implementation start and prioritization
4. Begin Phase 1 (Layout Restructure)