# 当前匿名工厂的有限接管范围审计（2026-10-30）

本次只读取已安装二进制、源码和既有日志；未启动 AA，未修改生产准入，未把
observer 记录升级为认证。本文件给出下一迭代的可执行证据字段和检查步骤。

## 1. 本次直接核实的二进制与方法

文件：`F:\AzureArchive_101\BepInEx\core\Il2CppInterop.Runtime.dll`

- 程序集版本：`1.5.3.0`
- MVID：`ab9a457d-353f-4e53-b665-3c12b9f380d3`
- 文件 SHA-256：`65AB051A681C2C1E52DF7596FF738BE401AAE72CFF882FF18241CF54A3EA38A4`

方法原始 IL 字节由 PE 的 RVA/section 映射和 tiny/fat method header 解析取得，
SHA-256 不包含方法头和异常表。长度同时与 Cecil `Body.CodeSize` 核对一致。

| 方法 | Token / RVA | IL 字节数 | 原始 IL SHA-256 |
| --- | --- | ---: | --- |
| `Il2CppObjectBase/InitializerStore<T>.Create()` | `0x060009BA` / `0x000114E8` | 350 | `7BE08A77ADBD891CA4FE7F044F88FDFC561A65C4504569F0567C43EEBE3FA15F` |
| `InitializerStore<T>.get_Initializer()` | `0x060009BB` / `0x00011652` | 21 | `4EFC9F4E7E67909B4FDC6473F2C5F07402C50457D7BCBE91BE6B7995E0ADB6AB` |
| `Il2CppObjectBase..ctor(IntPtr)` | `0x06000307` / `0x0000607F` | 14 | `8CD3F085BF06B02E0053B77D342DA32C1A8921EF92A48914250B8A8D528DCFF1` |
| `Il2CppObjectBase.CreateGCHandle(IntPtr)` | `0x0600030B` / `0x000060DC` | 42 | `6D830107AB1951AC02B60C1D70EE0B0676C5410C9EDBB0B18495F51A5F06137A` |

`InitializerStore<T>` 的泛型 T 是 `NonVariant`，**没有泛型类型约束**。
`Il2CppClassPointerStore<T>` 的 T 同样没有约束。
`CreatedTypeRedirect` 是 **public static、非 readonly** 的 `System.Type` 字段。
`get_Initializer()` 惰性读取/缓存 `_initializer`；不经过再次 Create 即可返回旧 delegate。

### 工厂实际生成的分支

1. 实际目标 `targetType = CreatedTypeRedirect ?? typeof(T)`。
2. DynamicMethod 名字由原始 T 的 AssemblyQualifiedName 生成；**名字不等于实际
   redirect 后的目标类型证明**。DynamicMethod 返回类型则是实际 targetType。
3. 存在公开 `targetType..ctor(IntPtr)` 时，生成 `ldarg.0; newobj ctor; ret`。
4. 没有该构造器时，生成 `GetUninitializedObject(targetType)`、类型转换、
   `CreateGCHandle(pointer)`、`isWrapped` 赋值，以及存在时的目标无参构造器调用；
   需要保留并审查该分支完整的实际 IL，不应凭“也是 wrapper”概括放行。
5. 工厂最终创建 `Func<IntPtr,T>`。基类 `Il2CppObjectBase..ctor(IntPtr)` 调用
   `CreateGCHandle`；后者检查非零 pointer/isWrapped，并调用
   `IL2CPP.il2cpp_gchandle_new(pointer, false)` 保存 GC handle。

**结论与限制：**工厂的发射模板是有限且可检查的，但它能选取带额外行为的目标
构造器。单独固定工厂 DLL/IL 哈希、名字或 delegate 签名，不足以证明生成代码只有
包装行为。目标构造器、基类构造器、类型初始化器和所调用方法仍须审查。
本次没有调用这些 delegate，也没有把构造器存在视为构造器无副作用。

## 2. 已有运行证据与覆盖起点

证据：`artifacts/acceptance-20261029-07-bootstrap-fps-handoff/bootstrap-final.json`。
这是 **2026-10-29 的历史运行**，不是正在运行进程的内存快照。

