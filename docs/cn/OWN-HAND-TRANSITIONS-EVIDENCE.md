# 本家公开手牌变化：局部时序证据

适用客户端 `2026.09.15.0000.0000` 和 `emj.uld` SHA-256 `DA6B6A98B5E1ECDD01EF7A37F3851A0BC90A8935F163CA6B0EEDCF2DA23F2E5F`。`PublicOwnHandTransitionTracker` 是托管观察器，不读取新内存、不执行游戏操作、不使 AI 就绪。

来自 `mjcn-live-20260924-091538` 的最小真实样本保存在 [own-visible-transition-20260924.json](evidence/own-visible-transition-20260924.json)，包含原始手牌资源标识、本家副露区域完整性、采样号、UTC 和源事件文件 SHA-256。仅裁掉与本例无关的其他公开字段，未修改资源、顺序、计数或 Stable 标志，也未插入合成过渡帧。

| 实际采样 | 公开下方牌 | 独立槽 135 | 本家副露 |
|---|---:|---|---:|
| 1422 | 13 张 | 无 | 经完整遍历为空 |
| 1434 | 14 张，库存唯一新增北 | 北 | 同上 |
| 1442 | 13 张，库存唯一移出南 | 无 | 同上 |

三个样本的 `LowerHandReading` 均来自既有两帧一致资源校验；本家副露区域 `Stable=true`。新模块对这三个真实 DTO 回放得到北的 `DrawCandidate` 和南的 `DiscardCandidate`。样本间其他帧被省略，**这只验证局部库存变化，不能证明完整事件历史或现场执行动作成功**。

模块要求固定版本与 ULD 的已核对 context、调用方提供的局边界 key，以及当前场景可见且读取无误。牌数必须满足 `13−3m` 或 `14−3m`，`m` 来自已稳定、完整枚举的本家公开副露组；每组种类形状、可见牌数量和解析结果必须一致。摸牌候选要求唯一新增牌与独立槽 135 同种且赤牌状态一致。排序只改变位置，不能产生事件。同种多张牌不声明物理副本身份，也不据此声称摸切。

吃碰后的额外弃牌阶段可以没有 135：要求已有旧库存、新增一个三张副露组、旧副露未变、手中唯一移出两张且都能匹配新增组，当前牌数为 `14−3m`。这只输出 `PostCallExtraTileCandidate`，不补造鸣牌来源。任意副露指纹变化均先重建基线，因此加杠消耗一张不能被误报为弃牌。

错误、场景退出、版本不符、局边界变化、采样倒序、超过两秒的采样间隙或持续不稳定均清除旧基线。短动画可等待既有两帧稳定后再比较。当前库存、摸牌候选和变化记录始终 `HistoryComplete=false`、`LegalTurnKnown=false`，不提供完整合法动作。

实际执行：

```powershell
& ./.work/dotnet/dotnet.exe test tests/Mahjong.Plugin.CN.Gameplay.Tests/Mahjong.Plugin.CN.Gameplay.Tests.csproj -c Release --filter 'FullyQualifiedName~PublicOwnHandTransitionTrackerTests' --no-restore --nologo
```

结果：**19 通过，0 失败，0 跳过**，包括真实样本回放、0～4 组副露的牌数、赤五、同种牌、排序、短动画和超时、错误/场景/版本/局边界清理、吃后弃牌及加杠不误报。状态：**已实现、构建通过、局部真实样本离线验证通过；整个实时事件链及 AI 完整输入仍待集成验证。**
