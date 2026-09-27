# 双立直与暗杠后的立直窗口（本地开发，未发布）

本次基于本地提交 `4c81ec2` 继续开发。按用户要求，未生成发布 ZIP、未上传、未修改安装索引、未替换正在使用的插件或 AI 引擎。已发布版本仍为 0.6.1.9；本页功能尚未在用户客户端生效。

## 规则和读取依据

- 规则参考 Square Enix [多玛方城役种表 Double Riichi](https://na.finalfantasyxiv.com/lodestone/playguide/contentsguide/goldsaucer/doman-mahjong/yaku_list/)：首张弃牌宣告立直，之前无人吃碰杠，双立直两番。本家当前立直的一番已由 akochan 计算，因此新桥接只加额外一番。规则说明不是国服内存结构证据。
- 沿用 [连续立直窗口](RIICHI-WINDOW-CONTINUATION.md) 的同帧四河、四副露库存、固定横牌节点、当前成立证据，以及观察段和两秒断采边界。
- 游戏 `2026.09.15.0000.0000`；固定 Emj ULD SHA256 `DA6B6A98B5E1ECDD01EF7A37F3851A0BC90A8935F163CA6B0EEDCF2DA23F2E5F`。没有新增签名或偏移。
- 暗杠图样沿用 [公开牌桌完整性核对](PUBLIC-TABLE-COMPLETENESS-AUDIT.md)：完整四槽、两个资源核验的公开背壳、两张同种公开正面、稳定的组几何。没有读取背面牌值或赤五身份。

## 已实现的判定

新增每家 `DoubleRiichi`，并同步本家 `OurDoubleRiichi`。它们是带来源、当前观察序号和时间的三态字段，未知不等于否。

| 证据 | 结果 |
| --- | --- |
| 当前明确未宣告立直 | 当前无双立直；不代表未来不能宣告 |
| 宣言横牌不是该家第一张可见弃牌，且当前成立已确认 | 否 |
| 宣言横牌是第一张、四家完整副露均为空，且当前成立已确认 | 是；副露在同局内保留，空副露证明此前无鸣牌 |
| 连续观察到宣告前已存在副露，随后首张弃牌宣告并成立 | 否 |
| 中途读到首张横牌和既有副露，但不知谁先发生 | 未知 |
| 已证明双立直，随后出现吃碰杠或自己的下一次弃牌 | 双立直保留；一发按独立窗口结束 |
| 换局、停用、断采、倒序、版本不符或当前库存不完整 | 清除或暂停输出；不能沿用旧的历史结论 |

中途重启可在“第一张横牌＋完整空副露＋当前成立”证据齐全时重新推导双立直，但不会由此恢复一发或完整历史。现有副露无法确定先后时，不推测它发生在立直之前或之后。

修复暗杠对连续证据的影响：图像 DTO 的 `AllVisibleSlotsDecoded/Stable` 正确保持 false，因为背壳不是解出的正面。窗口跟踪器独立核验稳定组、正面数、背壳数、区域完整枚举和错误列表，确认发生了杠即可取消一发，保留先前证明的双立直。它不会把背面补成两张“读到的牌”。结构缺项、正面不一致、未验证背壳或过渡仍拒绝。

## AI 接入和实际范围

`AkochanGlobalObservationProjector` 只传同观察序号、同时间、已确认的 `DoubleRiichi`；旧值和未知均传 JSON null。四家的值进入规范输入和输入哈希，改变资格会使旧决策缓存失效。

本地桥接 `mjcn-public-snapshot-v4` 在本家**当前**荣和／自摸估值中使用双立直额外一番，支持与一发叠加。回包必须回显本家资格并匹配实际附加番；组件源码哈希和安装文件哈希校验全部保留。v1/v2/v3 仍按各自能力检查，不冒充已经应用双立直。

仍有限制：对手的双立直虽进入输入，尚未加入其打点模型；本家未来摸牌模拟及宣告双立直前的选择尚未加这一番。本轮没有声称完成全局规则模型。有关差异会出现在引擎假设说明中。

固定上游 akochan：`53188a0b926fbab38177f88c3cd87d554cf412af`。v4 组件 SHA256：

| 组件 | SHA256 |
| --- | --- |
| types.cpp（沿用 v3） | `568ff21dc05765d6c927414ef0aed15eeb3b9ff8347260c463c6b189c84f042d` |
| mjutil.cpp | `c6132071160a95ffe1961267c228a6d3193586d620defa8fcd0d5c7f93c64c2a` |
| selector.cpp | `24ffcd35083e7b26be119037b905b05448e43be054c2e9fce8b4e9a85ed1c7e5` |
| global-main.cpp | `2ecb9f1f4e7d258836acc1aa9f09090839c68bdc48ba9dc0b82beea4a96d0ce9` |

## 实际验证结果

- **已实现、构建通过**：插件 Release DLL 和独立目录的原生 v4 引擎。没有调用发布打包流程。
- **离线验证通过**：Gameplay 测试 **885/885**，Core 测试 **419/419**，零失败、零跳过。覆盖四家方向、首张与后续弃牌、先鸣后立直、先立直后鸣、断采恢复、冲突、过期字段、真实 monitor→assembler→tracker 链、两背暗杠及错误图样拒绝。
- **离线验证通过**：原生引擎 **24 个构造场景**，包含双立直自摸、荣和、一发叠加；回包验证额外番数，实际选择器和牌估值也提高。例如自摸普通立直 `39.690666`、双立直 `60.342529`、双立直加一发 `74.470604`。这些是引擎评价值，**不是和牌点数**。
- **真实公开样本离线验证通过**：既有两段立直摘录分别在第六、第五张牌宣告，均识别为非双立直。此前漏荣和的 `ron-false-furiten-20260925.json` 在 v4 仍返回 `hora`；同一输入对现用 v3 的兼容回归也返回 `hora`。没有改写旧日志或补造漏帧。
- **国服实机验证待验证**：没有正向双立直的真实国服样本；没有用本轮未发布 DLL 完成实机自动对局。构造链路测试不能替代这一项。

结果文件：`.work/double-riichi-gameplay-full.txt`、`.work/double-riichi-core.txt`、对应测试项目的 TRX、`.work/double-riichi-native-probe.json`、`.work/double-riichi-real-ron.json`、`.work/double-riichi-v3-compat.json`。

原生构建目录 `.work/akochan-double-riichi-build/build-09f53dec7d5b473a92785de1bfca2ed1`；测试安装目录 `.work/engines/akochan-global-v4`。原生测试报告 SHA256 `42F5A6025F8342A1108F26144F529145F6FFED74421ABFCB06980A30C77E39E1`；本地 `system.exe` SHA256 `28B48F585BC8F786CDF4964F96F2E5E3F67F56627BB6D6BE4B049D858FFDF46E`。可执行文件受 akochan 原许可证约束，未纳入插件分发。

## 重现命令（源码和验证，不发包）

```powershell
$env:DOTNET_ROOT=(Resolve-Path .work/dotnet).Path
$env:DOTNET_ROLL_FORWARD='Major'
& .work/dotnet/dotnet.exe test tests/Mahjong.Plugin.CN.Gameplay.Tests -c Release
& .work/dotnet/dotnet.exe test tests/Mahjong.Cn.Core.Tests -c Release
# Destination 必须是新的空目录；保留现用引擎。
& scripts/setup-akochan.ps1 -GlobalSnapshot -BuildDirectory .work/akochan-double-riichi-build -Destination .work/engines/akochan-global-v4-rebuild
& .work/dotnet/dotnet.exe run --project tools/Mahjong.Cn.GlobalEngineProbe -c Release -- .work/engines/akochan-global-v4-rebuild
& .work/dotnet/dotnet.exe run --project tools/Mahjong.Cn.GlobalEngineProbe -c Release -- .work/engines/akochan-global-v4-rebuild tests/Mahjong.Plugin.CN.Gameplay.Tests/fixtures/ron-false-furiten-20260925.json
```

后续仍需补全临时／立直后振听的可证明过和窗口、完整事件恢复边界、NPC 赛制与完整终局规则，以及上述未来和牌／对手双立直模型。继续保留 `HistoryComplete=false`，不因新增字段而宣称全部缺口已解决。
