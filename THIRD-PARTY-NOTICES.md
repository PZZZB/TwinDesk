# Third-party components

TwinDesk starts the unmodified Deskflow 1.27.0 native engine as a separate process.

- Project: https://github.com/deskflow/deskflow
- Version: 1.27.0, released October 1, 2026.
- Source: https://github.com/deskflow/deskflow/tree/v1.27.0
- Source archive: https://github.com/deskflow/deskflow/archive/refs/tags/v1.27.0.tar.gz
- License: GPL-2.0-only with the upstream OpenSSL exception. The bundled engine retains its `LICENSE` and `LICENSE_EXCEPTION` files.
- Windows x64 package: https://github.com/deskflow/deskflow/releases/download/v1.27.0/deskflow-1.27.0-win-x64-portable.zip
- Package SHA-256: `8d19004cc3625623489d96580c1b58fd13ee78185689907fd4478595f93fab9a`

The engine package also contains its required Qt, OpenSSL, and Microsoft runtime libraries. Keep the complete upstream package and its notices together when redistributing it. TwinDesk's desktop interface uses Avalonia and restores its dependencies through NuGet.

Published bundles include a `licenses` directory with collected dependency license texts, notices, package metadata, and a manifest of sources and SHA-256 hashes. The self-contained .NET runtime's license and notices are included there as well. The Windows bundle retains the official Deskflow archive's license files unchanged. The corresponding unmodified Deskflow 1.27.0 source archive is available with the GitHub Release and from the upstream source link above.
