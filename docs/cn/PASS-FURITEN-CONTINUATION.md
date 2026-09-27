# 连续过和与振听：本地实现，尚未发布

适用读取范围：国服 `2026.09.15.0000.0000`，固定 Emj ULD SHA256
`DA6B6A98B5E1ECDD01EF7A37F3851A0BC90A8935F163CA6B0EEDCF2DA23F2E5F`。
继续使用原有版本、资源和事件证据检查，没有增加内存偏移或读取隐藏牌面。

## 规则与推导边界

规则语义依据 Square Enix [多玛方城振听 FAQ](https://na.finalfantasyxiv.com/lodestone/playguide/contentsguide/goldsaucer/doman-mahjong/faq/)
及[立直后的特殊规则](https://na.finalfantasyxiv.com/lodestone/playguide/contentsguide/goldsaucer/doman-mahjong/special_rule/)。
过和可导致临时振听，立直后过和持续到该局结束；振听不阻止自摸。官方页面是规则依据，不是国服布局或实测依据。

`PublicFuritenTracker` 从连续观察中新出现的荣和窗口建立候选，绑定本家手牌、副露、四家完整公开牌河及余牌数。
仅菜单消失不足以证明过和；菜单已关闭、本家手牌与副露不变且对手牌河新增恰好一张等条件同时满足，才记录已观察到的过和。
已确认立直时，窗口之后本家再次摸牌也能证明牌局继续、未执行该次荣和。立直状态不明确时不猜测振听持续时间。

当前本家普通/岭上摸牌能清除临时振听，却不能清除立直后过和。排序不改变手牌身份。
重新开始观察、换局、退桌、倒序/重复采样或超过两秒的断采均清除相关连续证据；不会从旧日志凭空恢复漏掉的过和。
已有暗杠使用稳定公开组、两张一致正面和两个已核验背面槽位继续绑定桌面；背面的牌面及赤牌身份仍未知。
共用 `PublicMeldInventoryProof` 也用于一发/双立直跟踪，避免同一暗杠在不同跟踪器中采用不同完整性标准。

## 真实日志中发现的旧荣和文字

`fixtures/furiten-stale-ron-20260925.json` 含 0.6.1.8 真实公开日志的 15 帧摘录，原解码流 SHA256 记录于文件。
序号 2941 出现的 Ron/Pass 文字，在后续本家摸牌、弃牌及余牌数变化之后仍存在。
因此不能把单帧“启用的荣和文字”直接认作新的荣和机会。跟踪器要求已观察到菜单由无荣和变为荣和，
或仍是绑定于同一桌面的已有窗口；第一次采样就有的荣和菜单不足以建立过和历史。
这份样本没有保存原始列表刷新标志，也没有充分的立直成立证据；测试不会补造这些数据。

## AI 接入

- 公开字段 `OurTemporaryFuriten`、`OurRiichiFuriten` 保留来源、序号和推导依据；只将当前已确认字段投影给 AI。
- 全局桥接 v5 接收 `own_temporary_furiten`、`own_riichi_furiten`。已确认任一为真时禁止本家所有牌种的荣和，保留自摸。
- 原引擎牌河振听判断继续保留；新字段为假不能消除牌河导致的振听。未知字段不会被说成已确认无振听。
- 校验引擎回显、34 种本家振听标记及当前游戏合法动作；正向振听与荣和同时存在时报告矛盾，避免错误操作。
- v1–v4 兼容配置继续保留；已知正向过和状态不能交给不支持它的旧桥接静默忽略。

本次原生源码补丁、固定源码哈希与桥接标识同步更新，构建脚本已有 v5 的 31 项检查。
原生引擎仅构建至工作区 `.work/engines/akochan-global-v5`，未覆盖用户插件或引擎。

## 实际验证

2026-09-25 本地执行：

- **构建通过**：插件 Release，固定国服 API 15 / Dalamud 15.0.3.5，.NET SDK 10.0.100，无编译警告或错误。
- **离线验证通过**：插件测试 947 项、核心测试 428 项；含此次停机提醒测试。
- **离线验证通过**：v5 原生引擎 31 项构造用例，覆盖临时/立直后振听阻止荣和、仍允许自摸、清除状态和牌河振听保留。
- **离线验证通过**：此前漏和牌真实样本 `ron-false-furiten-20260925.json` 在 v5 仍返回 `hora`；旧荣和文字真实摘录没有被重新认作有效窗口。
- **国服实机验证：待验证**。构造的过和正例不是用户实机过和验收；没有发布或更新安装索引。

复现命令（项目根目录）：

```powershell
$env:DOTNET_ROOT=(Resolve-Path .work/dotnet).Path
$env:DOTNET_ROLL_FORWARD='Major'
& .work/dotnet/dotnet.exe test tests/Mahjong.Plugin.CN.Gameplay.Tests/Mahjong.Plugin.CN.Gameplay.Tests.csproj -c Release --no-restore
& .work/dotnet/dotnet.exe test tests/Mahjong.Cn.Core.Tests/Mahjong.Cn.Core.Tests.csproj -c Release --no-restore
& ./scripts/setup-akochan.ps1 -GlobalSnapshot -BuildDirectory .work/akochan-observed-furiten-build -Destination .work/engines/akochan-global-v5
& .work/dotnet/dotnet.exe run --project tools/Mahjong.Cn.GlobalEngineProbe -c Release -- .work/engines/akochan-global-v5
& .work/dotnet/dotnet.exe run --project tools/Mahjong.Cn.GlobalEngineProbe -c Release -- .work/engines/akochan-global-v5 tests/Mahjong.Plugin.CN.Gameplay.Tests/fixtures/ron-false-furiten-20260925.json
```

## 尚未消除的缺口

漏采期间的过和、未确认立直成立时的持续时间、缺失的跨玩家先后事件仍不能从当前桌面完全恢复。
这不是完整过和历史重建，`HistoryComplete` 不会因此变成 true。NPC 赛制与完整终局估值等独立缺口继续保留，不能宣称全部信息已接入。

## 后续：无役成型牌与暗杠摸牌来源

上一版本只跟踪实际出现的荣和窗口；本次增加无役成型牌的连续过和推导。官方 FAQ 的 4/5 等 3/6 示例明确说明：
不能因为其中一侧没有役，就忽略放过该牌导致的临时振听。

新增 `PublicWinningShape` 复用上游向听算法，判断标准四组一对、七对和国士是否成型，不赋予和牌权限，
不把宝牌当役；同时核对手牌数量、副露结构以及含暗杠在内的本家实体数量，拒绝第五张同种牌。
已有的国服版本和公开资源门禁继续生效。

跟踪步骤：

1. 连续读取同一观察段的本家手牌、四家完整牌河与公开副露。
2. 在手牌及副露不变、余牌数一致或少一张的条件下，只出现一张新的对手弃牌，并且该牌能补成本家完整牌型，才建立候选。
3. 此时仍可能响应，**不立即标为振听**；后续另一个对手弃牌且桌面前缀保持一致，才证明前一窗口已过去。
4. 本家下一次已确认摸牌清除临时振听。已经确认的立直后过和不会清除；没有对手后续弃牌、直接轮到本家摸牌时，也可依据连续手牌变化确认立直后过和。

仅在启动时看到旧弃牌、一次新增多张、排序以外的手牌变化、副露变化、牌河身份变化、余牌数异常、断采或未知立直状态，
都不足以确定过和。只记录 `before`、`shape-discard`、`current` 观察证据，不创造“玩家点击放弃”的动作事件。
鸣牌发生在过和窗口内部等无法唯一判断的情形仍保留未知。

同时发现 `PublicDrawKindTracker` 与暗杠 DTO 不一致：它原先要求副露库存整体 `Stable=true`，
而两个未解码背面会使真实库存的整体 Stable/AllVisibleSlotsDecoded 为 false，即使公开组已经稳定。
现在与一发、双立直和过和跟踪共用 `PublicMeldInventoryProof`，依据两张公开正面及两个核验背面槽位确认结构。
相关旧测试原先仅提供分组标签、没有牌面；现已改为完整公开 DTO，使暗杠测试保留两个背面并保持整体图像不完整。
缺面、错面、未核验背面、过期或不可读区域仍不允许推导岭上摸牌。

本轮**已实现、构建通过、离线验证通过**：插件测试 973 项（新增 26 项）；真实旧荣和残留及正常摸牌样本仍通过。
构造实例 `789s` 副露、手牌 `234m55p12345s` 经上游评分器验证：3索成型但无役，6索有一气通贯；连续过和测试在没有荣和按钮时正确记录临时振听。
未增加内存偏移、未改变原生 v5 协议、未发布安装包。**无役过和与修正后的暗杠摸牌来源仍待国服实机验证**。

重现：运行本页插件测试命令，结果记录于 `.work/shape-furiten-full.txt` 和 `TestResults/shape-furiten-full.trx`。
