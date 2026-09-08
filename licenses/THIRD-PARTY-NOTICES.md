# GhostSHELL third-party notices

This file records the managed packages resolved for the GhostSHELL desktop
project and indexes the runtime notices currently bundled with the application.
Package authors retain all rights granted by their respective licenses.
GhostSHELL does not claim ownership of these components.

The application bundle also includes:

- `GHOSTSHELL-LICENSE.txt`, GhostSHELL's MIT license;
- `SMBLIBRARY-LGPL-3.0.txt`, `GPL-3.0.txt`, exact source provenance, and
  Native AOT replacement instructions for SMBLibrary 1.5.7.1;
- `DOTNET-LICENSE.txt` and `DOTNET-THIRD-PARTY-NOTICES.txt` for the
  self-contained .NET runtime;
- `GHOSTTY-LICENSE` for the pinned libghostty-vt source snapshot (commit
  `08f039fbb3dea9c6b1cdb5ff4550666598122346`);
- `JetBrainsMono-OFL.txt` for the embedded JetBrains Mono 2.304 regular,
  bold, italic, and bold-italic terminal faces;
- `CEF-LICENSE.txt` and `Chromium-CREDITS.html` for the pinned Chromium
  Embedded Framework runtime;
- `Exclr8CEF-MIT.txt` for GhostSHELL's pinned, patched Exclr8CEF binding;
- `runtimes/osx-arm64/native/THIRD-PARTY-NOTICES.md` and
  `runtime-dependencies.txt` for the separately receipted native SQL language
  worker closure;
- Lucide `panel-left-close`, `panel-right-close`, `panel-bottom-close`,
  `panel-top-close`, and `fullscreen` vector geometry under the ISC license;
- Mozilla Readability 0.6.0, embedded for browser-side article extraction,
  under the Apache-2.0 license (retained at
  `src/GhostShell.Browser/Assets/Readability.LICENSE.md`);
- package-specific copyright and repository metadata in the corresponding
  NuGet packages.

The pinned Ghostty resources also contain Bash and Zsh integration derived in
part from Kitty under GPL-3.0-or-later, as declared in those files, and
`bash-preexec` under MIT. The package retains the source notices and GPLv3 text.
The current macOS `libghostty-vt.dylib` links only Apple's system
`libSystem.B.dylib`; it does not link the obsolete gettext `libintl` library.
The project owner accepts this exact pinned Ghostty and shell-resource closure
for macOS distribution. No independent legal review was obtained.

SMBLibrary 1.5.7.1 is recorded at exact upstream commit
`255339717ccc9a278579d563f42939d9f2668506`, with a checked archive hash,
LGPLv3/GPLv3 texts, and instructions for rebuilding the Native AOT executable
with a modified library. Avalonia.Fonts.Inter 12.0.5 declares MIT for the
package but embeds Inter font binaries without an OFL file or
reserved-font-name notice in the NuGet archive. Upstream Inter is OFL-1.1 and
identifies `Inter` as a reserved font name. The project owner accepts the
documented SMBLibrary distribution path and the remaining Inter package
provenance uncertainty for this exact macOS closure. JetBrains Mono is tracked
separately by an exact source catalog, per-face hashes, build receipt, package
manifest, and retained OFL text.

The managed-component SBOM records engineering evidence. It is not legal
clearance. The exact macOS legal decision and every source record it reviewed
are bound by `MACOS-RELEASE-LEGAL.json`.

CEF is tracked separately by `cef-runtime-components.json`, a per-RID exact
build receipt, retained CEF license and Chromium credits, and a CEF-specific
SPDX document. Those artifacts preserve provenance; they are not legal
clearance by themselves. The project owner accepts the exact macOS CEF license
and generated Chromium credits and owns the security-update decision. Windows
and Linux remain outside this decision.

The native SQL language worker is tracked separately by its per-RID build
receipt, 48-entry runtime dependency inventory, generated third-party notice,
and one explicit review exception for the unlicensed original BesselJ Fortran
source noted by Apache Commons Math 3.6.1. Those components are intentionally
not duplicated in the managed NuGet table below. The project owner accepts the
recorded provenance exception for the exact macOS SQL worker closure.

