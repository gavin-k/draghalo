# Assets

全部由 `scripts/gen-icons.py` 生成，不要手改：改 Logo 就改脚本里的几何参数，然后 `uv run scripts/gen-icons.py` 重新生成并提交产物（构建不需要 Python）。母版 SVG 和 1024 px 大图在 `docs/design/exports/`。

Logo：橙色圆角底板（`#EA580C` → `#C2410C` 竖向渐变）上一圈白色圆环，按转换轮盘切成 6 个 60° 扇区（第一个从 -120° 起，所以正上方是一整个扇区）；正上方那个扇区全白，表示指针所在的选中项，其余五个半透明；圆心是一滴指向它的水滴。32 px 及以下的尺寸单独调过几何，直接缩小 256 px 的版本会把扇区间的缝糊掉。

- `DropHalo.ico`：窗口标题栏、托盘、exe 图标和 MSI 的「程序和功能」图标。16–48 px 是 32 位 DIB，64 px 起是 PNG；每个尺寸单独绘制，托盘按当前 DPI 的 `SM_CXSMICON`（16 / 20 / 24 / 28 / 32 / 36）取对应条目。
- MSIX 清单引用不带限定符的文件名（`Square44x44Logo.png` 等），同名的 `scale-*`、`targetsize-*`、`altform-unplated` / `altform-lightunplated` 变体由 MRT 按显示缩放和用途挑选；不带限定符的那张是兜底。底板就是图标本身，所以 plated 和 unplated 是同一张图。