- 总计 1,299 条记录；匿名 holder 有 96 条。
- 其中 94 条 creator 是实际 `InitializerStore<T>.Create`；每条都有 constructor
  与 CreateDelegate 事件。另 2 条来自测试插件 `BootstrapAttributionChecks.Emit`。
- 这说明当前可先审查一个具体的 interop factory 集合，而非从匿名程序集名称推断
  96 种未知 Mod 逻辑；也不能据此忽略其他生成途径。
- 开始快照列出 66 个已加载程序集，包括 BepInEx、Il2CppInterop、
  ModTheAzureArchive、Harmony/MonoMod 和系统库；没有 15 个普通 Mod，也没有匿名
  holder。没有匿名 holder 是缩小前置缺口的线索，不是所有早期动态代码均不存在的证明。
- 已有独立审计表明：当前 BepInEx 的 patcher Initialize 阶段完成后才执行 Finalizer，
  ModTheAzureArchive 从 Finalizer 安装普通 Mod 发现路径。见
  `artifacts/preloader-order-audit-20261029/installed-bootstrap-il.json`。
- 当前主安装 patcher 只有 `ModTheAzureArchive.dll`；实验副本额外有
  `AAVideoExport.RuntimeBootstrap.dll`。本次复核两份 ModTheAzureArchive 的 SHA 都是
  `9E3F26880208F0DAF7443ABB146D69EC6C549FCA7D60141A68B4DA8BF368FAE7`，
  bootstrap 是 `21C8FB6113BF8C785DC8D508DD22346694671A5893F6F026D492CAD16B5F8714`。

现有 `RuntimeObserver.Install()` 先装 11 个 observer hooks，再取得开始程序集清单，
最后把 `installed=true`。在此之前的 callback 直接返回；预加载框架初始化、patcher
构造/初始化及 observer 自身安装仍是明确前置缺口。开始清单只存 FullName，不存
各文件 SHA/MVID，更不等于其初始化代码已被验证。

旧记录全局 dropped/callback/install/rollback/reentrant counters 为 0，但存在
21 次 stack truncation 和 3 次 text truncation。不能把它称为零损失证明。

## 3. 现有 v1 证据缺少什么

`src/VideoExport.RuntimeBootstrap/RuntimeObserver.cs` 的 AttributionSnapshot 只含
id/name/holder、creator 字符串、三种事件计数、stack、delegate MethodInfo 类型/名字。
没有实际 ReturnType、参数类型、闭合 T、redirect、构造器 token/IL、factory 返回
delegate 的强关联 receipt、生成代码摘要或 patch 集合指纹。`GetAttributionJson` 的
弱引用 delegate 关联可复用，但普通 delegate 调用不会经过 `DynamicMethod.Invoke`。

因此 v1 API 的 `permitsStrictExport=false` 必须保持。应另加有限 factory receipt，
不要改写该字段来表示更多能力。

## 4. 下一迭代可实现的必要不变量

### 发现和实例 receipt

- 固定本节二进制、方法 token/IL，以及实际 .NET/Interop 支持库身份。启动时列出的
  实际 patcher/preloader 文件也记录 SHA/MVID；不用目录名或程序集前缀代替身份。
- 在精确 factory 的有类型入口/返回边界关联原始 T、生成的 DynamicMethod、实际
  targetType 与返回 delegate；stack/name 只作辅助，不单独作为 factory 身份。
- 记录 `T` 与 targetType 的完整闭合泛型身份、所属文件 SHA/MVID、继承链、
  实际 return/parameter 类型、所选构造分支；记录当时 redirect 的值并在租约中检查
  是否改变。优先使用 DynamicMethod 实际签名，不从 `Initializer<T>` 名字反推目标。
- 对 acquisition 前已缓存 `_initializer` 的闭合 T，只读取已有字段并关联到完整
  receipt；不要为“补证据”调用 getter 生成替代物。关联未知即标明缺口。

### 构造器行为递归检查

