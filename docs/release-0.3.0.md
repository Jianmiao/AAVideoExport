# AAVideoExport 0.3.0

这是 AAVideoExport 0.3.0 的生产构建与发布页。本版核心是把原先按具体 Mod 身份编写的适配逻辑移出生产路径，改为通用能力探测、通用时钟桥和通用准入策略。

**请先阅读“已知限制”：本版不是完整的零 per-Mod 动态兼容验收通过版本，G4 与 G6 仍未闭合，部分旧 per-Mod 效果在本版中不可用。**

## 构建身份

- 插件 `AAVideoExport.dll` SHA256：`B2FF1F617EE9EB76124443FF67B64A3DFE6CC4E91A457DD9A6629111296AF279`
- 核心 `AAVideoExport.Core.dll` SHA256：`E565807696588C679331F4EFA49BE549B41E0098EACC1C7A24504E19C66ABCF6`
- 版本：`0.3.0`
- 生产包 `AuditMode=false`，不含 audit bootstrap，不含 NativePath 测试 harness 或验收夹具。

## 本版验证结果

### G1 静态零 per-Mod：PASS

最终源码复跑 `tools/Test-ZeroPerModSource.ps1 -AAInstallPath F:\AzureArchive_101`：

- 扫描生产文件 106
- 实际安装 Mod 19
- 身份索引 81
- 违规 0
- `EXIT-CODE=0`

报告：`artifacts/release-0.3.0-final/logs/g1-static-final.json`、`g1-static-final.log`。

### G2 真实 AA 重复导出：PASS（含字体图集 1 LSB 例外）

`g2-generic-final-27-fontatlas`：

- 两次完整导出均为 450 帧 / 720,000 采样帧
- 6 passed / 0 failed
- 音频、事件、PTS 逐帧精确一致
- 导出画面为 authored-render 等价；UI 字形存在 font-atlas 路径的 1 LSB 级差异，因此**不是逐字节完全一致**
- 验收口径：`repeated-exact-audio-events-pts-and-authored-render-equivalence-with-font-atlas-exception`

### G3 未知时钟 Mod：PASS（证据来自 0F74 构建）

`g3-live-clock-04`：

- `UnknownClockMod` 的 `Test.Update` postfix 每 15 次输出 `event frame=0..465`
- `wall` 每 15 帧增加 5,000,000 ticks，与 `unity=0..14.966666` 同步
- bridge 日志：`wall clock resumed; 32 export reads`
- `PATH-CHECK RESULT 4 passed; 0 failed`

说明：该证据来自 `0F74` 构建（类型级 `BepInPlugin` 识别修复后）。最终 `B2FF` 相对 `0F74` 仅增加 AAVE 支撑程序集排除，理论上不影响桥接，但未在最终构建上重跑 G3。

### G4 未知 render-owner：未完成

`UnknownRenderOwnerMod` 仍只是引用 `Camera` / `RenderTexture` 的类型壳，没有实际独占渲染；尚无真实 AA 首帧前拒绝与“不输出错位电影”的证据。

### G5 第三方 Mod 哈希基线：已完成基线，非全量前后对照

- 99 文件 / 14,962,579 字节 / 15 Mod 归组
- 自比 PASS、篡改 FAIL 演示、幂等性验证完成
- 尚无最终发布包验收前后全量成对 `-Compare` 零差异证据

### G6 纯产品闭环：未完成

- 隔离 lab 仍有 5 个禁止项残留：Tests harness ×1、UnknownPassiveMod ×2、UnknownClockMod ×2
- 尚未完成无测试插件、无 harness 的纯产品 8 项矩阵运行
- 因此本版发布包不包含测试 harness；相关证据仅来自测试 harness 驱动的真实 AA 运行

## 安装

1. 下载 `AAVideoExport-0.3.0-win-x64-lite-bundle.zip`（不含 FFmpeg）或 `AAVideoExport-0.3.0-win-x64-full-bundle.zip`（含 `tools/ffmpeg.exe`、`tools/ffprobe.exe`）。
2. 解压后使用 PowerShell 7.2+ 执行只读计划：
   `pwsh -NoProfile -File .\Install-Mod.ps1 -AAInstallPath '你的 AA 安装目录' -PlanOnly`
3. 确认后去掉 `-PlanOnly` 安装；安装器会创建 `mods/AAVideoExport/0.3.0` 并切换当前 Profile 的 AAVideoExport 选择。
4. 回滚使用安装回执中的版本化 `Rollback-Mod.ps1`。

## 已知限制

- **旧 per-Mod 适配器已移出生产源码**：语音、BGM、动态背景、MoreEffects、菜单/启动边界、effects source 等旧适配类型在 `GenericCompatibilityStubs.cs` 中以 no-op 接缝保留类型和签名，**实际效果在本版中不可用**。这是本版最大的功能回退，不能视为与 0.2.12 功能等价。
- **G4/G6 未闭合**：未知 render-owner 的真实拒绝、纯产品无 harness 路径均未完成；本版不作“零 per-Mod 动态兼容整体通过”承诺。
- **G2 非逐字节一致**：UI 字体图集存在 1 LSB 差异，只满足 authored-render 等价口径。
- **G3 最终构建未复跑**：G3 证据来自 `0F74`，不是最终 `B2FF`。
- **G5 非全量前后对照**：仅有第三方 Mod 哈希基线与工具，未做最终包验收前后全量成对比较。
- **静态门禁边界**：G1 只扫描 `src` 下 `.cs` 字符串字面量，不覆盖运行期动态拼接、反射元数据或 IL 重写目标。

## 校验和

见 release 附件 `SHA256SUMS.txt`：

- lite bundle：`B6AFA3C5C2C5CD9920384B35A68B35EC6873606AE98E70113213A70D502C6ABC`
- full bundle：`18518898F58B32F64CC51C09390D7F9E06817796D4658D232A122625D1743A95`
- source zip：`4EFA27495F5DA8AA5B2A5F01EAEFBB21A720E07675C409E3F41FBE4777DCE15F`

## 源码

源码包 `AAVideoExport-0.3.0-source.zip` 包含 `src/`、`tools/`、`docs/`、`tests/`、`legacy/` 与 manifest/README/CHANGELOG/LICENSE 等；已排除 `.git`、`artifacts`、`bin`、`obj`、`*.user` 与 `local.props`。

---

本 release 只对上述已验证范围作陈述；未列出的行为不作为兼容性承诺。
