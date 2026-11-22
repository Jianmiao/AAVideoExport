# G6「产品-only 路径 exact release DLL 安装清单」

- 审计编号：`g6-install-audit-20261122-010832`
- 仓库根：`E:\aamod\AAVE-zero-per-mod-20261121`
- 审计性质：**只读**。未执行 `dotnet build`、未启动 AA、未执行 git/gh/上传、未在 `F:\` 下创建或修改任何文件。
- 结论：**PARTIAL**。包内载荷正确 ≠ 已安装到 lab ≠ 纯产品运行闭环（见第 8 节）。
- 配套机器可读产物：
  - `artifacts\zero-per-mod\g6-install-audit-20261122-010832.json`
  - `artifacts\zero-per-mod\g6-install-audit-20261122-010832.log`

判定图例：`= 当前构建`（哈希与候选 Release 一致）/ `≠ 当前构建`（哈希不一致）/ `缺失`（应在而不在）/ `多出`（载荷中不应出现的文件）。所有哈希均为本轮用 `Get-FileHash`（SHA256， zip 条目从流计算）实际复核，未从旧记录抄写。

---

## 1. 候选构建基准（reference）

| 项 | 值 |
| --- | --- |
| `manifest.json` 版本 | `0.3.0`（仓库根与包内 payload 一致，SHA256 `5C52B623423CBE854931002DA68318C2C7DDBA2CC6004CD24FD89201806A9865`，333 字节） |
| Plugin `AAVideoExport.dll` | `D1E849484D3BA535302ADB34BB328E1DB9CCC41D87E42C3F18FFB00BA3B87BB5`（498176 字节） |
| Core `AAVideoExport.Core.dll` | `E565807696588C679331F4EFA49BE549B41E0098EACC1C7A24504E19C66ABCF6`（385024 字节） |
| Core 二次输出交叉核对 | `src\VideoExport.Core\bin\Release\net6.0\AAVideoExport.Core.dll` 哈希同为 `E5658076…ABCF6`，与 Plugin 输出目录内的 Core 副本一致 |

基准程序集取自当前 Release 输出目录；Plugin/Core 与给定候选哈希逐一比对，结果均为 **MATCH**。

## 2. 产品-only 最小安装清单

安装目标根目录：`mods\AAVideoExport\0.3.0\`（相对 AA 安装目录）。下表为运行所需的最小文件集合；9 个运行时程序集 + 1 个清单元数据文件。

| # | 目标路径（相对 `mods\AAVideoExport\0.3.0\`） | 源文件绝对路径 | 字节大小 | SHA256 | 角色 |
| --- | --- | --- | --- | --- | --- |
| 1 | `AAVideoExport.dll` | `E:\aamod\AAVE-zero-per-mod-20261121\src\VideoExport.Plugin\bin\Release\net6.0\AAVideoExport.dll` | 498176 | `D1E849484D3BA535302ADB34BB328E1DB9CCC41D87E42C3F18FFB00BA3B87BB5` | Plugin 入口程序集 |
| 2 | `AAVideoExport.Core.dll` | `E:\aamod\AAVE-zero-per-mod-20261121\src\VideoExport.Core\bin\Release\net6.0\AAVideoExport.Core.dll` | 385024 | `E565807696588C679331F4EFA49BE549B41E0098EACC1C7A24504E19C66ABCF6` | Core 逻辑程序集 |
| 3 | `Vortice.Direct3D11.dll` | `E:\aamod\AAVE-zero-per-mod-20261121\src\VideoExport.Plugin\bin\Release\net6.0\Vortice.Direct3D11.dll` | 346624 | `B4F1C83F1DCFCAC11EEB54FCEA930E9907BDDE7E3C352D1406D74C558E3058D6` | 第三方运行时 |
| 4 | `Vortice.DXGI.dll` | `E:\aamod\AAVE-zero-per-mod-20261121\src\VideoExport.Plugin\bin\Release\net6.0\Vortice.DXGI.dll` | 92672 | `440F6B01B2B088CFE1B950501E6910F9E100BE7CD78A1E70E4FC42006F41F18D` | 第三方运行时 |
| 5 | `Vortice.DirectX.dll` | `E:\aamod\AAVE-zero-per-mod-20261121\src\VideoExport.Plugin\bin\Release\net6.0\Vortice.DirectX.dll` | 268288 | `7EB1371548EAF6E7F429BB310D2EDB4782AC0E98A328CFBD93B3C2DDAD59D838` | 第三方运行时 |
| 6 | `Vortice.D3DCompiler.dll` | `E:\aamod\AAVE-zero-per-mod-20261121\src\VideoExport.Plugin\bin\Release\net6.0\Vortice.D3DCompiler.dll` | 23552 | `4CCEA9AA2425CD9A91B1B7FB6C3C28939C179EBB46A5A72D0E3337D27F5546C5` | 第三方运行时 |
| 7 | `Vortice.Mathematics.dll` | `E:\aamod\AAVE-zero-per-mod-20261121\src\VideoExport.Plugin\bin\Release\net6.0\Vortice.Mathematics.dll` | 104960 | `5237F3211CA3104DADD777CF7DFB488A20AB602694929C647CBE0DDDFC5DFD7D` | 第三方运行时 |
| 8 | `SharpGen.Runtime.dll` | `E:\aamod\AAVE-zero-per-mod-20261121\src\VideoExport.Plugin\bin\Release\net6.0\SharpGen.Runtime.dll` | 76800 | `7DEA05F2B06719E6DDF3CB2D852633E3CF5AFB3BCC9E1F10E9AF5F36CB408EAB` | 第三方运行时 |
| 9 | `SharpGen.Runtime.COM.dll` | `E:\aamod\AAVE-zero-per-mod-20261121\src\VideoExport.Plugin\bin\Release\net6.0\SharpGen.Runtime.COM.dll` | 170496 | `74BA6A25ECDD48F7F90465BFFE038DC2FB09CC861C1F1C257D038F11FB4E9732` | 第三方运行时 |
| 10 | `manifest.json` | `E:\aamod\AAVE-zero-per-mod-20261121\manifest.json` | 333 | `5C52B623423CBE854931002DA68318C2C7DDBA2CC6004CD24FD89201806A9865` | Mod 清单元数据（版本 `0.3.0`） |

非运行时、不参与程序集身份判定的随附物料（允许出现在包内，不属于上述 9 个运行时程序集）：`icon.png`、`README.md`、`CHANGELOG.md`、`THIRD_PARTY_NOTICES.md`、`LICENSE`、`docs\`、`licenses\`；full bundle 另含 `tools\ffmpeg.exe`、`tools\ffprobe.exe` 及 `licenses\FFmpeg\` 文档/二进制。

## 3. 绝不能出现的文件 / 目录

下列任何一项出现在产品安装集（`mods\AAVideoExport\0.3.0\` 或 BepInEx 插件加载路径）即违反 G6「exact release build，无审计/测试插件」规则：

| 禁止类别 | 匹配规则 | 本轮观测结论 |
| --- | --- | --- |
| 测试程序集 | 任何 `*Tests*.dll`（含 `AAVideoExport.NativePath.Tests.dll` 及其余 Tests 夹具） | **lab 插件目录现存**：`AAVideoExport.NativePath.Tests\AAVideoExport.NativePath.Tests.dll`（1199104 字节，`7E6A9E516A889D16369F72DD87645256E78AD8CA4C680432E2A9C4AF507BB0C8`）；三处 bundle zip 内 **0 个**此类条目 |
| Unknown Mod 夹具 | `UnknownPassiveMod.dll`、`UnknownClockMod.dll`、`UnknownRenderOwnerMod.dll` | **lab 插件目录现存** `UnknownPassiveMod.dll`（4608 字节，`752E2DF953F5B72D7B016E74A5850676EABAA33920EE8302C6D8A84BF8CA4E6D`）、`UnknownClockMod.dll`（5120 字节，`0AC66C9EA98769784E9D9BAD6BD6D1BA75A65BF17060ABBE202A6EC78BA9FEAB`），且各在同名子目录下有一份副本；`UnknownRenderOwnerMod.dll` **未出现**；bundle zip 内 **0 个**此类条目 |
| Bootstrap 程序集 | `AAVideoExport.RuntimeBootstrap.dll` | 所有 payload 与 bundle zip 内 **未出现**（源码工程 `src\VideoExport.RuntimeBootstrap\` 的本地 build 产物不进入发布包） |
| Bootstrap 目录 | `bootstrap\` 目录及其内容 | 所有 payload 与 bundle zip 内 **未出现** |
| 调试符号 | 任何 `*.pdb` | 仅存在于 Release 构建输出目录（见 4.1），属于 build 中间产物；**三个 bundle zip 内 0 个 pdb**；打包脚本必须排除，不得进入安装集 |

## 4. 现状审计（逐处只读核对）

### 4.1 当前 Release 输出目录（a）

Plugin 工程目录：`src\VideoExport.Plugin\bin\Release\net6.0\`；Core 工程目录：`src\VideoExport.Core\bin\Release\net6.0\`。

| 文件 | Plugin 输出目录判定 | Core 输出目录判定 |
| --- | --- | --- |
| `AAVideoExport.dll` | `=` 当前构建（`D1E84948…B87BB5`，498176） | 缺失（Core 工程输出本就不含 Plugin，属正常） |
| `AAVideoExport.Core.dll` | `=` 当前构建（`E5658076…ABCF6`，385024） | `=` 当前构建（同哈希） |
| `Vortice.Direct3D11.dll` | `=`（`B4F1C83F…3058D6`） | 缺失（正常，第三方依赖随 Plugin 输出） |
| `Vortice.DXGI.dll` | `=`（`440F6B01…41F18D`） | 缺失（正常） |
| `Vortice.DirectX.dll` | `=`（`7EB13715…59D838`） | 缺失（正常） |
| `Vortice.D3DCompiler.dll` | `=`（`4CCEA9AA…5546C5`） | 缺失（正常） |
| `Vortice.Mathematics.dll` | `=`（`5237F321…5DFD7D`） | 缺失（正常） |
| `SharpGen.Runtime.dll` | `=`（`7DEA05F2…08EAB`） | 缺失（正常） |
| `SharpGen.Runtime.COM.dll` | `=`（`74BA6A25…4E9732`） | 缺失（正常） |

构建输出目录中的其余文件：`AAVideoExport.pdb`（134240 字节，`B8168A793A9697BD6DB1E53A311F00F851357CD30ABD2060976A9E5BBE581846`）、`AAVideoExport.Core.pdb`（71520 字节，`E6A872892B9F0884FAB5468428E14A6BD92E03228B0D20BE58E79914AAEA0ED5`）、`AAVideoExport.deps.json` / `AAVideoExport.Core.deps.json`。pdb 为 build 产物，**打包时必须排除**；deps.json 不属于产品安装清单。

### 4.2 STAGING lite / full bundle（b，新建预演包）

解包 payload 根：`AAVideoExport\0.3.0\`；zip 内前缀：`AAVideoExport/0.3.0/`。

| 站点 | 路径 | 9 个程序集判定 | 包内 manifest 版本 | 包体 SHA256 |
| --- | --- | --- | --- | --- |
| STAGING lite 解包 | `artifacts\release-0.3.0-draft\staging\20261122-002603-lite\bundle-0.3.0-9da00c42d2b84ba4b2ec512e4f8ed397\AAVideoExport\0.3.0\` | 9/9 `=` 当前构建 | `0.3.0` | — |
| STAGING lite zip | `artifacts\release-0.3.0-draft\staging\20261122-002603-lite\AAVideoExport-0.3.0-win-x64-lite-bundle.zip` | 9/9 `=`（条目流内哈希） | `0.3.0` | `FE81F1D194913A2A8C9F614435CA9FA11C7B86D484CD27BFE1E43FAC8622EDE1` |
| STAGING full 解包 | `artifacts\release-0.3.0-draft\staging\20261122-002603-full\bundle-0.3.0-203b65f7ed464f9489f4da47b3ba390c\AAVideoExport\0.3.0\` | 9/9 `=` 当前构建 | `0.3.0` | — |
| STAGING full zip | `artifacts\release-0.3.0-draft\staging\20261122-002603-full\AAVideoExport-0.3.0-win-x64-full-bundle.zip` | 9/9 `=`（条目流内哈希） | `0.3.0` | `97B75821AB0AA98F6B78E7E7943581116E25A694EA6EEBFA6A5D94BA6A7ACC3C` |

两个 zip 的**全部条目**（不含 payload 前缀限制）扫描结果：pdb / Tests dll / Unknown 夹具 / RuntimeBootstrap / `bootstrap/` 命中数均为 **0**。lite 包非运行时物料 19 项，full 包 60 项（含 FFmpeg 文档与工具二进制），均与当前构建物料一致。

### 4.3 旧包 `artifacts\zero-per-mod\package-0.3.0\`（c，陈旧，勿动）

| 文件 | 解包目录判定 | zip 条目判定 | 实际 SHA256 | 期望 SHA256 | 实际/期望大小 |
| --- | --- | --- | --- | --- | --- |
| `AAVideoExport.dll` | `≠` 当前构建 | `≠` 当前构建 | `DD35E888E1FFAEE7D42F8A6207A8F081F9A8048310FC235038DC8BDAAA2AED94` | `D1E84948…B87BB5` | 781824 / 498176 |
| `AAVideoExport.Core.dll` | `≠` 当前构建 | `≠` 当前构建 | `D202A7E733A1E7435AB002DDEAAE31458229784C91CB4464F00C6D526737F5B0` | `E5658076…ABCF6` | 386560 / 385024 |
| 7 个第三方运行时 | `=` 当前构建 | `=` 当前构建 | 与第 2 节基准逐一相同 | 同左 | 一致 |

- 旧包 zip：`artifacts\zero-per-mod\package-0.3.0\AAVideoExport-0.3.0-win-x64-lite-bundle.zip`，包体 SHA256 `86B520C7AF4233E4CE28DBCD5BF9B2C7884984193989BABA99353ABE9B636B9E`。
- 旧包内 `SHA256SUMS.txt`（`DCAF4DB14CF528DFC0AB9E2B7983146626ACE73E715F4695AA9E313C517640B4`）与当前 lite 包清单（`C5AF56507484EB221B28142EDF6BBB5C2AECDA8E1934DDBFB8E9E43BCF59146C`）不一致，属陈旧物料。
- 生成时间 2026-11-21 20:36，早于当前 Plugin 重建；旧包内同样无 pdb / Tests / Unknown / Bootstrap 条目。

### 4.4 隔离 lab 安装目录（d，只读，未写入、未启动 AA）

目标：`F:\AzureArchive_AAVE03Lab\mods\AAVideoExport\0.3.0`（`Test-Path = True`）。

| 范围 | 判定 |
| --- | --- |
| `mods\AAVideoExport\0.3.0\` 内 9 个程序集 | 9/9 静态 `=` 当前构建（Plugin `D1E84948…B87BB5`、Core `E5658076…ABCF6`，7 个第三方与基准一致）；`manifest.json` 版本 `0.3.0` |
| `SHA256SUMS.txt` | `≠` 当前 lite 包清单（实际 `DCAF4DB1…17640B4`，期望 `C5AF5650…59146C`），陈旧 |
| `tools\ffmpeg.exe`（175056384 字节，`FE946E77D795AC07DC9926C5E498F083B0A98CF9ACA8A14D7FD2E9CEF19EF062`）、`tools\ffprobe.exe`（174858752 字节，`68F9AD8BED9DBFE1D7D490F907C652B42FD9B8704D40D8DA1DB5C2D938450A58`） | 非运行时旧版工具，与当前 full 包对应文件哈希/大小不一致；当前 full 包内为 131008000 字节 `3CDC2434…0FD388D`、130870272 字节 `4C9E0AA2…75F16BA`。不影响程序集身份判定，正式安装时以当前包覆盖 |
| `F:\AzureArchive_AAVE03Lab\BepInEx\plugins\` | **5 个禁止项**（见第 3 节）：Tests harness ×1、`UnknownPassiveMod.dll` ×2（根目录 + 子目录副本）、`UnknownClockMod.dll` ×2（根目录 + 子目录副本）；无 `UnknownRenderOwnerMod.dll` |

读取证据：lab 目录完整文件列表快照（相对路径 / 字节数 / 最后写入时间 / SHA256）已写入配套 JSON 的 `sites.lab-install` 与 `sites.lab-plugins`，以及同名 `.log` 的 `[lab-install]`、`[lab-plugins]` 段落。本轮对 `F:\` 仅执行 `Test-Path`、`Get-ChildItem`、`Get-FileHash`，未创建/修改/删除任何文件，未启动 `AzureArchive.exe`。主仓库 AA 安装目录 `F:\AzureArchive_101` 按范围未纳入本次扫描。

## 5. 差异账本（所有不等于当前候选构建 / 禁止出现的文件）

| # | 站点 | 文件（相对路径） | 实际 SHA256 | 期望 SHA256 | 判定 | 影响 | 必须重打包 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | release-plugin | `AAVideoExport.pdb` | `B8168A793A9697BD6DB1E53A311F00F851357CD30ABD2060976A9E5BBE581846` | —（禁止项，无期望哈希） | 禁止项（build 输出） | 调试符号属 build 中间产物；若进入发布包/安装集即违反 exact release 要求 | 否（打包阶段排除即可，不需重建二进制） |
| 2 | release-plugin | `AAVideoExport.Core.pdb` | `E6A872892B9F0884FAB5468428E14A6BD92E03228B0D20BE58E79914AAEA0ED5` | — | 禁止项（build 输出） | 同上 | 否（打包阶段排除） |
| 3 | release-core | `AAVideoExport.Core.pdb` | `E6A872892B9F0884FAB5468428E14A6BD92E03228B0D20BE58E79914AAEA0ED5` | — | 禁止项（build 输出） | 同上 | 否（打包阶段排除） |
| 4 | legacy-extracted | `AAVideoExport.dll` | `DD35E888E1FFAEE7D42F8A6207A8F081F9A8048310FC235038DC8BDAAA2AED94` | `D1E849484D3BA535302ADB34BB328E1DB9CCC41D87E42C3F18FFB00BA3B87BB5` | `≠` 当前构建 | Plugin 身份对不上：以该载荷安装时，运行期 `provider_sha256` 与候选不一致，导出/上传即载荷身份错误，G6 无法证明 exact release build | **是**（旧包整体重建） |
| 5 | legacy-extracted | `AAVideoExport.Core.dll` | `D202A7E733A1E7435AB002DDEAAE31458229784C91CB4464F00C6D526737F5B0` | `E565807696588C679331F4EFA49BE549B41E0098EACC1C7A24504E19C66ABCF6` | `≠` 当前构建 | Core 身份对不上：Plugin 依赖的 Core 与候选不一致，行为/哈希无法对齐，G6 无法闭合 | **是**（旧包整体重建） |
| 6 | legacy-zip | `AAVideoExport.dll`（zip 条目） | `DD35E888…2AED94` | `D1E84948…B87BB5` | `≠` 当前构建 | 同 #4；zip 内载荷与解包目录一致陈旧 | **是**（旧包整体重建） |
| 7 | legacy-zip | `AAVideoExport.Core.dll`（zip 条目） | `D202A7E7…737F5B0` | `E5658076…ABCF6` | `≠` 当前构建 | 同 #5 | **是**（旧包整体重建） |
| 8 | lab-plugins | `UnknownClockMod.dll` | `0AC66C9EA98769784E9D9BAD6BD6D1BA75A65BF17060ABBE202A6EC78BA9FEAB` | —（禁止项） | 禁止项（已安装） | 产品-only 安装中出现 Unknown 夹具，违反 G6「无测试/审计插件」规则 | 否（打包项之外；GO 前从插件目录移除/隔离，本审计不执行删除） |
| 9 | lab-plugins | `UnknownPassiveMod.dll` | `752E2DF953F5B72D7B016E74A5850676EABAA33920EE8302C6D8A84BF8CA4E6D` | —（禁止项） | 禁止项（已安装） | 同 #8 | 否（GO 前清理） |
| 10 | lab-plugins | `AAVideoExport.NativePath.Tests\AAVideoExport.NativePath.Tests.dll` | `7E6A9E516A889D16369F72DD87645256E78AD8CA4C680432E2A9C4AF507BB0C8` | —（禁止项） | 禁止项（已安装，测试 harness） | 真机运行由测试 harness 驱动，不能作为 G6 纯产品证据 | 否（GO 前清理） |
| 11 | lab-plugins | `UnknownClockMod\UnknownClockMod.dll` | `0AC66C9E…BA9FEAB` | —（禁止项） | 禁止项（已安装，副本） | 同 #8 | 否（GO 前清理） |
| 12 | lab-plugins | `UnknownPassiveMod\UnknownPassiveMod.dll` | `752E2DF9…8CA4E6D` | —（禁止项） | 禁止项（已安装，副本） | 同 #8 | 否（GO 前清理） |
| 13 | legacy-extracted | `SHA256SUMS.txt` | `DCAF4DB14CF528DFC0AB9E2B7983146626ACE73E715F4695AA9E313C517640B4` | `C5AF56507484EB221B28142EDF6BBB5C2AECDA8E1934DDBFB8E9E43BCF59146C` | 非运行时漂移 | 包内校验清单对应旧载荷；重建包时必须按新 9 个程序集哈希重新生成 | **是**（随重建包一并刷新） |
| 14 | legacy-zip | `SHA256SUMS.txt`（zip 条目） | `DCAF4DB1…17640B4` | `C5AF5650…59146C` | 非运行时漂移 | 同 #13 | **是**（随重建包一并刷新） |
| 15 | lab-install | `SHA256SUMS.txt` | `DCAF4DB1…17640B4` | `C5AF5650…59146C` | 非运行时漂移 | lab 内清单仍对应旧载荷；重新安装当前包后应以新清单覆盖 | 否（随正式安装覆盖；本轮不写入 lab） |

## 6. 必须重打包项与旧包处置

### 6.1 必须重打包 / 刷新的项

1. **旧包 Plugin/Core 载荷陈旧，必须从当前 Release 重建 lite 与 full bundle**：以第 2 节 9 个程序集基准为准整体重打包（不是只替换单个 DLL），重建后逐条复核 zip 条目哈希与本清单一致，并重新生成包内 `SHA256SUMS.txt`。
2. **打包排除项固化进打包脚本**：`*.pdb`、`*Tests*.dll`、`UnknownPassiveMod.dll` / `UnknownClockMod.dll` / `UnknownRenderOwnerMod.dll`、`AAVideoExport.RuntimeBootstrap.dll`、`bootstrap\` 目录，不得进入任何 bundle payload。
3. **lab 插件目录清理（GO 前置，非打包动作）**：G6 纯产品运行前，从 `F:\AzureArchive_AAVE03Lab\BepInEx\plugins\` 移除 `AAVideoExport.NativePath.Tests\`、`UnknownPassiveMod\`、`UnknownClockMod\` 及其根目录同名 DLL；本审计只读，未执行删除。
4. **lab 非运行时物料刷新**：正式安装时以当前 full/lite 包覆盖旧 `tools\ffmpeg.exe` / `tools\ffprobe.exe` 与陈旧 `SHA256SUMS.txt`。

### 6.2 旧包处置结论

**旧包 `artifacts\zero-per-mod\package-0.3.0\` 不删除、不覆盖、不清理**：仅标注 `stale`（早期候选构建，2026-11-21 20:36），作为 G2 取证链与历史哈希对照保留。任何重建产物应写入新的 bundle 目录，不得回写旧包目录。

## 7. 产品-only 运行的可核验字段

要在运行日志中证明「exact release DLL」，必须**同时**登记 Plugin 与 Core 两个程序集的 SHA256：

| 建议字段名 | 对应文件 | 期望值（当前候选） |
| --- | --- | --- |
| `provider_sha256` | `AAVideoExport.dll`（Plugin） | `D1E849484D3BA535302ADB34BB328E1DB9CCC41D87E42C3F18FFB00BA3B87BB5` |
| `core_sha256` | `AAVideoExport.Core.dll`（Core） | `E565807696588C679331F4EFA49BE549B41E0098EACC1C7A24504E19C66ABCF6` |

当前真机记录（`artifacts\zero-per-mod\g2-generic-final-12/13/14/15\native-checks.log` 的 `PATH-CHECK STORY START`，以及早期 `g2-native-cancel-16\native-checks.log`）实际情况：

- 已登记字段：`provider`（路径）、`provider_sha256`、`harness_sha256`、`source_sha256_before`、`options_sha256`。
- 后期批次 `provider_sha256 = D1E84948…B87BB5`（与当前 Plugin 一致）；早期陈旧批次为 `DD35E888…2AED94`。
- **`core_sha256` 字段在所有真机批次中均缺失**；且每批都带 `harness_sha256`（测试 harness 驱动）。

这是 G6 未闭合的直接原因之一：运行日志无法同时证明 Plugin 与 Core 均为 exact release DLL，且现有运行不是纯产品路径。G6 通过时，产品-only 运行记录应同时出现 `provider_sha256` 与 `core_sha256`，二者与上表一致，且**不再出现** `harness_sha256` / 任何 Tests 或 Unknown 夹具加载记录。

## 8. 三层区分（避免把物料正确误判为运行闭环）

| 层级 | 当前状态 | 证据 |
| --- | --- | --- |
| ① 包内载荷正确 | **成立（仅物料层）**：STAGING lite/full 解包与 zip 内 9 个程序集均 `=` 当前候选，无禁止项，manifest `0.3.0` | 第 4.2 节；配套 JSON `staging-lite-*` / `staging-full-*` |
| ② 已安装到 lab | **静态层面成立、动态层面未证实**：lab `mods\AAVideoExport\0.3.0\` 9 个程序集哈希 `=` 当前候选；但插件目录仍残留 harness 与 Unknown 夹具，`SHA256SUMS.txt` 与工具二进制陈旧 | 第 4.4 节；配套 JSON `lab-install` / `lab-plugins` |
| ③ 纯产品运行闭环 | **未成立**：尚无产品-only（无 harness / 无审计插件）真机运行；运行记录缺 `core_sha256`；G2 同步矩阵本身仍 PARTIAL | 第 7 节；`docs\zero-per-mod\EVIDENCE-MATRIX.md` G2/G6 行 |

因此 G6 当前只能判 **PARTIAL**：物料对得上候选构建，不等于安装集干净，更不等于纯产品导出闭环已通过。

## 9. 只读复跑命令（PowerShell 7，GO 前可复跑）

下列脚本只读：对 Release 目录、STAGING zip（条目流内哈希，不落盘解包）、旧包、lab 目录逐文件计算 SHA256 并与基准比对；不构建、不启动 AA、不写 `F:\`。注：`release-core`（Core 工程二次输出）与 `lab-plugins`（插件目录，仅做禁止项扫描）本就不含完整 9 程序集，其 `MISSING` 行属预期，不参与载荷判定。

```powershell
# G6 read-only re-audit. Run from repo root: E:\aamod\AAVE-zero-per-mod-20261121
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = 'E:\aamod\AAVE-zero-per-mod-20261121'
$expected = @{
  'AAVideoExport.dll'        = 'D1E849484D3BA535302ADB34BB328E1DB9CCC41D87E42C3F18FFB00BA3B87BB5'
  'AAVideoExport.Core.dll'   = 'E565807696588C679331F4EFA49BE549B41E0098EACC1C7A24504E19C66ABCF6'
  'Vortice.Direct3D11.dll'   = 'B4F1C83F1DCFCAC11EEB54FCEA930E9907BDDE7E3C352D1406D74C558E3058D6'
  'Vortice.DXGI.dll'         = '440F6B01B2B088CFE1B950501E6910F9E100BE7CD78A1E70E4FC42006F41F18D'
  'Vortice.DirectX.dll'      = '7EB1371548EAF6E7F429BB310D2EDB4782AC0E98A328CFBD93B3C2DDAD59D838'
  'Vortice.D3DCompiler.dll'  = '4CCEA9AA2425CD9A91B1B7FB6C3C28939C179EBB46A5A72D0E3337D27F5546C5'
  'Vortice.Mathematics.dll'  = '5237F3211CA3104DADD777CF7DFB488A20AB602694929C647CBE0DDDFC5DFD7D'
  'SharpGen.Runtime.dll'     = '7DEA05F2B06719E6DDF3CB2D852633E3CF5AFB3BCC9E1F10E9AF5F36CB408EAB'
  'SharpGen.Runtime.COM.dll' = '74BA6A25ECDD48F7F90465BFFE038DC2FB09CC861C1F1C257D038F11FB4E9732'
}
$assemblies = $expected.Keys

