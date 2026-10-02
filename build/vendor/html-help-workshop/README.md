# Vendored: Microsoft HTML Help Workshop

`htmlhelp.exe` here is the Microsoft HTML Help Workshop 1.3 installer - the
self-extracting package that installs **`hhc.exe`**, the only supported way to
compile a `.chm`. It is vendored rather than downloaded because the Windows
release depends on it: the compiled help is the whole of the Windows help
payload (see [`docs/help/README.md`](../../../docs/help/README.md)), so a build
that cannot run `hhc.exe` cannot produce a shippable Windows build.

Install it with [`build/install-html-help-workshop.ps1`](../../install-html-help-workshop.ps1),
which verifies the hash below before running anything.

## What it is

| | |
|---|---|
| Product | Microsoft HTML Help Workshop 1.3 |
| Contents | `hhc.exe` (the help compiler), `hha.dll`, `hhaxref.chm`, `api.chm`, and the HTML Help runtime |
| Size | 3,509,072 bytes |
| SHA-256 | `b2b3140d42a818870c1ab13c1c7b8d4536f22bd994fa90aade89729a6009a3ae` |
| MD5 | `53899be5da83419d772d5b97e653da7c` |
| Original URL | `https://download.microsoft.com/download/0/A/9/0A939EF6-E31C-430F-A3DF-DFAE7960D564/htmlhelp.exe` |
| Archived at | `https://web.archive.org/web/20200918004813id_/<original url>` |

The MD5 is the one the [chocolatey
`html-help-workshop`](https://community.chocolatey.org/packages/html-help-workshop)
package pins, and it matches: the file here is byte-for-byte the one that
package installs. The SHA-256 is ours, because the install script refuses to run
anything whose SHA-256 is not the recorded one.

## Why it is in the repository

Microsoft no longer links to the original download, and the one surviving copy
is on the Internet Archive behind a redirect that serves an HTML page to
anything that does not ask for the raw bytes (`.../web/<stamp>id_/<url>` gets
the file; without `id_` you get a wayback page). So a build that fetched it
would depend on both an archive staying up and on getting the request shape
right - and it would depend on a third-party package manager to produce its own
release payload. The install script used to be `choco install html-help-workshop`;
it is now this directory.

## Licensing

The Microsoft HTML Help 1.4 SDK is freely redistributable - that is what the
chocolatey package relies on in shipping the same installer. Microsoft has not
published a dedicated EULA page for it; the SDK's own `license.txt` ships
*inside* the installer and is extracted to the same staging folder the install
script uses. If you replace this file, check the licence that comes with the
replacement rather than assuming this one carries over.

## Replacing it

1. Put the new installer here as `htmlhelp.exe`.
2. Recompute both hashes (`certutil -hashfile htmlhelp.exe SHA256` and `MD5`).
3. Update `$expectedSha256` in `build/install-html-help-workshop.ps1` and the
   table above.
4. Re-run the install script with `-Force` and then
   `docs/help/build-chm.ps1`, and confirm the compiled help still opens.

Never update the recorded hash to match a file without reading where that file
came from. The hash is the only thing standing between "the installer we
reviewed in 1997-era Microsoft packaging" and "whatever arrived this time", and
it is checked immediately before the file is executed with write access to
Program Files.
