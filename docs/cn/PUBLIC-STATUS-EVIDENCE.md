# 国服公开状态文字与局标题读取证据

适用客户端 `2026.09.15.0000.0000`，ClientStructs `f824354f4a6a2b1cd16cc8fcb670c7a66bf64880`，匹配 Lumina `cb3511aa350b72b5112d740fbe3a522d305fd808`。本页记录 2026-09-24 新增的真实公开读取及离线资源核查。下述字段初始输出仍为 `Candidate`，没有把格式匹配升级成完整牌局验证。

## 固定来源与范围

`ui/uld/emj.uld` SHA-256 为 `DA6B6A98B5E1ECDD01EF7A37F3851A0BC90A8935F163CA6B0EEDCF2DA23F2E5F`。静态父链与尺寸来自用户本机该版本资源，详细结构只留在排除于发布包的 `.work/uld-analysis/`。

`PublicStatusReader` 使用已有可见节点遍历结果和注入的有界内存读取函数。每个路径还要核对真实父链、类型、尺寸、可见／透明／draw-disabled 状态、有限正等比缩放和所属 addon/component 地址。路径字符串本身不授权读取。

| 公开候选 | 允许路径 | 结构证据 |
|---|---|---|
| 四家点数 | `38/12/{2,3}`；`40/42/44` 的 `13/{2,3}` | 1026/1025 面板内 1047，文本108×20；两个文本保留为独立候选，不提前择一 |
| 四家门风 | `38/9`；`40/42/44` 的 `10` | 同一面板内120×18文本；仅接受东南西北四值 |
| 左红点棒旁数量 | `22` | 父21→1，30×10文本；邻接24的红一点棒图像 |
| 右黑点棒旁数量 | `23` | 父21→1，30×10文本；邻接25的黑点棒图像 |
| 宝牌显示标签 | `27` | 父26→21→1，320×16；仅有限中文标签 |
| 桌面中央两位数字 | `105/2/2`、`105/3/2` | 1016下1064，30×60的 `AtkCounterNode`；每个只接收一位数字 |
| 文本局标、结果候选 | `15`、`55` | 只允许精确局标／结果枚举；当前样本未命中，不用作已知字段 |

根38/40/42/44的下／右／上／左映射按固定布局及当前截图核对；不会把数组位置0当成东家。点数允许规范分组逗号和负值，拒绝嵌入姓名、任意控制码、损坏UTF-8、非规范分组或越界值。名字段 `38/5,6`、`40/42/44` 的 `6,7`、聊天面板及对家暗手不在白名单。拒绝内容不写入DTO或日志。