function Audit-Dir($label, $dir) {
  if (-not (Test-Path -LiteralPath $dir)) { Write-Host "[$label] MISSING: $dir"; return }
  Write-Host "[$label] $dir"
  foreach ($a in $assemblies) {
    $p = Join-Path $dir $a
    if (-not (Test-Path -LiteralPath $p)) { Write-Host "  MISSING  $a"; continue }
    $h = (Get-FileHash -Algorithm SHA256 -LiteralPath $p).Hash
    $verdict = if ($h -eq $expected[$a]) { '= current' } else { '!= current' }
    Write-Host ("  {0,-10} {1,-30} {2}" -f $verdict, $a, $h)
  }
  Get-ChildItem -LiteralPath $dir -Recurse -File -Force |
    Where-Object { $_.Name -match '\.pdb$|Tests.*\.dll$|Unknown(Passive|Clock|RenderOwner)Mod|RuntimeBootstrap' } |
    ForEach-Object { Write-Host "  FORBIDDEN  $($_.FullName)" }
}

function Audit-Zip($label, $zip, $prefix) {
  if (-not (Test-Path -LiteralPath $zip)) { Write-Host "[$label] MISSING: $zip"; return }
  Write-Host "[$label] $zip"
  $z = [System.IO.Compression.ZipFile]::OpenRead($zip)
  try {
    foreach ($a in $assemblies) {
      $e = $z.Entries | Where-Object { $_.FullName -eq "$prefix$a" } | Select-Object -First 1
      if (-not $e) { Write-Host "  MISSING  $a"; continue }
      $s = $e.Open(); $sha = [System.Security.Cryptography.SHA256]::Create()
      try { $h = ([BitConverter]::ToString($sha.ComputeHash($s)) -replace '-','').ToUpper() } finally { $s.Dispose(); $sha.Dispose() }
      $verdict = if ($h -eq $expected[$a]) { '= current' } else { '!= current' }
      Write-Host ("  {0,-10} {1,-30} {2}" -f $verdict, $a, $h)
    }
    $bad = $z.Entries | Where-Object { $_.Name -match '\.pdb$|Tests.*\.dll$|Unknown(Passive|Clock|RenderOwner)Mod|RuntimeBootstrap|bootstrap/' }
    foreach ($b in $bad) { Write-Host "  FORBIDDEN  $($b.FullName)" }
  } finally { $z.Dispose() }
}

