# 下方牌面诊断的资源依据与边界

0.3.0.1 的祖先缩放投影及非整数屏幕边界补充见 [SCALING-CAPTURE.md](SCALING-CAPTURE.md)。原先的单位矩阵要求被有依据的正均匀变换计算替代，版本、资源、归属和正面壳检查继续保留。

本记录对应游戏构建 `2026.09.15.0000.0000`、Dalamud `15.0.3.5 / API 15`、ClientStructs `f824354f4a6a2b1cd16cc8fcb670c7a66bf64880`。它说明第二轮诊断为什么仅检查指定的下方组件，不代表已经准确识别本家手牌或摸牌。

本次研究读取本机游戏的静态 SqPack 资源、客户端文件的 CRC 调用链，以及用户主动提供的两份诊断。未读取游戏进程的隐藏牌，未提取其他玩家手牌。客户端程序、ULD、纹理及完整资源解析结果不进入源码包或插件包；只保留校验值、必要结构关系和研究结论。

## 实际使用的版本与文件

本机 Dalamud 提供的 `Lumina.dll` 为 `0.0.0-preview.0.384+cb3511aa350b72b5112d740fbe3a522d305fd808`，与其固定子模块一致。[对应的 UldFile 解析器](https://github.com/Dalamud-DailyRoutines/Lumina/blob/cb3511aa350b72b5112d740fbe3a522d305fd808/src/Lumina/Data/Files/UldFile.cs) 和 [ULD 结构读取代码](https://github.com/Dalamud-DailyRoutines/Lumina/blob/cb3511aa350b72b5112d740fbe3a522d305fd808/src/Lumina/Data/Parsing/Uld/UldRoot.cs) 用于读取本机文件，不以国际服最新版本替代。

使用 `GameData(sqpackDirectory).GetFile<UldFile>("ui/uld/emj.uld")` 实际取得：

| 项目 | 结果 |
| --- | --- |
| 文件 | `ui/uld/emj.uld` |
| 解压后的资源长度 | 184676 字节 |
| SHA-256 | `DA6B6A98B5E1ECDD01EF7A37F3851A0BC90A8935F163CA6B0EEDCF2DA23F2E5F` |
| Widget 节点 / 组件模板 / 纹理资产 | 150 / 74 / 39 |
| 牌壳资源 | `ui/uld/EmjTile.tex`，196×172 |
| 牌壳资源 SHA-256 | `D1358267A45BBC057199F71220C3761E75AD62CEBACF7838E23D95842AE726B7` |
| 高分辨率牌壳资源 | `ui/uld/EmjTile_hr1.tex` 存在，392×344 |

`ui/uld/emjl.uld` 和 `ui/uld/emj_l.uld` 均未取得文件。Addon 名与资源名不必一致，不能由此断言 EmjL 不存在；本次下方诊断不支持 EmjL。

首份真实布局采集的依据见 [LOWER-HAND-EVIDENCE.md](LOWER-HAND-EVIDENCE.md)。它已证实 Emj 的出现、退桌消失和若干组件路径，但没有牌面图标或文字内容，不能拿来验证牌面语义。

## 资源中能够确认的关系

ULD 的组件只有数字编号，没有“本家手牌”“摸牌”这类语义标签。下列坐标是资源的局部设计坐标，不是用户的屏幕位置。

| 区域组件 | 资源父链与模板 | 已确认的结构 |
| --- | --- | --- |
| 根节点 134、135 | `133 → 46 → 1`；模板 1055 | 下方布局，设计 y=640；单个组件 42×55 |
| 根节点 138、139 | `137 → 46 → 1`；模板 1058 | 右侧竖排布局 |
| 根节点 141、142 | `140 → 46 → 1`；模板 1059 | 上侧横排布局 |
| 根节点 144、145 | `143 → 46 → 1`；模板 1057 | 左侧竖排布局 |

第二轮仅允许 `Emj/134`、`Emj/135`，以及首份实机布局中观察到的 `Emj/1340001` 至 `Emj/1340016`。这些动态克隆编号不是 ULD 中静态列出的牌槽，也不表示始终存在相同数量的有效牌。

模板 1055 内的节点 9 是模板 1010（Button）。模板 1010 中：

| 节点 | 类型与尺寸 | 静态资源引用 | 结论 |
| --- | --- | --- | --- |
| `/9/4` | Image，40×52 | PartList 0 / Part 0 | 动态图像候选；资源没有给出固定牌面图标 |
| `/9/5` | Image，42×55 | PartList 18 / Part 0 | 静态空白正面牌壳，不能当成牌面图标 |
| `/9/6` | Collision | 无牌面资源 | 碰撞区域，不能计作牌 |

其中 4 和 5 共享实际父节点 3；资源中的同级链从 5 指向 4。首份诊断确实观察到了下方 `/9/4` 和 `/9/5` 的相同位置、对应尺寸。诊断路径会省略非组件祖先，所以还要检查真实指针父链，不能只凭显示路径确认归属。

PartList 18 含 23 个部件，Asset 21 对应 `EmjTile.tex`。Part 0 的 UV=(0,0)、尺寸42×55，本地检查纹理图像确认其为米白色正面空壳。Part 6 位于 UV=(0,56)、尺寸42×55，是棕色牌背。**纹理相同不等于当前展示正面**，必须同时确认当前选中的部件及其矩形。

右、上、左三家模板还包含动态图像节点。资源本身没有证明这些动态图像都对玩家公开，所以整个 `137/140/143` 区域及其内容完全排除。结算区域、其他副露或提示模板也不在此次牌面采集范围。

## 读取前必须成立的条件

以下是诊断设计约束，不是已经完成的国服验收项目：

1. 加载中的游戏、Dalamud、ClientStructs 身份符合精确版本配置；通过 `IDataManager` 取得的 Emj ULD 校验值与上表相同。
2. 用户明确启用第二轮下方图像诊断。默认关闭，不由旧设置自动开启。
3. 仅匹配列出的规范路径：根组件类型1055，节点9类型1010，面节点4和壳节点5均为Image。检查根组件确实属于 `133 → 46 → 1` 的下方布局。
4. Addon 就绪且可见，整条父链满足可见条件；尺寸、位置、透明度、旋转及缩放不能处于未知或动画状态。重叠判定必须使用一致的屏幕坐标单位，不能把缩放后的屏幕坐标和未缩放的局部宽高混用。
5. 先检查壳节点5：当前PartId=0、PartsList.Id=18、PartCount=23、Asset.Id=21、纹理类型Resource，当前部件仍为上表中的正面矩形。背面、未知部件、未知资源或读取失败时，应在读取面节点4的图像头、部件与资源之前返回。
6. 通过上述条件后，仅读取面节点4**当前选中部件**的资源图标编号和路径哈希标量（0.1.1.2新增 `FacePathHash`）。不得枚举其他部件，不读取资源路径字符串、文本、AtkValue内容或未经验证的麻将私有结构，也不执行原生游戏操作。路径哈希只用于诊断，图标未知时不能凭哈希回退或启用建议。
7. 候选超过14个、重复、重叠或不稳定时，不允许把它们确认成一组本家牌面。首份采集已有短暂15个内容组件的反例。图标可加载和用户逐张核对一致，只证明该次诊断画面相符，不能推导完整手牌、摸牌身份、排序语义或合法局面。

壳检查必须早于面资源读取；如果只在预览界面隐藏异常结果，原始面资源实际上已被采集，不能把两者描述成同一个保护。即使结构检查通过，遮挡和状态过渡仍需实机核对。

框架的对应结构依据：[AtkImageNode](https://github.com/ottercorp/FFXIVClientStructs/blob/f824354f4a6a2b1cd16cc8fcb670c7a66bf64880/FFXIVClientStructs/FFXIV/Component/GUI/AtkImageNode.cs)、[AtkUldPart](https://github.com/ottercorp/FFXIVClientStructs/blob/f824354f4a6a2b1cd16cc8fcb670c7a66bf64880/FFXIVClientStructs/FFXIV/Component/GUI/AtkUldPart.cs)、[AtkUldAsset](https://github.com/ottercorp/FFXIVClientStructs/blob/f824354f4a6a2b1cd16cc8fcb670c7a66bf64880/FFXIVClientStructs/FFXIV/Component/GUI/AtkUldAsset.cs)、[AtkResNode](https://github.com/ottercorp/FFXIVClientStructs/blob/f824354f4a6a2b1cd16cc8fcb670c7a66bf64880/FFXIVClientStructs/FFXIV/Component/GUI/AtkResNode.cs)。实现应使用这些对应程序集的字段，不另抄不受版本限制的偏移。

## 壳资源哈希的依据与限制

[AtkTextureResource](https://github.com/ottercorp/FFXIVClientStructs/blob/f824354f4a6a2b1cd16cc8fcb670c7a66bf64880/FFXIVClientStructs/FFXIV/Component/GUI/AtkTextureResource.cs) 将 `TexPathHash` 定义为完整路径的CRC32，`IconId` 为独立字段。`0.1.1.1` 错把 Lumina 的未最终取反值用于比较，导致 [第二份采集](TEXTURE-HASH-EVIDENCE.md) 的1036个候选全部被挡在牌面读取之前。固定客户端的离线调用链已证明应采用带最终取反的标准CRC32；详见 [算法、RVA和可复核脚本](TEXTURE-HASH-EVIDENCE.md)。

| 原始 ULD 路径（大小写不变） | 客户端最终 CRC32 |
| --- | --- |
| `ui/uld/EmjTile.tex` | `08BFF738` |
| `ui/uld/EmjTile_hr1.tex` | `17234180` |

`0.1.1.2` 仅允许这两条有依据的原始路径，删除旧的四个未取反值，也拒绝最终取反的小写变体。高清路径的结果与第二轮1036个实机样本一致；标准路径目前只有离线依据。CRC不能单独证明牌面归属，前述所有保护仍需通过。未知值继续在面资源读取前拒绝；不同缩放/旋转仍未支持。修复后的牌面图标无法从旧ZIP补出，必须重新采集。

## 文字长度及其他区域

旧版诊断的 `TextByteLength` 取自 `Utf8String.StringLength`。该字段在首份采集中全部为0，但匹配源码的公开 `Utf8String.Length/AsSpan` 使用的是 `BufUsed - 1`；不能据此说界面没有文字。[Utf8String 源码](https://github.com/ottercorp/FFXIVClientStructs/blob/f824354f4a6a2b1cd16cc8fcb670c7a66bf64880/FFXIVClientStructs/FFXIV/Client/System/String/Utf8String.cs) 与 [AtkTextNode 源码](https://github.com/ottercorp/FFXIVClientStructs/blob/f824354f4a6a2b1cd16cc8fcb670c7a66bf64880/FFXIVClientStructs/FFXIV/Component/GUI/AtkTextNode.cs) 是当前字段依据。即使改采BufUsed并通过数值边界检查，这也只是文本缓冲区元数据，不能验证分数或局数。

资源还显示四个中部方向区域（116/119/122/125）和右上五格区域（28至32），但没有提供足以确认牌河、宝牌指示牌或实际宝牌语义的标签。尤其 [EmjModule](https://github.com/ottercorp/FFXIVClientStructs/blob/f824354f4a6a2b1cd16cc8fcb670c7a66bf64880/FFXIVClientStructs/FFXIV/Client/UI/Misc/EmjModule.cs) 存在 `ShowTraditionalDoraIndicator` 显示选项，后续必须区分界面展示的是指示牌还是实际宝牌。这些区域尚未开放图像采集。

第三份采集已有442个下方图标与资源哈希配对吻合，两个静止画面获得人工一致确认，见 [TILE-RESOURCE-EVIDENCE.md](TILE-RESOURCE-EVIDENCE.md)。0.1.2.0基于两套已核对静态目录添加中文牌名，要求图标/哈希共同匹配并连续两次独立采样一致；局部牌面可能在结算阶段仍然存在，不能据此判定可操作状态。赤五、摸牌身份、完整牌局读取、建议质量和全部自动操作仍须分别验收。
