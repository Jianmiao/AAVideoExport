# AAVideoExport 0.3.0 — Release Notes（候选稿 / CANDIDATE DRAFT）

> **状态声明：本文件是 0.3.0 release notes 的候选草案，不是发布说明。**
> 门禁判定对齐 `docs/zero-per-mod/EVIDENCE-MATRIX.md`：**G1 PASS**（当前候选源码，2026-11-22 复跑 0 违规、exit 0）；**G2 PARTIAL、G3 NOT-RUN、G4 NOT-RUN、G5 PARTIAL、G6 PARTIAL、G7 PARTIAL**（禁止发布生效中）。除 G1 外门禁均未闭合；在 G1–G6 全部 PASS、冻结构建复核完成并取得主代理明确 GO 之前，本版本不得打包发布。
> 文中出现的 Plugin/Core DLL SHA256 与预演包整包 SHA256 均为**候选/预演值，尚未最终冻结**；发布哈希以冻结源码树复跑与冻结构建重新校验为准。
> 工作副本：`E:\aamod\AAVE-zero-per-mod-20261121`，分支 `zero-per-mod-0.3.0`，`manifest.json` 版本 `0.3.0`，发布目标 `github.com/Jianmiao/AAVideoExport`。

## 一句话概览

0.3.0 是朝“零 per-Mod 适配”方向重构的候选版本：生产代码不针对具体其他 Mod 做身份特判，改按通用 API 族（时钟、媒体、渲染）与通用能力探测接管导出期间的时间、渲染、音频与启动/菜单边界；当前候选源码静态门禁 G1 复跑 0 违规通过，但真机同步矩阵、未知时钟 / render-owner 验收、第三方 Mod 前后哈希、纯生产构建与发布授权均未闭合，本版不宣称“零 per-Mod 适配整体通过”。

## 本版核心

### 零 per-Mod 适配标准是什么

“零 per-Mod 适配”指：AAVE 的生产代码不针对任何具体其他 Mod 做特判；新增一个从未见过的 Mod 时，只要它遵守 AA 的通用运行时契约，AAVE 不需要改代码、该 Mod 也不需要改代码，实际导出仍通过完整同步矩阵。标准全文见 `docs/zero-per-mod/STANDARD.md`。

判定粒度（同标准文档）：

- **静态门禁**：生产 `src` 字符串字面量中不得出现实际安装 Mod 的程序集名/文件名/MVID/GUID/SHA256，违规数必须为 0。
- **动态门禁**：每个完整导出独立跑同步矩阵；不允许用平均、抽样或总帧数相等代替。
- **未知 Mod 门禁**：新增 fixture 后，AAVE 源码与该 Mod 源码都不改，必须实际跑 AA。
- **其他 Mod 不变**：DLL/配置哈希前后一致。

### 允许的通用机制

- 按 API 族的运行时调用点重写（时钟、媒体、渲染），不按 Mod 身份。
- `RenderControlV1` 或等价通用能力协商。
- 通用 capture scope：导出期间统一接管时间、渲染、音频、启动/菜单边界。
- 通用能力探测：能观测/能接管则支持；不能观测/不能接管则拒绝，并给出原因。
- 通用静态审计：从实际安装集合发现 Mod 名称，不把名称写进产品逻辑。

### 禁止项

- 生产代码中的 `if (assemblyName == "X")`、`if (mod.GUID == "...")`、`if (version == "1.2.3")`、`if (mvid == ...)`、`if (type.FullName == "X.Y")`。
- 按 DLL 文件名、启动配置文件名、目录名做 Mod 身份判断。
- `Certified / Observed / Opaque / Rejected` 这类 per-Mod 准入表。
- 为某个 Mod 单独写的 Harmony transpiler/postfix 类。
- 把测试/验收夹具的名称写进生产代码。

### 与 0.2.12 的关系

- `0.2.12` 是已发布的 legacy release，其验收结论、成片数据与 DLL 哈希属于旧版本，**不得作为 0.3.0 的已验证证据**；旧版记录见 `CHANGELOG.md`、`README.md` 与 `docs/moreeffects-clock-compatibility.md`。
- 0.2.12 时代按 Mod 身份编写的适配器源码保留在 `legacy/0.2.12-per-mod/`，仅供对照，不进入当前生产 `src`。
- 0.3.0 候选在同一工作副本上重构，沿用 0.2.12 建立的单遍音画导出、有界 GPU 读回/编码流水线、取消清理与公开 `RenderControlV1` v1 等工程能力；这些能力在 0.3.0 上的真机状态以本版门禁与本节证据为准，不沿用 0.2.12 的验收结论。

## 变更清单（候选方向，最终以冻结构建与 G1 报告为准）

