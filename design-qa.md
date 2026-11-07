# Windows 原生界面设计核验

status: implemented; visual, functional and performance checks passed on Windows ARM64 VM

final result: passed within the tested environment

更新日期：2026-11-07。最终复审后的 Windows 原生验收 252/252 项通过。45 次唤出最大 75.7 ms，100 次连续唤出最大 110.2 ms，10 次独立进程首次分组唤出最大 36.3 ms，全部满足原 150 ms 门槛。通过结论适用于本次 ARM64 虚拟机。

`artifacts/` 中的原始日志、截图和分析记录保留在执行验收的工作区。仓库记录验收范围与结果。

## 参考与比较方式

选定方案1「瓷白轻环」：[参考图](artifacts/redesign-20261106/selected-reference.png)，1487×1058 px。将参考图与真实Windows截图对照检查字体、间距、颜色、图标和文案。参考是多窗口概念图，不做不同尺寸的整图像素相减。

实现为原生WinUI 3。实测Windows 11 ARM64虚拟机，工作区3024×1716，200%缩放（日志scale 2）。设置窗口1280×1520 px对应640×760 DIP；任务窗口1040×880 px；入门1120×1440 px；停靠栏640×800 px。浏览器viewport和CSS检查不适用。

## 预期差异

1. 设置保留十个分区：唤出轮盘、轮盘外观、停靠栏、文件动作、我的工具、轮盘排布、输出、隐私与离线、语言、启动。概念图只显示三段，不能因此删除其他配置。
2. 组合键保留Shift / Ctrl / Alt / Win原生复选框、校验和自动保存；保留UI Automation标识。
3. 六扇区、中心区、推荐排序、不可用原因、搜索和分组外环沿用真实模型与原命中几何。中心在悬停时展示质量/操作说明。
4. 实色底盘和轻量边线替代Acrylic；不引入WebView、字体下载、持续动画、动态大阴影或新的常驻轮询。0–100%背景不透明度仍可配置。
5. 使用原生窗口边框、系统字体和Segoe图标，保留键盘焦点环。托盘使用单独的小尺寸透明Logo，窗口使用应用图标。

## 五项视觉核验

| 项目 | 观察与证据 |
|---|---|
| 字体 | 标题、正文、按钮和轮盘文字清晰；中文换行完整；[设置顶部](artifacts/redesign-20261106/ui/settings-top.png)、[入门](artifacts/redesign-20261106/ui/onboarding.png) |
| 间距 | 设置卡片、分区层级和末尾配置可达；[设置底部](artifacts/redesign-20261106/ui/settings-bottom.png)、[任务空态](artifacts/redesign-20261106/ui/queue-empty.png) |
| 颜色 | 瓷白/冷灰/蓝色强调；同一进程浅深色和高对比通过；[浅色轮盘](artifacts/redesign-20261106/ui/wheel-white-100.png)、[高对比轮盘](artifacts/redesign-20261106/theme/theme-wheel-hc-zero.png) |
| 图标 | 轮盘原生图标清楚；[托盘尺寸板](artifacts/redesign-20261106/tray-sizes.png)浅深背景可识别；16–48 px八尺寸加载及缺失文件回退均通过 |
| 内容 | 完整设置与安全提示保留；[空停靠栏](artifacts/redesign-20261106/app-20261106-214737-dock-empty.png)、[文件停靠栏](artifacts/redesign-20261106/app-20261106-214737-dock-filled.png)文件名/路径/清空入口清楚，源文件保留 |

透明度0%的背景对比度由背后内容决定；文字、图标、边线保持独立不透明。高对比模式强制实体背景，退出恢复原值，见[透明度恢复截图](artifacts/redesign-20261106/theme/theme-wheel-restored-zero.png)。

## 问题与修复闭环