SPDX license texts are available from <https://spdx.org/licenses/>.
The table below is the conservative managed third-party inventory in the
current `osx-arm64` release catalog. It contains 129 NuGet packages and three
separately licensed vendored projects, including the two Exclr8CEF bindings.
First-party GhostSHELL project assemblies are omitted; the self-contained
.NET runtimepack is indexed by the retained .NET license and notice files
rather than duplicated here.

This inventory is RID-specific. Every other release RID must regenerate and
validate its own exact managed-component catalog and SBOM against its publish
output before distribution. This table does not claim a cross-target inventory
and does not substitute for the release's exact native software bill of
materials and platform-specific owner decision.

The patched SqlClient source build is identified separately from stock NuGet
binaries. Its complete MIT notice and reviewed source inventories/runtime patch
are included under `Licenses` and `Licenses/Sources`. SSH.NET and SharpCompress
use stock NuGet package provenance. The managed SPDX record for SqlClient hashes the actual published assembly,
records the pinned upstream archive, and binds the reviewed source inputs. Public
strong-name identity compatibility is not an upstream private-signature claim.

| Package | Version | License |
|---|---:|---|
| `AWSSDK.Core` | `4.0.102.1` | Apache-2.0 |
| `AWSSDK.S3` | `4.0.102.4` | Apache-2.0 |
| `AngleSharp` | `1.5.2` | MIT |
| `Apache.Arrow` | `23.0.0` | Apache-2.0 |
| `Apache.Arrow.Scalars` | `23.0.0` | Apache-2.0 |
| `Avalonia.AvaloniaEdit` | `12.0.0` | MIT |
| `Avalonia.Controls.ColorPicker` | `12.0.5` | MIT |
| `Avalonia.Controls.DataGrid` | `12.0.1` | MIT |
| `Avalonia.Desktop` | `12.0.5` | MIT |
| `Avalonia.Fonts.Inter` | `12.0.5` | MIT |
| `Avalonia.FreeDesktop.AtSpi` | `12.0.5` | MIT |
| `Avalonia.FreeDesktop` | `12.0.5` | MIT |
| `Avalonia.HarfBuzz` | `12.0.5` | MIT |
| `Avalonia.Native` | `12.0.5` | MIT |
| `Avalonia.Remote.Protocol` | `12.0.5` | MIT |
| `Avalonia.Skia` | `12.0.5` | MIT |
| `Avalonia.Themes.Fluent` | `12.0.5` | MIT |
| `Avalonia.Win32` | `12.0.5` | MIT |
| `Avalonia.X11` | `12.0.5` | MIT |
| `Avalonia` | `12.0.5` | MIT |
| `AvaloniaEdit.TextMate` | `12.0.0` | MIT |
| `Azure.Core` | `1.38.0` | MIT |
| `Azure.Identity` | `1.11.4` | MIT |
| `BouncyCastle.Cryptography` | `2.7.0` | MIT |
| `ClickHouse.Client` | `7.14.0` | MIT |
| `Dock.Avalonia.Themes.Fluent` | `12.0.0.2` | MIT |
| `Dock.Avalonia` | `12.0.0.2` | MIT |
| `Dock.Controls.DeferredContentControl` | `12.0.0.2` | MIT |
| `Dock.Controls.ProportionalStackPanel` | `12.0.0.2` | MIT |
| `Dock.Controls.Recycling.Model` | `12.0.0.2` | MIT |
| `Dock.Controls.Recycling` | `12.0.0.2` | MIT |
| `Dock.MarkupExtension` | `12.0.0.2` | MIT |
| `Dock.Model.Inpc` | `12.0.0.2` | MIT |
| `Dock.Model` | `12.0.0.2` | MIT |
| `Dock.Settings` | `12.0.0.2` | MIT |
| `DuckDB.NET.Bindings.Full` | `1.5.5` | MIT |
| `DuckDB.NET.Data.Full` | `1.5.5` | MIT |
| `ExCSS` | `4.3.1` | MIT |
| `Exclr8Cef.WebView` | `0.8.0` | MIT (vendored project; see `Exclr8CEF-MIT.txt`) |
| `Exclr8Cef` | `0.8.0` | MIT (vendored project; see `Exclr8CEF-MIT.txt`) |
| `FirebirdSql.Data.FirebirdClient` | `10.3.1` | NOASSERTION (nuspec file: `license.txt`) |
| `FluentFTP` | `54.2.0` | MIT |
| `FluentIcons.Avalonia` | `2.1.333` | MIT |
| `FluentIcons.Common` | `2.1.333` | MIT |
| `FluentIcons.Resources.Avalonia` | `2.1.333` | MIT |
| `HarfBuzzSharp.NativeAssets.macOS` | `8.3.1.3` | MIT |
| `HarfBuzzSharp` | `8.3.1.3` | MIT |
| `LiteDB` | `5.0.21` | MIT |
| `Magick.NET-Q8-AnyCPU` | `14.16.0` | Apache-2.0 |
| `Magick.NET.Core` | `14.16.0` | Apache-2.0 |
| `Markdig` | `1.3.2` | BSD-2-Clause |
| `Mermaider` | `0.12.1` | MIT |
| `MicroCom.Runtime` | `0.11.4` | MIT |
| `Microsoft.Bcl.AsyncInterfaces` | `1.1.1` | MIT |
| `Microsoft.Bcl.Cryptography` | `9.0.4` | MIT |
| `Microsoft.Data.SqlClient` | `6.0.2` | MIT, patched source build; local project identity `Microsoft.Data.SqlClient.Routed/6.0.2`; `sqlclient-MIT.txt` |
| `Microsoft.Data.Sqlite.Core` | `10.0.10` | MIT |
| `Microsoft.Extensions.AI.Abstractions` | `10.5.2` | MIT |
| `Microsoft.Extensions.Caching.Abstractions` | `9.0.4` | MIT |
| `Microsoft.Extensions.Caching.Memory` | `9.0.4` | MIT |
| `Microsoft.Extensions.Configuration.Abstractions` | `8.0.0` | MIT |
| `Microsoft.Extensions.Configuration.Binder` | `8.0.0` | MIT |
| `Microsoft.Extensions.Configuration` | `8.0.0` | MIT |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | `10.0.10` | MIT |
| `Microsoft.Extensions.DependencyInjection` | `10.0.10` | MIT |
| `Microsoft.Extensions.Diagnostics.Abstractions` | `8.0.1` | MIT |
| `Microsoft.Extensions.Diagnostics` | `8.0.1` | MIT |
| `Microsoft.Extensions.Http` | `8.0.1` | MIT |
| `Microsoft.Extensions.Logging.Abstractions` | `10.0.7` | MIT |
| `Microsoft.Extensions.Logging` | `8.0.1` | MIT |
| `Microsoft.Extensions.ObjectPool` | `10.0.3` | MIT |
| `Microsoft.Extensions.Options.ConfigurationExtensions` | `8.0.0` | MIT |
| `Microsoft.Extensions.Options` | `9.0.4` | MIT |
| `Microsoft.Extensions.Primitives` | `9.0.4` | MIT |
| `Microsoft.IO.RecyclableMemoryStream` | `3.0.1` | MIT |
| `Microsoft.Identity.Client.Extensions.Msal` | `4.61.3` | MIT |
| `Microsoft.Identity.Client` | `4.61.3` | MIT |
| `Microsoft.IdentityModel.Abstractions` | `7.5.0` | MIT |
| `Microsoft.IdentityModel.JsonWebTokens` | `7.5.0` | MIT |
| `Microsoft.IdentityModel.Logging` | `7.5.0` | MIT |
| `Microsoft.IdentityModel.Protocols.OpenIdConnect` | `7.5.0` | MIT |
| `Microsoft.IdentityModel.Protocols` | `7.5.0` | MIT |
| `Microsoft.IdentityModel.Tokens` | `7.5.0` | MIT |
| `Microsoft.SqlServer.Server` | `1.0.0` | MIT |
| `ModelContextProtocol.Core` | `1.3.0` | Apache-2.0 |
| `MySqlConnector` | `2.4.0` | MIT |
| `NodaTime` | `3.2.2` | Apache-2.0 |
| `Npgsql` | `9.0.3` | PostgreSQL |
| `Onigwrap` | `1.0.11` | MIT |
| `Oracle.ManagedDataAccess.Core` | `23.7.0` | NOASSERTION (nuspec file: `LICENSE.txt`) |
| `PDFtoImage` | `5.3.0` | MIT |
| `Porta.Pty` | `1.0.7` | MIT |
| `RESPite` | `3.0.17` | MIT |
| `ReverseMarkdown` | `6.2.1` | MIT |
| `SMBLibrary` | `1.5.7.1` | LGPL-3.0-or-later |
| `SQLite3MC.PCLRaw.bundle` | `2.4.0` | MIT |
| `SQLite3MC.PCLRaw.lib` | `2.4.0` | MIT |
| `SQLite3MC.PCLRaw.provider` | `2.4.0` | MIT |
| `SQLitePCLRaw.core` | `3.0.2` | Apache-2.0 |
| `SSH.NET` | `2026.0.0` | MIT |
| `SharpCompress` | `0.50.3` | MIT |
| `ShimSkiaSharp` | `5.1.1` | MIT |
| `SkiaSharp.NativeAssets.macOS` | `4.150.1` | MIT |
| `SkiaSharp` | `4.150.1` | MIT |
| `SshNet.Agent` | `2026.0.0` | MIT |
| `StackExchange.Redis` | `3.0.17` | MIT |
| `Sugiyama` | `0.12.1` | MIT |
| `Svg.Animation` | `5.1.1` | MIT |
| `Svg.Controls.Skia.Avalonia` | `12.0.0.13` | MIT |
| `Svg.Custom` | `5.1.1` | MS-PL |
| `Svg.Model` | `5.1.1` | MIT |
| `Svg.SceneGraph` | `5.1.1` | MIT |
| `Svg.Skia` | `5.1.1` | MIT |
| `Sylinko.CSharpMath.Avalonia` | `12.0.0` | MIT |
| `System.ClientModel` | `1.0.0` | MIT |
| `System.Configuration.ConfigurationManager` | `9.0.4` | MIT |
| `System.Diagnostics.EventLog` | `9.0.4` | MIT |
| `System.Diagnostics.PerformanceCounter` | `8.0.0` | MIT |
| `System.DirectoryServices.Protocols` | `8.0.0` | MIT |
| `System.IdentityModel.Tokens.Jwt` | `7.5.0` | MIT |
| `System.IO.Hashing` | `10.0.5` | MIT |
| `System.Memory.Data` | `1.0.2` | MIT |
| `System.Security.Cryptography.Pkcs` | `9.0.4` | MIT |
| `System.Security.Cryptography.ProtectedData` | `10.0.10` | MIT |
| `TextMateSharp.Grammars` | `2.0.4` | MIT |
| `TextMateSharp` | `2.0.4` | MIT |
| `Tmds.DBus.Protocol` | `0.92.0` | MIT |
| `Vanara.Core` | `4.2.1` | MIT |
| `Vanara.PInvoke.Kernel32` | `4.2.1` | MIT |
| `Vanara.PInvoke.Shared` | `4.2.1` | MIT |
| `Velopack` | `1.2.0` | MIT |
| `bblanchon.PDFium.macOS` | `152.0.7961` | Apache-2.0 |
## Lucide icon geometry

The Dock drop-target vectors are adapted from Lucide Icons.

ISC License

Copyright (c) 2026 Lucide Icons and Contributors

Permission to use, copy, modify, and/or distribute this software for any
purpose with or without fee is hereby granted, provided that the above
copyright notice and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES WITH
REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY
AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY SPECIAL, DIRECT,
INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM
LOSS OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR
OTHER TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE OR
PERFORMANCE OF THIS SOFTWARE.

This notice is informational and is not legal advice. It must not be used as
evidence that the M4 release license gate is complete.
