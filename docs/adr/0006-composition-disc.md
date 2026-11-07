# ADR-0006 · 轮盘圆盘改为 Composition 纯色，不再用亚克力

- 状态：已接受（2026-11-07）

## 背景

ADR-0001 选 Windows App SDK 2.x 的直接原因是 `SystemBackdropElement` 能做「透明窗口 + 局部亚克力圆盘」，ADR-0004 的轮盘窗也按这个配方描述。实际用下来有三件事一直在打架：

- 系统「透明效果」关着（Win10 真机、远程桌面、省电模式）时亚克力回退成纯色，用户在两台机器上看到的轮盘不一样，设置页还要专门解释（M1 进展「透明效果提示」）。
- 亚克力圆盘和 XAML 形状遮罩叠在一起，首帧偶尔画出白块，白块自检、首帧截图比对都是为它加的保险。
- 2026-11-06 的界面重设计（`docs/design/README.md`）把轮盘定为纯色底盘加浅蓝选中态，亚克力已经不在视觉稿里。

## 决定

- 轮盘圆盘由 Composition 的椭圆几何直接画：一个填充视觉、一个 1 DIP 内描边视觉，颜色来自 `AppStyles` 解析的设计令牌，高对比下按系统 `UIElementColor` 取色，不透明度在高对比下恒为 1。
- `SystemBackdropElement` 从轮盘窗移除；DWM 透明配方（分层窗口、`NOACTIVATE | TOOLWINDOW | TOPMOST`）不变。
- 设置页的「透明效果」提示随之删除，`UISettings.AdvancedEffectsEnabled` 不再影响轮盘。
- `Tokens.xaml` 只保留浅色 / 深色两套字典；高对比不走 XAML 的 `ThemeDictionaries`。

## 后果

- 所有机器上的轮盘长得一样，验收不再分「透明开 / 关」两条路径；`design-qa.md` 记录 ARM64 虚拟机上 252/252 项通过，45 次唤出最大 75.7 ms，100 次连续唤出最大 110.2 ms，10 次冷启动首次唤出最大 36.3 ms。
- ADR-0001 里「选 2.x 是为了 `SystemBackdropElement`」的理由已经不成立；留在 2.x 的理由改为 Composition 动效和已验证的打包、签名链路。
- 以后要半透明，走 Composition 的 `CreateBackdropBrush` 一类方案，而不是回到 XAML 背景元素。
