# 凡夫 Mortal：来源、构建与限制

通过“设置 → 测试版”验证后，由用户选择并启用测试后端，支持提醒/自动模式。标准功能不要求测试验证，验证成功本身不切换或预热模型。操作说明见 [README-CN.md](../../README-CN.md)。

## 固定来源与许可

- Mortal 源码：`https://github.com/Equim-chan/Mortal`，提交 `0cff2b52982be5b1163aa9a62fb01f03ce91e0d2`。
- 社区权重：`https://huggingface.co/Yuchen1457/mortal-582500`，版本与 SHA256 固定于 [model-lock.json](../../tools/MortalBridge/model-lock.json)。这不是官方 Mortal 模型发布。
- 源码采用 AGPL-3.0；权重的来源、许可声明和运行库许可证随运行包保留，不能推定插件许可授权重新分发任何外部模型。
- `prepare.py`、`patch_native.py`、`native/mjcn_public.rs`、`package.py` 保留完整适配和可复现打包实现。Python、PyTorch、NumPy、观测形状和模型版本均按锁文件校验。

## 运行契约

`snapshot.py` 将当前四家公开状态送入固定原生适配器，不编造完整 mjai 历史，不读取对手暗手。当前合法动作由游戏读取层提供，C# 侧校验候选、哈希、已应用字段和引擎身份。凡夫单步吃/碰/放弃与 akochan 的多步协议分别解析。

插件嵌入 `session.py`，既有个人包无需为预热重新导入。用户主动选择并启用测试后端后，模型校验、加载和零特征预热在后台进行；预热不形成游戏建议。同一已选模型跨模式/小局复用进程，过时请求结果丢弃，退出或真实错误不允许旧动作继续执行，初始化可能耗时数秒。

## v2 公开输入适配

规则入口为 [DomanMatchRules.cs](../../Mahjong.Cn.Core/Engines/DomanMatchRules.cs)。桌型和计划局数只取实际副本观测（东风 4 局、半庄 8 局），不取排队偏好；未知桌型保留未知标记。连庄、领先庄家终局、无分数延长、同分按起始席位排序等规则依据 [官方多玛指南](https://na.finalfantasyxiv.com/lodestone/playguide/contentsguide/goldsaucer/doman-mahjong/special_rule/)，该指南主要描述半庄，国服各类终局仍待实测。时间限制触发的最后一局未读取，保持未知。最终目标标记为名次，未把局内点数当成麻将评分。

原生导入器接收 `MatchFirstRound`，据此计算计划终局，并按当前庄家和局数恢复初始席位，用于同分顺位。模型 V4 的第 27 行为比赛进度，依据锁定提交的 [obs_repr.rs](https://github.com/Equim-chan/Mortal/blob/0cff2b52982be5b1163aa9a62fb01f03ce91e0d2/libriichi/src/state/obs_repr.rs) 定位；[rules.py](../../tools/MortalBridge/rules.py) 把剩余计划局数映射到原模型的八局进度尺度：`clamp(MatchFirstRound + RoundWind * 4 + HandNumber - 1, 0, 7) / 7`。东一为 `4/7`、东四为 `1`；半庄进度保持原值。实际场风、门风和局数不改写。普通决策、立直后选牌和杠选牌均走同一编码入口。

这是兼容映射，未重新训练权重；原模型没有额外通道编码多玛连庄、时间终局或评分奖励，不能据此宣称完整东风棋力适配。`applied.match_context` 分别记录已应用赛程和未编码规则，C# 校验返回的场风、进度、终局、同分顺位；请求哈希覆盖规则及重复弃牌槽位。

历史导入按明确的 `RiverIndex` 绑定重复牌面，不用首次同牌面猜测。完整事件记录可恢复被碰/明杠跳过的巡目槽位、吃碰后的禁打牌和等待牌、弃牌当时的宝牌标记、摸牌次数、立直宣言时的原生缓存。生产读取层仍使用 `HistoryComplete=false`；新增离线完整样本不代表实机采集已完整。部分历史在 `feature_coverage` 中列出近似通道；模型没有“未知”通道，因此数值空缺/当前态近似并不等同于已知事实。

新运行包标识为 `mjcn-mortal-public-v2`，额外包含 `rules.py`。旧 v1 个人包仍可加载，并明确提示没有这些输入修正；仅升级插件 DLL 不会修改旧包中的原生库或脚本。新脚本拒绝与旧原生库混装。上游提交、权重、依赖版本、输入形状保持不变。回退时重新选择原有完整 v1 包，不单独替换其中的文件。

## 逐特征回归

[feature_replay.py](../../tools/MortalBridge/feature_replay.py) 使用人工构造的公开事件序列，分别交给锁定版本的原生事件回放和独立的公开快照归约器，比较全部 `1012 × 34` 特征及 46 个动作位。对手暗手与摸牌均为未知；测试菜单的合法许可取原生回放，只作为离线许可基准，不是国服 UI 读取的验证。

当前 24 个完整样本逐项一致，涵盖四种初始席位、中盘重复弃牌、吃碰/吃替限制、明杠/暗杠/加杠及后续弃牌、立直前后和立直暗杠、赤五、重新导入恢复。4 个缺失历史样本分别报告摸切未知、鸣牌跳巡、宝牌时序、立直历史缓存差异；不把有差异的恢复写成完整恢复。另有 36 个检查在实际 `react_batch` 入口截取普通、立直、杠决策输入，覆盖东风及半庄全部计划局；6 个矛盾输入检查拒绝缺失/重复事件及桌型冲突。

```powershell
# 使用个人包中的固定 Python 和已编译 v2 原生库；报告仅写本地忽略目录。
<个人包>/python/python.exe -B tools/MortalBridge/feature_replay.py --native-directory <个人包>/runtime --report .work/mortal-features.json
<个人包>/python/python.exe -B tools/MortalBridge/check_snapshot.py --directory <个人包> --repository . --report .work/mortal-snapshot.json
```

`package.py` 要求空输出目录；生成运行包前必须通过上述逐特征测试和真实权重推理检查，报告随个人包保存，对应修改源码和原始许可证一并保留。不通过时不生成新的运行 ZIP。

## 验证和未解决的限制

真实公开输入回归保留在 `tests/Mahjong.Cn.Core.Tests/fixtures/mortal-*.json`，协议测试为 `MortalMoveProtocolTests`。`tools/Mahjong.Cn.MortalProbe` 可运行真实模型并报告校验、预热和首个决策耗时；`tools/MortalBridge/test_runtime.py` 验证独立的公开事件契约。

模型按天凤四人半庄训练，国服东风终局、赤五和特殊计分可能影响棋力。缺少事件顺序、首巡、临时振听等历史时，仅能使用明确标注的不完整特征。Q 值不是胜率。离线输入通过、模型成功返回、游戏实际执行和建议质量是四件事；不得互相替代。

```powershell
dotnet run --project tools/Mahjong.Cn.MortalProbe -c Release -- <模型目录> tests/Mahjong.Cn.Core.Tests/fixtures/mortal-discard-20260926.json cancel-check
```

准备新运行包前阅读 `tools/MortalBridge/prepare.py`、`package.py` 的命令参数和固定来源，使用本地忽略目录保存运行库、权重和输出，不公开个人迁移包。
