# 连续样本中的开局与立直观察

证据来自用户本机 `mjcn-live-20260924-091538/events.jsonl`。原文件 265,788,039 字节，SHA-256：
`8f3bcf6bf48460141559bd14d37d011e06198698db1040a2481fdeb596cc6fd2`。
5182 条 `observation`，1666 条压缩 `heartbeat`。采集配置 100 ms，但静态帧被压缩；未声称每 100 ms 均有完整、原子快照。
manifest 游戏版本 `2026.09.15.0000.0000`，`LoadedDalamudIdentityVerified=false`；离线用固定资源/语义配置解释数据，不将该 manifest 当作完整运行时验证。

## 原始样本结论

| Sample | 观察 |
| --- | --- |
| 2 | 已在东四局，牌河非空，本家立直棒已亮；属于中途观察，不能恢复最初配牌或宣告时刻 |
| 1366 → 1422 | 南一局，余牌 70、八个牌河/副露区域完整枚举为空，随后本家稳定出现 13 张经资源目录核验的配牌 |
| 2149、2831、6654 | 标题依次南二、南三、南四；标题变化本身不是完整开局 |
| 3284 | 左方立直棒首次可见；分数和供托未立即相应更新 |
| 7052 | 右方立直棒首次可见；同样不能用扣分作为此处成立依据 |
| 7371 → 7427 | 新一场东一局，先空桌/余牌 70，再出现 13 张配牌 |
| 8146 → 8149 | 上方棒和横牌首次同帧可见，随后固定横牌槽 `Emj/123/4` 稳定，牌种 23；不能倒推这一帧内部的宣告/弃牌先后 |
| 8168 → 8169 | 本家仍为 14 张；立直棒从不可观测变为可见。证明棒可先于宣言弃牌，不证明不可观测等于 false |
| 8194 → 8198 | 本家固定横牌槽 `Emj/117/4` 出现牌种 16；随后部分河槽被过渡/重叠检查拒绝。拒绝不补成缺失牌 |
| 8421 → 8477 | 同为东一局，但本场数增加、余牌重新 70、空桌后重新配 13 张；须生成不同局 ID |

结算还出现 29342、25970 等非最终动画比分。不能把动画计分片段当作真实扣点事件。

## 已实现契约

`PublicRoundEventTracker.Observe(PublicSnapshot, AddonProbe)` 只消费同一观察的已确认局况与受控公开读取：

- `OpeningObserved`：完整当前局况/门风身份、余牌 70、八区有正面空区域证据；另一观察中同局出现 13 张稳定、目录核验的本家牌。保留开局空桌和配牌的两个原始序号，生成独立局 ID。
- `RiichiStickObserved`：四方列表路径/方向精确匹配，只有资源验证的 `true` 可产生观察；`null` 不变成 `false`，不取消立直，不推断成立。每局/观察段首次正面观察去重。
- `RiichiDeclarationDiscardObserved`：已观察开局、同局正面立直棒，加该方向固定横牌槽的稳定正面。保存牌面、槽路径、棒首次序号和当前横牌序号；其他河槽缺失不被补全。此记录只关联公开标记，不生成新的弃牌事件。
- 重复序号、倒序、旧字段来源拒绝；超过两秒无观察、不可读显示/局况使局 ID 失效，只有新的可信开局恢复。暂时不可见棒不清去重记录。插件重启/新会话清除全部状态。
- 持续有效的局 ID 每帧产生当前 `Derived` 引用，并保留原开局/配牌序号及当前局况序号；不绕开 `RequireSameObservation`。内部开局引用不改写。

始终 `HistoryComplete=false`、`RiichiAcceptanceConfirmed=false`，不清 `HistoryGap`，不直接发 mjai `reach`/`reach_accepted`，也不回填规则、初始点数、初始手牌给引擎。

**成立的剩余条件**：需要已确认的宣言弃牌事件，以及其后的真实回应结束/下一行动事件；结合实际规则处理宣言牌被荣和等例外。棒/横牌首次同帧只能提供区间证据。当前分数与供托显示在宣告时没有及时改变，因此不能使用 “分数 -1000、供托 +1” 的显示差分补造成立时刻。

## 实际验证

`tests/Mahjong.Plugin.CN.Gameplay.Tests/fixtures/round-riichi-20260924.json` 为 16 个真实样本及 116 个原始心跳的 183,592 字节受控节选。除指定字段/区域省略外值保持原样，没有合成中间帧；上方牌河保留 8146/8149 的原始正面/拒绝信息，其他不使用的牌河图像省略。心跳按其原始 Sample/Utc 和明确引用的 LastObservationSample 重放相同 DTO 的状态字段；心跳投影以 gzip/base64 无损压缩附于 JSON，测试解压，不插入推测时间或变化。这是局身份连续性测试，未把选取的心跳当作完整摸弃历史。

定向命令（本机 SDK 在 `.work/dotnet`；PATH 的 SDK 9 不支持此项目的 .NET 10）：

```powershell
$env:DOTNET_ROOT = (Resolve-Path -LiteralPath '.work\dotnet').Path
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
& '.work\dotnet\dotnet.exe' test tests/Mahjong.Plugin.CN.Gameplay.Tests/Mahjong.Plugin.CN.Gameplay.Tests.csproj --no-restore --filter FullyQualifiedName~PublicRoundEventTrackerTests --verbosity minimal
```

2026-09-24 实际结果：**13 项通过，0 失败**。包括真实开局、同标题连庄、棒先于弃牌、稳定横牌关联、中途进入、重复与超过两秒无观察失效，以及缺区域、坏资源、旧序号、配牌不足的拒绝。编译通过、真实记录离线验证通过；不等于立直成立或实局 AI 已完成。
