# DropHalo

**English** · [简体中文](README_zh.md)

An offline file converter for Windows 11. Drag a file, tap a key, and drop it onto a format.

[![Build](https://github.com/gavin-k/draghalo/actions/workflows/build.yml/badge.svg)](https://github.com/gavin-k/draghalo/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
![Status: preview](https://img.shields.io/badge/status-preview-lightgrey)

[Download](https://github.com/gavin-k/draghalo/releases) · [Supported conversions](#supported-conversions) · [Build from source](#build-from-source) · [Contributing](CONTRIBUTING.md)

DropHalo is an early preview for **Windows 11, x64 and ARM64**. This README describes the current source; published builds may have fewer features. The app and packages have used the name **DropHalo** since v0.1.2. The repository URL remains `gavin-k/draghalo`.

## Get started

1. Download a package for your architecture from [GitHub Releases](https://github.com/gavin-k/draghalo/releases). See the [packaging guide](installer/README.md) for MSI and MSIX requirements, including the Windows App SDK runtime needed for MSIX sideloading.
2. Open DropHalo and follow the first-run guide. You can try it with a sample image.
3. Start dragging a file in File Explorer. While holding the mouse button, briefly press and release **Shift**. Move onto a format in the wheel and release the mouse button to convert.

By default, results are saved beside the source file. Click the notification-area icon to open the task queue, check progress, undo supported operations, or open Settings. Closing the task window leaves DropHalo running in the notification area; its icon may be inside Windows' hidden-icons menu.

| Default gesture during a file drag | Wheel | Examples |
|---|---|---|
| Tap `Shift` | Convert | Images, documents, audio, video, subtitles, archives |
| Tap `Ctrl+Alt` | Tools | Compress or resize images, combine or split PDFs, OCR, extract audio |
| Tap `Win` | Actions | Copy paths, copy or move files, rename, create ZIP archives, send to the Recycle Bin |

Change these bindings in Settings. The default tap gesture is separate from holding a modifier while dropping a file in Explorer. Type a format name while the Convert wheel is open to search; hover over an item to see conversion limitations or why it is unavailable. Sending files to the Recycle Bin is disabled by default in the Actions wheel.

## Features

- **Local processing.** Built-in conversions read and write files on your computer. No upload or online account is required.
- **A temporary file dock.** A dock appears at the opposite screen edge when you start dragging files. Collect files from different folders, then drag them out individually or together. The dock stores paths without moving the originals; dragging them into a folder copies them. You can turn the dock off in Settings.
- **A task queue.** View progress, cancel, retry, reveal output files, and undo supported operations. Undoing a conversion sends its output to the Recycle Bin.
- **Output control.** Choose an output folder, a naming template, and what happens when a name already exists: number the new file, skip, replace, or ask.
- **A native interface.** WinUI 3 windows use light, dark, and Windows high-contrast colors. The wheel uses a solid-color surface with adjustable opacity; its animations follow the Windows animation setting.
- **Seven interface languages.** Simplified Chinese, English, Korean, Japanese, German, French, and Spanish. Follow the Windows display language or choose one in Settings, then confirm to restart and apply it.

Under **Settings → Wheel appearance**, background opacity ranges from 0 to 100% and updates immediately. Labels, icons, outlines, and selection states remain visible. High-contrast mode keeps the background opaque; leaving high contrast restores your chosen opacity. The setting is stored as `wheel.backgroundOpacity` in `settings.json`.

Touch and pen drags also have an optional stationary long-press gesture. It has been exercised with simulated input; physical touch and pen validation is still needed. Explorer context-menu integration, a standalone global hotkey, and a complete keyboard-only wheel workflow are not implemented.

## Supported conversions

These are the implemented conversion routes, rather than every format named in the catalog. Available targets depend on the source file and the required engine.

| Category | Input | Output and limits |
|---|---|---|
| Images | JPG, PNG, WebP, AVIF, GIF, BMP, TIFF; also HEIC, PSD, ICO, SVG | JPG, PNG, WebP, AVIF, GIF, BMP, TIFF, ICO. Raster conversion uses the first frame; PSD uses its merged image, ICO its largest image, and SVG its declared size. SVG references to external files are rejected. JPG and BMP flatten transparency onto white. ICO output is at most 256×256, with transparent padding for non-square images. HEIC output is unavailable. |
| Images → PDF | JPG, PNG, WebP, AVIF, GIF, BMP, TIFF | One PDF per input image; multi-page TIFF keeps its pages. Use the Tools wheel to combine multiple images into one PDF. |
| PDF | PDF | DOCX rebuilt from paragraphs and images, preserving basic text styling but not the original layout; PNG or JPG at 150 DPI per page; TXT from the text layer. Multi-page image output goes into a folder. Scanned pages need the separate OCR tool for text recognition. |
| Word documents | DOCX | TXT, Markdown, HTML. Markdown and HTML retain headings, lists, bold, italic, links, and tables. Revisions are treated as accepted; images, headers, footers, and footnotes are omitted. TXT uses tabs between table cells. |
| Presentations | PPTX | TXT, Markdown, HTML. Exports visible slides, text, tables, SmartArt text, and notes. Markdown and HTML retain basic text formatting and lists. Images, charts, animations, and slide layout are omitted. |
| Plain text | TXT | DOCX, with one paragraph per line. |
| Spreadsheets | XLSX, CSV, TSV | XLSX → CSV or TSV for each visible, nonempty worksheet; CSV ↔ TSV; CSV or TSV → a single-sheet XLSX. Formulas use cached results; styles and charts are omitted. Dates are exported as text. Leading-zero identifiers and numbers longer than 15 digits remain text when creating XLSX. |
| Video | MP4, MOV, MKV, WebM, AVI, RMVB | MP4, MOV, MKV, WebM, AVI, GIF, or any audio output below. Requires FFmpeg. Embedded subtitle, data, and attachment streams are not retained. |
| Audio | MP3, AAC, M4A, WAV, FLAC, OGG, Opus, AIFF, AMR | MP3, AAC, M4A, WAV, FLAC, OGG, Opus, AIFF. Requires FFmpeg; AMR is input-only. |
| Subtitles | SRT, VTT, ASS, SSA, LRC | Conversion between these formats. |
| Archives | ZIP, 7z, TAR, TAR.GZ, RAR | ZIP, 7z, TAR, TAR.GZ. RAR is input-only. Encrypted archives are unsupported; symbolic links, Unix permissions, and comments are not preserved. |

Text input supports UTF-8, BOM-marked UTF-16, and a GB18030 fallback that includes GBK. CSV accepts comma or semicolon delimiters and Excel's `sep=` line.

OCR uses **Windows.Media.Ocr** and the recognition languages installed in Windows. Windows AI TextRecognizer, RapidOCR, and Tesseract fallbacks are not included. The optional LibreOffice module and legacy DOC/PPT/XLS conversion are also not implemented. See the [engine notes](src/DropHalo.Engines/README.md) for detailed behavior.

## Create your own tools

DropHalo does not include an AI model. Give the [`drophalo-tool` skill](skills/drophalo-tool/SKILL.md) to your coding agent, such as Claude Code, Codex, or Cursor, and describe the operation you want. Tools live in:

```text
%LOCALAPPDATA%\DropHalo\tools\<slug>\
```

A tool contains a `tool.json` manifest and, when needed, a script. Recipes combine built-in operations and need no script permission. Script tools stay disabled until you review their command in **Settings → My tools** and enable them; changing the command requires approval again. DropHalo reloads tools when their files change.

Validate a tool without interrupting a running app:

```powershell
DropHalo.exe --validate-tool "C:\path\to\tool-folder"
DropHalo.exe --list-tools
```

The Tools and Actions wheel layouts are stored in `%LOCALAPPDATA%\DropHalo\layout.json`. Items can be rearranged or placed in groups that expand into an outer ring on hover. The skill explains how an agent can edit these layouts. See the [manifest schema](docs/tool-manifest.schema.json) and [tool design decisions](docs/adr/0005-user-tools-manifest-and-trust.md).

## Privacy and local data

Built-in file processing stays offline, without uploading files or collecting usage data. The app checks for stable updates after startup and every 24 hours. After your confirmation, it downloads, verifies the publisher's signature, installs, and reopens. Updates use the [official CNB mirror](https://cnb.cool/shhield-ai/drophalo) by default; Settings can select GitHub, disable automatic checks, or enable strict offline mode to prevent checks and downloads. MSI updates require administrator approval. Settings and tools are kept; the task list is cleared. Version 0.1.5 and earlier require one manual installation of a version containing the updater.

User-installed scripts run as separate processes and can make their own network requests. The counter in Settings observes the app's .NET network activity; it does not cover separate processes or native libraries' own connections.

Settings, tools, wheel layouts, recent choices, and logs are stored under `%LOCALAPPDATA%\DropHalo`. Logs include processed file paths, are retained for seven days, and can be opened or cleared from Settings. Configurations from the older DropWheel and DragHalo names are not migrated automatically.

## Build from source

The Windows app uses **C# / .NET 10 / WinUI 3** with Windows App SDK 2.5.1. Core and engine development can also be done on macOS or Linux; the UI and Shell integration require Windows.

### Windows

Use Windows 11, the .NET 10 SDK, and the `winapp` CLI. The setup script installs the CLI toolchain, enables Developer Mode, and creates a local development certificate. Run setup in an elevated PowerShell:

```powershell
git clone https://github.com/gavin-k/draghalo.git drophalo
cd drophalo
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\setup-windows.ps1 -SkipVisualStudio
```

Then build, test, and create an unsigned MSIX package:

```powershell
.\scripts\build-windows.ps1
.\scripts\build-windows.ps1 -Arch arm64
```

The default architecture is x64. The script downloads and verifies the pinned FFmpeg sidecar, builds the solution, runs Core and Engines tests, and writes packages to `artifacts/`. Use `-SkipPack` when no package is needed. If you build the app directly with `dotnet build`, run `scripts/fetch-sidecars.ps1` for the target architecture first; without FFmpeg, media operations are unavailable unless it is found on `PATH`.

### macOS / Linux

Install the .NET 10 SDK and make `dotnet` available on `PATH`. Media tests also need `ffmpeg` and `ffprobe`; on macOS, use `brew install ffmpeg`.

```bash
git clone https://github.com/gavin-k/draghalo.git drophalo
cd drophalo
./build.sh            # Restore, build, and test Core and Engines
```

This does not build or run the Windows UI. See [the macOS development guide](scripts/dev-mac.md) for the cross-platform workflow. On Windows, prefer the PowerShell build script above.

### Engines and packaging

| Area | Implementation |
|---|---|
| Images | Magick.NET / ImageMagick |
| Audio and video | FFmpeg LGPL build in a separate process; Media Foundation for H.264 encoding, AV1 + Opus for WebM re-encoding |
| PDF | PDFium through PDFtoImage, PdfPig, and SkiaSharp |
| Documents and spreadsheets | OOXML readers and writers; Open XML SDK is used in tests |
| OCR | Windows.Media.Ocr |
| Archives | SharpCompress |
| Windows integration | Win32 hooks, drag-image detection, OLE drop targets, and a notification-area icon |
| Packages | MSIX / MSIX bundle through `winapp`; MSI through WiX v5 |

[Build CI](.github/workflows/build.yml) runs on pushes and pull requests. The [release workflow](.github/workflows/release.yml) builds x64 and ARM64 packages and signs tagged releases through Azure Trusted Signing. Packaging instructions and runtime dependencies are in [`installer/README.md`](installer/README.md). Store and winget listings are not implied by the presence of package manifests.

## Repository map

```text
drophalo/
├─ docs/                    Product notes, architecture, ADRs, design references
├─ src/
│  ├─ DropHalo.App/          WinUI wheel, dock, queue, settings, onboarding, tray
│  ├─ DropHalo.Core/         Conversion graph, jobs, naming, settings, tools
│  ├─ DropHalo.Shell/        Win32 hooks, drag detection, OLE, file operations
│  ├─ DropHalo.Engines/      Image, media, PDF, document, sheet, archive, subtitle engines
│  └─ DropHalo.ContextMenu/  Planned Explorer integration; documentation only
├─ skills/drophalo-tool/     Agent instructions, manifest reference, example tools
├─ tests/                   Unit tests and Windows acceptance driver
├─ scripts/                 Setup, builds, sidecar downloads, development helpers
├─ installer/               MSIX, MSI, and winget manifest templates
└─ .github/workflows/       Build and release automation
```

## Documentation and contributing

- [Contributing guide](CONTRIBUTING.md)
- [Design system and references](docs/design/README.md)
- [Architecture](docs/02-architecture.md) and [architecture decisions](docs/adr/)
- [Technology research](docs/01-tech-stack.md) and [roadmap](docs/04-roadmap.md) — these also contain future proposals, not just implemented features
- [简体中文 README](README_zh.md)

## License

[MIT](LICENSE) © 2026 Gavin Lee. See [third-party licenses](docs/05-third-party-licenses.md) for bundled dependencies.
