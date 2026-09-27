# 凡夫 Mortal：来源、构建与限制

当前已接入主界面的模型选择及手动/自动模式，使用固定 Mortal V4 CPU 运行包；旧的试验阶段叙述不代表当前功能开关。操作说明见 [README-CN.md](../../README-CN.md)。

## 固定来源与许可

- Mortal 源码：`https://github.com/Equim-chan/Mortal`，提交 `0cff2b52982be5b1163aa9a62fb01f03ce91e0d2`。
- 社区权重：`https://huggingface.co/Yuchen1457/mortal-582500`，版本与 SHA256 固定于 [model-lock.json](../../tools/MortalBridge/model-lock.json)。这不是官方 Mortal 模型发布。
- 源码采用 AGPL-3.0；权重的来源、许可声明和运行库许可证随运行包保留，不能推定插件许可授权重新分发任何外部模型。
- `prepare.py`、`patch_native.py`、`native/mjcn_public.rs`、`package.py` 保留完整适配和可复现打包实现。Python、PyTorch、NumPy、观测形状和模型版本均按锁文件校验。

## 运行契约

`snapshot.py` 将当前四家公开状态送入固定原生适配器，不编造完整 mjai 历史，不读取对手暗手。当前合法动作由游戏读取层提供，C# 侧校验候选、哈希、已应用字段和引擎身份。凡夫单步吃/碰/放弃与 akochan 的多步协议分别解析。

插件嵌入 `session.py`，因此既有个人包无需为预热重新导入。模型完整校验、加载和零特征预热在后台提前进行；预热不形成游戏建议。同一已选模型跨模式/小局复用进程，过时请求结果丢弃，退出或真实错误不允许旧动作继续执行。进桌前确认“凡夫已预热”，初始化可能耗时数秒。

## 验证和未解决的限制

真实公开输入回归保留在 `tests/Mahjong.Cn.Core.Tests/fixtures/mortal-*.json`，协议测试为 `MortalMoveProtocolTests`。`tools/Mahjong.Cn.MortalProbe` 可运行真实模型并报告校验、预热和首个决策耗时；`tools/MortalBridge/test_runtime.py` 验证独立的公开事件契约。

模型按天凤四人半庄训练，国服东风终局、赤五和特殊计分可能影响棋力。缺少事件顺序、首巡、临时振听等历史时，仅能使用明确标注的不完整特征。Q 值不是胜率。离线输入通过、模型成功返回、游戏实际执行和建议质量是四件事；不得互相替代。

```powershell
dotnet run --project tools/Mahjong.Cn.MortalProbe -c Release -- <模型目录> tests/Mahjong.Cn.Core.Tests/fixtures/mortal-discard-20260926.json cancel-check
```

准备新运行包前阅读 `tools/MortalBridge/prepare.py`、`package.py` 的命令参数和固定来源，使用本地忽略目录保存运行库、权重和输出，不公开个人迁移包。