- 已修复：原高对比事件订阅在桌面抛COM异常。改用系统颜色与根元素主题通知，实际启动和同进程切换通过。
- 已修复：透明窗口的默认系统背景配置回调在高对比切换中抛异常。固定透明画刷明确覆盖该回调；同一进程完成HC启停、重新拖拽和opacity 0恢复。同步释放DWM区域句柄，避免重复配置泄漏。
- 已修复：浅色轮盘被旧白块诊断误判。Core诊断检查分布式细节；25项针对回归通过。Acceptance改为比较出现前后至少三象限的新细节，真实截图检查通过。
- 已修复（测试工具）：过早截图、阻塞消息等待、高对比结构字符串封送以及OS异步取消拖拽的验收时序；最终截图与退出、同PID检查均通过。没有用重启应用绕过主题刷新。
- 复审修复了较大方形拖图的四角干扰白块诊断的问题。应用和验收共用中心排除尺寸，四个缩放比例的回归覆盖 128 DIP 拖图。
- 复审将离屏预热与真实首帧记录分开。预热沿用实际窗口的尺寸和入场路径，压力验收检查首次用户输入前没有真实首帧记录。
- **性能门禁已在本次VM闭环。** 最初完整Acceptance为250通过、2失败；45次原计时最高372.7 ms，43/45在150 ms内。跟踪发现首帧前仍有XAML文本布局与圆形背景遮罩绘制，同时存在调度等待，不能将全部延迟归因某一行或VM。现已去除相同文本重复赋值、复用入场动画、按实际DPI预热分组窗口，并用Composition几何保持同样的底盘外观。修复后完整验收45/45唤出通过、最大94.1 ms；100次连续唤出最大66.4 ms；10次独立进程首次分组唤出最大48.3 ms。原150 ms阈值、计时公式及全部样本要求未放宽，历史失败和诊断边界保留在[性能记录](artifacts/wheel-perf-20261106/verification.md)。

## 验证结果与边界

- macOS：Core 481通过、1项Windows限定跳过；Engines 292通过；Shell编译通过。Windows Core 482/482通过。
- 最终Composition实现的ARM64与x64应用编译通过，各0 error、6项既有warning；ARM64实际运行。下列验收均在最终性能与透明背景修复后执行。
- [UI smoke](artifacts/wheel-perf-20261106/composition-ui/ui-result.json)：37项通过，包含十个设置分区、配置持久化、无关配置保留及三档预览正常退出；截图对照确认设计保持。
- [主题 smoke](artifacts/wheel-perf-20261106/composition-theme-visible/theme-result.json)：15项通过，同一PID，高对比/浅深色/透明度恢复通过，restorationErrors为空。首次运行因外部窗口遮挡失败，原证据保留。
- [完整验收](artifacts/wheel-perf-20261106/full-composition/acceptance.log)：252/252通过，包含轮盘、分组、停靠栏自动显示与拖入拖出、文件动作、工具、设置、重启及中英文流程。
- [连续唤出](artifacts/wheel-perf-20261106/composition-100/performance-20261107-021110.json)：100/100满足150 ms门槛，全部保持Explorer焦点，取消后无任务或输出；[首次分组唤出](artifacts/wheel-perf-20261106/composition-cold-group-results.json)：10个独立进程全部通过，非清空系统缓存的硬冷启动。
- 未做正式包安装升级、物理ARM64/x64运行、分数DPI和多显示器混合DPI、Explorer重启后的托盘重建实测。自动显示依赖SysDragImage，未保证所有无拖图来源。

## 复审后的复验

- 扩大中心拖图后的 4 个缩放用例均复现旧判定错误。修复后 Windows Core 482/482 通过，macOS Core 481 通过、1 个 Windows 专用用例跳过，Engines 292/292 通过。ARM64 和 x64 应用及 ARM64 验收工具均编译通过。
- 浅色设置、轮盘和深色任务窗口截图已目视检查。同一候选的 [UI 37 项](artifacts/redesign-review-20261107/review-20261107-080448-ui/ui-result.json)和[主题 15 项](artifacts/redesign-review-20261107/review-20261107-080448-theme/theme-result.json)通过，主题设置已恢复。
- [最终样本统计](artifacts/redesign-review-20261107/final-results.json)核对了 10 次冷启动、252 项完整功能验收和 100 次连续唤出。三个阶段使用相同的应用二进制。每次压力测试保持 Explorer 焦点，取消后无任务或文件输出。
- [环境与历史失败记录](artifacts/redesign-review-20261107/conditions.md)保留高负载期间的两批冷启动失败，以及一次完整验收中未收到 ZIP 文件进入的失败。负载回落后冷启动全部通过，复验的完整功能套件包含 ZIP 转 7z。ZIP 失败的具体原因尚未确定。

详细运行日志、过程和环境收尾见[性能验收记录](artifacts/wheel-perf-20261106/verification.md)；早期设计阶段证据见[原始验收记录](artifacts/redesign-20261106/verification.md)。
