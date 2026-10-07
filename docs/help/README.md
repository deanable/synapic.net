# Synapic help sources

The end-user and administrator help, authored as plain HTML and compiled into a
single **`Synapic.chm`** by Microsoft HTML Help Workshop (`hhc.exe`).

The topics are help *source*, and the app opens them itself (toolbar **Help**,
or <kbd>F1</kbd> for the topic that matches what you are looking at). The
compiled `.chm` is what Windows gets, **embedded inside `Synapic.dll`** and
verified against a SHA-256 before it is opened; macOS and Linux, which have no
`.chm` viewer, get the same topics as HTML in `help/`. See *How the app opens
this help* below.

## Layout

| File | What it is |
|------|------------|
| `Synapic.hhp` | The HTML Help project: compile options, window definition and the `[FILES]` list of everything compiled into the `.chm`. |
| `Synapic.hhc` | The Contents pane (table of contents) - the tree a user browses. |
| `Synapic.hhk` | The Index pane keywords. |
| `*.html` | One file per topic. |
| `help.css` | The single stylesheet, referenced by every topic. |
| `build-chm.ps1` | Locates `hhc.exe`, compiles the project, verifies the output, writes `help-payload.json` (the SHA-256 the app checks the bytes against). |
| `check-help.py` | Source checker: dead links, topics missing from `[FILES]`, missing anchors, non-ASCII bytes. |

## Build

```powershell
# Compile in place (writes docs/help/Synapic.chm)
./docs/help/build-chm.ps1

# Compile somewhere else, or point at a specific hhc.exe
./docs/help/build-chm.ps1 -OutputDirectory ./artifacts/win-x64 -HhcPath 'C:\Program Files (x86)\HTML Help Workshop\hhc.exe'

# Check the sources without compiling (no hhc.exe needed)
python docs/help/check-help.py

# Check the sources and tolerate a machine that cannot compile: warns, exits 0.
# That covers a missing hhc.exe *and* one that runs and writes nothing usable.
# Local convenience only - a checkout with no compiled help falls back to the
# HTML topics, which is fine while developing and is why CI does *not* use it.
./docs/help/build-chm.ps1 -AllowMissingCompiler
```

`hhc.exe` ships with **Microsoft HTML Help Workshop** (the HTML Help 1.4 SDK) and
with the Windows SDK. `build-chm.ps1` looks in the usual places, honours
`-HhcPath`, then `$env:HHC`, and explains where to download it if it finds
nothing. It is a 32-bit tool; it runs fine on 64-bit Windows, and there is no
supported way to run it on Linux or macOS, so the `.chm` is a build-on-Windows
artifact.

## How the app opens this help

`HelpService` (`src/Synapic.Main/Services/HelpService.cs`) finds and starts
the topic. The order is deliberate, and every entry is tried in turn:

| Platform | What | Where it comes from |
|----------|------|---------------------|
| Windows | `Synapic.chm`, opened at the requested topic through `hh.exe` and the `ms-its:` moniker | Embedded in `Synapic.dll` as `Synapic.Help.Synapic.chm`, checked against `Synapic.Help.help-payload.json`, unpacked to `%LOCALAPPDATA%\Synapic\help\` |
| macOS / Linux | The same topic as HTML, in the default browser | `help/` next to the app, falling back to a checkout's `docs/help` |
| Windows, no compiled help in the build | The same topic as HTML | As above - this is a checkout that has not run `build-chm.ps1` |

Two entry points use it: the toolbar **Help** button opens `index.html`, and
<kbd>F1</kbd> opens the topic for what the user is looking at - the nearest
control annotated with `HelpScope.Topic` in the views (a section's topic, or
one setting's `settings-reference.html#anchor` row), falling back to the
sidecar topics while the setup panel is what is gating them, otherwise the
wizard step on screen (`HelpTopics.ForStepIndex`).

**On Windows there is no second entry.** The compiled help is the whole of the
payload, and a compiled help that fails its hash, or an `hh.exe` that will not
start, is logged and refused rather than quietly replaced by loose HTML sitting
next to the binary. That is deliberate: silently serving different help would
hide a damaged install behind something that looks fine.

Payload, per build:

| File | Who copies it |
|------|---------------|
| `Synapic.chm` + `help-payload.json` | `Synapic.Main.csproj`, from this folder, as embedded resources (`Synapic.Help.*`) on every RID. Only when they exist - neither is committed |
| `help/*.html`, `help/help.css` | the same project, but only for **non-Windows** RIDs, which have no `.chm` viewer |

So the `.chm` is produced by CI on the Windows legs, before the app is
published. CI installs HTML Help Workshop first - from the installer vendored
in `build/vendor/html-help-workshop`, hash-checked by
`build/install-html-help-workshop.ps1` - and runs `build-chm.ps1` without
`-AllowMissingCompiler`: the `.chm` is the Windows payload now, so a Windows
publish without one would ship no help at all, and that fails the build rather
than publishing something broken.

## Getting the compiler

`hhc.exe` is a 1997 tool that Microsoft still redistributes but no longer links
to, so CI installs it explicitly:

```powershell
# Installs from the copy vendored in build/vendor/html-help-workshop, after
# checking its SHA-256. Does nothing if a working hhc.exe is already present.
./build/install-html-help-workshop.ps1
```

The vendored copy exists so that building the release payload does not depend
on chocolatey, or on the Internet Archive still serving the only working copy of
this installer. `build/vendor/html-help-workshop/README.md` has the provenance,
both hashes, and how to replace the file safely.

