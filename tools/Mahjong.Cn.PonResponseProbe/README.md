实录碰牌窗口离线回归探针

输入来自 2026-09-24 的真实会话日志，保留 sample 117 和 600 两帧公开牌面、13 张本人手牌（包含赤五筒）、对家六索弃牌事件及已启用的“碰／放弃”菜单。另保留真实 sample 115、116，仅用于响应高亮跟踪器预热，不进入投影器或增加弃牌事件。原始日志 SHA-256：

`b1f29b91f0b8bfc7e34236c4e0d91377c441f2e5ec91b23478c80f38b5cc8ff7`

在仓库根目录运行，需要固定版本国服 Dalamud 引用和已经通过 `scripts/setup-akochan.ps1 -GlobalSnapshot` 本机编译的引擎：

```powershell
$ponEngine = Join-Path $env:APPDATA 'XIVLauncherCN/pluginConfigs/Mahjong.Plugin.CN/engines/akochan-global'
.work/dotnet/dotnet.exe run --project tools/Mahjong.Cn.PonResponseProbe/Mahjong.Cn.PonResponseProbe.csproj -- docs/cn/evidence/pon-response-20260924-input.json $ponEngine .work/pon-response-report.json
```

第三个参数可省略，此时仅向控制台输出完整 JSON；指定的输出文件必须不存在。原生计算超时为 10 秒，与插件策略一致。探针会检查：

- 真实弃牌来源为对家（相对玩家 2），牌为六索（kind34=23）。
- 当前公开菜单与手牌能修正刻意注入的错误上游候选（七筒、来源 1）。该错误候选是回归测试条件，不声称原日志曾出现这个精确值。
- 首帧依据原记录的稳定牌面建立响应窗口；第二帧用修复后的 `PublicTableTracker` 重新计算亮度变化造成的旧稳定标志。用于比较的固定资源身份从已记录的公开牌面种类重建；没有重新验证原始客户端资源读取。
- 两帧相隔 483 次采样，静止响应窗口仍生成相同原生 AI 输入。
- 真实本地 akochan 输出经动作映射后包含当前可用的碰或放弃。

探针仅执行离线计算，不向游戏发送回调。运行报告不能作为实机碰牌操作成功的证明。完整重建条件与来源记录序号列在输入 fixture 中。

如仍保留原始事件日志，可重新提取相同输入（目标文件须不存在）：

```powershell
python scripts/extract-pon-response-fixture.py '<原始会话目录>/events.jsonl' '.work/pon-response-reextracted.json'
```

完整构建使用此探针生成 `artifacts/global-ai-pon-response.json`，发布验证结果以对应版本的同名报告附件为准。报告包含源日志、输入文件及被测插件程序集的 SHA-256；不能用旧程序集的报告代替新版本验证结果。
