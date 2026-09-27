实录吃牌窗口与中途恢复离线回归

本样本来自 0.6.0.1 实测：首次公开读取超出预算，未确认开局；观察到左家新增六万后，客户端显示“吃／放弃”，AI 卡在等待触发事件。

```powershell
$chiEngine = Join-Path $env:APPDATA 'XIVLauncherCN/pluginConfigs/Mahjong.Plugin.CN/engines/akochan-global'
.work/dotnet/dotnet.exe run --project tools/Mahjong.Cn.ChiResponseProbe/Mahjong.Cn.ChiResponseProbe.csproj -- docs/cn/evidence/chi-response-20260924-input.json $chiEngine .work/chi-response-report.json
```

第三个参数可省略，完整 JSON 会输出到控制台；输出文件须不存在。引擎超时为10秒，与插件一致。

此探针分别验证：

- 实录初次读取失败后，缺失开局仍可建立局内观察段；回放真实短区间，由 `PublicMonitorSession` 和牌河跟踪器产生新增弃牌事件，再交给全局投影、原生 AI 与动作映射。不得把 `RoundId` 或完整历史升级为已确认。
- 后续同一吃牌窗口的实际稳定快照可以独立恢复分析；根据当前公开菜单与牌面推断的来源须保留假设，不添加虚构历史事件。

旧日志在原始 sample249 到384之间存在约15秒的语义去重空档。新的高亮生命周期需要额外一次稳定确认，因此测试明确增加一个人工保持条件：在249之后100毫秒复用其公开内容，命名为模拟sample250。这不是实际客户端观测，报告将 `ReconstructedHoldSamples` 标为1，并在 AI 输入假设中说明。

没有直接注入弃牌事件，也不发送任何游戏回调。牌河跟踪器在上述明确的持续性条件下生成事件；此结果不能声称完全复现了未经修改的客户端采样流。表面牌资源从记录的已验证牌种重建；下方未日志化的矩形高度和宽度为离线布局条件。这验证托管观察与决策链，不代表重新验证客户端内存读取、实机点击或建议质量。所有重建条件写入 fixture 和输出报告。

输入只保留公开麻将信息，不含账号或聊天。原日志仍在写入时，提取脚本记录完整行前缀的字节数与 SHA-256，可按同一前缀重现：

```powershell
python scripts/extract-chi-response-fixture.py '<原会话>/events.jsonl' .work/chi-response-reextracted.json 25761346
```

完整构建报告为 `artifacts/global-ai-chi-response.json`；发布验证结果以对应版本同名附件为准。