Audit-Dir 'release-plugin' "$root\src\VideoExport.Plugin\bin\Release\net6.0"
Audit-Dir 'release-core'   "$root\src\VideoExport.Core\bin\Release\net6.0"
Audit-Dir 'staging-lite'   "$root\artifacts\release-0.3.0-draft\staging\20261122-002603-lite\bundle-0.3.0-9da00c42d2b84ba4b2ec512e4f8ed397\AAVideoExport\0.3.0"
Audit-Dir 'staging-full'   "$root\artifacts\release-0.3.0-draft\staging\20261122-002603-full\bundle-0.3.0-203b65f7ed464f9489f4da47b3ba390c\AAVideoExport\0.3.0"
Audit-Zip 'staging-lite-zip' "$root\artifacts\release-0.3.0-draft\staging\20261122-002603-lite\AAVideoExport-0.3.0-win-x64-lite-bundle.zip" 'AAVideoExport/0.3.0/'
Audit-Zip 'staging-full-zip' "$root\artifacts\release-0.3.0-draft\staging\20261122-002603-full\AAVideoExport-0.3.0-win-x64-full-bundle.zip" 'AAVideoExport/0.3.0/'
Audit-Dir 'legacy-extracted' "$root\artifacts\zero-per-mod\package-0.3.0\bundle-0.3.0-0dfcc2af201647af99dc94cb3373550a\AAVideoExport\0.3.0"
Audit-Zip 'legacy-zip' "$root\artifacts\zero-per-mod\package-0.3.0\AAVideoExport-0.3.0-win-x64-lite-bundle.zip" 'AAVideoExport/0.3.0/'
Audit-Dir 'lab-install' 'F:\AzureArchive_AAVE03Lab\mods\AAVideoExport\0.3.0'
Audit-Dir 'lab-plugins' 'F:\AzureArchive_AAVE03Lab\BepInEx\plugins'
```

## 10. 审计边界与产物

- 本轮写入范围仅两处：本文件 `docs\zero-per-mod\G6-EXACT-RELEASE-DLL-MANIFEST.md`；`artifacts\zero-per-mod\g6-install-audit-20261122-010832.json` 与同名 `.log`。仓库其它文件未修改。
- 禁止动作均未执行：未 `dotnet build`、未启动 AA 进程、未执行 git/gh/上传、未在 `F:\` 下创建或修改文件。
- 机器可读产物包含：逐站点逐文件字节大小与 SHA256、`= / ≠ / 缺失 / 多出 / 禁止项` 判定、差异账本、重打包项、运行证据字段与结论 `PARTIAL`。