- **通用准入与策略**：新增/重构通用静态策略与能力探测路径，按 API 族观测结果决定准入或拒绝，不按 Mod 名称。相关源码：`src/VideoExport.Plugin/GenericStaticModPolicy.cs`、`src/VideoExport.Plugin/StrictModAdmission.cs`、`src/VideoExport.Plugin/StrictCapabilityInventory.cs`、`src/VideoExport.Core/StrictCapability.cs`、`src/VideoExport.Plugin/StaticInertScope.cs`、`src/VideoExport.Plugin/NativeCaptureScope.cs`。
- **通用时钟桥（合成阶段）**：`src/VideoExport.Plugin/GenericStopwatchBridge.cs` 通过 Mono.Cecil 发现并补丁调用方方法，采集期返回合成视频时钟；当前仅覆盖 `Stopwatch.GetTimestamp`，**尚未接入生产采集路径**（见“已知限制”）。配套策略：`src/VideoExport.Plugin/GenericClockHooks.cs`、`src/VideoExport.Plugin/GenericClockPolicy.cs`、`src/VideoExport.Plugin/GenericRenderControlPolicy.cs`。
- **per-Mod 适配器退出生产源码**：0.2.12 时代按具体 Mod 编写的边界/适配类不再出现在生产 `src`，留存于 `legacy/0.2.12-per-mod/`。该退出是否彻底、是否仍有身份字面量残留，以 G1 静态门禁在冻结源码上的最终报告为准。
- **保留的既有能力**：单遍音画执行路径、有界 GPU 读回与编码队列、取消清理与原生场景恢复、公开 `RenderControlV1` v1；普通生产构建不运行开发审计、不携带审计 bootstrap。
- **包元数据**：`manifest.json` `version_number` 为 `0.3.0`；生产打包由 `tools/Package-Release.ps1` 从 manifest 读取版本，验包由 `tools/Test-ReleaseArchive.ps1` 执行。

> 说明：以上是候选源码的方向描述。“已移除/已保留”的最终状态须在冻结构建上由 G1 静态门禁、G2 真机矩阵与 G6 纯生产构建/身份核验确认；本清单不构成兼容性承诺。

## 已验证范围（逐条附证据路径）

本节只列当前有仓库内证据支撑的条目。合成/单元证据不启动 AA，不能替代原生 AA 验收；lab 证据的身份与边界按条目注明。门禁判定口径与 `docs/zero-per-mod/EVIDENCE-MATRIX.md` 对齐。

### G1 静态门禁（PASS，仅限当前候选源码）

- 2026-11-22 复跑 `tools/Test-ZeroPerModSource.ps1 -AAInstallPath 'F:\AzureArchive_101'`：扫描生产文件 106、实际安装 Mod 19、身份索引 81，**违规 0、违规涉及文件 0、退出码 0**；stdout `ZERO-PER-MOD STATIC: files=106 mods=19 violations=0`，报告末尾 `EXIT-CODE=0`。
- 对照 0.2.12 legacy 基线：203 违规（名称 166 / MVID·GUID 20 / SHA256 17）、20 个违规文件、118 扫描文件、18 Mod、退出码 1（FAIL）。该 203 项仅作删除/替换账本对照，不作为 0.3.0 通过证据。
- 边界：静态扫描只覆盖 `src` 下 `.cs` 字符串字面量，不扫描运行期动态拼接字符串、反射元数据或 IL 重写目标，不能仅凭本门禁断言运行期不存在按 Mod 身份的放行/拒绝分支；**冻结构建出包前须在冻结源码树复跑本门禁并归档报告，最终以冻结报告为准**。
- 证据：`artifacts/zero-per-mod/g1-static-20261122-002730.json`、`artifacts/zero-per-mod/g1-static-20261122-002730.log`、`tools/Test-ZeroPerModSource.ps1`；legacy 基线 `artifacts/zero-per-mod/BASELINE.md`、`artifacts/zero-per-mod/static-source-v2.json`。

### 合成 / 单元级（不启动 AA）

1. **通用静态策略分类**：`UnknownPassiveMod` 被判为 `GenericStaticModPolicy.static-inert.v1`，托管时钟站点、Harmony 补丁特性、行为回调、渲染/音频/视频引用、P/Invoke、游戏/Harmony 引用计数均为 0；`UnknownClockMod`（2 个时钟站点）被通用拒绝；`UnknownRenderOwnerMod`（2 个渲染/音频/视频引用）被通用拒绝。
   - 证据：`artifacts/zero-per-mod/STAGE1-FIXTURES.md`；夹具 `tests/fixtures/UnknownPassiveMod/`、`tests/fixtures/UnknownClockMod/`、`tests/fixtures/UnknownRenderOwnerMod/`；测试 `tests/VideoExport.GenericPolicy.Tests/`；策略源码 `src/VideoExport.Plugin/GenericStaticModPolicy.cs`。
2. **严格能力准入（通用路径）**：`VideoExport.StrictCapability.Tests` 29 passed / 0 failed，含：完整通用 static-inert 证明可准入未知模块；裸标记被拒绝；通用被动准入不启用预检缓存复用。
   - 证据：`artifacts/zero-per-mod/STAGE1-FIXTURES.md`；测试 `tests/VideoExport.StrictCapability.Tests/`；源码 `src/VideoExport.Core/StrictCapability.cs`、`src/VideoExport.Plugin/StrictCapabilityInventory.cs`。
