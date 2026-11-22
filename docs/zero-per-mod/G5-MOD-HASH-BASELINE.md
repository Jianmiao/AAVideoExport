# G5 第三方 Mod 哈希基线（0.3.0）

- 生成日期：2026-11-22（本地时间，UTC+8）
- 工作副本：`E:\aamod\AAVE-zero-per-mod-20261121`
- 本次范围：只读扫描与哈希计算；未启动 AA / 游戏进程，未修改 `src\`，未写入 `F:\` 任何字节，未上传、未打 tag、未发布。
- 配套脚本：`tools/Get-ThirdPartyModHashSnapshot.ps1`
- 基线快照：`artifacts/zero-per-mod/g5-baseline-20261122-010540.json`
- 本文不修改 `docs/zero-per-mod/EVIDENCE-MATRIX.md`（并档由另一条线负责）。

## 1. G5 门禁判定口径

G5 要求：导出前后，其他 Mod 的 DLL 与配置哈希不变。判定流程固定为三步：

1. **导出前快照**：在真实导出开始前，对安装目录第三方 Mod 载荷与 AA 用户设置/存档做一次全量哈希快照（before）。
2. **导出后快照**：同一次导出结束后，在同一台机器、同一安装目录再做一次快照（after）。
3. **零差异比对**：用 `-Compare` 逐条比对两份快照；仅当 `ADDED=0`、`REMOVED=0`、`CHANGED=0` 且退出码 0，G5 该轮才算 PASS。出现任何 ADDED / REMOVED / CHANGED，退出码 1，该轮 G5 不通过。

**基线 ≠ PASS**：本文件只记录导出前基线与工具能力验证。G5 必须由主代理在真实导出前后各跑一次快照、`-Compare` 零差异后才能闭合；当前 G5 判定仍为 PARTIAL。

## 2. 工具与用法

脚本：`tools/Get-ThirdPartyModHashSnapshot.ps1`（PowerShell 7，只读）。

参数：

| 参数 | 说明 |
|---|---|
| `-AAInstallPath` | AA 安装目录，默认 `F:\AzureArchive_101`；Mod 扫描其下 `mods\` |
| `-Output` | 快照 json 输出路径；文件已存在时明确报错退出，不静默覆盖 |
| `-Compare` | 基线快照路径；给出后，写完 after 快照并与基线逐条比对 |
| `-AfterSnapshot` | 可选，文件对文件比对（不扫描、不写文件），用于自比 / 篡改演示 |
| `-ConfigRoot` | 可选，覆盖 AA 用户设置根目录（默认 `%USERPROFILE%\AppData\LocalLow\foxxlight\AzureArchive`） |
| `-SaveRoot` | 可选，覆盖剧情存档/工程根目录（默认 `E:\AAdata\data`） |

快照条目字段：`category`（`mod-dll` / `mod-config` / `user-setting` / `user-save`）、`mod`、`relativePath`、`assemblyName`（解析失败留空）、`sizeBytes`、`sha256`、`lastWriteTimeUtc`。条目按 `category, relativePath` 稳定排序；输出 UTF-8 无 BOM json。

退出码：比对无差异 `0`；存在 ADDED / REMOVED / CHANGED `1`；扫描或参数错误非 0。

## 3. 本次基线实测统计

基线快照：`artifacts/zero-per-mod/g5-baseline-20261122-010540.json`
（`generatedAtUtc = 2026-11-21T17:05:41Z`，本地 2026-11-22 01:05:41）

| 分类 | 文件数 | 字节数 |
|---|---:|---:|
| `mod-dll`（第三方 Mod DLL） | 30 | 7,124,992 |
| `mod-config`（Mod 配置，含 LocalLow 下 Mod 自有配置目录） | 31 | 803,791 |
| `user-setting`（AA 用户设置 / overrides） | 7 | 8,126 |
| `user-save`（剧情存档 `.aas` / 工程 `.aap`） | 31 | 7,025,670 |
| **合计** | **99** | **14,962,579** |

- 扫描耗时：534 ms（快照字段 `summary.elapsedMilliseconds`）。
- 扫描根：`mods = F:\AzureArchive_101\mods`；`localLowRoot = C:\Users\21294\AppData\LocalLow\foxxlight\AzureArchive`；`dataRoot = E:\AAdata\data`。
- 日志：`artifacts/zero-per-mod/g5-baseline-20261122-010540.log`，`EXIT-CODE=0`。

### 3.1 按 Mod 归组清单

| Mod（顶层目录 / 配置目录） | DLL 数 | 代表 DLL SHA256 前 16 位 | 配置文件数 |
|---|---:|---|---:|
| AAMultilingual | 3 | `B62F46FAD43082F1` | 4 |
| AASkipIntro | 1 | `A78A9BB90FDD61C4` | 2 |
| Azurite | 12 | `76A46C4A896DFBDF` | 11 |
| ExampleCore | 1 | `4A209AD52EDD4A5B` | 1 |
| ExampleDependent | 1 | `3E387C762544C3A7` | 1 |
| ExampleStandalone | 1 | `82620F29C8F75B37` | 1 |
| RukariLib | 2 | `7413B9534A309F7B` | 2 |
| 人物配音支持 | 1 | `06C4FE4F87282258` | 1 |
| 可更改的主菜单音乐 | 1 | `5C56D01047FC6CAB` | 1 |
| 更多的画面效果 | 3 | `0117A9630160739C` | 1 |
| 脚本区间计时器 | 1 | `C504FBABAF1B1FE3` | 2 |
| 芙兰的动态背景兼容 | 1 | `779484939C616E22` | 1 |
| 芙兰的延时立绘与差分控制 | 1 | `6DB74B5B48F13E2E` | 1 |
| 音效汉化 | 1 | `29CA0E83813704E0` | 1 |
| SfxFolderMod（仅 LocalLow 配置目录） | 0 | — | 1 |
| 用户域（设置 7 + 存档/工程 31） | — | — | 38 |

> 代表 DLL 为该 Mod 排序后第一个 `mod-dll` 条目；完整哈希、大小、时间戳见基线 json `entries`。

### 3.2 排除项清单及理由

| 排除对象 | 位置 | 排除理由 |
|---|---|---|
| AAVE 自身目录 `AAVideoExport` | `mods\AAVideoExport\`（含历史 0.2.4–0.2.12 各版本） | AAVE 自有载荷，不属于“其他 Mod”；该目录 310 个文件 / 2,817,293,479 字节，不计入第三方基线 |
| 验收夹具 / 测试 harness 名模式 | `UnknownPassiveMod`、`UnknownClockMod`、`UnknownRenderOwnerMod`、`AAVideoExport.NativePath`、名称后缀 `.Tests` | 夹具与 harness 是验收工具，不是第三方 Mod；本次 `F:\AzureArchive_101` 安装目录未命中上述名称 |
| 引擎缓存目录 | LocalLow `com.unity.addressables` | Unity Addressables 缓存，运行期可变，不纳入 Mod 不变性比对 |
| AAVE 输出备份目录 | LocalLow `VideoBackup` | AAVE 自身导出/备份产物，不属于第三方载荷 |
| 运行期日志 | LocalLow `Player.log`、`Player-prev.log` | 每次启动重写的运行日志，不纳入哈希比对 |
| mods 下非配置资产 | 本次第三方 165 文件中，范围外 105 个：`.md` 38、`.png` 21、`.sample` 14、无扩展名 16、`.cs` 4、`.csproj` 2、`.pdb` 2、`.ps1` 2、`.gitignore` 1、`.idx` 1、`.pack` 1、`.props` 1、`.rev` 1、`.sha256` 1 | 当前快照范围限定 DLL + 配置扩展名（`.json/.txt/.cfg/.ini/.xml/.toml/.yaml/.yml`）；其余资产列入第 5 节缺口 |
| 引擎/导出缓存 | `E:\AAcache` | 缓存目录，未扫描 |
| 工程回收站 | `E:\AAdata\data\projects\.aap-trash\` | 已删除工程残留，不纳入用户域比对 |

## 4. 工具验证记录

### 4.1 自比 PASS（同一快照自比，退出码 0）

命令（文件对文件，不重新扫描）：

```powershell
pwsh -NoProfile -File .\tools\Get-ThirdPartyModHashSnapshot.ps1 `
  -AAInstallPath 'F:\AzureArchive_101' `
  -Compare 'artifacts\zero-per-mod\g5-baseline-20261122-010540.json' `
  -AfterSnapshot 'artifacts\zero-per-mod\g5-baseline-20261122-010540.json'
```

