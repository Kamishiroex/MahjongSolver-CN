# 实时 akochan 输入完成条件

检查日期：2026-09-24。检查对象是当前开发树，不代表新版本已发布或国服实机验收通过。
原生引擎固定为 `critter-mj/akochan` 提交 `53188a0b926fbab38177f88c3cd87d554cf412af`。
本表追踪接线和语义缺口，原始节点与版本证据仍以 [公开字段证据](VISIBLE-FIELDS-EVIDENCE.md)、[牌桌布局证据](PUBLIC-TABLE-EVIDENCE.md) 为准。

本表针对完整牌局 AI；不会用假设东一局、无立直或清空牌河冒充真实输入，也不放宽 `ReadinessEvaluator`。0.5.0.0 另提供明确标注假设的[实验手牌分析](GLOBAL-AI.md)，只参与门清14张普通弃牌，不宣称满足本表的完整输入条件。

## 原生输入不是一个当前手牌数组

原生 [main.cpp 的 pipe](https://github.com/critter-mj/akochan/blob/53188a0b926fbab38177f88c3cd87d554cf412af/main.cpp) 保存事件序列。它在本人 `tsumo`、他家 `dahai/kakan` 后才请求决策；收到新 `start_kyoku` 时保留 `start_game` 并清除上一局。

[share/types.cpp](https://github.com/critter-mj/akochan/blob/53188a0b926fbab38177f88c3cd87d554cf412af/share/types.cpp) 的 `get_game_state_start_kyoku` / `go_next_state` 从以下信息重建局面：

| 输入 | 必要内容 | 当前生产路径缺口 |
| --- | --- | --- |
| `start_game` | 明确的赛程/结束条件映射 `kyoku_first`、赤牌规则 `aka_flag` | `PublicRuleState` 仍未知；离线支持 0/4 和赤牌开启不是国服规则已核对的证据 |
| `start_kyoku` | 场风、局数、本场、供托、庄家、四家开局点数、初始宝牌指示牌、本人配牌 | 当前局况数值已有明确读侧，但仍缺可信开局边界、完整本人配牌及初始而非当前点数；旧默认值不能使用 |
| 玩家身份 | 四个固定玩家 ID 与画面方位、当前庄家的对应 | 已提供经四门风循环核对的本地匿名方位 ID，不需要姓名；缺帧/矛盾不沿用，仍须与连续事件的行动者绑定 |
| `tsumo` | 行动者；本人精确牌面及赤牌，他家只能 `?`；普通摸牌/补牌顺序 | 本人独立槽仍有过渡歧义；他家可见背牌数量和独立槽已留候选，尚未转换成确认事件 |
| `dahai` | 行动者、精确牌面、赤牌、明确的 `tsumogiri` | 当前桌面牌河图像不足以补回摸切/手切；未知不能写成 `false` |
| `chi/pon/daiminkan` | 行动者、来源玩家、被鸣牌及消费牌 | 副露当前图像与旧跟踪器仍不足以无歧义重建来源及事件顺序 |
| `ankan/kakan` | 类型、本人/公开牌面、赤牌；加杠关联既有碰 | 四张当前牌面不能单独区分所有历史；暗杠背面禁止解码 |
| `reach/reach_accepted` | 宣告与成立的各自时机 | 点棒和横牌候选未接确认事件；原生在成立时扣 1000 并增供托，不能重复合并当前点数 |
| `dora` | 每次公开追加的指示牌及出现时机 | 新读侧可保留全部当前公开槽位候选，并独立读取显示模式；事件追加时机仍未确认 |
| 请求绑定 | 当前观察版本、明确的决策窗口、动作菜单/可弃槽位 | 离线 `AkochanReplay` 不是实时输入适配器，当前没有从严格快照创建实局请求的生产接线 |

他家初始牌和摸牌用 `?` 是正确的公开协议，不属于缺信息；不得为了填事件而读隐蔽牌面。它与“漏记了一次谁摸牌”是两种情况。

## 当前读取与严格模型之间的实际接线

1. `PublicMonitorSession.Observe` 把受控下方图像填入 **`LowerVisibleFaces`**，并通过 `PublicObservationAssembler` 将同帧公开桌面和有界状态文本投影到规范化快照。连续实机后新增局边界、本人库存变化和鸣牌跟踪，已接监视与事件日志；它仍明确设置 `Synchronization=HistoryGap`，没有把下方图像升级成完整 `OwnHand`。稳定图像不证明动画中的完整实体手牌、摸牌、可弃槽位。
2. 新 `PublicTableImageReader` / `PublicTableTracker` 提供四侧可见牌面、组件分组、读取拒绝及区域可读性。装配器把它们放入独立的 `Players.*.RiverImages/MeldImages`，保留槽位、分组、坐标、旋转、显示排列、颜色样式、完整性和来源候选；不填有事件语义的 `River/Melds`。两正面两背壳的暗杠外观作为候选保留，没有把背面的赤牌身份补成普通牌。`PublicMonitorSession` 拥有独立跟踪器，下方手牌暂不可读时仍保留同帧公开区域观察。
3. `StatusCandidates` 保留窄语法数值和枚举。默认无上下文时仍为候选；固定游戏、Dalamud、ClientStructs、简中和 ULD 全部匹配后，可使用 `CreateAuditedContext` 的明确映射。当前映射只覆盖 [本轮公开字段证据](LIVE-PUBLIC-STATUS-EVIDENCE.md) 所列比分/门风、中央两位余牌、本场、供托、明确宝牌模式和 [局标题图像目录](evidence/round-title-resource-catalog.json)。其中下/右比分与门风有逐值实机对照，上/左依赖同类静态布局和有限字形映射，**没有声称上/左也完成逐值实机核对**。宝牌牌面仍单独保留候选来源。
4. `VisibleActionMenu` 独立保存有限动作枚举、行位置、当前版本和 Enabled 状态；它不填完整 `LegalActions`，不生成 `ActionOccurred`。通用“杠”不分明、暗、加杠。三家的 `OpponentHandAppearance` 只保存公开背壳数和独立显示槽，完全不含暗手牌面，也不生成摸牌事件。
5. `JournalFeature` 保存托管观察、恢复点、错误，以及新 `public_round_signal`、`public_hand_delta`、`public_call_event`。来源匹配的吃碰杠与本人唯一库存差分分别保存，尚未统一成完整有序 mjai 前缀；`ActionSubmitted` 仍不等于游戏完成操作。
6. 原玩法 `StateSnapshot` 的分数、计数、宝牌单值来自旧偏移；场风、庄家、立直等含占位。不能把这些值复制进新 `Field<T>.Known` 来通过就绪检查。
7. 日志检查点与当前桌面核对可以恢复旧实验策略的部分跟踪状态。这一能力不自动恢复完整原生 AI 历史，`HistoryComplete` 必须保持独立。
8. `AkochanDecisionEngine.AnalyzeOfflineAsync` 当前只接受经过结构验证的离线序列，返回 `IsLiveGameDecision=false`。启用实时功能还需要可信事件适配、异步请求生命周期和过期结果丢弃，不能只改这一标记。

## 完整输入逐字段缺口

下表对应 `ReadinessProfiles.CompleteDecision`；“已可装配”说明真实生产读侧有接线，仍受各字段自己的来源与异常检查约束，不代表整局 AI 就绪。四家字段中的 `*` 明确指 Lower、Right、Upper、Left 四个屏幕方向。

| 严格字段 | 当前新增公开读侧能否提供 | 剩余具体条件 |
| --- | --- | --- |
| `RoundId` | 已接连续开局跟踪 | 余牌70、八区可见且空、同局13张资源验证配牌才能建立；已验证连庄，错误或长间隙失效，不用单独标题或观察会话 ID 代替 |
| `DecisionWindowId` | 尚不能 | 可见菜单、手牌槽位与实际轮到本人必须形成稳定、唯一且可撤销的决策窗口 |
| `Rules.RuleSetId` | 尚不能 | 需要公开的实际规则/对局选项映射，旧 DomanRuleSet 常量不是读取证据 |
| `Rules.MatchType` | 尚不能 | 需要实际对局类型的公开来源，不能只因进入 Emj 推定东风或半庄 |
| `Rules.EndCondition` | 尚不能 | 结束条件须与实际对局类型核对后映射到原生赛程参数 |
| `Rules.MinimumHan` | 尚不能 | 必须核对实际最低役要求，不能把番数和有役要求混为一项 |
| `Rules.RedFivesEnabled` | 尚不能 | 已看到赤五只能证明本次有赤牌，尚缺规则级配置与分布契约 |
| `Players.*.Score` | 已可装配 | 仅精确面板数字；重叠双文本不一致会 Conflict；来源分别注明实机逐值或静态同类布局证据 |
| `Players.*.SeatWind` | 已可装配 | 四风有限枚举；读取失败不套用上次门风；屏幕方向仍是另一字段 |
| `Players.*.PlayerId`、`OurPlayerId`、`DealerPlayerId` | 条件满足时可装配 | 四家门风必须同帧已确认、互异，并按下→右→上→左循环。匿名 ID 定义为 0→1→2→3，本人固定 0，庄家由门风东的位置确定；不会把本人固定 0 当成东家 |
| `RoundWind`、`HandNumber` | 已可装配 | 使用固定版本的局标题图像资源目录；与同帧文字冲突则拒绝；不据此产生开局事件 |
| `Honba`、`RiichiSticks` | 已可装配 | 已定位的上方黑点棒/红点棒数字，严格上下文；未知或隐藏不填 0 |
| `WallRemaining` | 已可装配 | 中央十位与个位同时有效才合成，附两条推导输入；缺一位未知，越界冲突；不再用弃牌数相减 |
| `TurnPlayerId` | 尚不能 | 对手独立显示槽和动作菜单只是当前外观，需要连续轮转/鸣杠逻辑确认，不从一张稳定背牌生成摸牌事件 |
| `OwnHand` | 当前只有 `LowerVisibleFaces` | 需要当前完整实体手牌、当前副露类型及动画阶段的数量一致性；不能直接升级所有稳定可见下方图像 |
| `HasDrawnTile`、条件要求的 `DrawnTileSlot` | 尚不能 | 独立槽外观与真实本人摸牌身份仍需动作阶段和连续样本核对；排序、摸切同牌、吃碰后弃牌需分别处理 |
| `DoraMode` | 已可装配 | 只接明确传统式/多玛式/指示牌标签，单纯“宝牌”保持未知 |
| `DoraDisplay` | 当前可装配 Candidate | 已公开的连续 0..4 槽精确资源校验，坏槽不导出伪完整列表；仍需模式与动态追加时机的整局验收 |
| `Players.*.RiichiDeclared` | 尚不能 | 横牌、点棒和菜单各有不同时间语义，需要宣告的可靠可见变化 |
| `Players.*.RiichiEstablished` | 尚不能 | 成立与宣告不同；需要确认点棒/扣分与成功弃牌顺序，避免原生重复扣分 |
| `Players.*.River` | 当前 `RiverImages` 已有经实录核对的摸切／被鸣颜色 | 连续弃牌子序列跟踪正在接入；动画未知颜色保持未知，不能把显示顺序当完整事件顺序 |
| `Players.*.Melds` | 当前图像加来源匹配的独立鸣牌事件 | 整份实录已重放来源明确的吃碰、大明杠和非五暗杠；旧采集仍有漏读组，加杠缺实际样本，完整历史投影尚未完成 |
| `LegalActions` | 只有当前 `VisibleActionMenu` | 菜单成功解析不证明完整动作集；弃牌许可、通用杠分支、吃的具体组合和当前被鸣牌尚未统一 |
| `DiscardableSlots` | 尚不能 | 正在核对本人可见手牌按钮的真实 enabled 状态；必须是当前版本槽位且与完整本人手牌一致 |

除此之外，就绪检查仍要求会话身份、同一观察边界、稳定状态和 `Synchronization=Synchronized`。当前新字段不会清除 `HistoryGap`。`OurDoubleRiichi`、四家 `Ippatsu/RiichiDiscardIndex` 虽不是该静态必填列表的独立项，原生决策仍会从事件历史消费这些信息；它们不能被遗漏或用常量代替。

## 能继续补齐的信息与来源

| 工作顺序 | 已有窄范围来源 | 要完成的判断/接线 |
| --- | --- | --- |
| 1. 当前公开库存 | 已固定 ULD 的四侧牌河、四侧副露、下方手牌图像 | 区分真实空区域与读取失败；只统计完整可读组；通过守恒及红五身份检查；牌河显示位置不冒充事件序号 |
| 2. 局况数值 | 四家面板精确数字/门风文本候选、桌面局况数字组件 | 独立定位验证后才接值；固定白名单只解析数字/四风，拒绝原文且排除邻近姓名；静态长度不能确认语义 |
| 3. 宝牌及规则 | `28..32` 显示组、对应资源及客户端公开规则/选项 | 建立显示模式和全部已公开追加牌；确认赛程、最低役要求及赤牌规则与原生参数对应 |
| 4. 连续事件 | 严格图像变化、已存在的 Addon 生命周期、游戏可见操作菜单 | 观察开局边界和行动者，追踪本人摸牌/排序与他家摸牌动画、摸切/手切、横牌及成立；漏帧/矛盾立即标 gap |
| 5. 新快照投影 | 上述确认字段和确认事件账本 | 同一次观察构造 `PublicSnapshot`、独立匿名 ID、`RoundId`、`DecisionWindowId`；每字段附适用版本和证据，不用旧默认值补空 |
| 6. 原生实局请求 | 完整单局事件前缀、当前严格就绪报告 | 在后台调用引擎，保留完整 `Moves` 批次；应用前再次核对会话、版本、窗口、牌槽和菜单；恢复旧日志从不自动重新开启输入 |

现有采样不能证明被丢弃的瞬时事件不存在。采样频率、动画稳定期和去重必须配合完整性检测；不能用“已经连续读了几帧”代替事件覆盖证明。

## 中断恢复的上限

完整、按顺序写入的事件前缀可以重放。中断期间当前桌面可以补充公开库存并校验恢复点；它不能普遍还原被鸣走的弃牌、历史摸切/手切、立直成立时刻、一发中断或已经结束的一局。

原生 [tenpai_prob_calc.cpp](https://github.com/critter-mj/akochan/blob/53188a0b926fbab38177f88c3cd87d554cf412af/ai_src/tenpai_prob_calc.cpp) 和 [mjutil.cpp](https://github.com/critter-mj/akochan/blob/53188a0b926fbab38177f88c3cd87d554cf412af/ai_src/mjutil.cpp) 会消费弃牌顺序及摸切信息，`types.cpp` 的 `is_ippatsu_valid` / `count_tsumo_num` 消费事件顺序。因此仅有最终桌面不能等价为完整前缀；不能伪造事件送给现有原生实现。若发生不可补全的间隙，需要等待下个可信开局重新同步，或另行设计明确支持不完整信息的后端；当前后端没有这项能力。

## 固定原生实现的杠后顺序约束

`types.cpp` 的 `count_tsumo_num` 要求 **`ankan → dora → tsumo` 紧邻**。暗杠直接接补牌会触发原生断言；插入别的事件会使其无法识别补牌。本次在 `AkochanReplay` 加入 `ANKAN_REPLACEMENT_ORDER_INVALID`，保留原有行动者及牌数检查；不自动插入、重排或猜测缺失事件。新增回归覆盖本人/他家漏宝牌、两种插入位置、连续暗杠以及错误补牌行动者。此文不宣称这些测试已经运行。

大明杠/加杠的原生分类缺陷已完成 [本地构建补丁与真实原生验证](NATIVE-KAN-COUNTER-FIX.md)：`daiminkan/kakan → dora → tsumo` 现在可以正确计作补牌，真实原版 3 个失败场景在补丁版通过，暗杠顺序断言保留。C#加载器已核对 manifest 的 `localPatches` 身份；旧引擎处理受影响序列前返回 `AKOCHAN_KAN_DORA_PATCH_REQUIRED`，不会继续误计。现有插件配置目录的旧引擎未替换，不能把 `.work` 中新构建成功当成游戏已经使用修复。整个过程没有重排真实输入事件。

构建通过、公共样本重放通过与国服实时输入完整性分别验收。本清单没有把任何未知字段标成已验证，也没有启用实局 AI。