结构读取依据是固定 [AtkTextNode](https://github.com/ottercorp/FFXIVClientStructs/blob/f824354f4a6a2b1cd16cc8fcb670c7a66bf64880/FFXIVClientStructs/FFXIV/Component/GUI/AtkTextNode.cs)、[AtkCounterNode](https://github.com/ottercorp/FFXIVClientStructs/blob/f824354f4a6a2b1cd16cc8fcb670c7a66bf64880/FFXIVClientStructs/FFXIV/Component/GUI/AtkCounterNode.cs) 与 [Utf8String](https://github.com/ottercorp/FFXIVClientStructs/blob/f824354f4a6a2b1cd16cc8fcb670c7a66bf64880/FFXIVClientStructs/FFXIV/Client/System/String/Utf8String.cs)。使用 `BufUsed - 1`，不使用此前误解的 `StringLength`；只复制指针、容量、已用长度三个标量，再读取最多64字节正文及末尾NUL。不会复制整个文本节点、内联字符串区或 `OriginalTextPointer`，不会调用游戏原生方法。

## 当前实机样本

本地只读探针复用生产读取器，从当前运行的国服客户端取得 `.work/live-public-20260924-080532.json`，SHA-256 `F1F7D992426894E20D0EFD9C52664AA9F7E01A569A1DD647182CE4A1C1B5C07F`。四次采样中前两次因既有总预算超限停止，**不能计为成功帧**；第3、4帧均取得17项候选，无读取错误。

| 屏幕方位 | 实读点数（两节点一致） | 实读门风 |
|---|---:|---|
| 下方 | 25000 | 北 |
| 右方 | 25000 | 东 |
| 上方 | 27000 | 南 |
| 左方 | 23000 | 西 |

同帧左／右棒计数均为0，中央两个数字为6、0，标签为“宝牌（多玛式）”。更早当前局截图中央为69；继续采样时下降到60。该值来自当前实际显示，**没有用70减去牌河数量计算**。只保存牌桌与右上状态区的局部截图供同机对照；不分发原始截图。

Square Enix [官方界面说明](https://na.finalfantasyxiv.com/lodestone/playguide/contentsguide/goldsaucer/doman-mahjong/navigate/)说明玩家名下方向表示门风、星号表示庄家，右上两种点棒分别关联立直供托与本场，并展示该区域的局标与宝牌。它支持区域含义，不能替代本国服版本的节点定位与实测。

## 局风／局数：实际来自动态图像19

固定ULD的 `Emj/19` 为640×80动态 Image，真实父链19→16→1，16自身缩放0.5。相邻 `Emj/20` 的 parts list29只有一项，资产32 `ui/uld/GradationLine.tex`、UV0,0,320×4；**20是拉伸背景，不是局数**。

`RoundTitleResourceReader`只读取可见19当前选择的一个part、该asset及 `AtkTextureResource.TexPathHash/IconId` 两个标量。不读路径字符串、其他part或纹理正文。`.work/live-public-20260924-081123.json` 中实读：list0/count1/part0、UV0,0,640×80、asset0、Resource、IconId121452、hash346047217即 `0x14A042F1`；同屏为东二局。

本地用固定Lumina实际读取 `ui/icon/121000/chs/` 图标资源，分别解码普通和 `_hr1` 版本并逐个目视核对。完整16项路径、尺寸、内容SHA-256和客户端路径CRC记录在 [round-title-resource-catalog.json](evidence/round-title-resource-catalog.json)，图片仅保存在 `.work/round-title-analysis/`。

| IconId | 逐个确认的文字 | 普通路径CRC | 高分辨率路径CRC |
|---:|---|---|---|
| 121451 | 东一局 | BBC15A27 | 2D2D7E34 |
| 121452 | 东二局 | FC6120F7 | 14A042F1 |
| 121453 | 东三局 | C1010947 | 03DB56B2 |
| 121454 | 东四局 | 7321D557 | 67BA3B7B |
| 121455 | 南一局 | 4E41FCE7 | 70C12F38 |
| 121456 | 南二局 | 09E18637 | 494C13FD |
| 121457 | 南三局 | 3481AF87 | 5E3707BE |
| 121458 | 南四局 | B6D13856 | 818EC86F |

`RoundTitleCatalog.TryDecode`要求固定客户端版本及上述现场已观察的完整resource结构、IconId与对应hash同时匹配，才返回文字、局风0东/1南和局数1..4。不会按图标编号加减推算；相邻121459起已是桌名、晋级和段位等资源。该映射证明这8种固定字图的内容，不等于8局均已完成实机流程验证。

## 自身牌面按钮候选

`PublicHandInteractionReader`只在同一采样已经完成受控自身牌面采集后运行；所有牌面都须属于下方白名单、布局无重叠且资源IconId/hash均在已核对目录中。逐牌重新核对 `face4 -> [3,2,1] -> button9(1010) -> [1] -> 自家1055 -> 133 -> 46 -> 1` 与同父公开正面壳5，然后读取按钮owner的 `NodeFlags.Enabled`。依据是固定 [AtkComponentButton.IsEnabled](https://github.com/ottercorp/FFXIVClientStructs/blob/f824354f4a6a2b1cd16cc8fcb670c7a66bf64880/FFXIVClientStructs/FFXIV/Component/GUI/AtkComponentButton.cs#L24)，不会读取额外私有按钮字段或发送回调。

输出仍为候选：`AllVisibleFacesBound`只说明这些公开牌面均绑定到已核对按钮；`CompleteLegalDiscards`与`DrawIdentityVerified`始终为false。固定独立槽135只标注 `SeparateSlotCandidate`，不能仅据此宣布一次摸牌。普通摸打、碰后禁吃替及立直选牌必须分别实测，未核对前不能将Enabled全集交给自动操作。

## 宝牌模式与剩余阻碍

顶端宝牌的固定组件 `28..32` 使用模板1006，组件原始尺寸50×60、局部缩放0.75；其公开脸图 `2` 原始尺寸40×52、局部缩放1。2026-09-24只读实机元数据 `.work/live-public-20260924-082917.dora-metadata.json`（SHA-256 `9030C9B723F478069B4714CC67977C414305AE127154E71DF75973DA6F596E7B`）中当前可见28、29均符合；当前选中资源是list0/count1/part0、UV0,0、40×52，ImageFlags=128。`PublicDoraReader` 因此只接受该公开组件缩放，不将无来源的1.0组件尺寸假设兼容进去；图像翻转、裁剪、未知资源身份仍拒绝。合成守卫测试中专门拒绝组件缩放1.0。该记录不证明未出现的里宝牌、其他模式或其他客户端版本。

当前“宝牌（多玛式）”标签来自节点27实际显示；本机 `EmjAddon` 行197/198分别为传统式／多玛式标签，中文EXD SHA-256 `562DD709133FA479A9DA0D6A260D53FC028CFEFF02F2A1472860431263AFF9A1`。更多说明与实际宝牌／指示牌转换区别见 [PUBLIC-TABLE-EVIDENCE.md](PUBLIC-TABLE-EVIDENCE.md)。

固定 [EmjModule](https://github.com/ottercorp/FFXIVClientStructs/blob/f824354f4a6a2b1cd16cc8fcb670c7a66bf64880/FFXIVClientStructs/FFXIV/Client/UI/Misc/EmjModule.cs) 提供 `ShowTraditionalDoraIndicator`；命名入口为 `EmjModule.Instance()`→`UIModule.Instance()`→`GetEmjModule()`，后者是固定接口虚函数45。生产若核对此用户显示设置，应只读取命名字段的一个合法布尔值，不读取整块用户模块，也不能把设置本身当成宝牌牌面。模式改变时清除旧建议与稳定基线。

`PublicActionMenuReader`另限定固定ULD中的104(1052)→3(1030列表)→行(1029)→Text4(310×24)。它核对真实父链、行模板和完整可见性，仅输出 `ChineseActionLabels` 中已由本机中文 `EmjAddon` 行16..23核对的吃、碰、杠、立直、和牌、自摸、放弃、取消。当前行的 Enabled 来自固定 `AtkComponentButton.IsEnabled` 同样使用的 owner `NodeFlags.Enabled`。不使用来源注释仍带问号的 `ComponentFlags & 2`。

该菜单DTO只声称“当前可见行已解析”，不声称合法动作全集。行按屏幕Y排序，但不是回调参数映射；菜单不可见不等于允许放弃或出牌；通用“杠”也不能自行区分暗杠／明杠／加杠。`.work/live-public-20260924-080646.json` 的240帧中没有出现104子节点，因此这条新增路径目前由静态资源和合成测试支持，不能宣称菜单实机读取已通过。

本页没有完成立直成立时点、合法动作全集、结果类型与赢家／放铳家的可靠读取；它们不能由点数变化、菜单提供某动作或callback返回Submitted推造。也没有默认任何未显示风位、25000点或空副露。后续需将同帧已核对状态、公开牌面和事件连续性一并投影，在输入仍缺失或矛盾时保持策略／自动操作关闭。

独立临时工程只链接上述生产状态／标题／菜单读取器、其新测试和相同的 `UiNode` 元数据DTO，实际通过115项测试。覆盖合成父链、错面板、隐藏／透明、越界长度、损坏UTF-8、玩家名路径不触碰、Counter与Text布局区分、菜单启用位／缺行／未知标签，以及局标题8种图标双分辨率与错误版本／hash／UV拒绝。未调用原生函数，未引用或改写主项目构建输出；TRX位于 `.work/public-status-tests/TestResults/public-status-tests.trx`。最终完整项目集成结果由统一构建报告记录。
