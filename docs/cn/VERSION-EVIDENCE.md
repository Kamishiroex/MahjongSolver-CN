# 本机国服版本与结构依据

当前构建依赖身份以 [local-version-evidence.json](local-version-evidence.json) 为准：国服 Dalamud **15.0.3.6 / API 15**，提交 `fbef681c15fd7c57a850b32b4020fcf5474374c7`，ClientStructs `af18b1116ddd23d1eddfc345f8eef6974d8f84d3`，游戏 `2026.09.15.0000.0000`，.NET 10，SDK 10.0.100。

## 5.2.1.1 框架更新与后续兼容

2026-10-04 核对本机 `26-10-03-01` 的 DLL 元数据、SHA-256、运行时配置及对应源码：[Dalamud 固定提交](https://github.com/ottercorp/Dalamud/commit/fbef681c15fd7c57a850b32b4020fcf5474374c7)、[ClientStructs 固定提交](https://github.com/ottercorp/FFXIVClientStructs/commit/af18b1116ddd23d1eddfc345f8eef6974d8f84d3)。Dalamud 的子模块提交与安装的 ClientStructs 一致，游戏版本未变，API 和 .NET 要求未变。

相对上一构建 `a198a02b / 243dc41e4`，5.2.1 DLL 直接引用的 55 个 ClientStructs 类型大小及既有字段偏移/类型未变，152 个引用成员的签名、常量及互操作属性未变。`AgentGoldSaucer` 新增不使用的陆行鸟字段；`AtkUnitBase.GetScaledWidth/Height` 仅参数改名；`GSInfoEmj`、报名、匹配确认、麻将节点和事件结构未发生对应源码变更。因此沿用原读牌/操作参数和评分 profile，不把这次静态比对当作新框架上的完整实机验证。

运行时不再要求框架/ClientStructs 的提交号与构建完全相等。仍要求固定游戏构建、简体中文、API 15、.NET 10，且 Dalamud 不低于 15.0.3.6、来源身份可读。随后用内嵌的 [framework-contract.json](../../Mahjong.Plugin.CN/framework-contract.json) 核对实际加载的类型、使用中的成员和互操作属性；原生结构包括字段偏移、大小、顺序布局、枚举、内嵌值类型和虚表。接口新增方法或显式布局中兼容的新字段不会单独触发停用；既有契约改变则显示具体 `VERSION_CONTRACT` 错误，保留停止保护。

契约通过 CLR 元数据检查，按插件生命周期缓存，不调用游戏函数、不读取玩家数据、不联网，也不在用户机器上自动重建基准。历史日志保持原始来源身份；只对当前已通过契约的加载环境应用原公开布局，客户端 ULD 校验仍保留。兼容性检查不能证明未来框架的所有内部行为正确，也不能让未知游戏补丁自动兼容。

维护时先核对对应源码，再通过以下命令导出候选契约到忽略目录，审查差异后才替换正式 JSON；禁止为了让失败消失而盲目重新生成。回归会检查当前编译引用是否全部被正式契约覆盖。

```powershell
dotnet build tools/UiRenderHost/UiRenderHost.csproj -c Release
dotnet tools/UiRenderHost/bin/Release/net10.0-windows/Mahjong.Plugin.CN.Gameplay.Tests.dll --framework-contract .work/framework-contract-candidate.json
```

`--framework-contract` 仅导出元数据，不运行界面或连接游戏。正式构建仍严格校验本节固定库的哈希，并从对应源码包重新构建比较 DLL；运行时允许兼容更新并不放宽发布可复现要求。

## 历史版本依据

下文是早期同客户端资源与 ABI 的历史依据，不代表当前宿主版本或新的实机验收结论。

采集日期：2026-09-23。这里记录的是本机实际安装文件和对应固定提交；不是对所有国服启动器版本的兼容承诺，也不是国服牌局读取通过的证明。

## 实际目标

| 项目 | 实际证据 |
| --- | --- |
| 游戏构建号 | `game/ffxivgame.ver`：`2026.09.15.0000.0000` |
| 选择的 Hooks 目录 | `%APPDATA%/XIVLauncherCN/addon/Hooks/26-09-18-01` |
| Dalamud 程序集/API | `15.0.3.5` / **15** |
| Dalamud GitHash | `cac6159a2c76e62a4e1f7bc347454c205808bb04` |
| Dalamud ScmVersion | `26-09-18-01` |
| Dalamud GitHashClientStructs 元数据 | `f824354f4`，此处只有 9 位，不能与完整 SHA 做等长比较 |
| FFXIVClientStructs InformationalVersion | `1.0.0+f824354f4a6a2b1cd16cc8fcb670c7a66bf64880` |
| FFXIVClientStructs FileVersion | `7.56.2.9263` |
| Dalamud/ClientStructs 目标框架 | `.NETCoreApp,Version=v10.0` |
| 宿主运行时 | `Dalamud.runtimeconfig.json` 要求 `Microsoft.NETCore.App` 和 `Microsoft.WindowsDesktop.App`，均为 `10.0.0`，`rollForward=LatestMinor` |

`dev` 目录的 Dalamud 和 FFXIVClientStructs SHA-256 与 `26-09-18-01` 完全一致。较旧的 `26-09-17-01` 是 `15.0.3.4+7f26792ec8a4f8462aa3a348642d45892078d92f`，ClientStructs 为 `43742121c5944d5dec6d3ed9e054234ee91bb18f`，不在本次精确配置范围。目录存在不表示本次游戏进程一定加载它；插件运行时仍需核对真正加载的程序集身份。

本机 Dalamud 的 `RepositoryUrl` 程序集元数据为 `https://github.com/Dalamud-DailyRoutines/Dalamud`。固定提交可从 ottercorp 仓库获取，但不能据此把本机安装宣称为所有国服玩家通用的官方发行版本。源代码中的 `AtkModule` 注释提到 CN 7.55，ClientStructs 文件版本却是 7.56.2；本交付以原始游戏构建号与程序集提交为准，不从这些字段猜测商业补丁号。

纯版本数据及 SHA-256 见 [local-version-evidence.json](local-version-evidence.json)。未读取启动器账户配置、凭据、聊天记录或角色数据。

## 可复核源码

已分别获取到 `.work/DalamudCN` 和 `.work/ClientStructsCN`，均为 detached HEAD。它们只用于调查，不随插件打包。源码固定链接：

- [Dalamud 项目文件](https://github.com/ottercorp/Dalamud/blob/cac6159a2c76e62a4e1f7bc347454c205808bb04/Dalamud/Dalamud.csproj)：版本 `15.0.3.5`；[公共构建属性](https://github.com/ottercorp/Dalamud/blob/cac6159a2c76e62a4e1f7bc347454c205808bb04/Directory.Build.props)：`net10.0-windows`。
- [PluginManager](https://github.com/ottercorp/Dalamud/blob/cac6159a2c76e62a4e1f7bc347454c205808bb04/Dalamud/Plugin/Internal/PluginManager.cs#L83)：API 等级取程序集版本的 Major，故为 15。
- [Dalamud 的依赖目录](https://github.com/ottercorp/Dalamud/tree/cac6159a2c76e62a4e1f7bc347454c205808bb04/lib)：`FFXIVClientStructs` 子模块固定为 `f824354f4a6a2b1cd16cc8fcb670c7a66bf64880`；[.gitmodules](https://github.com/ottercorp/Dalamud/blob/cac6159a2c76e62a4e1f7bc347454c205808bb04/.gitmodules) 将其上游设为 `Dalamud-DailyRoutines/FFXIVClientStructs`。
- [版本接口](https://github.com/ottercorp/Dalamud/blob/cac6159a2c76e62a4e1f7bc347454c205808bb04/Dalamud/Plugin/VersionInfo/IDalamudVersionInfo.cs)：通过 `IDalamudPluginInterface.GetDalamudVersion()` 获取 `Version`、`ScmVersion`、`GitHash`、`GitHashClientStructs`。ClientStructs 完整 SHA 另取加载中程序集的 `AssemblyInformationalVersionAttribute`。

使用本机 `Mono.Cecil.dll` 的 `AssemblyDefinition.ReadAssembly` 只读元数据，确认以上值；没有执行 Dalamud 或游戏程序集的初始化代码。程序集版本、源码版本、子模块提交三者一致，可以把本机 DLL 作为编译引用。SDK 必须支持 .NET 10；原本安装的 SDK 9.0.101 不支持该目标框架。安装 SDK 后的实际构建结论见交付验证记录，不能仅凭本节声称构建通过。

## 诊断读取依据与界限

以下依据都限定在 ClientStructs `f824354f4a6a2b1cd16cc8fcb670c7a66bf64880`。实现应引用本机 ClientStructs 字段，而非把表中偏移另抄成不受版本约束的常量。

| 能力/数据 | 依据 | 确认程度 |
| --- | --- | --- |
| Addon 按名查找 | [IGameGui](https://github.com/ottercorp/Dalamud/blob/cac6159a2c76e62a4e1f7bc347454c205808bb04/Dalamud/Plugin/Services/IGameGui.cs#L105) 返回 `AtkUnitBasePtr`；公开 `Address`、`IsNull`、`IsReady`、`IsVisible`、`Name` | API 源码确认；实际麻将窗口命中待实测 |
| Emj/EmjL 名称候选 | [ida/data.yml](https://github.com/ottercorp/FFXIVClientStructs/blob/f824354f4a6a2b1cd16cc8fcb670c7a66bf64880/ida/data.yml#L23999) 有 `AddonEmj`、`AddonEmjL`，第一行版本正好是 `2026.09.15.0000.0000` | 只是候选依据，不等于 `GetAddonByName` 实机成功；不用其中绝对地址 |
| Addon 基础头 | [AtkUnitBase](https://github.com/ottercorp/FFXIVClientStructs/blob/f824354f4a6a2b1cd16cc8fcb670c7a66bf64880/FFXIVClientStructs/FFXIV/Component/GUI/AtkUnitBase.cs)：大小 `0x238`，`UldManager=0x28`、`RootNode=0xC8`，`AtkValues=0x178`、`AtkValuesCount=0x1E2` | 基础结构源码确认；AtkValue 牌局语义完全未知，不导出未知值 |
| 可见节点 | [AtkResNode](https://github.com/ottercorp/FFXIVClientStructs/blob/f824354f4a6a2b1cd16cc8fcb670c7a66bf64880/FFXIVClientStructs/FFXIV/Component/GUI/AtkResNode.cs)：`NodeId=0x8`、`Type=0x40`、`X/Y=0x44/0x48`、`NodeFlags=0xAE`，Visible 位 `0x10` | 只对当前可见父链/节点采集，不能由单个 Visible 位推断整桌状态 |
| 图像部件 | [AtkImageNode](https://github.com/ottercorp/FFXIVClientStructs/blob/f824354f4a6a2b1cd16cc8fcb670c7a66bf64880/FFXIVClientStructs/FFXIV/Component/GUI/AtkImageNode.cs)：`PartsList=0xC0`、`PartId=0xC8`；同目录 `AtkUldPartsList/AtkUldPart/AtkUldAsset/AtkTexture` 给出部件与资源关系 | 可诊断部件、位置和图标 ID；图标/部件到牌面以及区域到各家尚未验证 |
| 生命周期 | [IAddonLifecycle](https://github.com/ottercorp/Dalamud/blob/cac6159a2c76e62a4e1f7bc347454c205808bb04/Dalamud/Plugin/Services/IAddonLifecycle.cs) 与 [AddonEvent](https://github.com/ottercorp/Dalamud/blob/cac6159a2c76e62a4e1f7bc347454c205808bb04/Dalamud/Game/Addon/Lifecycle/AddonEvent.cs)：`PostSetup`、`PostRefresh`、`PreFinalize` 等 | 可观察，不修改事件参数；关闭/退桌后不得持有旧指针 |
| 用户界面事件 | [AddonReceiveEventArgs](https://github.com/ottercorp/Dalamud/blob/cac6159a2c76e62a4e1f7bc347454c205808bb04/Dalamud/Game/Addon/Lifecycle/AddonArgTypes/AddonReceiveEventArgs.cs) 有 `AtkEventType`、`EventParam` | 框架 API 确认，但麻将各操作对应的事件/回调参数未知；不能直接重放 |

该固定提交**没有可供引用的 `AddonEmj` C# 结构**，`Game/UI/Emj.cs` 仅声明一个未解析的 `0x38` 结构。`AddonGSInfoEmj` 是对局次数、段位、点数等金碟信息界面，`UI/Misc/EmjModule` 是麻将显示设置，两者都不能当作牌桌状态结构。国服底层 UI 的确存在差异：此提交的 `AtkModule`/`RaptureAtkModule`/`UIModule` 带有明确的 `-0x10` 修正，所以不能替换成国际服最新 NuGet 或自建国际服 ClientStructs。

手牌/摸牌/牌河/副露/宝牌/座位/局数/立直状态/点数的国服节点映射、排序模式、赤五含义、动画过渡，以及弃牌/吃/碰/各类杠/立直/荣和/自摸的回调语义，目前均待国服实机快照。诊断探针不启用这些未验证读取/操作，不扫描隐藏手牌，不导出未知内存块、未知 AtkValue 值或自由文本。已确认的基础结构并不能替代牌局语义验证。

## 首份实机确认与第二轮限定

`0.1.0.0` 的真实 NPC 对局诊断已经确认同一组加载中的程序集身份，以及 `Emj` 的出现/退桌生命周期；`EmjL` 本次未出现。上表的名称候选由此得到有限实机支持，不能外推为其他麻将界面或完整牌义映射通过。原始 ZIP 的 SHA256、计数与边界见 [LOWER-HAND-EVIDENCE.md](LOWER-HAND-EVIDENCE.md)。

`0.1.1.1` 新增的下方图像诊断使用 [LOWER-HAND-EVIDENCE.md](LOWER-HAND-EVIDENCE.md) 中的本机静态资源及真实结构依据。每段显式开启后核验 ULD SHA256，限制规范路径、父链、模板、可见性、恒等变换、候选数量与重叠，先确认正面牌壳部件/矩形/资产/资源哈希，才读该范围当前选中图像的 IconId。没有运行时哈希或牌义匹配的实测前，仍标为待验证，不能作为引擎输入。其他区域不开放。

## 复核命令

```powershell
Get-Content (Join-Path $gameDirectory 'game/ffxivgame.ver') # Set your own gameDirectory
$cnHooks = Join-Path $env:APPDATA 'XIVLauncherCN\addon\Hooks\26-09-18-01'
Get-Content (Join-Path $cnHooks 'Dalamud.runtimeconfig.json')
(Get-Item (Join-Path $cnHooks 'Dalamud.dll')).VersionInfo.ProductVersion
(Get-Item (Join-Path $cnHooks 'FFXIVClientStructs.dll')).VersionInfo.ProductVersion
Get-FileHash (Join-Path $cnHooks 'Dalamud.dll') -Algorithm SHA256
Get-FileHash (Join-Path $cnHooks 'FFXIVClientStructs.dll') -Algorithm SHA256
git -C .work/DalamudCN rev-parse HEAD
git -C .work/DalamudCN ls-tree HEAD lib/FFXIVClientStructs
git -C .work/ClientStructsCN rev-parse HEAD
```

不要为让探针运行而关闭 Dalamud 的 API/版本兼容性检查。版本不符时，应保持中文界面和版本报告可用，禁止继续原生 UI 遍历，补齐对应版本证据后再添加新配置。