3. **通用秒表桥合成行为**：夹具 3 个方法、5 个 `Stopwatch.GetTimestamp` 站点中发现 3 个调用方方法并完成补丁；采集期视频时间 0 时各方法返回同一原点时间，0.5s 返回原点 +0.5s（误差 1ms 内），冻结视频时间重复读取同值，1.0s 返回原点 +1.0s；`EndExport` 后桥停用并恢复连续时钟。既有时钟组 26/26、统一时钟行为组 10/10、`ModClockAudit` 16 个夹具站点（6 个音频）通过。
   - 证据：`artifacts/zero-per-mod/STAGE2-GENERIC-CLOCK.md`；测试 `tests/VideoExport.GenericClock.Tests/`、夹具 `tests/VideoExport.GenericClock.Fixture/`；源码 `src/VideoExport.Plugin/GenericStopwatchBridge.cs`。
4. **静态门禁 legacy 基线（重构起点，非通过证据）**：v2 基线在 0.2.12 R2 副本上发现 18 个第三方 Mod、69 条身份索引、扫描 118 个生产文件，违规 203（名称 166、MVID/GUID 20、SHA256 17）、涉及 20 个文件，退出码 1（FAIL）。这是 0.3.0 重构要清零的台账，**不是 0.3.0 的通过记录**；对照数值见 G1 节与 `docs/zero-per-mod/EVIDENCE-MATRIX.md` 1.1 节。
   - 证据：`artifacts/zero-per-mod/BASELINE.md`、`artifacts/zero-per-mod/static-source-v2.json`；门禁脚本 `tools/Test-ZeroPerModSource.ps1`。

### 隔离 lab 真实 AA（G2 PARTIAL）

环境：隔离副本 `F:\AzureArchive_AAVE03Lab`（标记 `aave-test-lab.json`，源 `F:\AzureArchive_101`）；加载未知被动 Mod `UnknownPassiveMod.dll`；现有真机批次均由原生验收 harness `AAVideoExport.NativePath.Tests` 驱动/观测（**不满足 G6 纯生产要求**）；headless 启动，未在用户主桌面保留 AA 窗口。

- **Run A — 两次完整导出，无取消**：6 passed / 0 failed；两份导出各 644 帧 / 1,030,400 个采样；重复导出在身份、帧数、时长、解码内容上严格匹配 PASS。
  - 证据：`artifacts/zero-per-mod/g2-native-smoke-10/`（`native-checks.log`、`story-media-01.json.decoded.*`、`story-media-02.json.decoded.*`、`story-render-control-evidence.json`、`story-panel-boundary-evidence.json`）；汇总 `artifacts/zero-per-mod/G2-REAL-AA.md`。
- **Run B — 一次刻意取消 + 两次完整导出**：共 3 次尝试，1 次取消（12 个采集帧）、2 次完整导出；两份完整导出各 644 帧 / 1,030,400 采样，文件 SHA256 一致；PCM 比较 `MismatchedSamples=0`、`MaximumErrorLsb=0.5`；实际 Azurite 渲染控制恢复 PASS；D2 检查（单遍剧情导出、重复严格身份、剧情源未变、生产未生成审计时间线/基线文件、取消后清理、实际渲染控制恢复）PASS。结果 7 passed / 1 failed：唯一失败是 test-only 混合 observer 的标签布局断言，32 条 `Original AUTO label layout changed`，标签文本在 `AUTO`/`自动` 间不稳定；该失败属 observer/布局断言，不是帧、音频或时间轴错位，记录保留、未豁免。
  - 证据：`artifacts/zero-per-mod/g2-native-cancel-15/`（`native-checks.log`、`controlled-story-export-evidence.json`、`story-media-02.json.original-pcm-comparison.json`、`story-media-03.json.original-pcm-comparison.json`、`story-render-control-evidence.json`）；汇总 `artifacts/zero-per-mod/G2-REAL-AA.md`。
- **Run C — 重复取消，恢复超时（真实失败记录，未丢弃）**：4 passed / 5 failed；第二次完整导出后宿主清理超时，Azurite 映射、FPS/VSync/tier 与 Unity 时钟未能在清理后恢复；32 条混合 observer 标签布局错误复发。
  - 证据：`artifacts/zero-per-mod/g2-native-cancel-16/`（`native-checks.log`、`controlled-story-export-evidence.json`、`story-render-control-evidence.json`）；汇总 `artifacts/zero-per-mod/G2-REAL-AA.md`。
- **确定性重建后的前置稳定失败**：`g2-native-smoke-17` 在 attempt 0 之前即未通过——空闲导出面板与实际 Azurite 映射未在新尝试前稳定。
  - 证据：`artifacts/zero-per-mod/g2-native-smoke-17/`（`native-checks.log`、`story-panel-boundary-evidence.json`）；汇总 `artifacts/zero-per-mod/G2-REAL-AA.md`。
