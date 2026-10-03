# AA 渲染协作协议 v1

本协议固定公开接口和交接时序，适用于 Azurite 与 AAVideoExport。内部字段、类实现、编码器版本和 UI 布局不是协议的一部分。兼容性由协议主版本决定，不以插件版本、DLL 哈希或 MVID 作为新版接口的接受条件。

## 公开接口

由 AAVideoExport.dll 提供，完整类型名为 `AAVideoExport.Integration.RenderControlV1`，所有成员均为 public static：

```csharp
int ProtocolVersion { get; }       // 必须为 1
bool IsReady { get; }              // 生命周期及恢复状态可供协调
bool IsRenderingOwned { get; }     // 面板、加载、捕获或清理仍持有渲染控制权
event System.Action BeforeAcquire;
event System.Action AfterRelease;
```

以上名称、参数、返回类型和含义为 v1 的固定契约。不得将事件类型改成 Il2CppSystem.Action，不得用字段替代属性。新增可选成员不影响 v1；改变既有语义应另建 v2 类型，保留 v1 直到消费者迁移。

## 交接顺序

1. 在 Unity 主线程首次接管前，提供者先令 `IsRenderingOwned=true`，同步发出 `BeforeAcquire`。
2. 消费者在该回调内恢复自己持有的帧率、垂直同步、按需绘制间隔、相机和预览纹理。回调不得调用新的接管操作。
3. 所有接管前回调成功返回后，提供者才读取 Unity 原始状态作为快照，并修改这些状态。回调失败则中止本次接管，不能继续读取错误快照。
4. 导出面板、准备、捕获及恢复可嵌套持有控制权；内部子阶段结束不等于交还。子阶段切换期间不能短暂发布 `false`。
5. 最后一个持有者确认全局状态恢复完成后，提供者令 `IsRenderingOwned=false`，再发出 `AfterRelease`。消费者重新开始观察空闲状态，不沿用导出前的空闲时间。
6. 恢复失败时不得发布“已经交还”的假状态；提供者应进入不可协调状态，消费者停止优化直到恢复或重启。

协议状态查询必须轻量、无渲染副作用。Unity 状态写入及交接事件发生在主线程；任务线程只处理文件、编码等异步工作。

## 发现、加载与卸载

Azurite 首先查找公开协议类型，验证 v1 签名后缓存委托。协议存在但版本或签名不符时，保守停止渲染优化，不能回退读取不稳定私有成员假装兼容。

未提供该公开接口的旧 AAVideoExport 仍可使用已有的受限旧适配器；只有旧适配器才需要核对已验证的二进制或渲染控制代码。

IsReady=false 时不得开始新的 Azurite 渲染优化。晚加载的 Azurite 必须先订阅再读取状态；发现导出已持有控制权时，不得恢复或覆盖导出已经写入的 Unity 状态。注销时移除同一托管委托实例，避免重复订阅或残留回调。

## 发布要求

- AAVideoExport 常规更新应保持 v1 类型和上述时序；改动界面、编码器、超分参数等不需要 Azurite 增加 DLL 哈希。
- 两边发布前运行协议生命周期测试和消费者测试：顺序、嵌套、取消、异常、关闭面板、卸载、晚加载、未知主版本、回调失败及解除订阅。
- 协议保证协作，不承诺 GPU 利用率或核心频率。Azurite 不设置 GPU 核心频率；滚动期间必须保持正常绘制，并覆盖滚轮事件、惯性与列表位置变化。

导出协议实现位于 `src/VideoExport.Plugin/RenderControlV1.cs`，并在面板、宿主和捕获作用域中接入。后续版本发布前须运行协议及恢复测试。
