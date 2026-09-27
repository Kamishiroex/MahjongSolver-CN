# CN UI 结构探针离线测试

这些是**合成基础 UI 结构 fixture**，不是国服真实麻将牌局、截图或内存采集。测试把固定版本 ClientStructs 声明的结构写入托管 byte 数组，用虚拟地址字典注入生产 `VisibleUiReader`；不启动游戏、不加载 Dalamud 服务、不执行游戏原生构造器/回调或内存 API。

结构声明必须是本次审计的 `FFXIVClientStructs 1.0.0+f824354f4a6a2b1cd16cc8fcb670c7a66bf64880`。该身份也有测试，防止换 DLL 后误把不同布局测试通过当成同版本证据。生产默认每次采样 2048 节点、8192 次内存读取、20 ms；测试注入确定性时钟，避免 JIT/机器负载导致随机超时。

```powershell
.work/dotnet/dotnet.exe test tests/Mahjong.Plugin.CN.Tests/Mahjong.Plugin.CN.Tests.csproj -c Release
# 非默认位置：追加 -p:DalamudLibPath=<26-09-18-01目录>
```

覆盖父链可见性、组件嵌套、隐藏时不追踪内容指针、碰撞裁剪位语义、环/重复节点、空指针、非法数量、部分读取、坐标异常、跨 Addon 节点/读次数/时间预算与采样重置。文本节点仅单独读取 `NodeText.BufUsed` 的 8 字节，按固定源码 `Length` 合同减去终止符；不复制 inline 文本或跟随正文指针。0.1.0.0 的 `StringLength=0` 不能作为无文字证据。

第二轮测试验证精确下方路径、前置几何门、模板/父链/变换、正面牌壳矩形与资源hash。无效壳或非白名单节点必须在读取牌面图标前被拒绝；默认结构模式仍完全不读取图像资源。内存都是 SyntheticMemory，不能替代实机牌义验证。

这些测试能证明给定合成结构输入的安全解析行为；不能证明候选 Addon 在国服实机存在、界面线程时序完整、实际牌桌字段映射准确或决策质量。对应国服真实样本仍待采集，见 `docs/cn/ACCEPTANCE.md`。

本机执行记录（2026-09-23）：使用 `.work/dotnet/dotnet.exe`、SDK 10.0.100、上述固定 ClientStructs，Release 构建并运行 **28/28 通过、0 失败、0 跳过**。结果文件：`artifacts/tests/cn-ui/cn-ui-probe.trx`。首次构建曾被 xUnit2031 分析器拦住两处过滤后 Assert.Single 的写法，修正为断言谓词重载后得到此通过结果；没有放宽 warnings-as-errors。