- **产品候选重复导出批次（通过部分）**：`g2-generic-final-09/` 与 `g2-generic-final-12/` RESULT 均为 6 passed; 0 failed；两批次各自两次导出指纹一致：450 帧 / 720,000 采样帧，视频 SHA256 `7B479BD96B80CFC51F042DCCEA67AABB9AD7C0C96978771868786D5996462EFC`，PCM16 SHA256 `8146B8845B0937684CDEE448A602805C152C4006BE6195083B58BA830BA92C11`；`final-12` 渲染控制证据 `Passed=true`、`RestoredAttempts=2`。
  - 证据：`artifacts/zero-per-mod/g2-generic-final-09/`、`artifacts/zero-per-mod/g2-generic-final-12/`（`native-checks.log`、`story-media-*.json.decoded.*`、`story-render-control-evidence.json`）。
- **失败批次（未闭合）**：`g2-generic-final-13/`、`g2-generic-final-14/`、`g2-generic-final-15/` RESULT 均为 2 passed; 3 failed，报错为 `Sequence contains no matching element` / `Cancellation needs an installed live mixed-story observer`，且 `actual-azurite-render-control-restoration` 失败。
  - 证据：`artifacts/zero-per-mod/g2-generic-final-13/`、`artifacts/zero-per-mod/g2-generic-final-14/`、`artifacts/zero-per-mod/g2-generic-final-15/`（`native-checks.log`）。

### G3 / G4 合成层证据（真机 NOT-RUN）

- **G3 未知时钟 Mod**：合成策略下 `UnknownClockMod` 被通用策略拒绝（`managed-clock-call-sites=2`）；通用 Stopwatch 桥合成测试 3 方法 / 5 调用点 PASS。生产 `Plugin.Load()` 尚未初始化/打补丁；仅覆盖 `Stopwatch.GetTimestamp`，`DateTime.Now/UtcNow`、`Environment.TickCount/TickCount64`、Unity `Time.realtimeSinceStartup/unscaledTime` 未桥接；该 fixture 从未在真实 AA 安装中加载运行。
  - 证据：`artifacts/zero-per-mod/generic-policy.log`、`artifacts/zero-per-mod/STAGE2-GENERIC-CLOCK.md`、`tests/fixtures/UnknownClockMod/`、`tests/VideoExport.GenericClock.Tests/`。
- **G4 未知 render-owner**：合成策略下 `UnknownRenderOwnerMod` 被通用策略拒绝（`render-audio-video-references=2`）；严格准入单测 29 passed / 0 failed。fixture 仅为引用 `Camera`/`RenderTexture` 的类型壳，运行期未实际独占渲染；无真机拒绝时点（首帧前）与“无错位成片”证据。
  - 证据：`artifacts/zero-per-mod/generic-policy.log`、`artifacts/zero-per-mod/strict-capability-v3.log`、`artifacts/zero-per-mod/STAGE1-FIXTURES.md`、`tests/fixtures/UnknownRenderOwnerMod/`、`tests/VideoExport.StrictCapability.Tests/`。

### G5 其他 Mod 载荷前后不变（PARTIAL）