记录：`artifacts/zero-per-mod/g5-compare-self-20261122-010609.log`

```text
G5 COMPARE: baseline=99 after=99 ADDED=0 REMOVED=0 CHANGED=0
EXIT-CODE=0
```

### 4.2 篡改副本 FAIL（退出码 1）

对临时副本 `artifacts/zero-per-mod/.g5-demo/tampered.json`（仅临时文件，真实基线快照未改动）做两处合成改动：一个已有 DLL 哈希/大小改写、一个虚构 `mods/FakeMod/Fake.dll` 新增。

记录：`artifacts/zero-per-mod/g5-compare-tampered-20261122-010624.log`

```text
G5 COMPARE: baseline=99 after=100 ADDED=1 REMOVED=0 CHANGED=1
  [CHANGED] mod-dll|mods/芙兰的动态背景兼容/5.2.0/FlandreScarletBackground.dll  before=779484939…313B1 after=0000…0000
  [ADDED]   mod-dll|mods/FakeMod/Fake.dll  before= after=AAAA…AAAA
EXIT-CODE=1
```

### 4.3 真机目录重扫 + 比对（扫描模式 `-Compare`，退出码 0）

对同一安装目录重新扫描生成 after 快照（`.g5-demo/after-rescan.json`），再与基线比对：

