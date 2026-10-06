# Third-party notices

Clipboard Manager is licensed under the GNU General Public License v3.0 or later (see `LICENSE`).
The released `ClipboardManager.exe` is a self-contained build and bundles the components below.
Each keeps its own license.

## .NET runtime and Windows Presentation Foundation (WPF)

- Copyright (c) .NET Foundation and Contributors
- License: MIT — https://github.com/dotnet/runtime/blob/main/LICENSE.TXT and https://github.com/dotnet/wpf/blob/main/LICENSE.TXT
- The runtime itself contains further third-party components. Their notices ship in the runtime's
  `THIRD-PARTY-NOTICES.TXT`, which every release attaches as `THIRD-PARTY-NOTICES.txt`.
- The native WPF libraries (`wpfgfx_cor3.dll`, `PresentationNative_cor3.dll`, `D3DCompiler_47_cor3.dll`,
  `vcruntime140_cor3.dll`, `PenImc_cor3.dll`) are part of the Microsoft Windows Desktop runtime
  pack and are redistributed unmodified under its terms. They are treated as System Libraries of
  the runtime in the sense of GPLv3 section 1.

## Microsoft.Data.Sqlite

- Copyright (c) .NET Foundation and Contributors
- License: MIT — https://github.com/dotnet/efcore/blob/main/LICENSE.txt

## SQLitePCLRaw (core, provider.winsqlite3)

- Copyright 2014–2025 SourceGear, LLC
- License: Apache License 2.0 — https://github.com/ericsink/SQLitePCL.raw/blob/main/LICENSE.TXT
- NOTICE: "SQLitePCLRaw — Copyright 2014-2025 SourceGear, LLC. This product includes software
  developed at SourceGear (https://www.sourcegear.com/)."

## SQLite

- The SQLite library is not bundled. The app uses `winsqlite3.dll`, which is part of Windows.
- SQLite is in the public domain — https://sqlite.org/copyright.html

## Fonts and icons

- Icons in the user interface come from the Segoe Fluent Icons / Segoe MDL2 Assets fonts that
  ship with Windows. They are not bundled.
- The Clipboard Manager logo is original artwork of this project (GPL-3.0-or-later).