- 配置侧：`SfxFolderMod\config.txt` SHA256 `D52E199B3947596238A64F7EF1CC1C380A4D7DEA57970ABFB1B089D3C8A77F72`，`Restored=true`；AA 设置文件与注册表恢复 `Restored=true`、`RestoreErrors=[]`；剧情存档前后 SHA256 一致（`78D2B7D2…EF22`，`story-source-unchanged` PASS）。
- 缺口：尚无真实导出前 / 后的成对快照比对。全量快照能力已就绪（`tools/Get-ThirdPartyModHashSnapshot.ps1`，基线 `artifacts/zero-per-mod/g5-baseline-20261122-010540.json`：99 文件 / 30 DLL / 15 Mod，只读、`F:\` 零写入；自比 99/99 PASS、篡改 FAIL、重扫零差异均已演示），但基线不等于门禁 PASS；现有恢复记录仍只是清理后还原证明。
- 方法、范围与未覆盖项（mods 下 105 个非配置资产、`E:\AAcache`）见 `docs/zero-per-mod/G5-MOD-HASH-BASELINE.md`。
- 证据：`artifacts/zero-per-mod/g2-native-smoke-10.state/settings-restoration.json`、`artifacts/zero-per-mod/g2-native-smoke-10/native-checks.log`、`artifacts/zero-per-mod/g2-generic-final-12/native-checks.log`。

### G6 构建身份与纯生产路径（PARTIAL）

- 当前 release 二进制（只读哈希核对，未重建）：Plugin `D1E84948…B87BB5`、Core `E5658076…6ABCF6`；`g2-generic-final-12/`、`-13/`、`-14/`、`-15/` 的 `native-checks.log` 记录 `provider_sha256=D1E84948…`，与当前 release 插件 DLL 一致。
- 缺口：每条真机运行 `STORY START` 均带 `harness_sha256=CE4BF603…`（测试 harness 已加载）；运行记录只登记插件哈希，未登记 Core DLL 运行内哈希；旧包 `package-0.3.0` 内仍是早期候选哈希（插件 `DD35E888…`、Core `D202A7E7…`）；无纯产品（无 harness、无审计 bootstrap、无测试程序集）运行记录。
- 证据：`src/VideoExport.Plugin/bin/Release/net6.0/AAVideoExport.dll`、`src/VideoExport.Core/bin/Release/net6.0/AAVideoExport.Core.dll`、`artifacts/zero-per-mod/g2-generic-final-12/native-checks.log`、`artifacts/zero-per-mod/package-0.3.0/bundle-0.3.0-0dfcc2af201647af99dc94cb3373550a/SHA256SUMS.txt`。

### 候选构建身份（未冻结）

当前工作副本 Release 输出（`src/VideoExport.Plugin/bin/Release/net6.0/`、`src/VideoExport.Core/bin/Release/net6.0/`）：

- Plugin `AAVideoExport.dll` SHA256 `D1E849484D3BA535302ADB34BB328E1DB9CCC41D87E42C3F18FFB00BA3B87BB5`
- Core `AAVideoExport.Core.dll` SHA256 `E565807696588C679331F4EFA49BE549B41E0098EACC1C7A24504E19C66ABCF6`

以上为候选值，**尚未最终冻结**；发布前须以冻结构建重新校验，bundle 内 `SHA256SUMS.txt` 与 `delivery-manifest.json` 必须与冻结值一致。现有 staged 包 `artifacts/zero-per-mod/package-0.3.0/` 内 DLL 仍是更早的 lab 候选身份（见 `G2-REAL-AA.md` 构建身份说明），不得直接发布。

## 发布前门禁状态

| 门禁 | 判定内容 | 当前状态（对齐 `docs/zero-per-mod/EVIDENCE-MATRIX.md`） |
|---|---|---|
| **G1 生产源码静态零 per-Mod 违规** | 生产 `src` 字符串字面量不得命中实际安装 Mod 的程序集名/文件名/MVID/GUID/SHA256，违规数必须为 0。 | **PASS（当前候选源码）**：106 扫描文件 / 19 Mod / 81 身份索引 / 0 违规 / exit 0（`g1-static-20261122-002730.json`、`.log`）。静态扫描只覆盖 `.cs` 字面量，不证明运行期动态身份判断；**冻结出包前须在冻结源码树复跑并归档该报告，最终以冻结报告为准**。 |
| **G2 真实 AA 重复导出同步矩阵** | 逐帧、PCM、文件 SHA 完全一致；取消后恢复再导出一致；每次导出独立跑矩阵。 | **PARTIAL。** 通过部分：Run A（644 帧×2）、Run B（取消后两份，PCM `MismatchedSamples=0`）、`g2-generic-final-09/` 与 `-12/`（450 帧×2，6/0）。未闭合：32 条 mixed observer 标签布局报错、Run C 第二次导出后恢复超时、`smoke-17` 启动 settle 失败、`final-13/14/15` 2 过 3 败；现有真机批次全部由测试 harness 驱动，无纯产品路径证据。 |
| **G3 未知时钟 Mod 不改代码导出** | 未知 Stopwatch / Unity Time Mod 不改 AAVE 代码、不改 Mod 代码，通过完整导出矩阵。 | **NOT-RUN。** 仅合成策略拒绝（`managed-clock-call-sites=2`）与通用桥合成测试 PASS；桥未接入生产 capture，`DateTime` / `TickCount` / Unity `Time` 时钟族未补齐，fixture 从未在真实 AA 安装中运行。 |
| **G4 未知 render-owner 通用拒绝** | 独占渲染的 opaque Mod 由通用能力探测在首帧前拒绝，不产出错位电影；拒绝不得按 Mod 名称产生。 | **NOT-RUN。** 仅合成策略拒绝（`render-audio-video-references=2`）与准入单测 29/0；fixture 为类型壳，运行期未实际独占渲染，无真机拒绝时点与“无错片输出”证据。 |
| **G5 其他 Mod DLL / 配置前后哈希不变** | 导出前后第三方 Mod 的 DLL 与配置文件哈希一致，AAVE 不改写其他 Mod 载荷。 | **PARTIAL。** 单条 Mod 配置（SfxFolderMod `config.txt`）与 AA 设置/注册表恢复 `Restored=true`、剧情存档 SHA 一致；全量快照工具与基线已就绪（99 文件 / 30 DLL / 15 Mod），但**缺真实导出前 / 后的成对 `-Compare` 零差异证据**，基线本身不构成 PASS。 |
| **G6 独立矩阵 / exact release DLL / 无测试插件** | 每次导出独立同步矩阵；运行 exact release 构建；生产载荷不含测试插件 / 审计程序集。 | **PARTIAL。** 插件 `provider_sha256=D1E84948…` 与当前 release 插件 DLL 一致；但每条真机运行均带 `harness_sha256=CE4BF603…`、`core_sha256` 字段全部缺失，旧包内为早期候选哈希，无纯产品（无 harness / 无审计 bootstrap）运行记录；隔离 lab 的 `BepInEx\plugins\` 仍残留 Tests harness ×1 与 `UnknownPassiveMod` ×2、`UnknownClockMod` ×2。安装清单与重打包项见 `docs/zero-per-mod/G6-EXACT-RELEASE-DLL-MANIFEST.md`。 |
| **G7 发布前禁止上传 / 发布** | 未过 G1–G6 前禁止上传源码或发布 release；0.2.12 的 overclaim 不得沿用。 | **PARTIAL（禁止发布约束生效中）。** `CHANGELOG.md` 顶部仍为 0.2.12 条目，无 0.3.0 发布条目；本地仅旧 bundle 与 STAGING 预演包，未联网上传；最终 G7 PASS 需 G1–G6 全 PASS 后由打包/文档复核签署。 |

**结论：本文件是候选稿（CANDIDATE DRAFT）。** G1 仅在当前候选源码上 PASS；G2–G7 均未闭合。在 G1–G6 全部 PASS、冻结构建哈希复核完成并取得主代理明确 GO 之前，禁止创建 tag、禁止上传 release、禁止对外引用本版兼容性结论。

## 已知限制

- **任意 opaque Mod 只能通用拒绝**：无协议、独占渲染/音频且无法观测的 Mod，允许被通用能力探测拒绝；拒绝必须发生在首帧前，不能输出错位电影。拒绝不算“兼容通过”，但符合“不改代码、不特判”的工程边界；任何拒绝都必须由通用能力探测产生，不能由 Mod 名称产生。证据：`docs/zero-per-mod/STANDARD.md`（能力边界）。
- **虚拟时钟对 UI / 倒计时的边界**：通用秒表桥目前只覆盖 `Stopwatch.GetTimestamp`；`DateTime.Now/UtcNow`、`Environment.TickCount/TickCount64`、Unity `Time.realtimeSinceStartup/unscaledTime` 尚未桥接；桥尚未接入生产采集路径；桥不掌握动画队列状态。采集期外读取原始墙钟，`EndExport` 后恢复连续时钟。UI 刷新、索引刷新、日志、超时与倒计时类读取不在逻辑视频时钟接管范围内，仍按真实时间运行。证据：`artifacts/zero-per-mod/STAGE2-GENERIC-CLOCK.md`。
- **可选 Mod 效果未验证**：实际安装集合中的可选 Mod（如 MoreEffects、Multilingual、CharacterVoice 等）在 0.3.0 上无真机验收记录，全部为候选/待验收；`docs/moreeffects-clock-compatibility.md` 记录的是 0.2.12 时代的适配范围与验收，不能作为 0.3.0 兼容结论。
- **静态扫描与静态策略不能证明运行时行为**：G1 只扫描 `src` 下 `.cs` 字符串字面量，不覆盖运行期动态拼接字符串、反射元数据或 IL 重写目标；通用静态策略同样是静态分析，对可观测 API 族 fail-closed，但不能替代真实 AA 同步验收；合成夹具与单元测试不启动 AA。
- **编码与性能边界**：实际可用编码器、帧率、码率、显卡与读回设置以本机真实编码器探测为准；硬件编码失败不会暗中改为 CPU 编码；本版不作跨 GPU 性能或提速承诺。

## 安装与校验方式（以冻结包为准；当前 staged 包不可发布）

### bundle 结构

生产包根目录含安装/回滚/启动脚本、`delivery-manifest.json`、`SHA256SUMS.txt`、`README.md`、`CHANGELOG.md`、许可证与 `docs/`；载荷位于 `AAVideoExport/0.3.0/`：

```text
交付目录/
├─ Install-Mod.ps1
├─ Rollback-Mod.ps1
├─ Deployment.Common.ps1
├─ Start-AA-AAVE.ps1
├─ delivery-manifest.json
├─ SHA256SUMS.txt
├─ README.md
├─ CHANGELOG.md
├─ LICENSE / THIRD_PARTY_NOTICES.md
├─ docs/
└─ AAVideoExport/0.3.0/
   ├─ manifest.json
   ├─ AAVideoExport.dll
   ├─ AAVideoExport.Core.dll
   ├─ 图形依赖 DLL、icon.png、docs、licenses
   └─ tools/ffmpeg.exe、ffprobe.exe（仅 full bundle）
