# 应用更新与 CNB 镜像

应用启动 45 秒后检查一次，此后每 24 小时检查正式版。默认来源为 `shhield-ai/drophalo` 的 CNB 公开镜像，可在设置中切换到 `gavin-k/draghalo` 的 GitHub Release。关闭自动检查仍可手动检查；严格离线会取消正在进行的检查或下载，并禁止新请求。

发现新版后显示安装按钮并发出托盘通知。用户确认后才下载，按当前进程架构及安装类型选择 MSI 或 MSIX。安装前再次检查该版本仍是所选来源提供的最新正式版。正在排队或执行的文件任务会阻止安装；下载期间启动了任务也会重新检查。设置、工具及自定义 `--data-dir` 会保留，进程内任务列表会清空。

## 信任边界

- 只接受固定官方仓库的三段正式版标签，不安装相同版本、降级或预发布。
- 清单和校验和限制大小，下载限制长度、总时长和无进度等待；仅允许 HTTPS 及精确的 GitHub/CNB 下载域，不把账号令牌放入客户端。
- 下载完成校验 SHA-256；安装 helper 在只读文件锁下再次检查长度、SHA-256、Windows 可信签名和固定发行者 Subject。证书由 Azure Trusted Signing 轮换，因此不固定叶证书指纹。
- MSI 必须匹配 DropHalo 的 UpgradeCode、ProductName 和选中版本。系统安装进程单独请求管理员授权，使用 `/norestart`，不自动重启电脑；原用户负责重新打开应用。
- MSIX 必须匹配 `GavinLee.DropHalo`、Publisher、选中版本及架构，随后由 Windows 的 `Add-AppxPackage` 再执行系统验证；不启用无签名或降级选项。重新打开时查询新版安装目录。
- 取消、下载损坏或验证失败不会退出当前应用。安装失败后尝试重新打开原版本，并在数据目录写入 `update-result.json`；MSI 诊断日志保留在本次临时更新目录。

0.1.5 及更早版本没有更新器，需先手动安装一次新版。这里的自动更新是应用内自动检查、用户确认后安装；应用关闭时不会独立运行后台更新服务。

## 镜像发布

`.cnb.yml` 仅接受可信手动事件与每 5 分钟的定时事件，不监听 push。任务共用锁，不会因写回清单而无限触发。GitHub 的签名构建继续由原有发布流程完成；CNB 复制这些原始产物，不重新构建或签名。

CNB 同仓任务使用构建时注入、结束即销毁的 `CNB_TOKEN`：

1. 拉取 GitHub 公开源码及标签，合并到 CNB main，并推送原始标签。
2. 枚举 GitHub 从 v0.1.6 开始的所有正式 Release，逐版本要求五种安装包及 `SHA256SUMS.txt` 恰好全部就绪；短时间连续发布也不会漏掉中间版本。
3. 下载原始文件，校验大小、SHA-256 和 GitHub 提供的 digest；上传 CNB 后逐项读回比对。
4. 每个版本的六个文件核验后，才发布 CNB Release，记录 `updates/releases/<tag>.json`。记录包括上游资产 ID、长度、SHA-256 及本次读回对应的 CNB 资产 ID 和平台哈希。后续只有这些信息均未改变，才复用已核验记录；同名同长度不足以判定文件一致。
5. 再次读取 GitHub latest，只将仍匹配的对应 CNB Release 设为 latest，并提交 `updates/latest.json` 和逐版本记录。历史版本不会改变最新指针。

失败时保留旧清单，下次手动或定时任务可重试；同名不同内容的既有附件不会被覆盖。CNB main 包含镜像清单提交，因此是保留上游历史的派生镜像，版本标签仍指向 GitHub 的原始源码提交。

GitHub 发布流程会等待 CNB 的对应版本记录，再匿名下载 CNB 的六个公开文件，逐项与本次签名产物的长度和 SHA-256 比对，并确认两源的最新指针一致，全部通过才显示发布成功。最长等待 20 分钟；超时、缺包或内容不符都会使发布流程失败。GitHub Release 在等待开始前已经公开，两站之间存在调度、排队和传输造成的短暂同步窗口；验证失败不会自动撤回已公开的 GitHub Release。

已发布的同一版本不得覆盖安装包。重跑发布流程时，已有 GitHub 文件必须与本次产物完全一致才可复用；重新构建或签名改变文件后，必须增加版本号。镜像网络故障恢复后，可以运行只读的 `Verify release mirrors` 工作流复验，不必重新打包。

应用通过公开 git raw 读取 CNB 清单，因为 CNB API 的 latest 读取需要认证。每 5 分钟是检查间隔，并非完成同步的时限；需要立即启动镜像时，在 CNB main 代码页点击“同步 GitHub 镜像”。无需为 GitHub 创建长期 CNB 令牌。共享出口的 GitHub 匿名限额或源不可达会使本次同步失败并保留旧清单，不代表上游没有更新。

## 验证入口

`dotnet test tests/DropHalo.Core.Tests` 覆盖版本选择、来源、严格离线、重定向、校验失败、文件保留及设置持久化。`python3 -m unittest discover -s tests -p 'test_sync_cnb_release.py' -v` 覆盖同长度替换、缺少平台哈希、同步中途变更、连续多版本和公开包验证。

`python3 scripts/sync-cnb-release.py --verify v0.1.6 --wait 1200` 是只读的线上一致性核验；加 `--assets-dir <目录>` 可额外比对本地六个原始产物。Windows 界面验收为 `DropHalo.Acceptance updates --exe <DropHalo.exe>`，实际安装验收还需使用真实签名包及当前用户会话。
