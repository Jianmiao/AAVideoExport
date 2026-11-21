# AAVE 0.3.0 零 Per-Mod 验收证据矩阵

- 生成日期：2026-11-22（本地时间，UTC+8）
- 工作副本：`E:\aamod\AAVE-zero-per-mod-20261121`
- 本次范围：仅产出文档与只读扫描；未修改 `src\`、未执行 `dotnet build`、未改动 `artifacts\zero-per-mod\package-0.3.0` 下旧包、未执行 git 操作、未联网上传。
- 判定取值仅限四值：**PASS / PARTIAL / FAIL / NOT-RUN**。无直接证据的条目判 NOT-RUN 或 PARTIAL，并写明缺什么。
- 基线口径：0.2.12 的全部结果仅作为 legacy 基线对照，不作为 0.3.0 的通过证据。
- 证据路径均相对于仓库根目录；哈希为 SHA256（大写十六进制）。

## 1. 总表

| 门禁 | 门禁含义 | 当前判定 | 关键数值 | 主要证据路径 | 缺口 | 闭合所需动作 | 责任归属 |
|---|---|---|---|---|---|---|---|
| G1 | 生产 `src` 静态零 per-Mod 违规：字符串字面量不得命中实际安装 Mod 的程序集名 / 文件名 / MVID / GUID / SHA256；违规数必须为 0 | PASS | 违规 0；违规涉及文件 0；扫描文件 106；实际安装 Mod 19；身份索引 81；退出码 0 | `artifacts/zero-per-mod/g1-static-20261122-002730.json`；`artifacts/zero-per-mod/g1-static-20261122-002730.log`；`tools/Test-ZeroPerModSource.ps1` | 静态扫描只覆盖 `src` 下 `.cs` 字符串字面量；无法证明运行期动态构造的身份判断不存在 | 在后续真机/打包门禁中复核运行期准入路径；静态门禁随源码变更复跑并留痕 | 主代理真机 + 文档 |
| G2 | 真实 AA 重复导出逐帧、PCM、文件 SHA 完全一致；取消后恢复再导出一致 | PARTIAL | 成功批次：644 帧 / 1,030,400 采样帧×2（视频 SHA `991398ED…`、PCM `ED50C5EE…`；取消后视频 `AAD050F7…`、PCM `ED50C5EE…`，MismatchedSamples=0）；450 帧 / 720,000 采样帧×2（视频 `7B479BD9…`、PCM `8146B884…`）。未闭合：mixed observer 标签布局报错 32；Run C 恢复超时 4 过 5 败；`smoke-17` 未在 attempt 0 前 settle | `artifacts/zero-per-mod/G2-REAL-AA.md`；`artifacts/zero-per-mod/g2-native-smoke-10/`；`artifacts/zero-per-mod/g2-native-cancel-15/`；`artifacts/zero-per-mod/g2-generic-final-09/`；`artifacts/zero-per-mod/g2-generic-final-12/`；失败记录 `artifacts/zero-per-mod/g2-native-cancel-16/`、`artifacts/zero-per-mod/g2-native-smoke-17/`、`artifacts/zero-per-mod/g2-generic-final-13/`、`artifacts/zero-per-mod/g2-generic-final-14/`、`artifacts/zero-per-mod/g2-generic-final-15/` | mixed observer 标签布局/文案断言间歇失败；第二次完整导出后 Azurite/FPS/VSync/tier/Unity 时钟恢复超时；确定性重建后 `smoke-17` 启动 settle 失败；现有真机运行全部由测试 harness 驱动；无纯产品 GUI/无头产品路径运行 | 在 release 构建上重跑完整取消/重复导出矩阵；定位并消除 observer 布局断言与恢复超时；在不加载测试 harness 的产品路径下取得重复导出一致证据 | 主代理真机 |
| G3 | 未知 Stopwatch / Unity Time Mod 不改 AAVE 代码、不改 Mod 代码也能通过完整导出矩阵 | NOT-RUN | 合成策略：`UnknownClockMod` 被通用策略拒绝，`managed-clock-call-sites=2`；通用 Stopwatch 桥合成测试 3 方法 / 5 调用点 PASS，但未接入生产 capture；真机 BepInEx 日志中无 `UnknownClockMod` 加载记录 | `artifacts/zero-per-mod/generic-policy.log`；`artifacts/zero-per-mod/STAGE2-GENERIC-CLOCK.md`；`tests/fixtures/UnknownClockMod/`；`tests/VideoExport.GenericClock.Tests/` | 桥只覆盖 `Stopwatch.GetTimestamp`；`DateTime.Now/UtcNow`、`Environment.TickCount/TickCount64`、Unity `Time.realtimeSinceStartup/unscaledTime` 未桥接；生产 `Plugin.Load()` 未初始化/打补丁；从未在真实 AA 安装中运行该 fixture | 把时钟桥接入 capture 作用域并补齐剩余时钟族；将 `UnknownClockMod` 装入真实 AA，不改两侧代码跑通完整同步矩阵并留痕 | 主代理真机 |
| G4 | 未知 render-owner 被通用能力拒绝，且首帧前拒绝、不输出错片电影 | NOT-RUN | 合成策略：`UnknownRenderOwnerMod` 被通用策略拒绝，`render-audio-video-references=2`；严格准入单测 29 过 0 败；真机日志中无该 fixture 加载记录 | `artifacts/zero-per-mod/generic-policy.log`；`artifacts/zero-per-mod/STAGE1-FIXTURES.md`；`artifacts/zero-per-mod/strict-capability-v3.log`；`tests/fixtures/UnknownRenderOwnerMod/` | fixture 仅为类型壳，运行期未实际独占渲染；无真机拒绝记录；无“首帧前拒绝、未产出错位成片”的证据 | 改造 fixture 使其在导出期实际独占渲染；在真实 AA 中验证通用能力探测在首帧前拒绝，并核验无错片输出 | 主代理真机 |
| G5 | 其他 Mod 的 DLL 与配置前后哈希不变 | PARTIAL | 全量基线快照（2026-11-22 实测）：99 文件 / 14,962,579 字节（mod-dll 30 / mod-config 31 / user-setting 7 / user-save 31），15 个 Mod 归组，30 个 DLL 程序集名全部解析成功；工具自比 99/99 PASS（exit 0）、真机重扫零差异（exit 0）、合成篡改 FAIL（ADDED=1 CHANGED=1，exit 1）。旧证据：`SfxFolderMod\config.txt` `D52E199B…3C8A77F72` `Restored=true`；剧情存档 `78D2B7D2…EF22` 前后一致 | `docs/zero-per-mod/G5-MOD-HASH-BASELINE.md`；`tools/Get-ThirdPartyModHashSnapshot.ps1`；`artifacts/zero-per-mod/g5-baseline-20261122-010540.json`；`artifacts/zero-per-mod/g5-compare-self-20261122-010609.log`；`g5-compare-tampered-20261122-010624.log`；`g5-compare-rescan-20261122-010957.log`；`g2-native-smoke-10.state/settings-restoration.json` | 基线 ≠ PASS：尚无真实导出前 / 导出后的成对快照比对；mods 下 105 个非配置资产（`.md` 38、`.png` 21、`.sample` 14、无扩展名 16 等）未纳入哈希；`E:\AAcache` 未扫描；配置识别依赖扩展名；内存态与运行期临时文件不在范围 | 主代理在每次真机导出前后各跑一次 `tools/Get-ThirdPartyModHashSnapshot.ps1` 并 `-Compare` 零差异（exit 0）、归档成对证据；并决定非配置资产与 `E:\AAcache` 是否纳入范围 | 主代理真机 |
| G6 | 每次导出独立同步矩阵；使用 exact release DLL；无测试插件 / 测试程序集 | PARTIAL | 当前 release DLL 哈希（只读核对）：`AAVideoExport.dll` `D1E849484D3BA535302ADB34BB328E1DB9CCC41D87E42C3F18FFB00BA3B87BB5`；`AAVideoExport.Core.dll` `E565807696588C679331F4EFA49BE549B41E0098EACC1C7A24504E19C66ABCF6`。`g2-generic-final-12/`、`g2-generic-final-13/`、`g2-generic-final-14/`、`g2-generic-final-15/` 的 `native-checks.log` 记录 `provider_sha256=D1E84948…`（与 release 插件 DLL 一致）；但每次真机运行均记录 `harness_sha256=CE4BF603…`；运行记录未登记 Core DLL 哈希。STAGING 预演包一致性复核（仅物料层，非发布证据）：lite/full 包内 Plugin/Core 实际哈希均为 `D1E84948…` / `E5658076…`，与候选一致；lite 15 项、full 17 项校验 PASS、退出码 0；source 包 564 条目、无 bin/obj/dll 载荷、manifest 0.3.0、4 个关键文件与工作副本 MATCH | `src/VideoExport.Plugin/bin/Release/net6.0/AAVideoExport.dll`；`src/VideoExport.Plugin/bin/Release/net6.0/AAVideoExport.Core.dll`；`artifacts/zero-per-mod/g2-generic-final-12/native-checks.log`（`STORY START` 字段 `provider` / `provider_sha256` / `harness_sha256`）；`artifacts/zero-per-mod/package-0.3.0/bundle-0.3.0-0dfcc2af201647af99dc94cb3373550a/SHA256SUMS.txt`；`artifacts/release-0.3.0-draft/PACKAGE-CONSISTENCY.md`；`artifacts/release-0.3.0-draft/logs/cosistency-lite.log`；`artifacts/release-0.3.0-draft/logs/cosistency-full.log`；`artifacts/release-0.3.0-draft/logs/cosistency-source.log` | 真机运行加载了测试 harness；Core DLL 运行内哈希未记录；旧包 bundle 内仍是早期候选哈希（插件 `DD35E888…`、Core `D202A7E7…`），与当前 release 哈希不一致；无纯产品（无审计/测试插件）运行记录；包一致性子项只证明预演物料对得上候选构建，不能替代纯产品运行闭环 | 定稿 release 双 DLL 并在运行记录中同时登记插件与 Core 哈希；在不加载 harness/审计插件的产品路径完成导出；重新生成包内 SHA256SUMS 并核对 | 主代理真机 + 打包 |
| G7 | 未过 G1–G6 前禁止上传或发布 | PARTIAL | `CHANGELOG.md` 顶部条目为 0.2.12，无 0.3.0 发布条目；`package-0.3.0` 仅为本地旧 bundle；本次会话未联网上传 | `CHANGELOG.md`；`artifacts/zero-per-mod/package-0.3.0/`；本矩阵第 1 节各门禁判定 | G2/G3/G4/G5/G6 未闭合，尚不具备发布授权；G7 的最终放行签署需在 G1–G6 全部 PASS 后执行 | 待 G1–G6 全 PASS 后，由打包/文档核对无预发布上传、登记发布证据与 release notes 边界说明 | 打包 + 文档 |

### 1.1 G1 基线对照（0.2.12 legacy vs 本次 0.3.0 复跑）

| 指标 | 0.2.12 基线（legacy，`static-source-v2.json`） | 0.3.0 本次复跑（`g1-static-20261122-002730.json`） |
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

**判定依据**：`docs/zero-per-mod/STANDARD.md`“判定粒度”规定，生产 `src` 字符串字面量中不得出现实际安装 Mod 的程序集名/文件名/MVID/GUID/SHA256，违规数必须为 0。扫描器从 `F:\AzureArchive_101\mods` 实际发现 Mod 集合，不在脚本内硬编码 Mod 名（`tools/Test-ZeroPerModSource.ps1` 第 15–52 行发现逻辑，第 60–105 行字面量匹配逻辑）。

**逐条证据**：

1. `artifacts/zero-per-mod/g1-static-20261122-002730.json`
   - `schema = aave.zero-per-mod.static.v2`
   - `generatedAtUtc = 2026-11-21T16:27:51`（本地 2026-11-22 00:27:51，UTC+8）
   - `aaInstallPath = F:\AzureArchive_101`
   - `sourceRoot = E:\aamod\AAVE-zero-per-mod-20261121`
   - `filesScanned = 106`
   - `violations = 0`；`details = []`（0 条违规明细，违规涉及文件 0）
   - `discoveredModNames.Count = 19`；`discoveredModIdentities.Count = 81`
2. `artifacts/zero-per-mod/g1-static-20261122-002730.log`
   - stdout：`ZERO-PER-MOD STATIC: files=106 mods=19 violations=0`
   - 末尾记录 `EXIT-CODE=0`
3. 扫描脚本 `tools/Test-ZeroPerModSource.ps1` SHA256 `F71019F1F7D7C6707A2B271BFE9B6651642729CA8C252232A09204B71AAE70AB`，与 `artifacts/zero-per-mod/STAGE0.md` 第 37 行记录一致。
4. 本次复跑命令（PowerShell 7，未加沙箱提权参数）：

   ```powershell
   pwsh -NoProfile -File .\tools\Test-ZeroPerModSource.ps1 `
     -AAInstallPath 'F:\AzureArchive_101' `
     -Output 'artifacts\zero-per-mod\g1-static-20261122-002730.json'
   ```

5. Legacy 基线对照：`artifacts/zero-per-mod/static-source-v2.json`（`violations=203`、`filesScanned=118`、`discoveredModNames.Count=18`、退出码 1）；汇总见 `artifacts/zero-per-mod/BASELINE.md`、`artifacts/zero-per-mod/STAGE0.md`。历史中间快照 `static-source-v4.json` / `static-source-v5.json` / `static-source-v6.json` 违规均为 0，仅作过程记录。

**仍未证明的部分**：

- 静态门禁不扫描运行期动态拼接的字符串、反射元数据或 IL 重写目标；不能仅凭本门禁断言运行期不存在按 Mod 身份的放行/拒绝分支。
- 未在本次复跑中重新构建产品二进制；源码与 `bin/Release` 输出的对应关系以既有构建记录为准（见 G6）。

### G2 — 真实 AA 重复导出同步矩阵

**判定依据**：`docs/zero-per-mod/STANDARD.md`“动态门禁”规定每个完整导出独立跑同步矩阵，不允许用平均、抽样或总帧数相等代替；重复导出须逐帧、PCM、文件内容一致。

**逐条证据（已通过部分）**：

1. Run A（两次完整导出，无取消）：`artifacts/zero-per-mod/g2-native-smoke-10/`
   - `story-media-01.json.decoded.fingerprint.json` 与 `story-media-02.json.decoded.fingerprint.json`：均为 640×360@30fps、644 帧、1,030,400 采样帧；视频 SHA256 均 `991398ED349D5D423D1C8575461614512343E82E63DCFE5F50CD45C2689D508C`；PCM16 SHA256 均 `ED50C5EEE1804AEDC8AAD2CC17A77D441A71D8B16FE67C200091AF6E618B1CF7`。
   - `native-checks.log`：`PASS repeated-strict-identity-frame-sample-duration-decoded-content-match`、`PASS story-source-unchanged`、`PASS actual-azurite-render-control-restoration`。
2. Run B（一次取消 12 帧后两次完整导出）：`artifacts/zero-per-mod/g2-native-cancel-15/`
   - 两次完整导出指纹：644 帧 / 1,030,400 采样帧；视频 SHA256 均 `AAD050F717B34DEA8963FE2D4E1455A4DB61155C8637D54AEB40AF8BA2D77834`；PCM16 SHA256 均 `ED50C5EE…18B1CF7`。
   - `story-media-03.json.original-pcm-comparison.json`：`Passed=True`、`MismatchedSamples=0`、`MaximumErrorLsb=0.5`、`ClippedSamples=0`、`SampleFrames=1030400`。
   - `native-checks.log`：`PASS deliberate-cancel-cleanup-before-real-retry`、`PASS actual-azurite-render-control-restoration`；记录 1 次取消 + 2 次完成。
3. 产品候选重复导出批次：`artifacts/zero-per-mod/g2-generic-final-09/`（RESULT 6 passed; 0 failed）与 `artifacts/zero-per-mod/g2-generic-final-12/`（RESULT 6 passed; 0 failed）
   - 两批次各自两次导出指纹一致：450 帧 / 720,000 采样帧；视频 SHA256 `7B479BD96B80CFC51F042DCCEA67AABB9AD7C0C96978771868786D5996462EFC`；PCM16 SHA256 `8146B8845B0937684CDEE448A602805C152C4006BE6195083B58BA830BA92C11`。
   - `g2-generic-final-12/story-render-control-evidence.json`：`Passed=true`，`RestoredAttempts=2`，两次 attempt 均 `acquired=1 / released=1 / observedOwned=true`。
4. 汇总记录：`artifacts/zero-per-mod/G2-REAL-AA.md`（Run A / Run B / Run C 与构建身份说明）。

**未通过 / 未闭合部分**：

1. mixed observer 标签布局断言间歇失败：`g2-native-cancel-15/native-checks.log` 记录 `controlled-story-export-evidence attempts=3; passes=3; errors=32`，32 条均为 `Original AUTO label layout changed`（标签文案 `AUTO`/`自动` 不稳定）；属 observer/布局断言，保留未豁免。
2. Run C 恢复超时：`artifacts/zero-per-mod/g2-native-cancel-16/native-checks.log` RESULT 4 passed; 5 failed；第二次完整导出后宿主清理超时，Azurite 映射 / FPS·VSync·tier / Unity 时钟未恢复。
3. 确定性重建后启动 settle 失败：`artifacts/zero-per-mod/g2-native-smoke-17/` 在 attempt 0 前即失败（Idle 面板与 Azurite 映射未 settle）。
4. `g2-generic-final-13/`、`g2-generic-final-14/`、`g2-generic-final-15/` RESULT 均为 2 passed; 3 failed：`native-story-export: Sequence contains no matching element` / `Cancellation needs an installed live mixed-story observer`，且 `actual-azurite-render-control-restoration` 失败。
5. 现有真机运行均由 `AAVideoExport.NativePath.Tests` harness 驱动（见 G6）；未取得不加载 harness 的纯产品路径导出证据；虚拟桌面曾返回 `NO_SESSION`，无产品 GUI 运行记录。

**仍未证明的部分**：release 构建上的取消→重复导出一致性；恢复超时是产品缺陷还是 observer 时序问题尚未区分；长剧情 / 多 Mod 共存场景的同步矩阵未覆盖。

### G3 — 未知时钟 Mod 不改代码导出

**判定依据**：未知 Mod 读取 `Stopwatch.GetTimestamp` / Unity `Time.realtimeSinceStartup` 等时钟，AAVE 与该 Mod 均不改代码，实际导出仍通过完整同步矩阵。

**逐条证据（仅合成 / 单测层）**：

1. `artifacts/zero-per-mod/generic-policy.log`：`UnknownClockMod.dll => generic-static-policy-rejected | managed-clock-call-sites=2`。
2. `artifacts/zero-per-mod/STAGE2-GENERIC-CLOCK.md`：`GenericStopwatchBridge` 基于 IL 调用点发现并重写 `Stopwatch.GetTimestamp`；fixture `tests/VideoExport.GenericClock.Fixture`（3 方法 / 5 调用点）合成测试 PASS；导出期返回统一视频时钟，非导出期恢复原始时钟。
3. Fixture 清单：`artifacts/zero-per-mod/STAGE1-FIXTURES.md` 记录 `UnknownClockMod.dll` SHA256 `0AC66C9EA98769784E9D9BAD6BD6D1BA75A65BF17060ABBE202A6EC78BA9FEAB`；源码工程 `tests/fixtures/UnknownClockMod/`。

**未通过 / 未闭合部分**：

- 生产 `Plugin.Load()` 尚未调用桥的初始化/打补丁（`STAGE2-GENERIC-CLOCK.md` “What is not done”）。
- 仅覆盖 `Stopwatch.GetTimestamp`；`DateTime.Now/UtcNow`、`Environment.TickCount/TickCount64`、Unity `Time.realtimeSinceStartup/unscaledTime` 未桥接。
- 全部 `g2-native-*` 的 BepInEx 日志中均无 `UnknownClockMod` 加载记录；该 fixture 从未在真实 AA 安装中运行。

**仍未证明的部分**：未知时钟 Mod 在真实 AA 导出中走通用时钟后帧/音频/PTS 一致；非 Stopwatch 时钟族的行为校正。

### G4 — 未知 render-owner 通用拒绝且不输出错片

**判定依据**：无协议、独占渲染的 opaque Mod 由通用能力探测在首帧前拒绝；拒绝不算兼容通过，但不得产出错位电影；不得按 Mod 名称拒绝。

**逐条证据（仅合成 / 单测层）**：

1. `artifacts/zero-per-mod/generic-policy.log`：`UnknownRenderOwnerMod.dll => generic-static-policy-rejected | render-audio-video-references=2`。
2. `artifacts/zero-per-mod/strict-capability-v3.log`：严格准入 29 passed; 0 failed，含通用静态惰性证明放行未知模块、裸标记拒绝、通用 RenderControlV1 参与者准入等用例。
3. Fixture 清单：`artifacts/zero-per-mod/STAGE1-FIXTURES.md` 记录 `UnknownRenderOwnerMod.dll` SHA256 `55C32CC4238FC6F016AADC2F36F41DBACB56FCEFE92A105612E40FB71F0C3FE8`；源码工程 `tests/fixtures/UnknownRenderOwnerMod/`。

**未通过 / 未闭合部分**：

- fixture 仅为引用 `UnityEngine.Camera` / `RenderTexture` 的类型壳，运行期未实际独占渲染（`STAGE1-FIXTURES.md` “Limits”）。
- 全部 `g2-native-*` 日志中无 `UnknownRenderOwnerMod` 加载记录；无真机拒绝时点（首帧前）与“无错片输出”的证据。

**仍未证明的部分**：真实独占渲染场景下通用探测的拒绝时点、拒绝原因文本、以及导出目录无错位成片。

### G5 — 其他 Mod DLL 与配置前后哈希不变

**判定依据**：导出前后第三方 Mod 的 DLL 与配置文件哈希一致；AAVE 不得改写其他 Mod 载荷。

**逐条证据**：

1. `artifacts/zero-per-mod/g2-native-smoke-10.state/settings-restoration.json`
   - `Restored=true`、`RegistryRestored=true`、`RestoreErrors=[]`、`EvidenceErrors=[]`。
   - 第三方配置 `C:\Users\21294\AppData\LocalLow\foxxlight\AzureArchive\SfxFolderMod\config.txt` SHA256 `D52E199B3947596238A64F7EF1CC1C380A4D7DEA57970ABFB1B089D3C8A77F72`，`Restored=true`。
   - 同文件记录 AA 设置文件（`user_settings.json` 及 `.bk.1/2/3`、临时文件）恢复哈希。
2. 剧情存档未改：`g2-native-smoke-10/native-checks.log`、`g2-native-cancel-15/native-checks.log`、`g2-generic-final-09/native-checks.log`、`g2-generic-final-12/native-checks.log` 均有 `PASS story-source-unchanged`；存档 `AAVE-OfficialAuthoring-20261030.aas` 前后 SHA256 均 `78D2B7D2C2DDDD87145E5C301703F7B04AE45A55AFB19236150084C1FED3EF22`。

3. 全量哈希快照能力（2026-11-22 新增）：`tools/Get-ThirdPartyModHashSnapshot.ps1` 只读扫描 `F:\AzureArchive_101\mods` 的第三方部分（排除 AAVE 自身目录与 Unknown 夹具 / `.Tests`）及 AA 用户设置、剧情存档目录，输出 UTF-8 无 BOM、稳定排序的 json。基线 `artifacts/zero-per-mod/g5-baseline-20261122-010540.json`：99 文件 / 14,962,579 字节（mod-dll 30 / 7,124,992；mod-config 31 / 803,791；user-setting 7 / 8,126；user-save 31 / 7,025,670），15 个 Mod 归组，30 个 DLL 程序集名全部解析成功；扫描前后 `mods` 均为 475 文件 / 2,829,252,068 字节（第三方 165 / 11,958,589），`F:\` 零写入。
   - 工具自比 PASS：`artifacts/zero-per-mod/g5-compare-self-20261122-010609.log`（99/99，ADDED=0 / REMOVED=0 / CHANGED=0，exit 0）。
   - 合成篡改 FAIL：`g5-compare-tampered-20261122-010624.log`（99/100，ADDED=1 / CHANGED=1，exit 1；仅改临时副本 `artifacts/zero-per-mod/.g5-demo/tampered.json`）。
   - 现场重扫零差异：`g5-compare-rescan-20261122-010957.log`（99/99，全 0，exit 0）。
   - 幂等性：输出路径已存在时拒绝覆盖（`g5-idempotency-20261122-010635.log`，exit 1）。
   - 方法说明与缺口见 `docs/zero-per-mod/G5-MOD-HASH-BASELINE.md`。

**未通过 / 未闭合部分**：

- 尚无真实导出前 / 导出后的成对快照比对：基线快照、工具自比与重扫零差异只证明“工具可用、当前磁盘状态自洽”，**不构成 G5 PASS**。
- 哈希范围未覆盖 mods 下 105 个非配置资产（`.md` 38、`.png` 21、`.sample` 14、无扩展名 16、`.cs` 4、`.csproj` 2、`.pdb` 2、`.ps1` 2，及 `.gitignore` / `.idx` / `.pack` / `.props` / `.rev` / `.sha256` 各 1）；`E:\AAcache` 未扫描；配置识别依赖扩展名，非标准落点可能漏扫。
- 哈希门禁不覆盖内存态与运行期临时文件。
- 旧恢复记录（`settings-restoration.json`）证明的是“清理后还原”，未证明导出运行期间其他 Mod 载荷未被改写。

**仍未证明的部分**：多次导出（含取消/恢复）前后，全部第三方 Mod DLL 与配置字节级不变。

### G6 — 独立同步矩阵、exact release DLL、无测试插件

**判定依据**：每次导出独立跑同步矩阵；运行的必须是 exact release 构建；生产载荷不含测试插件 / 审计程序集。

**逐条证据**：

1. 当前 release 二进制（只读哈希核对，未重建）：
   - `src/VideoExport.Plugin/bin/Release/net6.0/AAVideoExport.dll` SHA256 `D1E849484D3BA535302ADB34BB328E1DB9CCC41D87E42C3F18FFB00BA3B87BB5`
   - `src/VideoExport.Plugin/bin/Release/net6.0/AAVideoExport.Core.dll` SHA256 `E565807696588C679331F4EFA49BE549B41E0098EACC1C7A24504E19C66ABCF6`
2. `artifacts/zero-per-mod/g2-generic-final-12/native-checks.log`（`STORY START` 字段）：`provider=F:\AzureArchive_AAVE03Lab\mods\AAVideoExport\0.3.0\AAVideoExport.dll`，`provider_sha256=D1E849484D3BA535302ADB34BB328E1DB9CCC41D87E42C3F18FFB00BA3B87BB5`，与当前 release 插件 DLL 一致；`g2-generic-final-13/`、`g2-generic-final-14/`、`g2-generic-final-15/` 的 `provider_sha256` 同为 `D1E84948…` 前缀。
3. 每次导出独立目录与独立媒体指纹：`g2-generic-final-09/`、`g2-generic-final-12/` 各自持有 `story-media-01/02.json.decoded.*` 指纹与 PCM 证据。
4. 旧包（只读，未改动）：`artifacts/zero-per-mod/package-0.3.0/bundle-0.3.0-0dfcc2af201647af99dc94cb3373550a/SHA256SUMS.txt` 登记的插件 `dd35e888…`、Core `d202a7e7…` 为早期候选哈希，与当前 release 哈希不一致。
5. STAGING 预演包一致性复核（物料层，非发布证据）：`artifacts/release-0.3.0-draft/` 下 lite / full / source 三个预演包经 `tools/Verify-ReleaseBundle.ps1` 与条目扫描复核——lite 15 项、full 17 项 PASS、退出码 0，包内 Plugin/Core 实际哈希 = `D1E84948…` / `E5658076…`；source 包 564 条目、无 bin/obj/dll/exe/pdb 载荷、`manifest.json` 版本 0.3.0，`tools/Test-ZeroPerModSource.ps1`、`tools/Verify-ReleaseBundle.ps1`、`tools/New-ReleaseChecksums.ps1`、`docs/zero-per-mod/STANDARD.md` 四个文件 zip 内哈希与工作副本 MATCH。明细见 `artifacts/release-0.3.0-draft/PACKAGE-CONSISTENCY.md` 与 `logs/cosistency-lite.log`、`cosistency-full.log`、`cosistency-source.log`。该子项只证明物料对得上候选构建，**不**构成纯产品运行闭环证据。

**未通过 / 未闭合部分**：

- 每条真机运行 `STORY START` 均带 `harness_sha256=CE4BF603C52D8918C724740206764FC2844B3DB5702F234FC6C3515526CDBE3B`，即测试 harness 已加载，不满足“无测试插件”。
- 运行记录只登记插件 DLL 哈希，未登记 `AAVideoExport.Core.dll` 运行内哈希；Core 与 release 一致性未在真机证据中闭环。
- 旧 lab bundle（`artifacts/zero-per-mod/package-0.3.0/`）内二进制仍是早期候选哈希；新出的 STAGING 预演包虽与候选构建物料一致，但属非冻结候选产物，GO 后须从冻结程序集目录重新打包。

**仍未证明的部分**：纯产品路径（无 harness、无审计 bootstrap、无测试程序集）下的 release 双 DLL 完整导出矩阵。

### G7 — 发布前禁止上传 / 发布

**判定依据**：未过 G1–G6 前，禁止上传源码或发布 release；0.2.12 的 overclaim 不得沿用。

**逐条证据**：

1. `CHANGELOG.md` 顶部发布条目为 `0.2.12 — … 2026-10-31`，无 0.3.0 发布条目。
2. `artifacts/zero-per-mod/package-0.3.0/` 仅存本地早期 bundle；本次任务未执行联网上传或发布动作，未改动该目录。
3. 本矩阵第 1 节：G2 PARTIAL、G3 NOT-RUN、G4 NOT-RUN、G5 PARTIAL、G6 PARTIAL，前置门禁未全绿。

**未通过 / 未闭合部分**：

- G7 目前是“禁止发布”约束生效中；最终 G7 PASS 需在 G1–G6 全部 PASS 后，由打包/文档复核无预发布上传、release notes 写明实际证据与边界。

## 3. 证据清单

| 文件 / 目录（相对仓库根） | 生成时间（本地） | 覆盖门禁 | 是否可复现 |
|---|---|---|---|
| `artifacts/zero-per-mod/g1-static-20261122-002730.json` | 2026-11-22 00:27:51 | G1 | 可复现：`tools/Test-ZeroPerModSource.ps1` + 固定命令；脚本哈希 `F71019F1…E70AB` |
| `artifacts/zero-per-mod/g1-static-20261122-002730.log` | 2026-11-22 00:27:51 | G1 | 同上；含 stdout 与 `EXIT-CODE=0` |
| `artifacts/zero-per-mod/static-source-v2.json` | 2026-11-21 20:11:25（`generatedAtUtc` 12:11:25） | G1（legacy 基线） | 可复现，仅作 0.2.12 对照，不作通过证据 |
| `artifacts/zero-per-mod/BASELINE.md` | 2026-11-21 20:28:02 | G1（legacy 基线分布） | 记录性；引用 v2 明细 |
| `artifacts/zero-per-mod/STAGE0.md` | 2026-11-21 20:28:05 | G1 / 工具链基线 | 记录性；含 SDK、AA 可执行文件哈希 |
| `artifacts/zero-per-mod/STAGE1-FIXTURES.md` | 2026-11-21 20:27:45 | G3 / G4（合成） | fixture 工程可重建；真机结论不可由本文复现 |
| `artifacts/zero-per-mod/STAGE2-GENERIC-CLOCK.md` | 2026-11-21 20:10:16 | G3（合成） | 单测 `tests/VideoExport.GenericClock.Tests` 可复跑 |
| `artifacts/zero-per-mod/G2-REAL-AA.md` | 2026-11-21 21:07:17 | G2 | 记录性；真机批次需 AA lab 环境重放 |
| `artifacts/zero-per-mod/generic-policy.log` | 2026-11-21 23:26:54 | G3 / G4（合成策略） | 可由 `VideoExport.GenericPolicy.Tests` 复跑 |
| `artifacts/zero-per-mod/strict-capability-v3.log` | 2026-11-21 23:30:27 | G4 / 准入单测 | 可由 `VideoExport.StrictCapability.Tests` 复跑（29 过 0 败） |
| `artifacts/zero-per-mod/staticinert-v6.log` | 2026-11-21 23:32:48 | G5 相关 / 准入单测 | 可由 `VideoExport.StaticInertMod.Tests` 复跑（26 过 0 败） |
| `artifacts/zero-per-mod/build-plugin-final.log` | 2026-11-21 23:29:04 | G6（历史构建记录） | 记录性；本次未重建，不作为 0.3.0 release 构建证据 |
| `artifacts/zero-per-mod/g2-native-smoke-10/` | 2026-11-21 20:51:25 | G2（Run A） | 真机记录；复现需 AA lab + harness |
| `artifacts/zero-per-mod/g2-native-smoke-10.state/` | 2026-11-21 20:51:26 | G5（配置恢复） | 真机记录；含 `settings-restoration.json` |
| `artifacts/zero-per-mod/g2-native-cancel-15/` | 2026-11-21 20:58:52 | G2（Run B） | 真机记录；含 PCM 比对 JSON |
| `artifacts/zero-per-mod/g2-native-cancel-16/` | 2026-11-21 21:00:46 | G2（Run C 失败记录） | 真机失败记录，保留未丢弃 |
| `artifacts/zero-per-mod/g2-native-smoke-17/` | 2026-11-21 21:06:44 | G2（settle 失败记录） | 真机失败记录 |
| `artifacts/zero-per-mod/g2-generic-final-09/` | 2026-11-22 00:04:01 | G2（6/0 批次） | 真机记录；harness 驱动 |
| `artifacts/zero-per-mod/g2-generic-final-12/` | 2026-11-22 00:10:54 | G2 / G6（release 插件哈希批次） | 真机记录；harness 驱动 |
| `artifacts/zero-per-mod/g2-generic-final-13/` | 2026-11-22 00:12:02 | G2（失败记录） | 真机失败记录 |
| `artifacts/zero-per-mod/g2-generic-final-14/` | 2026-11-22 00:13:56 | G2（失败记录） | 真机失败记录 |
| `artifacts/zero-per-mod/g2-generic-final-15/` | 2026-11-22 00:17:38 | G2（失败记录） | 真机失败记录 |
| `artifacts/zero-per-mod/package-0.3.0/bundle-0.3.0-0dfcc2af201647af99dc94cb3373550a/SHA256SUMS.txt` | 旧包（未改动） | G6（旧包哈希对照） | 只读；不得作为当前 release 哈希证据 |
| `artifacts/release-0.3.0-draft/PACKAGE-CONSISTENCY.md` | 2026-11-22 | G6（包一致性子项，STAGING） | 记录性；仅证明物料对得上候选构建，不作发布证据 |
| `artifacts/release-0.3.0-draft/logs/cosistency-lite.log` | 2026-11-22 | G6（lite 预演包一致性） | 可复现：`tools/Verify-ReleaseBundle.ps1` + 固定参数；15 项 PASS、退出码 0 |
| `artifacts/release-0.3.0-draft/logs/cosistency-full.log` | 2026-11-22 | G6（full 预演包一致性） | 可复现：同上；17 项 PASS、退出码 0 |
| `artifacts/release-0.3.0-draft/logs/cosistency-source.log` | 2026-11-22 | G6（源码包一致性） | 条目扫描 + 四文件 zip/工作副本哈希比对；退出码 0 |
| `docs/zero-per-mod/G5-MOD-HASH-BASELINE.md` | 2026-11-22 | G5（方法、基线统计、缺口） | 方法文档；基线可由脚本复现 |
| `tools/Get-ThirdPartyModHashSnapshot.ps1` | 2026-11-22 | G5（快照/比对工具） | 可复现：`-Output` 生成快照、`-Compare` 比对，差异 exit 1 |
| `artifacts/zero-per-mod/g5-baseline-20261122-010540.json` | 2026-11-22 01:05:40 | G5（导出前基线） | 可复现；99 文件 / 30 DLL / 15 Mod |
| `artifacts/zero-per-mod/g5-compare-self-20261122-010609.log` | 2026-11-22 01:06:09 | G5（工具自比 PASS 演示） | 可复现；99/99，exit 0 |
| `artifacts/zero-per-mod/g5-compare-tampered-20261122-010624.log` | 2026-11-22 01:06:24 | G5（篡改 FAIL 演示） | 临时副本构造；ADDED=1 / CHANGED=1，exit 1 |
| `artifacts/zero-per-mod/g5-compare-rescan-20261122-010957.log` | 2026-11-22 01:09:57 | G5（现场重扫零差异） | 可复现；99/99，exit 0 |
| `docs/zero-per-mod/G6-EXACT-RELEASE-DLL-MANIFEST.md` | 2026-11-22 | G6（产品-only 安装清单、差异账本、重打包项） | 只读审计文档；含可复跑命令 |
| `artifacts/zero-per-mod/g6-install-audit-20261122-010832.json` | 2026-11-22 01:08:32 | G6（10 个站点逐文件哈希与判定） | 可复现：只读审计脚本 |
| `artifacts/zero-per-mod/g6-install-audit-20261122-010832.log` | 2026-11-22 01:08:32 | G6（人读快照） | 同上 |
| `docs/zero-per-mod/STANDARD.md` | — | G1–G7 口径定义 | 标准文本 |

## 4. 结论：距离“可打包”的缺口（按优先级排序）

当前状态：**G1 PASS；G2 PARTIAL；G3 NOT-RUN；G4 NOT-RUN；G5 PARTIAL；G6 PARTIAL；G7 PARTIAL（禁止发布生效中）**。尚不具备打包 / 发布条件。

1. **G6 纯产品 release 运行闭环（阻塞打包的第一前置）**：在不加载测试 harness / 审计插件的产品路径，使用当前 release 双 DLL（`D1E84948…` 插件、`E5658076…` Core）完成导出，并在运行记录中同时登记 `provider_sha256` 与 `core_sha256`；现有真机批次均带 `harness_sha256`，不能作为 G6 证据。安装清单与逐站差异见 `docs/zero-per-mod/G6-EXACT-RELEASE-DLL-MANIFEST.md`；隔离 lab 的 `BepInEx\plugins\` 仍残留 5 个禁止项（Tests harness ×1、`UnknownPassiveMod` ×2、`UnknownClockMod` ×2），GO 前须清理（本轮只读未删）。
2. **G2 真机同步矩阵闭合**：在 release 构建上重跑取消→重复导出；消除 mixed observer 标签布局/文案断言的 32 条间歇报错；定位并消除第二次完整导出后的 Azurite/FPS/VSync/tier/时钟恢复超时；复现并处理 `smoke-17` 启动 settle 失败；取得纯产品路径下逐帧 / PCM / 文件 SHA 一致证据。
3. **G3 未知时钟 Mod 真机验收**：把通用时钟桥接入生产 capture 作用域，补齐 DateTime / Environment.TickCount / Unity Time 时钟族；将 `UnknownClockMod` 装入真实 AA，不改两侧代码跑通完整同步矩阵。
4. **G4 未知 render-owner 真机拒绝验收**：让 fixture 实际独占渲染；验证通用探测在首帧前拒绝、给出原因、且不产出错位电影。
5. **G5 第三方 Mod 前后哈希门禁**：工具与基线已就绪（`tools/Get-ThirdPartyModHashSnapshot.ps1`、`g5-baseline-20261122-010540.json`）；缺的是真实导出前 / 后的成对快照与 `-Compare` 零差异（exit 0），需在每次真机导出流程中执行并归档。
6. **G7 发布签署**：G1–G6 全部 PASS 后，核对无预发布上传，更新包内 SHA256SUMS 与 release notes（写明实际证据与能力边界），再进入打包发布流程。

> 边界声明：本矩阵仅记录截至 2026-11-22 00:27（本地）的本机证据；合成单测与 harness 驱动的真机批次均不能替代纯产品 release 验收。0.2.12 的任何通过结论仅作 legacy 基线对照。
