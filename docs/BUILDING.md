# Building DDO Studio 1.7.2

## Development preview

From a Windows Command Prompt in the source root:

```bat
build-preview-and-run.cmd
```

The script builds the backend, exporter, and desktop application and launches the local preview configuration.

## End-user release

```bat
build-release.cmd
```

The release builder:

1. restores the .NET projects for win-x64;
2. publishes the backend, exporter, and desktop application as self-contained components;
3. stages the offline Three.js viewer runtime;
4. removes PDB files from the portable release;
5. stages the WebView2 Evergreen standalone installer;
6. builds the Inno Setup installer.

Expected installer output:

```text
release\installer\DDOStudio-Setup-1.7.2.exe
```

Use `BUILD_END_USER_INSTALLER.cmd` as the convenience wrapper for the same final release flow.

## Runtime data

DDO DAT content is never part of the build tree. The installed application reads the user's own local DDO installation. Generated indexes, preview caches, aliases, and diagnostics are written under LocalAppData at runtime.

## Model index refresh

Version 1.7.2 uses model-index schema 7. An older LocalAppData index is rebuilt automatically so standalone weapons and shields return to normal model search while unreliable worn/composed equipment remains excluded from the public browser.

## Final 1.7.2 viewer defaults

The release ships `viewer/animation-names.json` as the built-in animation label database. Runtime `%LOCALAPPDATA%\DDO Asset Studio\animation-aliases.json` remains a user-only override and is never packaged. Weapon previews use class-based Position Y `1.40x`, Rotate X `90°` defaults and prefer looping compatible `0x05005943`; non-weapon models continue to prefer compatible `0x05000440`.
