# 日志无损重放与分卷验证

## 固定决策复盘

先构建本工具，然后使用 `dotnet run --project tools/Mahjong.Cn.JournalReplay --` 加下列参数。输出文件必须不存在，不覆盖基线：

```text
--corpus-export <整场日志目录或ZIP> <corpus.json>
--corpus-run <corpus.json> <before.json> upstream
--corpus-run <同一个corpus.json> <after.json> upstream
--corpus-compare <before.json> <after.json> <comparison.json>
```

插件战绩页的“本地技术详情 → 导出固定决策样本”生成相同输入格式。只导出最近 200 条决策中完整且哈希相符的输入（去重，最多 8 MiB）；旧记录缺少快照/原生输入会计入跳过数，不用当前桌面补造旧局面。每次决策保留当前公开输入、实际来源/配置、候选、过滤、保护覆盖与已测耗时。原日志仍保存提交和后续观察，导出不会访问游戏。

显式在 `--corpus-run` 最后指定 `mortal <已导入目录>` 或 `akochan <已导入目录>` 可离线运行对应测试后端；不会导入、下载、修改已安装配置或授予游戏操作权限。原生输入缺失会报告错误。仅刷新离线时间有效期，验证规范输入哈希未变，再使用现有候选映射与保护。标准 `upstream` 路径不加载实验后端。报告记录实际构建及安装清单哈希，不含本机路径。

先保留基线包/源码/模型及设置，再在两个构建下使用同一个 corpus 文件各跑一次。比较输出动作、候选、过滤和保护差异、逐例耗时与错误；缺例单列，不因动作不同宣称棋力提高。耗时受预热和 CPU 负载影响。可用 `--corpus-fixture <corpus.json>` 生成三个明确标注的构造局面检查工具链，它们不是实机样本或强度评测。导出仍含公开牌局数据，仅留本机，不自动上传。

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
