# 公开牌河弃牌子序列

`PublicRiverEventTracker` 只处理已核对客户端 `2026.09.15.0000.0000`、固定 ULD context 和调用方提供的已验证 `roundToken`。没有新内存读取或自动操作。

必须观察到四家牌河均经完整枚举且稳定为空，才建立从开局开始的基线。以后只有唯一新增、资源与颜色已稳定解码的公开槽位产生 `DiscardObserved`；事件序号来自相邻观察中唯一新槽出现的顺序，屏幕行列只保存为显示位置。横放牌同样处理，不据横放单独判断立直成立。

已记录槽位仍存在但被遮挡或拒读时，保留旧弃牌记录，将 `CurrentDecoded` 置为 false；不会删除历史，也不会把旧牌填成本次成功解析。精确红色覆盖变化经 `PublicDiscardSemantics.WasClaimed` 生成一次 `CalledMarkObserved`，不重复产生弃牌或补造鸣牌者。

同槽资源身份改变、旧槽消失、多牌同时新增、长于两秒的采样间隙、读取错误、边界不明等保留可诊断 issue。该局的 gap 一旦出现就不自动抹除。中盘开始只保留库存，不伪造已有牌的发生顺序。`HistoryComplete` 永远为 false；`HasContiguousDiscardPrefix` 只描述当前已观察的弃牌子序列，不代表全部麻将事件齐全或 AI 可用。

最小真实回放 [river-prefix-example-20260924.json](evidence/river-prefix-example-20260924.json) 摘取旧会话 4 个原始公开 DTO：1422 四河空、1437 南牌仍在动画、1441 本家南稳定、1451 右家七万稳定。实际回放先等待动画，再得到顺序 1、2；没有补造采样帧。保留来源 UTC、原事件文件 SHA-256，未保存地址或账号。

实际验证：

```powershell
& ./.work/dotnet/dotnet.exe test tests/Mahjong.Plugin.CN.Gameplay.Tests/Mahjong.Plugin.CN.Gameplay.Tests.csproj -c Release --filter 'FullyQualifiedName~PublicRiverEventTrackerTests' --no-restore --nologo
```

**18 通过、0 失败、0 跳过。** 包括真实 DTO 回放、动画去重、旧槽遮挡、资源冲突、多槽无序、红标变化、场景/版本/边界/长间隙与中盘开始。

已知限制：原始采集中未解码的赤五/横牌等新槽不会自动补齐。一个旧的新槽尚待稳定时又出现另一新槽，当前实现保守地记录多槽无序；尚未实现按首次出现时间排队后再校验资源。整场回放仍有明确历史缺口，不能据局部回放成功宣称完整事件或实时 AI 已可用。
