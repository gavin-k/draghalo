# Assets

全部由 `scripts/gen-icons.py` 生成，不要手改：改 Logo 就改脚本里的几何参数，然后 `uv run scripts/gen-icons.py` 重新生成并提交产物（构建不需要 Python）。母版 SVG 和 1024 px 大图在 `docs/design/exports/`。

Logo：蓝色圆角底板（`#1684FF` → `#0067E8` 竖向渐变），白色开口光环环抱水滴。光环只保留两个清晰的弧段；32 px 及以下使用单独的几何尺寸，避免小图标的开口糊在一起。

- `DropHalo.ico`：窗口标题栏、exe 图标和 MSI 的「程序和功能」图标。16–48 px 是 32 位 DIB，64 px 起是 PNG，每个尺寸单独绘制。
- `DropHalo.Tray.ico`：透明底的 `#0067F4` 蓝色光环与水滴，含 16 / 20 / 24 / 28 / 32 / 36 / 40 / 48 / 64 px，按当前 DPI 的 `SM_CXSMICON` 取条目。图标只在启动时加载，Explorer 重启时复用；文件加载失败会记录错误并回退到 exe 内嵌的主图标。`docs/design/exports/drophalo-tray.svg` 和 `drophalo-tray-256.png` 是其设计导出。
- MSIX 清单引用不带限定符的文件名（`Square44x44Logo.png` 等），同名的 `scale-*`、`targetsize-*`、`altform-unplated` / `altform-lightunplated` 变体由 MRT 按显示缩放和用途挑选；不带限定符的那张是兜底。底板就是图标本身，所以 plated 和 unplated 是同一张图。

生成脚本会解码每个 ICO 尺寸，检查透明像素、可见像素、托盘的蓝色和光环开口。Windows 验收还需覆盖不同缩放、托盘点击、Explorer 重启，以及暂时缺失托盘文件时的内嵌图标回退。
