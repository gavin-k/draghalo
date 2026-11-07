# DropHalo

[English](README.md) · **简体中文**

Windows 11 上的离线文件转换工具。拖动文件，轻点按键，松手到目标格式即可转换。

[![Build](https://github.com/gavin-k/draghalo/actions/workflows/build.yml/badge.svg)](https://github.com/gavin-k/draghalo/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
![Status: preview](https://img.shields.io/badge/status-preview-lightgrey)

[下载](https://github.com/gavin-k/draghalo/releases) · [支持的转换](#支持的转换) · [从源码构建](#从源码构建) · [参与贡献](CONTRIBUTING.md)

DropHalo 目前是面向 **Windows 11、x64 和 ARM64** 的早期预览版。本文描述当前源码，已发布的安装包可能尚未包含所有功能。程序和安装包从 v0.1.2 起使用 **DropHalo** 名称，仓库地址仍为 `gavin-k/draghalo`。

## 开始使用

1. 从 [GitHub Releases](https://github.com/gavin-k/draghalo/releases) 下载对应架构的安装包。MSI 与 MSIX 的要求见[打包说明](installer/README.md)，其中包括侧载 MSIX 所需的 Windows App SDK 运行时。
2. 打开 DropHalo，按照首次启动的指南操作，也可以用示例图片试一试。
3. 在资源管理器里开始拖动文件。保持鼠标按钮按下，轻按并松开 **Shift**，把指针移到轮盘中的目标格式，再松开鼠标按钮开始转换。

默认把结果保存在源文件旁边。点击任务栏通知区域中的图标，可以打开任务队列、查看进度、撤销支持的操作或进入设置。关闭任务窗口后，DropHalo 仍在通知区域运行；图标可能收在 Windows 的隐藏图标菜单里。

| 拖动文件时的默认手势 | 轮盘 | 示例 |
|---|---|---|
| 轻点 `Shift` | 转换 | 图片、文档、音频、视频、字幕、压缩包 |
| 轻点 `Ctrl+Alt` | 工具 | 压缩或缩小图片、合并或拆分 PDF、识别文字、提取音频 |
| 轻点 `Win` | 动作 | 复制路径、复制或移动文件、重命名、创建 ZIP、放入回收站 |

可以在设置里修改这些键位。默认的轻点手势与资源管理器中按住修饰键拖放的操作相区分。转换轮盘打开时，直接输入格式名称可以搜索；指针停在某一项上，可以查看转换限制或不可用原因。动作轮盘的「放入回收站」默认关闭。

## 功能

- **本地处理。** 内置转换在本机读写文件，不需要上传文件或登录在线账号。
- **临时停靠栏。** 开始拖动文件时，停靠栏会出现在另一侧屏幕边缘。可以从不同文件夹收集文件，再逐个或一起拖出。停靠栏只暂存路径，不移动原文件；拖到文件夹里是复制。可以在设置里关闭。
- **任务队列。** 查看进度、取消、重试、在文件夹中显示输出，并撤销支持的操作。撤销转换会把输出放入回收站。
- **输出设置。** 选择输出文件夹、命名模板，以及遇到重名时自动编号、跳过、替换或每次询问。
- **原生界面。** WinUI 3 窗口支持浅色、深色和 Windows 高对比配色。轮盘使用可调不透明度的纯色底盘，动画遵循 Windows 的动画设置。
- **中英双语。** 界面默认跟随 Windows 显示语言，也可以在设置里指定语言，重启后生效。

在「**设置 → 轮盘外观**」中可调整 0–100% 的背景不透明度，修改立即生效。文字、图标、轮廓和选中状态保持可见。高对比模式下背景保持不透明，退出后恢复所选不透明度。配置保存在 `settings.json` 的 `wheel.backgroundOpacity` 中。

触屏或笔拖动时，还可以启用停住长按唤出轮盘的手势。目前用模拟输入验证过，仍需实体触屏和笔设备验证。资源管理器右键菜单、独立全局快捷键，以及完整的纯键盘轮盘操作尚未实现。

## 支持的转换

以下列出已经实现的转换路径，不代表格式目录中的每个名称都能转换。可用目标取决于源文件和所需引擎。

| 类别 | 输入 | 输出与限制 |
|---|---|---|
| 图片 | JPG、PNG、WebP、AVIF、GIF、BMP、TIFF；另可读 HEIC、PSD、ICO、SVG | JPG、PNG、WebP、AVIF、GIF、BMP、TIFF、ICO。位图转换只取首帧；PSD 取合成图，ICO 取最大图，SVG 按其声明尺寸栅格化。拒绝引用外部文件的 SVG。JPG 和 BMP 的透明部分填白。ICO 最大 256×256，非正方形图片补透明边。尚不能输出 HEIC。 |
| 图片 → PDF | JPG、PNG、WebP、AVIF、GIF、BMP、TIFF | 每张输入图片生成一个 PDF，多页 TIFF 保留各页。把多张图片合为一个 PDF，请使用工具轮盘。 |
| PDF | PDF | DOCX 按段落和图片重建，保留基本文字样式但不保留原版式；PNG 或 JPG 每页 150 DPI；TXT 提取文字层。多页图片输出放在文件夹中。扫描页需使用独立的 OCR 工具识别文字。 |
| Word 文档 | DOCX | TXT、Markdown、HTML。Markdown 和 HTML 保留标题、列表、粗体、斜体、链接和表格。修订按全部接受处理，图片、页眉、页脚和脚注不保留。TXT 用 Tab 分隔表格单元格。 |
| 演示文稿 | PPTX | TXT、Markdown、HTML。导出可见幻灯片、文字、表格、SmartArt 文字和备注；Markdown 和 HTML 保留基本文字格式和列表。图片、图表、动画和幻灯片版式不保留。 |
| 纯文本 | TXT | DOCX，每行一段。 |
| 表格 | XLSX、CSV、TSV | XLSX 的每个可见且非空的工作表转为 CSV 或 TSV；CSV 与 TSV 互转；CSV 或 TSV 转为单工作表 XLSX。公式使用缓存结果，样式和图表不保留，日期导出为文字。生成 XLSX 时，以零开头的编号和超过 15 位的数字保持为文字。 |
| 视频 | MP4、MOV、MKV、WebM、AVI、RMVB | MP4、MOV、MKV、WebM、AVI、GIF，或下列音频输出格式。需要 FFmpeg，不保留内嵌字幕、数据流和附件。 |
| 音频 | MP3、AAC、M4A、WAV、FLAC、OGG、Opus、AIFF、AMR | MP3、AAC、M4A、WAV、FLAC、OGG、Opus、AIFF。需要 FFmpeg；AMR 仅支持输入。 |
| 字幕 | SRT、VTT、ASS、SSA、LRC | 这些格式之间互转。 |
| 压缩包 | ZIP、7z、TAR、TAR.GZ、RAR | ZIP、7z、TAR、TAR.GZ。RAR 仅支持输入；不支持加密压缩包，不保留符号链接、Unix 权限和注释。 |

文本输入支持 UTF-8、带 BOM 的 UTF-16，并以兼容 GBK 的 GB18030 作为回退。CSV 支持逗号或分号分隔，以及 Excel 的 `sep=` 行。

OCR 使用 **Windows.Media.Ocr** 和 Windows 已安装的识别语言，未包含 Windows AI TextRecognizer、RapidOCR 或 Tesseract 回退。可选 LibreOffice 模块以及旧版 DOC/PPT/XLS 转换也尚未实现。更详细的行为见[引擎说明](src/DropHalo.Engines/README.md)。

## 制作自己的工具

DropHalo 不内置 AI 模型。把 [`drophalo-tool` 技能](skills/drophalo-tool/SKILL.md)交给 Claude Code、Codex 或 Cursor 等编程助手，描述你想要的操作。工具存放在：

```text
%LOCALAPPDATA%\DropHalo\tools\<slug>\
```

一个工具包含 `tool.json` 清单，以及按需使用的脚本。配方工具组合内置操作，不需要授予脚本运行权限。脚本工具默认关闭，需要在「**设置 → 我的工具**」查看命令并启用；命令变更后需要重新确认。工具文件变化时，DropHalo 会自动重新加载。

可以在不打断已运行程序的情况下验证工具：

```powershell
DropHalo.exe --validate-tool "C:\path\to\tool-folder"
DropHalo.exe --list-tools
```

工具和动作轮盘的布局保存在 `%LOCALAPPDATA%\DropHalo\layout.json`。可以调整项目位置，也可以放入分组，指针停在分组上会展开外环。技能中说明了如何让助手编辑布局。另见[清单结构](docs/tool-manifest.schema.json)和[工具设计决策](docs/adr/0005-user-tools-manifest-and-trust.md)。

## 隐私与本地数据

内置处理保持离线，DropHalo 当前不会自动检查更新或下载可选模块。用户安装的脚本作为独立进程运行，可以自行联网。设置中的计数器观察主程序的 .NET 网络活动，不覆盖独立进程或原生库自行建立的连接。

设置、工具、轮盘布局、最近选择和日志存放在 `%LOCALAPPDATA%\DropHalo` 下。日志包含处理过的文件路径，保留七天，可在设置里打开或清除。旧版 DropWheel 和 DragHalo 的配置不会自动迁移。

## 从源码构建

Windows 程序使用 **C# / .NET 10 / WinUI 3** 和 Windows App SDK 2.5.1。Core 和引擎也可以在 macOS 或 Linux 上开发；界面和 Shell 集成需要 Windows。

### Windows

需要 Windows 11、.NET 10 SDK 和 `winapp` CLI。环境配置脚本会安装 CLI 工具链、开启开发者模式，并生成本地开发证书。在管理员 PowerShell 中运行配置：

```powershell
git clone https://github.com/gavin-k/draghalo.git drophalo
cd drophalo
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\setup-windows.ps1 -SkipVisualStudio
```

随后构建、测试并生成未签名 MSIX 包：

```powershell
.\scripts\build-windows.ps1
.\scripts\build-windows.ps1 -Arch arm64
```

默认架构为 x64。脚本会下载并校验固定版本的 FFmpeg sidecar，构建解决方案，运行 Core 和 Engines 测试，并把安装包写入 `artifacts/`。不需要安装包时可加 `-SkipPack`。直接用 `dotnet build` 构建程序时，先针对目标架构运行 `scripts/fetch-sidecars.ps1`；没有 FFmpeg 时，除非能在 `PATH` 中找到它，否则音视频操作不可用。

### macOS / Linux

安装 .NET 10 SDK，并把 `dotnet` 放入 `PATH`。音视频测试还需要 `ffmpeg` 和 `ffprobe`；macOS 可运行 `brew install ffmpeg`。

```bash
git clone https://github.com/gavin-k/draghalo.git drophalo
cd drophalo
./build.sh            # 恢复依赖、构建并测试 Core 和 Engines
```

这不会构建或运行 Windows 界面。跨平台流程见 [macOS 开发说明](scripts/dev-mac.md)。Windows 上优先使用前面的 PowerShell 构建脚本。

### 引擎与打包

| 部分 | 实现 |
|---|---|
| 图片 | Magick.NET / ImageMagick |
| 音视频 | 独立进程运行的 FFmpeg LGPL 构建；H.264 编码使用 Media Foundation，WebM 重新编码使用 AV1 + Opus |
| PDF | 经 PDFtoImage 调用 PDFium，加上 PdfPig 和 SkiaSharp |
| 文档与表格 | OOXML 读写器；Open XML SDK 用于测试 |
| OCR | Windows.Media.Ocr |
| 压缩包 | SharpCompress |
| Windows 集成 | Win32 钩子、拖拽图像检测、OLE 放置目标、通知区域图标 |
| 安装包 | `winapp` 生成 MSIX / MSIX bundle，WiX v5 生成 MSI |

[构建 CI](.github/workflows/build.yml)在 push 和 pull request 时运行。[发布工作流](.github/workflows/release.yml)构建 x64 和 ARM64 安装包，并通过 Azure Trusted Signing 签署标签发布。打包方式和运行时依赖见 [`installer/README.md`](installer/README.md)。仓库中有安装包清单，并不代表已在 Store 或 winget 上架。

## 仓库结构

```text
drophalo/
├─ docs/                    产品说明、架构、ADR、设计参考
├─ src/
│  ├─ DropHalo.App/          WinUI 轮盘、停靠栏、队列、设置、引导、托盘
│  ├─ DropHalo.Core/         转换图、任务、命名、设置、工具
│  ├─ DropHalo.Shell/        Win32 钩子、拖拽检测、OLE、文件操作
│  ├─ DropHalo.Engines/      图片、媒体、PDF、文档、表格、压缩、字幕引擎
│  └─ DropHalo.ContextMenu/  计划中的资源管理器集成，目前仅有文档
├─ skills/drophalo-tool/     编程助手技能、清单参考、示例工具
├─ tests/                   单元测试和 Windows 验收程序
├─ scripts/                 环境配置、构建、sidecar 下载、开发辅助
├─ installer/               MSIX、MSI、winget 清单模板
└─ .github/workflows/       构建与发布自动化
```

## 文档与贡献

- [贡献指南](CONTRIBUTING.md)
- [设计系统与参考](docs/design/README.md)
- [架构](docs/02-architecture.md)与[架构决策](docs/adr/)
- [技术调研](docs/01-tech-stack.md)与[路线图](docs/04-roadmap.md)：其中也包含未来方案，不只描述已实现的功能
- [English README](README.md)

## 许可证

[MIT](LICENSE) © 2026 Gavin Lee。随附依赖的许可见[第三方许可证](docs/05-third-party-licenses.md)。
