# 日志无损重放与分卷验证

只读核对已有整场摘要（含旧归档，不修改原始日志）：

```powershell
.work/dotnet/dotnet.exe run --project tools/Mahjong.Cn.JournalReplay -- --summaries '<插件 logs 目录>' '.work/match-summary-check.json'
```

输出只有记录数、有效来源样本数及未知情况，不包含角色标识、路径或完整牌局；旧日志缺失的配置、名次和评分不会被补造。

```powershell
.work/dotnet/dotnet.exe run --project tools/Mahjong.Cn.JournalReplay/Mahjong.Cn.JournalReplay.csproj -- '<原始 events.jsonl>' '.work/journal-replay-new'
python scripts/analyze-game-logs.py .work/journal-replay-new/verified-export.zip --output .work/journal-replay-summary.json
```

输出目录必须不存在。工具读取完整且通过哈希校验的日志前缀，通过生产写入器重写全部原始记录。测试阈值为 256 KiB，生产默认阈值为 32 MiB；新写入器使用新的外层会话 ID，逐条验证原有类型、序号、时间、Data 完全一致，包括候选、冲突和历史缺口。减少至少 80%、发生至少一次轮转、完整重放无误才返回成功。

输出目录含重写日志、ZIP 和 `report.json`。日志仍是用户自己的公开牌局数据，不自动上传；报告只有体积、条数、哈希与验证结果，不含账号或聊天。完整构建可传 `-JournalReplayFile` 执行同一验证，并发布不含完整牌局的汇总报告。未提供实录时不能声称执行过此项。

为使体积比较保守，本工具连旧版原始桌面记录也无损保存；新版插件正常运行时额外将原始诊断移到有界缓冲、降低历史检查点频率。此工具不会发送游戏回调，也不衡量 AI 策略质量。