The steps that script performs are not obvious and are worth knowing before
changing it: the installer is an EXE wrapping an INF, `/Q /T:` without `/C`
only checks for an existing install and pops a modal dialog while exiting 0, the
INF's `hhupd.exe` automatic updater has to be commented out or it hangs an
unattended run, and the INF section is installed through 32-bit
`rundll32 advpack.dll,LaunchINFSection` with the section name left as an empty
field.

## Conventions that keep the `.chm` honest

These exist because HTML Help Workshop fails quietly: a bad link or a topic
missing from `[FILES]` produces a `.chm` that opens, looks fine, and is wrong.

1. **ASCII only, typography through entities.** Write `&mdash;`, `&rsaquo;`,
   `&rarr;`, `&le;`, `&deg;` instead of the characters themselves, and declare
   `<meta http-equiv="Content-Type" content="text/html; charset=windows-1252">`
   in every topic. The CHM viewer is MSHTML-era: UTF-8 pages render correctly
   only when the charset is declared, and a stray byte shows up as mojibake in
   the Contents and Index panes even when the page body looks right.
   `check-help.py` fails on any non-ASCII byte in a help source.
2. **Every topic is listed in `Synapic.hhp` `[FILES]`.** A page that is not
   listed is compiled *out*: it is still reachable from the Contents if the
   `.hhc` names it, but the CSH/`/embed` and full-text index behaviour gets
   confusingly inconsistent. The checker compares the folder against `[FILES]`
   in both directions.
3. **Every topic is reachable from the Contents or the Index.** A page nobody
   links to is a page nobody reads; the checker reports orphans too.
4. **No JavaScript and no external resources.** The CHM viewer blocks or breaks
   both, and this help has to work offline from a read-only `.chm`.
5. **One `<h1>` per topic**, matching the Contents entry, so the page a user
   lands on looks like the entry they clicked.
6. **Relative links only** (`href="step2-tag-fields.html"`), never absolute
   paths, so the same sources work both compiled and opened from disk.

## Maintaining it when the UI changes

The help claims to describe what the app actually does, so a change to a label,
a checkbox or a persisted setting is a help change too. When you touch Step 1-4
or the sidecar panel:

- Labels live in `src/Synapic.Main/Views/*.axaml`; match them exactly,
  including the `&mdash;` in headings like "Step 2 &mdash; Engine".
- Persisted values live in `EngineSettingsStore` /
  `DaminionConnectionStore`; `admin-settings-files.html` lists those names and
  registry paths, so update it with them.
- Paths and files (logs, crashes, model cache, `config.json`,
  `system-prompts.json`) are in `docs/codebase-guide.md` section 7; keep the
  administrator topics and that table in step.
- New topic? Add the file, its `[FILES]` line, a Contents entry, at least one
  Index keyword, and a link from a page a user would be reading when they need
  it. Then run `python docs/help/check-help.py`.

## Known gotchas

- **A `.chm` that came from a network share or the internet shows blank pages.**
  That is Windows' attachment-zone security on the compiled help, not a build
  fault. It does not apply here: the file the app opens is unpacked to
  `%LOCALAPPDATA%` from bytes inside the assembly, so it is a local file this
  session wrote and carries no mark-of-the-web. Worth knowing before believing a
  bug report about the help being empty - and worth remembering if you ever open
  a locally built `.chm` straight out of `docs/help` to eyeball it.
- **Topic-level context sensitivity needs no `[MAP]`/`[ALIAS]`.** The app opens
  a topic by name (`hh.exe ms-its:Synapic.chm::/step2-engine.html`) rather than
  through `HtmlHelp()`'s numeric context ids, so `Synapic.hhp` stays as it is.
  What it does need is for every name in `HelpTopics` to exist here *and* be
  listed in `[FILES]`, which `HelpServiceTests` enforces - and for every
  `HelpScope.Topic` annotation in the views to resolve to a topic *and* anchor
  that exist, which `HelpScopeTests` enforces.
- **A stale `Synapic.chm` in a checkout is embedded as-is.** MSBuild embeds
  whatever `.chm` is on disk at build time, so after editing a topic and
  rebuilding you are still looking at the old page until you re-run
  `build-chm.ps1`. Delete `Synapic.chm` *and* `help-payload.json` together to
  go back to the HTML sources - one without the other is not a payload at all
  (see below).
- **`hhc.exe`'s exit code means nothing in either direction.** It returns 0
  having compiled nothing, and 1 having compiled fine, which is why
  `build-chm.ps1` proves the `.chm` was written during this run, greps the
  tool's output for errors, and resets `$LASTEXITCODE` itself - GitHub's `pwsh`
  wrapper ends every step with `exit $LASTEXITCODE`, so a stale 1 from hhc would
  fail a build whose help compiled perfectly.
- **A failed `hhc.exe` can leave a 0-byte `.chm` and say nothing.** Silent
  failure looks exactly like success to an existence check, so the script refuses
  anything under 4 KB and deletes it. That matters more than it sounds: the
  `.chm` is the whole Windows payload now, so a stub would make the help open
  nothing at all. `-AllowMissingCompiler` downgrades that to a warning, and is
  for local convenience only - it is what the old CI did, on the theory that a
  missing `.chm` still left working HTML to open, which stopped being true when
  Windows stopped shipping it. CI now installs HTML Help Workshop first and
  fails the build if no `.chm` comes out.
- **The `.chm` and its hash are a pair.** `build-chm.ps1` writes
  `help-payload.json` next to the `.chm` and deletes it on every failure path,
  because a hash that outlives the file it describes would make the app refuse
  help that is actually fine. If you delete or replace one by hand, delete the
  other.
- **Neither the `.chm` nor the manifest is committed** (see the
  `docs/help/*.chm` and `docs/help/help-payload.json` lines in `.gitignore`):
  both are build artifacts. "Sources + script" is the committed truth, so a help
  edit is reviewable as text.
