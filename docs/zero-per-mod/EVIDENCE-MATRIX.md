# AAVE 0.3.0 零 Per-Mod 验收证据矩阵

- 生成日期：2026-11-22（本地时间，UTC+8）
- 工作副本：`E:\aamod\AAVE-zero-per-mod-20261121`
- 本次范围：文档、只读扫描与 `-SkipBuild` 打包复核；未修改 `src\`、未执行 `dotnet build`、未启动 AA、未执行 git / gh 上传或发布动作、未写入 `F:\`。
- 判定取值仅限四值：**PASS / PARTIAL / FAIL / NOT-RUN**。无直接证据的条目判 NOT-RUN 或 PARTIAL，并写明缺什么。
- 基线口径：0.2.12 的全部结果仅作为 legacy 基线对照，不作为 0.3.0 的通过证据。
- 证据路径均相对于仓库根目录；哈希为 SHA256（大写十六进制）。

## 1. 总表

| 门禁 | 门禁含义 | 当前判定 | 关键数值 | 主要证据路径 | 缺口 | 闭合所需动作 | 责任归属 |
|---|---|---|---|---|---|---|---|
| G1 | 生产 `src` 静态零 per-Mod 违规：字符串字面量不得命中实际安装 Mod 的程序集名 / 文件名 / MVID / GUID / SHA256；违规数必须为 0 | PASS | 违规 0；违规涉及文件 0；扫描文件 106；实际安装 Mod 19；身份索引 81；退出码 0（v7 复跑，2026-11-22 01:58:24） | `artifacts/zero-per-mod/static-source.json`；`tools/Test-ZeroPerModSource.ps1` | 静态扫描只覆盖 `src` 下 `.cs` 字符串字面量；无法证明运行期动态构造的身份判断不存在 | 在后续真机/打包门禁中复核运行期准入路径；静态门禁随源码变更复跑并留痕 | 主代理真机 + 文档 |
| G2 | 真实 AA 重复导出等价性：逐帧 / 音频 / 事件 / PTS 一致；authored-render 等价；取消后恢复再导出一致 | PASS（含显式 font-atlas 1 LSB 例外） | 最终批次 `g2-generic-final-27-fontatlas`：2 次完整导出，450 帧 / 720,000 采样帧，**6 passed / 0 failed**；`ExactDecodedAudio=true`、`ExactFrameCountsAndPts=true`、`ExactDecodedPixels=true`、`RawExact=false`；ChangedPixels=2、ChangedChannels=3、MaxChannelDifference=1（UI 字形 1 LSB 差异） | `artifacts/zero-per-mod/g2-generic-final-27-fontatlas/`（`native-checks.log`、`story-authored-render-equivalence-02.json`、`story-media-01/02.json.decoded.fingerprint.json`） | 例外仅限显式测试原生字形投影 / 图集规则；本批由测试 harness 驱动（见 G6）；取消→重复导出矩阵、长剧情与多 Mod 共存场景未在本批覆盖；**不得表述为逐像素完全一致或字节级相同电影** | 在纯产品路径复核重复导出等价性；维持例外范围台账；扩展取消恢复与多 Mod 共存场景 | 主代理真机 |
| G3 | 未知 Stopwatch / Unity Time Mod 不改 AAVE 代码、不改 Mod 代码也能通过完整导出矩阵 | PASS | 最终批次 `g3-live-clock-04`：未知 live clock Mod 触发 `Test.Update` 事件 **465 次**；`Stopwatch` 读取被通用桥接为虚拟时间，导出期 **32 次**读取；wall 与 unity 时钟同步递增；RESULT **4 passed / 0 failed**，单遍 450 帧 / 720,000 采样帧 | `artifacts/zero-per-mod/g3-live-clock-04/`（`native-checks.log`、`launcher.log`、`story-media-01.json.decoded.fingerprint.json`）；生产接线 `src/VideoExport.Plugin/Plugin.cs`、`src/VideoExport.Plugin/ExportHost.cs`、`src/VideoExport.Plugin/GenericStopwatchBridge.cs` | 本批运行身份为中间构建 `provider_sha256=0F740CD4…`；桥接目前只覆盖 `Stopwatch.GetTimestamp`，`DateTime.Now/UtcNow`、`Environment.TickCount/TickCount64`、Unity `Time.realtimeSinceStartup/unscaledTime` 未桥接；批次由 harness 驱动 | 在最终生产构建上复跑；补齐剩余时钟族；纯产品路径复核导出矩阵 | 主代理真机 |
| G4 | 未知 render-owner 被通用能力拒绝，且首帧前拒绝、不输出错片电影 | NOT-RUN | 合成策略：`UnknownRenderOwnerMod` 被通用策略拒绝，`render-audio-video-references=2`；严格准入单测 29 过 0 败；真机日志中无该 fixture 加载记录。主代理未给本门禁结果，**不得声称通过** | `artifacts/zero-per-mod/generic-policy.log`；`artifacts/zero-per-mod/STAGE1-FIXTURES.md`；`artifacts/zero-per-mod/strict-capability-v3.log`；`tests/fixtures/UnknownRenderOwnerMod/` | fixture 仅为类型壳，运行期未实际独占渲染；无真机拒绝记录；无“首帧前拒绝、未产出错位成片”的证据 | 改造 fixture 使其在导出期实际独占渲染；在真实 AA 中验证通用能力探测在首帧前拒绝，并核验无错片输出 | 主代理真机 |
| G5 | 其他 Mod 的 DLL 与配置前后哈希不变 | PARTIAL | 全量基线快照（2026-11-22 实测）：99 文件 / 14,962,579 字节（mod-dll 30 / mod-config 31 / user-setting 7 / user-save 31），15 个 Mod 归组，30 个 DLL 程序集名全部解析成功；工具自比 99/99 PASS（exit 0）、真机重扫零差异（exit 0）、合成篡改 FAIL（ADDED=1 CHANGED=1，exit 1） | `docs/zero-per-mod/G5-MOD-HASH-BASELINE.md`；`tools/Get-ThirdPartyModHashSnapshot.ps1`；`artifacts/zero-per-mod/g5-baseline-20261122-010540.json`；`g5-compare-self-20261122-010609.log`；`g5-compare-tampered-20261122-010624.log`；`g5-compare-rescan-20261122-010957.log`；`g2-native-smoke-10.state/settings-restoration.json` | 基线 ≠ PASS：尚无真实导出前 / 导出后的成对快照比对；mods 下非配置资产（`.md`、`.png`、`.sample` 等）未纳入哈希；`E:\AAcache` 未扫描；配置识别依赖扩展名；内存态与运行期临时文件不在范围 | 主代理在每次真机导出前后各跑一次 `tools/Get-ThirdPartyModHashSnapshot.ps1` 并 `-Compare` 零差异（exit 0）、归档成对证据；并决定非配置资产与 `E:\AAcache` 是否纳入范围 | 主代理真机 |
| G6 | 每次导出独立同步矩阵；使用 exact release DLL；无测试插件 / 测试程序集 | PARTIAL（纯产品闭环尚未完成） | 最终生产构建（只读核对一致）：Plugin `AAVideoExport.dll` `B2FF1F617EE9EB76124443FF67B64A3DFE6CC4E91A457DD9A6629111296AF279`（498,688 B）；Core `AAVideoExport.Core.dll` `E565807696588C679331F4EFA49BE549B41E0098EACC1C7A24504E19C66ABCF6`（385,024 B）。最终包物料层验包：lite 15/15、full 17/17、禁入项扫描全 PASS、source 562 条目全 PASS；载荷固定 9 个运行时程序集；无 harness / 夹具 / RuntimeBootstrap / `bootstrap\` 段 / `*.pdb` / `*.user` | `src/VideoExport.Plugin/bin/Release/net6.0/AAVideoExport.dll`；`src/VideoExport.Core/bin/Release/net6.0/AAVideoExport.Core.dll`；`artifacts/release-0.3.0-final/logs/verify-lite-final.log`；`verify-full-final.log`；`verify-extra-scan.log`；`verify-source-final.log`；`artifacts/zero-per-mod/g6-install-audit-20261122-010832.json`；`docs/zero-per-mod/G6-EXACT-RELEASE-DLL-MANIFEST.md` | 现有真机批次全部由测试 harness 驱动，运行记录带 `harness_sha256`；运行记录未登记 Core 的 `core_sha256` 字段；无产品-only（无 harness、无审计 bootstrap、无测试程序集）运行同时登记 Plugin + Core 哈希；lab `BepInEx\plugins\` 仍残留 Tests harness 与 Unknown 夹具。物料层验包通过 ≠ 纯产品运行闭环 | 在不加载 harness / 审计插件的产品路径完成导出，并在运行记录中同时登记 `provider_sha256` 与 `core_sha256`；清理 lab 残留夹具；重新核对包内 `SHA256SUMS.txt` 与 manifest | 主代理真机 + 打包 |
| G7 | 发布授权：门禁结论归档后由人工 / 主代理执行上传与发布；不得覆盖历史 release | 已获授权，正在发布 | 发布 GO 已下达；本会话未执行 git / gh 上传或发布动作；最终物料位于 `artifacts/release-0.3.0-final/`；0.2.12 release 及其 assets 不得改写或替换 | `CHANGELOG.md`；`artifacts/release-0.3.0-final/`（物料、`AAVideoExport-0.3.0-SHA256SUMS.txt`、`logs/`）；本矩阵第 1 节各门禁判定 | 上传动作、release 资产与线上哈希登记在发布执行后归档；G4 / G5 / G6 未闭合项必须在 release notes 与本矩阵中如实保留 | 发布执行后归档上传记录与线上资产哈希；核对无预发布上传、release notes 写明实际证据与边界 | 打包 + 文档 |

### 1.1 G1 基线对照（0.2.12 legacy vs 本次 0.3.0 复跑）

| 指标 | 0.2.12 基线（legacy，`static-source-v2.json`） | 0.3.0 本次复跑（`static-source.json`，v7） |
|---|---:|---:|
| 违规条数 | 203（名称 166 / MVID·GUID 20 / SHA256 17） | 0 |
| 违规涉及文件数 | 20 | 0 |
| 扫描生产文件数 | 118 | 106 |
| 实际安装 Mod 数 | 18 | 19 |
| 身份索引（名称/MVID/哈希） | 69 | 81 |
| 门禁退出码 | 1（FAIL） | 0（PASS） |

基线明细与按 Mod / 按文件分布见 legacy 证据 `artifacts/zero-per-mod/BASELINE.md` 与 `artifacts/zero-per-mod/static-source-v2.json`；该 203 项仅作为删除/替换账本对照，不作为 0.3.0 通过证据。

## 2. 分门禁证据

### G1 — 生产源码静态零 per-Mod 违规

**判定依据**：`docs/zero-per-mod/STANDARD.md`“判定粒度”规定，生产 `src` 字符串字面量中不得出现实际安装 Mod 的程序集名/文件名/MVID/GUID/SHA256，违规数必须为 0。扫描器从 `F:\AzureArchive_101\mods` 实际发现 Mod 集合，不在脚本内硬编码 Mod 名（`tools/Test-ZeroPerModSource.ps1` 发现逻辑与字面量匹配逻辑）。

**逐条证据**：

1. `artifacts/zero-per-mod/static-source.json`（v7，2026-11-22 01:58:24 本地 / `generatedAtUtc = 2026-11-21T17:58:24Z`）
   - `schema = aave.zero-per-mod.static.v2`
   - `aaInstallPath = F:\AzureArchive_101`；`sourceRoot = E:\aamod\AAVE-zero-per-mod-20261121`
   - `filesScanned = 106`
   - `violations = 0`；`details = []`（0 条违规明细，违规涉及文件 0）
   - `discoveredModNames.Count = 19`；`discoveredModIdentities.Count = 81`
2. 扫描命令（PowerShell 7）：

   ```powershell
   pwsh -NoProfile -File .\tools\Test-ZeroPerModSource.ps1 `
     -AAInstallPath 'F:\AzureArchive_101' `
     -Output 'artifacts\zero-per-mod\static-source.json'
   ```

3. Legacy 基线对照：`artifacts/zero-per-mod/static-source-v2.json`（`violations=203`、`filesScanned=118`、`discoveredModNames.Count=18`、退出码 1）；汇总见 `artifacts/zero-per-mod/BASELINE.md`、`artifacts/zero-per-mod/STAGE0.md`。历史中间快照 `g1-static-20261122-002730.json/.log`、`static-source-v4.json` / `v5.json` / `v6.json` 违规均为 0，仅作过程记录。

**仍未证明的部分**：

- 静态门禁不扫描运行期动态拼接的字符串、反射元数据或 IL 重写目标；不能仅凭本门禁断言运行期不存在按 Mod 身份的放行/拒绝分支。
- 未在本次复跑中重新构建产品二进制；源码与 `bin/Release` 输出的对应关系以既有构建记录与 G6 物料层验包为准。

### G2 — 真实 AA 重复导出等价性矩阵

**判定依据**：`docs/zero-per-mod/STANDARD.md`“动态门禁”规定每个完整导出独立跑同步矩阵，不允许用平均、抽样或总帧数相等代替；重复导出须在音频、事件、PTS 与 authored-render 契约范围内等价。

**最终通过批次（PASS，含例外）**：

1. `artifacts/zero-per-mod/g2-generic-final-27-fontatlas/`
   - 2 次完整导出（无取消），各 450 帧 / 720,000 采样帧；`native-checks.log` RESULT **6 passed; 0 failed**。
   - 等价性台账 `story-authored-render-equivalence-02.json`：契约 *authored-render equivalence with font-atlas exception*；`ExactDecodedPixels=true`、`ExactDecodedAudio=true`、`ExactFrameCountsAndPts=true`、`RawExact=false`；`Frames=450`、`ChangedPixels=2`、`ChangedChannels=3`、`MaxChannelDifference=1`。
   - 例外 `Scope`：显式 test-only 原生字形投影 / 图集规则；例外变更 UV 字形三角形之外的原始像素保持精确；要求相同文本 / 字体 / 网格 / 颜色 / 变换与整数图集位移；**This is not a byte-identical movie claim.**
   - 两份导出指纹一致：视频 SHA256 `F617C96B8BC50C570DDCEC1626DA2D97B52CA8AACCF9EB2C49A13F1C8E0633DF`；PCM16 SHA256 `8146B8845B0937684CDEE448A602805C152C4006BE6195083B58BA830BA92C11`；时间线 SHA256 `A4EA68F6700534446BCBA873127DEDAC936669A79CFA7ED21C4AD68A45D8D438`。
   - 运行身份：`provider_sha256=B2FF1F617EE9EB76124443FF67B64A3DFE6CC4E91A457DD9A6629111296AF279`（与最终 Plugin DLL 一致）；`harness_sha256=047A1B4DA32651BB51E9B47D7523F92956F4B999BBCB8AB741DF1527A264935C`。
   - 导出物：`story-exports/native-story-11252-01.mov`、`story-exports/native-story-11252-02.mov`（各 9,944,304 B）；`story-source-unchanged` PASS；`actual-azurite-render-control-restoration` PASS。

**历史批次（取证链保留，不作为当前门禁通过证据）**：

1. Run A `g2-native-smoke-10/`：两次完整导出，644 帧 / 1,030,400 采样帧，视频 SHA `991398ED…`、PCM `ED50C5EE…`，严格身份匹配 PASS。
2. Run B `g2-native-cancel-15/`：一次取消 12 帧后两次完整导出，644 帧 / 1,030,400 采样帧，PCM `MismatchedSamples=0`；唯一失败为 test-only 混合 observer 标签布局断言（32 条 `Original AUTO label layout changed`）。
3. 失败记录：`g2-native-cancel-16/`（Run C 恢复超时，4 过 5 败）、`g2-native-smoke-17/`（启动 settle 失败）、`g2-generic-final-13/`、`-14/`、`-15/`（2 过 3 败）；早期通过批次 `g2-generic-final-09/`、`-12/` 为过程记录。汇总 `artifacts/zero-per-mod/G2-REAL-AA.md`。

**仍未证明的部分**：取消→重复导出在最终构建与纯产品路径上的等价性；例外之外长剧情 / 多 Mod 共存场景的同步矩阵；现有真机批次均由 harness 驱动（见 G6）。

### G3 — 未知时钟 Mod 不改代码导出

**判定依据**：未知 Mod 读取 `Stopwatch.GetTimestamp` / Unity `Time.realtimeSinceStartup` 等时钟，AAVE 与该 Mod 均不改代码，实际导出仍通过完整同步矩阵；动态准入与桥接不依赖 Mod 名字。

**最终通过批次（PASS）**：

1. `artifacts/zero-per-mod/g3-live-clock-04/`
   - 未知 live clock Mod 触发 `Test.Update` 事件 **465 次**；其 `Stopwatch` 读取被通用桥接为虚拟时间，导出期共 **32 次**读取（桥接计数 `_captureReads`，`EndExport` 导出读日志）；wall 时钟与 unity 时钟同步递增。
   - `native-checks.log` RESULT **4 passed; 0 failed**；单遍 450 帧 / 720,000 采样帧；导出物 `story-exports/native-story-15064-01.mov`（9,944,308 B）；`launcher.log` 记录 `pid=15064 desktop=AAVE03Test-12736`；剧情源未变、渲染控制恢复 PASS。
   - 运行身份：`provider_sha256=0F740CD4151B0955E9411A9B1D8B6D499A4975733125D4C784D6F727EF810DD5`（中间构建）；`harness_sha256=047A1B4D…`。
2. 桥接进入生产 capture 的源码位置：
   - `src/VideoExport.Plugin/Plugin.cs`：`GenericStopwatchBridge.Initialize`、`AttachLoadedModules`、`Shutdown`。
   - `src/VideoExport.Plugin/ExportHost.cs`：`BeginExport` 接入桥接视频时钟、`EndExport` 恢复连续时钟。
   - `src/VideoExport.Plugin/GenericStopwatchBridge.cs`：`ReadTimestamp`（采集期计数 `_captureReads` 并返回虚拟时间）、`BeginExport` / `EndExport` / `Shutdown`。

**合成 / 单测层背景**：

- `artifacts/zero-per-mod/generic-policy.log`：`UnknownClockMod.dll => generic-static-policy-rejected | managed-clock-call-sites=2`。
- `artifacts/zero-per-mod/STAGE2-GENERIC-CLOCK.md`：`GenericStopwatchBridge` 基于 IL 调用点发现并重写 `Stopwatch.GetTimestamp`；夹具 3 方法 / 5 调用点合成测试 PASS；导出期返回统一视频时钟，非导出期恢复原始时钟。
- 早期真机尝试：`g3-live-clock-01/`、`-02/`、`-03/` 为过程记录。

**仍未证明的部分**：

- 桥接目前只覆盖 `Stopwatch.GetTimestamp`；`DateTime.Now/UtcNow`、`Environment.TickCount/TickCount64`、Unity `Time.realtimeSinceStartup/unscaledTime` 未桥接。
- 本批运行身份为中间构建；需在最终生产构建与纯产品路径上复跑完整同步矩阵。

### G4 — 未知 render-owner 通用拒绝且不输出错片

**判定依据**：无协议、独占渲染的 opaque Mod 由通用能力探测在首帧前拒绝；拒绝不算兼容通过，但不得产出错位电影；不得按 Mod 名称拒绝。

**逐条证据（仅合成 / 单测层，NOT-RUN）**：

1. `artifacts/zero-per-mod/generic-policy.log`：`UnknownRenderOwnerMod.dll => generic-static-policy-rejected | render-audio-video-references=2`。
2. `artifacts/zero-per-mod/strict-capability-v3.log`：严格准入 29 passed; 0 failed，含通用静态惰性证明放行未知模块、裸标记拒绝、通用 RenderControlV1 参与者准入等用例。
3. Fixture 清单：`artifacts/zero-per-mod/STAGE1-FIXTURES.md` 记录 `UnknownRenderOwnerMod.dll` SHA256 `55C32CC4238FC6F016AADC2F36F41DBACB56FCEFE92A105612E40FB71F0C3FE8`；源码工程 `tests/fixtures/UnknownRenderOwnerMod/`。

**未通过 / 未闭合部分**：

- fixture 仅为引用 `UnityEngine.Camera` / `RenderTexture` 的类型壳，运行期未实际独占渲染（`STAGE1-FIXTURES.md` “Limits”）。
- 全部真机日志中无 `UnknownRenderOwnerMod` 加载记录；无真机拒绝时点（首帧前）与“无错片输出”的证据。主代理未给本门禁结果，不得声称通过。

**仍未证明的部分**：真实独占渲染场景下通用探测的拒绝时点、拒绝原因文本、以及导出目录无错位成片。

### G5 — 其他 Mod DLL 与配置前后哈希不变

**判定依据**：导出前后第三方 Mod 的 DLL 与配置文件哈希一致；AAVE 不得改写其他 Mod 载荷。

**逐条证据**：

1. `artifacts/zero-per-mod/g2-native-smoke-10.state/settings-restoration.json`
   - `Restored=true`、`RegistryRestored=true`、`RestoreErrors=[]`、`EvidenceErrors=[]`。
   - 第三方配置 `C:\Users\21294\AppData\LocalLow\foxxlight\AzureArchive\SfxFolderMod\config.txt` SHA256 `D52E199B3947596238A64F7EF1CC1C380A4D7DEA57970ABFB1B089D3C8A77F72`，`Restored=true`。
   - 同文件记录 AA 设置文件（`user_settings.json` 及 `.bk.1/2/3`、临时文件）恢复哈希。
2. 剧情存档未改：多条真机记录含 `PASS story-source-unchanged`；存档 `AAVE-OfficialAuthoring-20261030.aas` 前后 SHA256 均 `78D2B7D2C2DDDD87145E5C301703F7B04AE45A55AFB19236150084C1FED3EF22`。
3. 全量哈希快照能力：`tools/Get-ThirdPartyModHashSnapshot.ps1` 只读扫描 `F:\AzureArchive_101\mods` 的第三方部分（排除 AAVE 自身目录与 Unknown 夹具 / `.Tests`）及 AA 用户设置、剧情存档目录，输出 UTF-8 无 BOM、稳定排序的 json。基线 `artifacts/zero-per-mod/g5-baseline-20261122-010540.json`：99 文件 / 14,962,579 字节（mod-dll 30 / 7,124,992；mod-config 31 / 803,791；user-setting 7 / 8,126；user-save 31 / 7,025,670），15 个 Mod 归组，30 个 DLL 程序集名全部解析成功；扫描前后 `mods` 均为 475 文件 / 2,829,252,068 字节（第三方 165 / 11,958,589），`F:\` 零写入。
   - 工具自比 PASS：`artifacts/zero-per-mod/g5-compare-self-20261122-010609.log`（99/99，ADDED=0 / REMOVED=0 / CHANGED=0，exit 0）。
   - 合成篡改 FAIL：`g5-compare-tampered-20261122-010624.log`（99/100，ADDED=1 / CHANGED=1，exit 1；仅改临时副本 `artifacts/zero-per-mod/.g5-demo/tampered.json`）。
   - 现场重扫零差异：`g5-compare-rescan-20261122-010957.log`（99/99，全 0，exit 0）。
   - 幂等性：输出路径已存在时拒绝覆盖（`g5-idempotency-20261122-010635.log`，exit 1）。
   - 方法说明与缺口见 `docs/zero-per-mod/G5-MOD-HASH-BASELINE.md`。

**未通过 / 未闭合部分**：

- 尚无真实导出前 / 导出后的成对快照比对：基线快照、工具自比与重扫零差异只证明“工具可用、当前磁盘状态自洽”，**不构成 G5 PASS**。
- 哈希范围未覆盖 mods 下非配置资产（`.md`、`.png`、`.sample`、无扩展名、`.cs`、`.csproj`、`.pdb`、`.ps1` 等）；`E:\AAcache` 未扫描；配置识别依赖扩展名，非标准落点可能漏扫。
- 哈希门禁不覆盖内存态与运行期临时文件。
- 旧恢复记录（`settings-restoration.json`）证明的是“清理后还原”，未证明导出运行期间其他 Mod 载荷未被改写。

**仍未证明的部分**：多次导出（含取消/恢复）前后，全部第三方 Mod DLL 与配置字节级不变。

### G6 — 独立同步矩阵、exact release DLL、无测试插件

**判定依据**：每次导出独立跑同步矩阵；运行的必须是 exact release 构建；生产载荷不含测试插件 / 审计程序集。

**逐条证据**：

1. 最终生产构建（只读哈希与大小核对，未重建）：
   - `src/VideoExport.Plugin/bin/Release/net6.0/AAVideoExport.dll` SHA256 `B2FF1F617EE9EB76124443FF67B64A3DFE6CC4E91A457DD9A6629111296AF279`，498,688 B。
   - `src/VideoExport.Core/bin/Release/net6.0/AAVideoExport.Core.dll` SHA256 `E565807696588C679331F4EFA49BE549B41E0098EACC1C7A24504E19C66ABCF6`，385,024 B。
2. 最终包物料层验包（`artifacts/release-0.3.0-final/`，`-SkipBuild` 从上述 Release 输出打包）：
   - `logs/verify-lite-final.log`：15 项 PASS，exit 0；`logs/verify-full-final.log`：17 项 PASS，exit 0。
   - `logs/verify-extra-scan.log`：无 NativePath harness、无 UnknownPassiveMod / UnknownClockMod / UnknownRenderOwnerMod 夹具、无 RuntimeBootstrap、无 `bootstrap\` 段、无 `*.pdb` / `*.user`；包内流 SHA256 与最终 Plugin / Core 哈希交叉核对 PASS。
   - `logs/verify-source-final.log`：source 包 562 条目，顶层 `src/tools/docs/tests/legacy` 与关键文件齐全，无 bin/obj/.git/artifacts 段、无二进制 / 归档 / 媒体扩展名、无 `*.user` / `local.props`，4 个关键文件 zip / 工作副本 MATCH。
   - 载荷固定 9 个运行时程序集（清单见 `docs/release-0.3.0.md`“生产载荷程序集清单与排除项”）；`delivery-manifest.json` `AuditMode=false`、`BootstrapRelativePath=null`。
3. 安装审计快照：`artifacts/zero-per-mod/g6-install-audit-20261122-010832.json` / `.log`（01:08 对当时候选构建的只读逐站审计；其 reference 插件哈希为早期候选 `D1E84948…`，已被最终 `B2FF1F61…` 取代，Core 哈希 `E5658076…` 未变）。结论 `PARTIAL`：包内载荷正确 ≠ 已安装闭环 ≠ 纯产品运行通过；lab `BepInEx\plugins\` 残留 Tests harness 与 Unknown 夹具。
4. 陈旧产物（只读，未改动）：`artifacts/zero-per-mod/package-0.3.0/` 内为更早 lab 候选身份（插件 `DD35E888…`、Core `D202A7E7…`）；`artifacts/release-0.3.0-draft/` 下 00:26 批次预演包已被最终物料取代，其预演 SHA 不得作为发布哈希。

**未通过 / 未闭合部分**：

- 每条真机运行 `STORY START` 均带 `harness_sha256`，即测试 harness 已加载，不满足“无测试插件”。
- 运行记录只登记插件 DLL 哈希，未登记 `AAVideoExport.Core.dll` 运行内哈希；Core 与 release 一致性未在真机证据中闭环。
- 无纯产品（无 harness、无审计 bootstrap、无测试程序集）运行同时登记 Plugin + Core 哈希。
- 隔离 lab 的 `BepInEx\plugins\` 仍残留 Tests harness ×1 与 `UnknownPassiveMod` ×2、`UnknownClockMod` ×2；产品-only 运行前须移除/隔离（本轮只读未删）。

**仍未证明的部分**：纯产品路径（无 harness、无审计 bootstrap、无测试程序集）下的 release 双 DLL 完整导出矩阵。

### G7 — 发布授权与历史版本保护

**判定依据**：门禁结论归档后由人工 / 主代理执行上传与发布；0.2.12 的 overclaim 不得沿用，历史 release 及其 assets 不得改写或替换。

**逐条证据**：

1. 发布 GO 已下达；本会话未执行联网上传或发布动作，未改动历史 release。
2. 最终物料与整包校验和位于 `artifacts/release-0.3.0-final/`；门禁状态以本矩阵第 1 节为准。
3. `CHANGELOG.md` 顶部新增 0.3.0 条目；0.2.12 及更早条目保留为 legacy 记录。

**未闭合部分**：

- 上传动作、线上 release 资产与线上哈希登记在发布执行后归档；release notes 须写明实际证据与能力边界，未闭合项（G4 / G5 / G6）不得在对外表述中省略。

## 3. 证据清单

| 文件 / 目录（相对仓库根） | 生成时间（本地） | 覆盖门禁 | 是否可复现 |
|---|---|---|---|
| `artifacts/zero-per-mod/static-source.json` | 2026-11-22 01:58:24 | G1（v7 复跑） | 可复现：`tools/Test-ZeroPerModSource.ps1` + 固定命令；106 文件 / 19 Mod / 81 身份 / 0 违规 |
| `artifacts/zero-per-mod/g2-generic-final-27-fontatlas/` | 2026-11-22 02:01:10 | G2 最终 PASS 批次 | 真机记录；harness 驱动；含 font-atlas 例外台账 `story-authored-render-equivalence-02.json` |
| `artifacts/zero-per-mod/g3-live-clock-04/` | 2026-11-22 01:47:06 | G3 最终 PASS 批次 | 真机记录；harness 驱动；465 次 Test.Update / 32 次导出读 |
| `artifacts/release-0.3.0-final/AAVideoExport-0.3.0-SHA256SUMS.txt` | 2026-11-22 02:09:59 | G6（物料层整包校验和） | 记录性；登记 lite / full / source 三个物料哈希 |
| `artifacts/release-0.3.0-final/logs/verify-lite-final.log` | 2026-11-22 02:08:47 | G6（lite 验包） | 可复现：`tools/Verify-ReleaseBundle.ps1` + 固定参数；15 项 PASS、exit 0 |
| `artifacts/release-0.3.0-final/logs/verify-full-final.log` | 2026-11-22 02:08:54 | G6（full 验包） | 可复现：同上；17 项 PASS、exit 0 |
| `artifacts/release-0.3.0-final/logs/verify-extra-scan.log` | 2026-11-22 02:07:00 | G6（禁入项 + 流哈希交叉核对） | 只读扫描；全部 PASS |
| `artifacts/release-0.3.0-final/logs/verify-source-final.log` | 2026-11-22 02:09:37 | G6（source 包条目与关键文件核对） | 条目扫描 + 四文件 zip/工作副本哈希比对；全 PASS |
| `artifacts/release-0.3.0-final/logs/pack-lite-final.log`、`pack-full-final.log`、`pack-source-final.log` | 2026-11-22 02:05–02:09 | G6（打包记录） | `-SkipBuild` 打包；含批次目录、载荷路径与整包 SHA256 |
| `artifacts/release-0.3.0-final/20261122-020532-lite/`、`20261122-020532-full/`、`source/` | 2026-11-22 02:05–02:09 | G6（最终物料） | 打包树与 zip；哈希以 `AAVideoExport-0.3.0-SHA256SUMS.txt` 为准 |
| `artifacts/zero-per-mod/static-source-v2.json` | 2026-11-21 20:11:25（`generatedAtUtc` 12:11:25） | G1（legacy 基线） | 可复现，仅作 0.2.12 对照，不作通过证据 |
| `artifacts/zero-per-mod/BASELINE.md` | 2026-11-21 20:28:02 | G1（legacy 基线分布） | 记录性；引用 v2 明细 |
| `artifacts/zero-per-mod/STAGE0.md` | 2026-11-21 20:28:05 | G1 / 工具链基线 | 记录性；含 SDK、AA 可执行文件哈希 |
| `artifacts/zero-per-mod/STAGE1-FIXTURES.md` | 2026-11-21 20:27:45 | G3 / G4（合成） | fixture 工程可重建；真机结论不可由本文复现 |
| `artifacts/zero-per-mod/STAGE2-GENERIC-CLOCK.md` | 2026-11-21 20:10:16 | G3（合成） | 单测 `tests/VideoExport.GenericClock.Tests` 可复跑 |
| `artifacts/zero-per-mod/G2-REAL-AA.md` | 2026-11-21 21:07:17 | G2 历史汇总 | 记录性；真机批次需 AA lab 环境重放 |
| `artifacts/zero-per-mod/generic-policy.log` | 2026-11-21 23:26:54 | G3 / G4（合成策略） | 可由 `VideoExport.GenericPolicy.Tests` 复跑 |
| `artifacts/zero-per-mod/strict-capability-v3.log` | 2026-11-21 23:30:27 | G4 / 准入单测 | 可由 `VideoExport.StrictCapability.Tests` 复跑（29 过 0 败） |
| `artifacts/zero-per-mod/staticinert-v6.log` | 2026-11-21 23:32:48 | G5 相关 / 准入单测 | 可由 `VideoExport.StaticInertMod.Tests` 复跑（26 过 0 败） |
| `artifacts/zero-per-mod/g2-native-smoke-10/` | 2026-11-21 20:51:25 | G2 历史 Run A | 真机记录；复现需 AA lab + harness |
| `artifacts/zero-per-mod/g2-native-smoke-10.state/` | 2026-11-21 20:51:26 | G5（配置恢复） | 真机记录；含 `settings-restoration.json` |
| `artifacts/zero-per-mod/g2-native-cancel-15/` | 2026-11-21 20:58:52 | G2 历史 Run B | 真机记录；含 PCM 比对 JSON |
| `artifacts/zero-per-mod/g2-native-cancel-16/` | 2026-11-21 21:00:46 | G2（Run C 失败记录） | 真机失败记录，保留未丢弃 |
| `artifacts/zero-per-mod/g2-native-smoke-17/` | 2026-11-21 21:06:44 | G2（settle 失败记录） | 真机失败记录 |
| `artifacts/zero-per-mod/g2-generic-final-09/`、`-12/` | 2026-11-22 00:04 / 00:10 | G2 早期过程批次 | 真机记录；harness 驱动 |
| `artifacts/zero-per-mod/g2-generic-final-13/`、`-14/`、`-15/` | 2026-11-22 00:12–00:17 | G2（失败记录） | 真机失败记录 |
| `artifacts/zero-per-mod/g3-live-clock-01/`、`-02/`、`-03/` | 2026-11-22 01:19–01:27 | G3 早期过程批次 | 真机过程记录 |
| `artifacts/zero-per-mod/package-0.3.0/`（旧包 SHA256SUMS） | 旧包（未改动） | G6（旧包哈希对照） | 只读；不得作为当前 release 哈希证据 |
| `docs/zero-per-mod/G5-MOD-HASH-BASELINE.md` | 2026-11-22 | G5（方法、基线统计、缺口） | 方法文档；基线可由脚本复现 |
| `tools/Get-ThirdPartyModHashSnapshot.ps1` | 2026-11-22 | G5（快照/比对工具） | 可复现：`-Output` 生成快照、`-Compare` 比对，差异 exit 1 |
| `artifacts/zero-per-mod/g5-baseline-20261122-010540.json` | 2026-11-22 01:05:40 | G5（导出前基线） | 可复现；99 文件 / 30 DLL / 15 Mod |
| `artifacts/zero-per-mod/g5-compare-self-20261122-010609.log` | 2026-11-22 01:06:09 | G5（工具自比 PASS 演示） | 可复现；99/99，exit 0 |
| `artifacts/zero-per-mod/g5-compare-tampered-20261122-010624.log` | 2026-11-22 01:06:24 | G5（篡改 FAIL 演示） | 临时副本构造；ADDED=1 / CHANGED=1，exit 1 |
| `artifacts/zero-per-mod/g5-compare-rescan-20261122-010957.log` | 2026-11-22 01:09:57 | G5（现场重扫零差异） | 可复现；99/99，exit 0 |
| `docs/zero-per-mod/G6-EXACT-RELEASE-DLL-MANIFEST.md` | 2026-11-22 | G6（产品-only 安装清单、差异账本、重打包项） | 只读审计文档；含可复跑命令 |
| `artifacts/zero-per-mod/g6-install-audit-20261122-010832.json` | 2026-11-22 01:08:32 | G6（10 个站点逐文件哈希与判定） | 可复现：只读审计脚本；reference 为当时候选构建 |
| `artifacts/zero-per-mod/g6-install-audit-20261122-010832.log` | 2026-11-22 01:08:32 | G6（人读快照） | 同上 |
| `docs/zero-per-mod/STANDARD.md` | — | G1–G7 口径定义 | 标准文本 |

## 4. 结论：当前门禁状态与剩余缺口（按优先级排序）

当前状态：**G1 PASS；G2 PASS（含 font-atlas 1 LSB 例外）；G3 PASS；G4 NOT-RUN；G5 PARTIAL；G6 PARTIAL（纯产品闭环尚未完成）；G7 已获授权并正在发布。**

已通过项不构成“零 per-Mod 适配整体通过”的推论；以下两项仍是阻塞性未闭合项，其余为部分闭合与待扩展项：

1. **G6 纯产品 release 运行闭环（阻塞项）**：在不加载测试 harness / 审计插件的产品路径，使用最终 release 双 DLL（Plugin `B2FF1F61…AF279`、Core `E5658076…6ABCF6`）完成导出，并在运行记录中同时登记 `provider_sha256` 与 `core_sha256`；现有真机批次均带 `harness_sha256`，不能作为 G6 证据。物料层验包（9 程序集、无 harness / 夹具 / bootstrap / pdb）只证明包内载荷正确，不等于纯产品运行闭环。隔离 lab 的 `BepInEx\plugins\` 残留 Tests harness 与 Unknown 夹具，产品-only 运行前须清理。
2. **G4 未知 render-owner 真机拒绝验收（阻塞项）**：让 fixture 实际独占渲染；验证通用探测在首帧前拒绝、给出原因、且不产出错位电影；主代理未给结果前不得对外声称该类目通过。
3. **G5 第三方 Mod 前后哈希门禁**：工具与基线已就绪；缺的是真实导出前 / 后的成对快照与 `-Compare` 零差异（exit 0），需在每次真机导出流程中执行并归档。
4. **G3 剩余时钟族与最终构建复跑**：补齐 `DateTime` / `Environment.TickCount` / Unity `Time` 时钟族；在最终生产构建与纯产品路径上复跑完整同步矩阵。
5. **G2 纯产品路径复核与例外维持**：在纯产品路径复核重复导出等价性；维持 font-atlas 例外范围台账，不把例外表述为逐像素或字节级一致；扩展取消恢复与多 Mod 共存场景。
6. **G7 发布归档**：上传与线上 release 资产登记由人工 / 主代理执行；发布后归档线上哈希与 release notes 边界说明，核对未改写 0.2.12 release 及其 assets。

> 边界声明：本矩阵记录截至 2026-11-22 02:10（本地）的本机证据；合成单测与 harness 驱动的真机批次均不能替代纯产品 release 验收。0.2.12 的任何通过结论仅作 legacy 基线对照。
