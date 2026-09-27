# 国服移植前上游审计

审计对象固定为 [`21ae5ca9aa1fa3785baa540245b51efa346f9d37`](https://github.com/XeldarAlz/FFXIV-AutoMahjongSolver/commit/21ae5ca9aa1fa3785baa540245b51efa346f9d37)，提交时间 `2026-07-20T12:14:45Z`。本文描述这个上游基线，**不表示当前国服移植包实际启用了这些路径**。下面的行号均指该提交，后续本地修改可能改变行号。

审计日期：2026-09-23。证据分为“代码确认”“上游作者/问题报告声明”“本次国服实测”。最后一类目前没有证据，不能相互替代。

## 项目与发布状态

- 仓库地址为 `XeldarAlz/FFXIV-AutoMahjongSolver`；README 和工程中的旧名 `FFXIV-DomanMahjongSolver` 在 GitHub 重定向到它。
- 基线 README 顶部明确称当前版本不稳定、等待重写。兼容表仅列 EU/NA/JP/OC，没有 CN；该兼容表是上游自述，不是本次验证。
- 本次 GitHub REST 查询返回 43 个 release；最新为 [`v0.1.2.3`](https://github.com/XeldarAlz/FFXIV-AutoMahjongSolver/releases/tag/v0.1.2.3)，发布时间 `2026-05-27T11:46:37Z`，对应 tag 不等于本次采用的 HEAD。`Directory.Build.props:18` 版本也为 `0.1.2.3`。
- API 的 issue 列表包含 PR，共 46 条；相关 issue 目前标记 closed。**关闭不等于当前源码功能完整或国服验证通过**。
- 以上是审计日期的查询结果；当前状态可在上游 [发布页](https://github.com/XeldarAlz/FFXIV-AutoMahjongSolver/releases) 和 [问题页](https://github.com/XeldarAlz/FFXIV-AutoMahjongSolver/issues) 复核。仓库不保存无构建用途的整份 API 应答。

## 架构及复用边界

| 部分 | 上游入口 | 可复用性/限制 |
|---|---|---|
| 游戏对象查找 | `Mahjong.Plugin.Dalamud/GameState/MahjongAddon.cs:12` | 只枚举 `Emj`、`EmjL`；名字本身不证明区域或布局兼容 |
| 生命周期和采集 | `GameState/AddonEmjReader.cs:65`、`:300` | 注册 PostSetup、PreFinalize、PostRefresh、PostReceiveEvent，并轮询；基于 Dalamud/ClientStructs ABI |
| 布局及读取 | `GameState/Variants/BaseEmjVariant.cs:44`、`:80`、`:96`；`data/layouts` | JSON profile 指定偏移，读取原始 addon 内存；不可直接复制为 CN profile |
| 副露补全 | `GameState/MeldTracker.cs:94` | 根据手牌减少/弃牌计数变化推断自己的副露；不是真实副露结构体读取 |
| 快照和计算 | `GameState/StateAggregator.cs:64`、`:95` | 缓存 `StateSnapshot`、`DiscardScorer.Score` 和 `IPolicy.Choose`；须先验证完整性 |
| 决策引擎 | `Mahjong.Core`、`Mahjong.Engine`、`Mahjong.Policy.Abstractions`、`Mahjong.Policy`、`Mahjong.Rules` | 不依赖游戏内存；可以直接复用并独立测试 |
| 界面 | `UI/MainWindow.cs`、`HandOverlay.cs`、`SettingsWindow.cs` | 上游英文界面/提示；读出数据与手牌显示位置不总是一致 |
| 自动动作 | `Actions/AutoPlayLoop.cs`、`InputDispatcher.cs` | 依赖状态码、节点、按钮排序、回调协议；全部需要目标版本证据 |
| 诊断与网络 | `Telemetry/*`、`Logging/*`、`GameState/InputEventLogger.cs` | 上游有默认后台上传、原始内存 dump 和启用的 hook；不能不加审计地复用 |

建议 CN 和国际服共享 `StateSnapshot`/规则/决策引擎，分别实现版本限定的读取适配器与动作适配器。每个字段携带“已证实/未知/矛盾”状态；未知座风不能写成“东且已知”。动作适配器需要每类操作独立证据与许可状态。诊断入口应独立于读取是否已适配，不加载未证实的原生 hook。

## 上游私有布局和协议证据

以下是**上游基线的事实记录，不是国服参数表**。上游 JSON 没有游戏 build、Dalamud/ClientStructs commit、采集文件哈希、客户端语言等证据标识。

| 项目 | 上游内容 | 证据强度/国服结论 |
|---|---|---|
| Addon | `Emj`、`EmjL` | `MahjongAddon.cs:12`；CN 待验证，不可由语言推断 |
| 最小包装结构 | `AddonEmjStruct.cs:6` 使用 Explicit、Size `0x300`，只含 `AtkUnitBase` | 不是完整麻将 addon 结构；不能据此认为后面的私有偏移有效 |
| 自己/下家/对家/上家点数 | `0x500/0x7E0/0xAC0/0xDA0` | 两个 JSON 一致；无 CN 实测来源 |
| 对应弃牌计数 | `0x4FE/0x7DE/0xABE/0xD9E` | 同上；计数不能代替牌河内容 |
| 14 个手牌槽/首枚宝牌 | `0xDB8 + i*4` / `0xFD8` | `BaseEmjVariant` 实际按这些位置读；CN 待验证 |
| 贴图起始值 | Emj `76041`、EmjL `76001` | `HandArrayDecoder` 还在 ±8 内启发式搜索；这不是布局真实性检查 |
| 赤五别名 | texture base + `34/35/36` → 普通五的 ID `4/13/22` | `HandArrayDecoder.cs:12`；适用于上游假设的牌面，不能跨主题/版本保证 |
| 弹窗节点 | host `104`、shell `3`、meldContainer `61` | JSON 值；`InputDispatcher` 部分节点仍硬编码 |
| 状态码 | 弃牌 `30`、鸣牌 `15/28`、自宣 `6`、摸后 idle `5`；动作模块另有吃候选 `25`、结算 `29` | 上游逻辑，非 CN 官方 enum |
| 状态值/候选牌 | AtkValue 0；吃目标 19；碰扫描 16..21 | 上游 profile；不同类型/值数量必须拒绝误解析 |
| 生命周期事件 | PostSetup/PreFinalize/PostRefresh/PostReceiveEvent | Dalamud 公开事件名；对应 CN API 是否相同应由目标依赖证实 |
| 输入 hook 签名 | `E8 ?? ?? ?? ?? 0F B6 E8 8B 44 24 20` | `InputEventLogger.cs:17,101`，构造即 Enable；无 CN 验证 |
| 弃牌 asm 签名 | `41 FF 86 00 10 00 00 8B 85 90 00 00 00 41 89 86 04 10 00 00` | `NativeAsmDiscardCapture.cs:19`；factory 注释明确称此签名会撞到 idle 代码，实际 factory 不启用 asm hook，仅做探测/轮询 |
| 弃牌回调 | `[15, textureId]` 后 `[7, slot]` | `InputDispatcher.cs:67` 注释声称来自 2026-05-23 人工弃牌；没有对应 CN capture，且当前实现忽略这两次返回值，直接回报 Ok |
| 鸣牌/和牌等 | 上游按钮行/列表选择及专用分支 | `docs/dispatch-protocol.md` 混合已采集、推测、待验证，且部分内容已过时；不能据相邻 opcode 猜国服协议 |

可复核基线：[BaseEmjVariant](https://github.com/XeldarAlz/FFXIV-AutoMahjongSolver/blob/21ae5ca9aa1fa3785baa540245b51efa346f9d37/Mahjong.Plugin.Dalamud/GameState/Variants/BaseEmjVariant.cs)、[InputDispatcher](https://github.com/XeldarAlz/FFXIV-AutoMahjongSolver/blob/21ae5ca9aa1fa3785baa540245b51efa346f9d37/Mahjong.Plugin.Dalamud/Actions/InputDispatcher.cs)、[协议文档](https://github.com/XeldarAlz/FFXIV-AutoMahjongSolver/blob/21ae5ca9aa1fa3785baa540245b51efa346f9d37/docs/dispatch-protocol.md)。

## 读取缺口：上游已有，不能归因于国服

1. **座位和局风被假定。** `BaseEmjVariant.cs:140-143` 写死 `OurSeat=0`、`RoundWind=0`、`SeatInfoKnown=true`；`StateSnapshot.Empty` 同时默认为 `DealerSeat=0/Honba=0/RiichiSticks=0/TurnIndex=0`。这会影响役牌、计分、排序和局界。国服必须有证据才设置为已知。
2. **牌河内容没有配置来源。** 两个 JSON 均不包含 `SelfDiscardArray/ShimochaDiscardArray/ToimenDiscardArray/KamichaDiscardArray`，而 `BaseEmjVariant.cs:270-273` 在 offset 缺失时返回空表；所以这里得到的四家牌河均为空，仅有数量。不得显示成“对手尚未弃牌”。
3. **对手副露与立直全部空/false。** `BaseEmjVariant.cs:243-265` 的 `Melds=[]/Riichi=false/Ippatsu=false`；自己的立直、一发、两立直沿用 Empty 默认。对手模型得不到防守关键输入。
4. **宝牌只读一个 int。** `BaseEmjVariant.cs:198-205` 没有追加宝牌列表；杠后宝牌不能完整反映。里宝牌为空是可见性上较保守的行为，不能为了补全建议读取和牌前不可见里宝牌。
5. **摸牌和赤五的物理身份丢失。** 读手牌跳过空槽后返回 `List<Tile>`，`Tile` 只有 34 类 ID，赤五仅作为总数 `AkaDora` 保存。`FindAddonSlot` 对同种牌优先选 13，不区分赤五与普通五。不能由此承诺弃普通五保留赤五，或从压缩后列表下标直接定位 UI。
6. **赤五价值并非逐候选完整处理。** `DiscardScorer.cs:28,68` 的 `CountDora` 只接收指示牌，不接收赤五槽身份；`MeldTracker.cs:39` 也明确记录“吃碰对手赤牌会少算”。引擎有赤宝牌总量评分能力，不等于任意弃牌候选都保留正确赤宝牌价值。
7. **墙牌数只是估计。** `BaseEmjVariant.cs:233-240` 用 `70 - 四家弃牌数总和`，不等于实读剩余摸牌数；吃碰、杠、未弃出的摸牌/过渡帧影响等式。该估计不适合用作唯一换局标识。
8. **非法值被吞掉。** `BaseEmjVariant.cs:218-220` 把过大的弃牌计数变成 0；`HandArrayDecoder.cs:84` 跳过未知牌；`BuildLegalActions` 在非已知弹窗分支仅凭 `hand.Count % 3 == 2` 发出可弃牌标志。这些行为可能把不完整/过渡状态当作正常局面。
9. **动作文本只匹配英文。** `BaseEmjVariant.cs:543-565,577-599` 精确比较 `Pon/Chi/Kan/Ron/Riichi/Tsumo`；中文动作标签会失配。但实际国服标签应从国服 UI 资源/诊断确认，而非只按常识替换字符串。
10. **规则说明有矛盾。** 当前 `Mahjong.Rules/Rulesets/DomanRuleSet.cs:21` 为 `MinHan=2`，`docs/ruleset.md:171` 把最低番数列为未解决问题；v0.1.2.0 发布说明却描述无最低番数限制。`DiscardScorer.cs:34` 同样写死两番惩罚。需用国服规则和实测明确，发布说明不足以解决该冲突。

## 生命周期、过渡状态及停止语义

- 已有改进：`StateAggregator.cs:69-79` 在快照 null 时清空建议缓存；`AutoPlayLoop.cs:108,134` 保留过渡帧的去重上下文；结算状态使用 3.5 秒稳定窗；`MeldTracker` 对手牌与弃牌计数写入次序允许 30 tick 延后。这些是上游实现事实，不是时序正确性的完整证明。
- `MeldTracker.cs:81-91` 仅以“估计墙牌数向上跳超过 5”判新局。`AddonEmjReader.OnPreFinalize` 清 Observation，但没有 `MeldTracker.Clear()`；本次检索 Plugin/Reader/AutoPlayLoop 也没有退出时调用 Clear。退桌后很快重新入桌、局数/手牌写入不同步可能沿用旧副露。该风险来自代码路径，尚未国服实测复现。
- `StateAggregator.cs:81` 碰到 schema mismatch 直接 return，没有清旧缓存；候选动作仅把候选数量纳入 hash，未把每个候选牌组合纳入，存在同数量新候选保留旧建议的风险。
- **停止按钮不能取消已经排队的上游动作。** `AutoPlayLoop.cs:450-469` 的 `RunOnTick` 延迟闭包没有再次检查 `disposed/IsAutomationArmed`，`Dispose` 仅解绑 `Update`。切 Off 或卸载发生在等待期间，闭包仍可能执行。这是静态代码确认的缺口；应使用取消/会话代次以及执行前统一 gate。
- 自动动作判定的上下文仅有 state/hand count，同一场景长时间重复可能再试；`DispatchResult.Ok` 有时仅表示调用发出。真正成功应从对应版本的状态变化/手牌或弹窗更新确认，不应由 bool 或无异常直接判断。

## 隐藏信息和遥测审计

- `Mahjong.Core/StateSnapshot.cs:3-18` 的对手模型只包含牌河、公开副露和公开状态，未建模对手暗手。这一点支持复用核心状态边界。
- 但上游 `Plugin.cs:168-188` **无条件**构造 `TelemetryUploader` 与 `MemoryDumpRecorder`；fallback endpoint 初始为 enabled，`EndpointResolver.cs:50` 失败仍启用。`Configuration.cs` 没有用户级遥测开关。`EnableGameLogging` 不能阻止其他 stream 的采集/发送。
- `TelemetryUploader.cs:15-16` 自动扫描并上传 games/errors/findings/memdumps/discards/inputs/sigprobes，间隔 60 秒；本次审计没有运行此插件或向该端点发送数据。
- `MemoryDumpRecorder.cs:25-34,114-169` 复制 addon `0x1300`、root `0x400`、AtkValues 原始字节、seat pool `0x1000`、以及硬编码 AgentId=5 的 `0x2000` 内存。代码没有可见字段白名单；是否含姓名/不可见牌等具体内容**未证实**，也无法保证不含，因此不能作为符合“只采正常可见信息”的国服诊断导出方案。
- `InputEventLogger.cs:50-54,101-103` 的 `Enabled=false` 仅控制 verbose 日志，底层 FireCallback hook 实际仍启用；不能把 UI 的调试开关误当成没有 hook。
- `TelemetryEnvelope.cs:28` 把 ClientLanguage 字符串命名为 ClientRegion。界面语言不能可靠判服区；CN 版本应该读取目标构建证据，不沿用该推断。

CN 推荐仅导出明确的版本、识别状态、可见 UI 节点必要标识、解析字段及错误位置；不导出姓名/聊天、内存地址、大块内存、任意 UI 文本或对手隐藏牌。不自动上传，用户手动交付一个本地诊断文件。

## 可复用离线入口与证据级别

| 入口/样本 | 性质 | 可以证明 | 不能证明 |
|---|---|---|---|
| `BaseEmjVariant.BuildSnapshotFromMemory` | 独立于游戏指针的读取函数，但所在程序集仍依赖 Dalamud | 给定布局、字节和 AtkValues 的解析回归 | 输入布局与真实 CN 一致 |
| `Replay/fixtures/state30_our_turn_emj.json`、`state30_our_turn_emjl.json`、`state15_pon_offer_emj.json` | `ReplayFixtureTests.Regenerate_synthetic_seed_fixtures` 生成的 3 个**合成**样本 | JSON/profile/解析函数的一致性 | 任意客户端实测、完整牌河/座位/换局 |
| `HandArrayDecoderTests` | 部分测试注释引用上游 2026-05-25 观察，字节在代码内构造 | 空洞手牌、别名和寻槽逻辑回归 | 独立 CN 采样、赤五物理身份正确 |
| `MeldTrackerTests`、`MeldTrackerInteractionTests`、`ActionStateMachineTests` | 合成状态序列 | 指定交错顺序和状态机不变量 | 所有动画/客户端事件顺序 |
| `data/replays/synthetic-east-1/2.tenhou.json` | README 明确为合成 Tenhou 格式 | 牌谱 parser/引擎输出基线 | 真实对局决策质量、国服胜率 |
| Engine/Rules/Policy tests | 规则牌型与算法案例 | 有依据的算法行为及回归 | CN 读取/操作正确或胜率优秀 |

示例验证命令（执行结果由主交付构建报告给出，本文没有宣称运行成功）：

```powershell
dotnet test tests/Mahjong.Engine.Tests/Mahjong.Engine.Tests.csproj -c Release
dotnet test tests/Mahjong.Rules.Tests/Mahjong.Rules.Tests.csproj -c Release
dotnet test tests/Mahjong.Policy.Tests/Mahjong.Policy.Tests.csproj -c Release
dotnet test tests/Mahjong.Plugin.Game.Tests/Mahjong.Plugin.Game.Tests.csproj -c Release
# 原上游 Dalamud tests 需要其匹配的 SDK/FFXIVClientStructs，不能拿 CN DLL 强行替换后宣称上游原测试通过。
dotnet test tests/Mahjong.Plugin.Dalamud.Tests/Mahjong.Plugin.Dalamud.Tests.csproj -c Release --filter FullyQualifiedName~Replay
```

真实 CN fixture 应附：游戏 build、Dalamud API/runtime/ClientStructs 来源、牌面主题、采集步骤、可见字段期望、脱敏方法、文件哈希和连续帧/事件顺序。先验证读取，再在同一已验证局面评估建议；不要用“建议牌看起来合理”反推读取正确。

## 相关已知问题

上游 [#30](https://github.com/XeldarAlz/FFXIV-AutoMahjongSolver/issues/30)（EmjL 建议/吃牌），[#39](https://github.com/XeldarAlz/FFXIV-AutoMahjongSolver/issues/39)（荣和动作后异常），[#51](https://github.com/XeldarAlz/FFXIV-AutoMahjongSolver/issues/51)（自摸异常），[#52](https://github.com/XeldarAlz/FFXIV-AutoMahjongSolver/issues/52)（读牌错位），[#53](https://github.com/XeldarAlz/FFXIV-AutoMahjongSolver/issues/53)（副露后高亮错位），[#54](https://github.com/XeldarAlz/FFXIV-AutoMahjongSolver/issues/54)（Traditional 牌面）是需要回归的历史场景。#48 明确列对手视图等缺口；#49 讨论 JP/OC profile。issue 正文、closed 标记和当前代码应一并审查，不能据 closed 宣称修复或 CN 支持。

## 依赖与分发

- 上游插件使用 `Dalamud.NET.Sdk/15.0.0`；插件测试声明 `net10.0-windows7.0`；纯逻辑项目原目标 `net8.0`。CN 目标不能从这些值推定，必须根据当前国服 Dalamud 构建另行选定。
- 插件显式 NuGet 依赖 `Microsoft.Extensions.DependencyInjection 9.0.0`，并从 Dalamud runtime 目录引用 Reloaded Hooks；FFXIVClientStructs/Dalamud 为框架提供引用。CN 壳若不使用 DI/native hooks，不必引入这组依赖。
- 上游为 **AGPL-3.0-or-later**，保留根目录 `LICENSE.md`、`NOTICE`、作者 Xeldar Alz 和修改说明。源码及与产物对应的构建材料应随交付提供；第三方框架 DLL 不应随意混入插件 ZIP，必要依赖按实际打包结果和各自许可证说明。
- 本文仅记录移植所需许可证材料与工程处理，并未替代完整许可证正文。