```

- `lite` bundle 不带 FFmpeg/ffprobe；`full` bundle 额外携带实际编码器及其许可证与来源文件。
- 生产包**不含** `AAVideoExport.RuntimeBootstrap.dll` 或任何审计 bootstrap；不要把整个 bundle 直接解压进 `mods`。
- 管理脚本要求 PowerShell 7.2+（`pwsh`）；使用已构建交付包不需要 .NET SDK。

### STAGING 预演物料（非发布物，GO 后须作废重出）

`artifacts/release-0.3.0-draft/` 下已有一批 2026-11-22 00:26（UTC+8）的 `-SkipBuild` 预演包。**这些 zip、整包 SHA256 与包内 DLL 哈希均为候选/预演值，不是发布哈希**；G1–G6 闭合、主代理 GO、冻结构建复核完成后，须从冻结程序集目录用全新输出目录重新出包并重新校验，届时本批预演包一律作废，不得沿用。

| 预演包 | 相对路径（基于 `artifacts/release-0.3.0-draft/`） | 大小（bytes） | SHA256（预演值） | 结构校验 |
|---|---|---:|---|---|
| lite bundle | `staging\20261122-002603-lite\AAVideoExport-0.3.0-win-x64-lite-bundle.zip` | 846,213 | `FE81F1D194913A2A8C9F614435CA9FA11C7B86D484CD27BFE1E43FAC8622EDE1` | 15/15 PASS（`logs/verify-bundle-lite-pass.log`，exit 0） |
| full bundle | `staging\20261122-002603-full\AAVideoExport-0.3.0-win-x64-full-bundle.zip` | 257,863,149 | `97B75821AB0AA98F6B78E7E7943581116E25A694EA6EEBFA6A5D94BA6A7ACC3C` | 17/17 PASS（`logs/verify-bundle-full-pass.log`，exit 0）；含 `tools/ffmpeg.exe`、`tools/ffprobe.exe`、`licenses/FFmpeg/LICENSE.txt` |
| source 源码包 | `source\AAVideoExport-0.3.0-source.zip` | 1,433,196 | `5B80B728C1A11B82EB6D386C0C1369AE26F9207C705BF555BA3AD0040CA60FDA` | 1/1 OK（`logs/checksums-source.log`，exit 0）；564 条目，已排除 `.git`/`artifacts`/`bin`/`obj`/`*.user`/`local.props` |

- 旁侧校验和：各目录 `SHA256SUMS.txt`（lite 45 行 / full 86 行 / source 1 行）与 `<zip>.sha256`；生成与验证工具为 `tools/New-ReleaseChecksums.ps1`、`tools/Verify-ReleaseBundle.ps1`。
- 另有更早的陈旧产物 `artifacts/zero-per-mod/package-0.3.0/`，内嵌早期 lab 候选 DLL 身份，与当前候选身份不一致，不得复用。
- 预演包明细与 FAIL 对照场景见 `artifacts/release-0.3.0-draft/STAGING-README.md`。

### SHA256SUMS 用法

- 包内 `SHA256SUMS.txt` 列出除自身外全部载荷文件的 SHA256 与相对路径；ZIP 旁侧另有 `<zip>.sha256` 记录整包哈希。
- 解包后在包根目录逐条核对（PowerShell 模板，发布核验时执行）：

  ```powershell
  Get-Content .\SHA256SUMS.txt | ForEach-Object {
    $parts = $_ -split '  ', 2
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $parts[1]).Hash.ToLower()
    [pscustomobject]@{ Expected = $parts[0]; Actual = $actual; Ok = ($actual -eq $parts[0]) }
  }
  ```

- 整包哈希核对：将 `<zip>.sha256` 中的期望值与 `(Get-FileHash -Algorithm SHA256 <zip>).Hash` 比对；也可在有 `sha256sum` 的环境使用 `sha256sum -c <zip>.sha256`。

### DLL 哈希核对

1. 解包后核对 `AAVideoExport/0.3.0/AAVideoExport.dll` 与 `AAVideoExport/0.3.0/AAVideoExport.Core.dll` 的 SHA256，必须与发布时重新校验的冻结值一致；Plugin/Core 必须来自同一冻结构建。
2. `delivery-manifest.json` 中的 `PluginSha256` / `CoreSha256` 必须与包内 `SHA256SUMS.txt` 及实际文件一致；`AuditMode` 必须为 `false`、`BootstrapRelativePath` 必须为 `null`。
3. 安装前可先只读计划：`pwsh -NoProfile -File .\Install-Mod.ps1 -AAInstallPath '你的 AA 安装目录' -PlanOnly`；正式安装由安装器校验完整校验和、程序集版本、生产/审计构建模式与当前 Profile，仅创建新的 `mods/AAVideoExport/0.3.0` 目录并切换当前 Profile 的 AAVideoExport 选择，不覆盖其他 Mod 与旧版本目录。
4. 回滚使用安装回执路径运行版本化回滚脚本；备份、Profile 与旧自有 bootstrap 的恢复条件以回执为准。当前无真实 AA 安装上的安装回执与回滚演练记录，属 G2/G6 真机与纯生产路径缺口，不作为已验证项。

## 证据索引表

| 陈述 / 条目 | 仓库证据路径 |
|---|---|
| 门禁判定矩阵（G1–G7 口径、状态与证据） | `docs/zero-per-mod/EVIDENCE-MATRIX.md` |
| 零 per-Mod 标准定义、允许/禁止机制、判定粒度、能力边界 | `docs/zero-per-mod/STANDARD.md` |
| G1 PASS（当前候选源码）：106 文件 / 19 Mod / 81 身份 / 0 违规 / exit 0 | `artifacts/zero-per-mod/g1-static-20261122-002730.json`、`artifacts/zero-per-mod/g1-static-20261122-002730.log`、`tools/Test-ZeroPerModSource.ps1` |
| 静态门禁 legacy 基线：18 Mod / 69 身份 / 118 文件 / 203 违规 / 20 违规文件 / exit 1 FAIL | `artifacts/zero-per-mod/BASELINE.md`、`artifacts/zero-per-mod/static-source-v2.json` |
| 中间静态扫描报告（过程产物，非闭合证据） | `artifacts/zero-per-mod/static-source-v5.json`、`artifacts/zero-per-mod/static-source-v6.json` |
| Stage 1 夹具、通用静态策略、严格能力准入结果 | `artifacts/zero-per-mod/STAGE1-FIXTURES.md` |
| 夹具源码 | `tests/fixtures/UnknownPassiveMod/`、`tests/fixtures/UnknownClockMod/`、`tests/fixtures/UnknownRenderOwnerMod/` |
| 通用策略 / 准入源码 | `src/VideoExport.Plugin/GenericStaticModPolicy.cs`、`src/VideoExport.Core/StrictCapability.cs`、`src/VideoExport.Plugin/StrictCapabilityInventory.cs` |
| Stage 2 通用秒表桥合成行为与未完成项 | `artifacts/zero-per-mod/STAGE2-GENERIC-CLOCK.md`、`src/VideoExport.Plugin/GenericStopwatchBridge.cs` |
| 时钟 / 策略 / 准入测试 | `tests/VideoExport.GenericPolicy.Tests/`、`tests/VideoExport.StrictCapability.Tests/`、`tests/VideoExport.GenericClock.Tests/`、`tests/VideoExport.GenericClock.Fixture/` |
| 策略分类与准入单测日志 | `artifacts/zero-per-mod/generic-policy.log`、`artifacts/zero-per-mod/strict-capability-v3.log` |
| G2 lab 汇总（Run A/B/C、smoke-17、构建身份说明、未闭合项） | `artifacts/zero-per-mod/G2-REAL-AA.md` |
| Run A 证据目录 | `artifacts/zero-per-mod/g2-native-smoke-10/` |
| Run B 证据目录 | `artifacts/zero-per-mod/g2-native-cancel-15/` |
| Run C 证据目录 | `artifacts/zero-per-mod/g2-native-cancel-16/` |
| smoke-17 证据目录 | `artifacts/zero-per-mod/g2-native-smoke-17/` |
| G2 通过批次（450 帧×2，6/0） | `artifacts/zero-per-mod/g2-generic-final-09/`、`artifacts/zero-per-mod/g2-generic-final-12/` |
| G2 失败批次（2/3） | `artifacts/zero-per-mod/g2-generic-final-13/`、`artifacts/zero-per-mod/g2-generic-final-14/`、`artifacts/zero-per-mod/g2-generic-final-15/` |
| G5 配置 / AA 设置 / 存档恢复证据 | `artifacts/zero-per-mod/g2-native-smoke-10.state/settings-restoration.json`、`artifacts/zero-per-mod/g2-native-smoke-10/native-checks.log`、`artifacts/zero-per-mod/g2-generic-final-12/native-checks.log` |
| G5 全量快照工具、基线与比对演示 | `tools/Get-ThirdPartyModHashSnapshot.ps1`、`docs/zero-per-mod/G5-MOD-HASH-BASELINE.md`、`artifacts/zero-per-mod/g5-baseline-20261122-010540.json`、`g5-compare-self-20261122-010609.log`、`g5-compare-tampered-20261122-010624.log`、`g5-compare-rescan-20261122-010957.log` |
| G6 产品-only 安装清单与逐站差异账本 | `docs/zero-per-mod/G6-EXACT-RELEASE-DLL-MANIFEST.md`、`artifacts/zero-per-mod/g6-install-audit-20261122-010832.json`、`g6-install-audit-20261122-010832.log` |
| G6 预演包与候选构建一致性复核 | `artifacts/release-0.3.0-draft/PACKAGE-CONSISTENCY.md`、`artifacts/release-0.3.0-draft/logs/cosistency-lite.log`、`cosistency-full.log`、`cosistency-source.log` |
| G6 插件身份与旧包哈希对照 | `artifacts/zero-per-mod/g2-generic-final-12/native-checks.log`、`artifacts/zero-per-mod/package-0.3.0/bundle-0.3.0-0dfcc2af201647af99dc94cb3373550a/SHA256SUMS.txt` |
| 打包 / 验包 / 校验和脚本 | `tools/Package-Release.ps1`、`tools/Test-ReleaseArchive.ps1`、`tools/New-ReleaseChecksums.ps1`、`tools/Verify-ReleaseBundle.ps1` |
| STAGING 预演物料明细（非发布物） | `artifacts/release-0.3.0-draft/STAGING-README.md`、`artifacts/release-0.3.0-draft/staging/20261122-002603-lite/`、`artifacts/release-0.3.0-draft/staging/20261122-002603-full/`、`artifacts/release-0.3.0-draft/source/`、`artifacts/release-0.3.0-draft/logs/` |
| 陈旧产物（旧 lab 候选身份，不得复用） | `artifacts/zero-per-mod/package-0.3.0/` |
| 0.2.12 per-Mod 适配器留存（legacy 对照） | `legacy/0.2.12-per-mod/` |
| 旧版适配与验收背景（legacy，不作 0.3.0 证据） | `docs/moreeffects-clock-compatibility.md`、`CHANGELOG.md`、`README.md` |