```powershell
pwsh -NoProfile -File .\tools\Get-ThirdPartyModHashSnapshot.ps1 `
  -AAInstallPath 'F:\AzureArchive_101' `
  -Output 'artifacts\zero-per-mod\.g5-demo\after-rescan.json' `
  -Compare 'artifacts\zero-per-mod\g5-baseline-20261122-010540.json'
```

记录：`artifacts/zero-per-mod/g5-compare-rescan-20261122-010957.log`

```text
G5 SNAPSHOT: dll=30 mod-config=31 user-setting=7 user-save=31 total=99 bytes=14962579 elapsedMs=399
G5 COMPARE: baseline=99 after=99 ADDED=0 REMOVED=0 CHANGED=0
EXIT-CODE=0
```

### 4.4 幂等性（输出已存在时报错，不覆盖）

对已存在的基线路径再次执行快照写入，脚本在扫描前报错退出，基线文件哈希未变。

记录：`artifacts/zero-per-mod/g5-idempotency-20261122-010635.log`

```text
Output snapshot already exists; refusing to overwrite: artifacts\zero-per-mod\g5-baseline-20261122-010540.json
EXIT-CODE=1
```

## 5. 当前证据缺口

1. **基线不是 PASS**：`g5-baseline-20261122-010540.json` 只是导出前快照。必须由主代理在真实导出前、后各跑一次快照，`-Compare` 零差异（ADDED=0 / REMOVED=0 / CHANGED=0，退出码 0）后，G5 该轮才算闭合。
2. **未覆盖 mods 下非配置资产**：本次第三方 165 文件中 105 个未纳入哈希范围，含 `.md` 38、`.png` 21、`.sample` 14、无扩展名 16、`.cs` 4、`.csproj` 2、`.pdb` 2、`.ps1` 2、`.gitignore` 1、`.idx` 1、`.pack` 1、`.props` 1、`.rev` 1、`.sha256` 1（图标/美术、文档、调试符号、源码与工程文件、打包索引/样本等）；若 G5 要求“全部第三方字节不变”，需扩展扩展名或改为逐文件全量清单。
3. **未扫描 `E:\AAcache`**：引擎/导出缓存目录未纳入；若缓存被导出流程改写，需单独界定是否属于 G5 范围。
4. **配置识别基于扩展名**：未按 Mod 私有目录约定做深度语义识别；新型 Mod 的非标准配置落点可能漏扫，需在真机验收时核对。
5. **本次未在真实导出前后成对执行**：现有比对仅为工具自比与合成篡改演示，不能作为 G5 通过证据。
6. **运行期改写不可见**：哈希门禁只证明文件字节前后一致；不覆盖内存态、注册表（除快照记录的恢复字段外）、或运行期生成后即删除的临时文件。

## 6. 可直接复制的操作命令

导出前（在真实导出开始前执行，仓库根目录下）：

```powershell
pwsh -NoProfile -File .\tools\Get-ThirdPartyModHashSnapshot.ps1 `
  -AAInstallPath 'F:\AzureArchive_101' `
  -Output 'artifacts\zero-per-mod\g5-before-<yyyyMMdd-HHmmss>.json'
```

导出后（同一次导出结束后执行，自动写 after 快照并与基线比对）：

```powershell
pwsh -NoProfile -File .\tools\Get-ThirdPartyModHashSnapshot.ps1 `
  -AAInstallPath 'F:\AzureArchive_101' `
  -Output 'artifacts\zero-per-mod\g5-after-<yyyyMMdd-HHmmss>.json' `
  -Compare 'artifacts\zero-per-mod\g5-before-<yyyyMMdd-HHmmss>.json'
```

判定：退出码 0 且 `ADDED=0 REMOVED=0 CHANGED=0` → 该轮 G5 哈希比对通过；退出码 1 → 按日志中的 ADDED / REMOVED / CHANGED 条目定位被改动的 Mod 载荷。

## 7. 本次未做的事（自证）

- 未启动 AA 或任何游戏进程；脚本只做目录枚举与文件哈希读取。
- 未写入 `F:\` 任何字节。扫描前后 `F:\AzureArchive_101\mods` 目录统计一致（只读数字自证）：

| 口径 | 扫描前 | 扫描后 |
|---|---:|---:|
| mods 全部文件数 | 475 | 475 |
| mods 全部字节数 | 2,829,252,068 | 2,829,252,068 |
| AAVE 自身（`AAVideoExport`）文件数 / 字节 | 310 / 2,817,293,479 | 310 / 2,817,293,479 |
| 第三方文件数 / 字节 | 165 / 11,958,589 | 165 / 11,958,589 |

- 未修改 `src\`；未执行 `dotnet build`；未改动 `artifacts\zero-per-mod\package-0.3.0` 旧包；未执行 git 操作；未联网上传、未打 tag、未发布。
- 未改动 `docs/zero-per-mod/EVIDENCE-MATRIX.md`。
