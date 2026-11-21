# MenuBgmSelector 原生边界证据（2026-10-25）

本次探针针对隔离 AA `artifacts/native-lab-20261025-13`，使用实际加载的
MenuBgmSelector 程序集执行一次只读 RenderControlV1 ownership 边界采样。
探针没有打开文件对话框、选择或切换 BGM、启动播放、修改配置或写入文件。

## 结果

- 原生结果：`PATH-CHECK RESULT 1 passed; 0 failed`。
- 证据：`artifacts/acceptance-20261025-menubgm-boundary-03/native-checks.log`。
- `MenuBgmSelector` 程序集：版本 `1.1.4.0`，加载类型数 `35`。
- RenderControlV1 ownership 前、中、后均观察到 `AudioSource=3`、活动数 `3`、播放数 `0`。
- 进程线程数三个采样点均为 `121`。
- 释放后 `RenderControlV1.IsRenderingOwned=false`，helper 正常退出（exit code 0）。

## 解释边界

线程数和 AudioSource 数量/播放状态是瞬时观察，不能证明任意线程属于
MenuBgmSelector，也不能证明它未来不会在 Story capture 中打开对话框、切换
BGM、启动播放或执行文件操作。因此该结果只证明当前 CatalogScene 中的
ownership 边界没有观测到播放变化，**不升级严格准入级别**；MenuBgmSelector
仍为 `Opaque`，必须继续完成选择/恢复、进入剧情、取消、重启和双遍音频/PTS
验证，或提供严格 DSP 事件适配器。