1. 从实际 targetType 所选 ctor 开始，定位准确 metadata token、原始 IL、字段与
   调用操作数；检查实际泛型参数和方法解析结果。
2. 递归检查 base ctor、该类型/基类需要执行的 `.cctor`、所有非基础数据操作调用。
   给已访问方法做 identity 集合，拒绝无法解释的递归/间接调用，报告准确边界。
3. 第一版只接受已证明的 IntPtr 包装构造链，允许最终落到上述已检查的 GC-handle
   管理叶节点；它是明确的 native 生命周期操作，不能标成“无 native 调用”。
4. fallback、额外字段初始化、任意 delegate/event 注册、线程/计时器、音频/场景/
   渲染写入等不能从工厂模板继承结论，必须各有具体解释或另行接管。未知目标不放行。
5. 不执行未知 ctor 来测试其“无副作用”。真实包装结果验证仅使用本次普通 native
   流程已产生的合法对象/pointer，并比较对象类型与包装语义，不用伪 pointer 探测。

### 生命周期与失效

- Scope 以 factory + target recipe/ctor closure 为单位，不以匿名 holder 为单位。
  收集首遍实际使用的集合；第二遍使用稳定的类型/构造 recipe identity 比较，不用
  native pointer 或进程内 ordinal 充当跨遍身份。
- 原始生成和 delegate 返回不能被替换。外来 factory/ctor/相关 emitter patch、
  redirect 或 cached delegate 改变、未归属生成、晚加载未知目标均使当前 receipt
  失效。已检查的新 framework T 可在受控准备阶段补完证据，不能在发布时默默扩范围。
- 证据需要直接采集不可截断的身份/hash字段，并记录本 scope 的 loss/overflow。
  若仍依赖当前全局截断 stack，则旧 run07 不能满足完整 proof；不能挑选零损失字段
  忽略其它证据缺口。
- 租约在两遍、取消、场景切换与 commit 前持续检查；发布线程只消费已封存且未失效
  的 receipt，不在 worker 临时读取 Unity/native 对象。不得只检查一次启动快照。

这些是针对当前受信任进程内组件的有限协调条件，不是任意未知代码的对抗式沙箱。

## 5. 有界验证清单

- 精确 factory 身份及预加载身份不符时拒绝 scope。
- 已知 IntPtr 包装 ctor 正例；有额外事件/音频/计时副作用 ctor 反例；无 T 约束不能
  意外让任意目标进入正例。
- redirect 为 null、合法已检查目标、未检查目标、获取后改变，分别验证结果。
- fallback/无参 ctor 路径单独记录，未实现递归证明时不得混入首版正例。
- CreateDelegate 有记录但构造记录缺失、scope 建立前 cached delegate、同名不同
  DynamicMethod、两个不同闭合 T，不能混淆。
- 真正 late foreign patch、late target/redirect 改变、observer overflow/丢失时，
  当前 native capture 能终止并恢复，已失效 receipt 不能跨 worker commit。
- 当前 94 个实例逐项产生 recipe 清单；不要求认证永远不会加载的未来目标。
- 最终以全 Default 当前 15 Mods 的真实两遍电影，逐帧事件/PCM/PTS、一致源资源、
  取消重试与 Azurite FPS/VSync 恢复收口；工厂元数据测试只是前置，不代替电影结果。

## 6. 避免无法产生证据的准入循环

不要要求“整个匿名程序集先通过完整电影”才允许验证当前 factory，也不要为试验把
生产 `CanAdmitStrictExport` 覆写为 true。可以在明确实验副本里做普通 native preload
与受控 effect rehearsal：使用实际对象/资源、已安装 controller、合法生成结果，
把捕获和媒体写到诊断暂存区且禁止正常输出发布，收集上述具体 receipt。

随后只让满足 exact identity + 已安装控制边界 + retained receipt 的当前集合进入
生产 strict 路径，输出 commit 继续要求本次完整两遍校验与恢复成功。这样开跑依据是
可执行控制能力，电影成功仍是交付依据；不通过名称豁免或吞掉未知调用来打破循环。
