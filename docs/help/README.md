# Synapic help sources

The end-user and administrator help, authored as plain HTML and compiled into a
single **`Synapic.chm`** by Microsoft HTML Help Workshop (`hhc.exe`).

Everything in this folder is help *source*: it is not part of the app build, no
.NET project references it, and nothing here affects the shipped binaries until
you compile the `.chm` and put it next to `Synapic.exe`.

## Layout

| File | What it is |
|------|------------|
| `Synapic.hhp` | The HTML Help project: compile options, window definition and the `[FILES]` list of everything compiled into the `.chm`. |
| `Synapic.hhc` | The Contents pane (table of contents) - the tree a user browses. |
| `Synapic.hhk` | The Index pane keywords. |
| `*.html` | One file per topic. |
| `help.css` | The single stylesheet, referenced by every topic. |
| `build-chm.ps1` | Locates `hhc.exe`, compiles the project, verifies the output. |
| `check-help.py` | Source checker: dead links, topics missing from `[FILES]`, missing anchors, non-ASCII bytes. |

## Build

```powershell
# Compile in place (writes docs/help/Synapic.chm)
./docs/help/build-chm.ps1

# Compile somewhere else, or point at a specific hhc.exe
./docs/help/build-chm.ps1 -OutputDirectory ./artifacts/win-x64 -HhcPath 'C:\Program Files (x86)\HTML Help Workshop\hhc.exe'

# Check the sources without compiling (no hhc.exe needed)
python docs/help/check-help.py
```

`hhc.exe` ships with **Microsoft HTML Help Workshop** (the HTML Help 1.4 SDK) and
with the Windows SDK. `build-chm.ps1` looks in the usual places, honours
`-HhcPath`, then `$env:HHC`, and explains where to download it if it finds
nothing. It is a 32-bit tool; it runs fine on 64-bit Windows, and there is no
supported way to run it on Linux or macOS, so the `.chm` is a build-on-Windows
artifact.

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

- Labels live in `src/Synapic.Avalonia/Views/*.axaml`; match them exactly,
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
  fault: right-click the `.chm` &rarr; Properties &rarr; tick *Unblock* (or ship
  it in the installer, which writes a local file). Worth knowing before
  believing a bug report about the help being empty.
- **The app does not open this help yet.** There is no Help menu or <kbd>F1</kbd>
  shortcut in `MainWindow.axaml`; users double-click `Synapic.chm`. If you wire
  it up later, `helpers\HHActiveX`-based `HtmlHelp()` from `hhctrl.ocx` is the
  usual route, and the shipped `.chm` needs a context map (`[MAP]`/`[ALIAS]` in
  `Synapic.hhp`) if you want topic-level context sensitivity.
- **`hhc.exe` returns 0 in some failure cases**, which is why `build-chm.ps1`
  also asserts that the `.chm` was written during this run and greps the tool's
  output for errors.
- **The `.chm` is not committed** (see the `docs/help/*.chm` line in
  `.gitignore`): it is a build artifact. "Sources + script" is the committed
  truth, so a help edit is reviewable as text.
