# 下方牌壳 TexPathHash 的离线证据

适用范围：国服客户端 `2026.09.15.0000.0000`、ClientStructs `f824354f4a6a2b1cd16cc8fcb670c7a66bf64880`、Lumina `cb3511aa350b72b5112d740fbe3a522d305fd808`。本记录只确认下方白名单牌壳资源的路径哈希算法，**不等于牌面图标、手牌或整桌读取实机验证通过**。

## 结论与最小修复

`AtkTextureResource.TexPathHash` 使用带最终取反的标准 CRC32。匹配版本的 `Lumina.Misc.Crc32.Get(path)` 返回未最终取反的状态。因此本用途应计算 `~Lumina.Misc.Crc32.Get(path)`，输入为原始 ULD 路径的 UTF-8 字节，不包括结尾 NUL。

本机 `Emj` ULD 中资产 21 的路径为 `ui/uld/EmjTile.tex`。已定位的客户端 `LoadTexture` 保留输入路径大小写，在高分辨率分支中于扩展名前插入 `_hr1`；计算哈希前没有改成小写。仅以下两个值有当前证据支持：

| 原始路径 | Lumina 返回值（错误的旧比较值） | 客户端最终值（应比较） |
| --- | --- | --- |
| `ui/uld/EmjTile.tex` | `F74008C7` | `08BFF738` |
| `ui/uld/EmjTile_hr1.tex` | `E8DCBE7F` | `17234180` |

小写变体的标准 CRC 分别为 `77551AB9`、`87D7A4E6`，但 ULD 没有采用该拼写，不应顺便加入允许集合。旧的四个未取反值也应全部淘汰。SqPack 查询可以用不同大小写找到同一资源，不能据此推断此处的路径哈希也会规范化大小写。

第二轮采集 `mjcn-diagnostic-20260923-054913-31b5f68253d644b88ebe55d857b4aafe.zip` 中，1036 个拒绝样本的壳哈希都是 `17234180`。它与原始大小写高分辨率路径的计算值一致。修复依据是下面的静态算法与路径证据；观测值本身不是新增任意哈希允许项的理由。

## 固定公开源码

- [AtkTextureResource](https://github.com/ottercorp/FFXIVClientStructs/blob/f824354f4a6a2b1cd16cc8fcb670c7a66bf64880/FFXIVClientStructs/FFXIV/Component/GUI/AtkTextureResource.cs)：`TexPathHash` 位于结构开头，注释为完整路径 CRC32；`IconId` 是另一个字段。
- [AtkTextureResourceManager](https://github.com/ottercorp/FFXIVClientStructs/blob/f824354f4a6a2b1cd16cc8fcb670c7a66bf64880/FFXIVClientStructs/FFXIV/Component/GUI/AtkTextureResourceManager.cs)：给出 `LoadTexture(path, textureScale)` 及成员函数签名。
- [客户端 Crc32 声明](https://github.com/ottercorp/FFXIVClientStructs/blob/f824354f4a6a2b1cd16cc8fcb670c7a66bf64880/FFXIVClientStructs/FFXIV/Client/System/Crypt/Crc32.cs)：提供 `FromBuffer`、`GetDigest` 的签名。声明本身不提供算法；算法依据是下面的本机静态检查。
- [Lumina Crc32 实现](https://github.com/Dalamud-DailyRoutines/Lumina/blob/cb3511aa350b72b5112d740fbe3a522d305fd808/src/Lumina/Misc/Crc32.cs)：默认 seed 为 0，多项式为 `0xEDB88320`；初始状态为 `uint.MaxValue ^ seed`。末尾表达式 `~(crcLocal ^ uint.MaxValue)` 等于 `crcLocal`，因此没有客户端的最后一次取反。字符串重载使用 UTF-8，并从计算长度中排除 NUL。

资源结构、限定父链和资产路径来源见 [LOWER-HAND-EVIDENCE.md](LOWER-HAND-EVIDENCE.md)。本次没有改变白名单、牌壳部件、UV、尺寸、变换或布局保护条件。

## 本机程序的静态调用链

只读取已安装 `ffxiv_dx11.exe` 文件，通过 PE 节表解析、公开签名搜索和本地 Iced 解码器离线查看相关函数。没有注入、调用游戏原生函数、连接进程或读取运行中的牌局内存。以下位置均为 **RVA**，需加本文件的映像基址 `0x140000000` 才是首选 VA；不能直接拿这些位置用于其他客户端。

- 文件长度：`51881216` 字节。
- SHA-256：`7BA28760BC53CBBBF66B63105FEA6C1BFE09CB6B1C6353FC8FB0FF03CC1A30F4`。
- `ida/data.yml` 中的绝对地址未用于本结论。虽然其版本行相同，初查绝对地址与此 CN EXE 不一致；本次依靠固定源码中的成员签名，在本机 `.text` 中各自只匹配一次，再解析相对调用。

| 公开签名命中的调用位置 | 解析到的函数入口 | 身份 |
| --- | --- | --- |
| `0x6C85B0` | `0x63D590` | `AtkTextureResourceManager.LoadTexture` |
| `0x95F492` | `0x1F62C0` | `Crc32.FromBuffer` |
| `0x95C79A` | `0x1F6370` | `Crc32.GetDigest` |

`LoadTexture` 的预哈希路径经过以下步骤：保存原始 path；若 `textureScale == 1`，直接使用该路径；否则寻找扩展名，复制前缀，插入 `_hr1`，再附加原扩展名。静态后缀字符串位于 RVA `0x21789B4`，由 `0x63D605` 的 RIP 相对引用得到。随后按结尾 NUL 计算字节长度，在 `0x63D651` 调用 `FromBuffer`，在 `0x63D65A` 调用 `GetDigest`。这个路径准备和哈希区间没有大小写转换。所得 digest 暂存于 `r15d`，并在 `0x63D775` 写入新建纹理资源结构的第一个 DWORD，与公开 `TexPathHash` 字段一致。

`FromBuffer` 在 `0x1F62E9` 清零 seed，在 `0x1F62EB` 调用实际计算函数 `0x1FCEF0`，随后把结果写到 CRC 对象 digest 字段。`GetDigest` 返回该字段。

计算函数在 `0x1FCF08` 对 seed 取反，在 `0x1FD221` 再对最终状态取反，然后返回。其 RIP 相对表引用在 `0x1FCEFB`，表地址为 `0x2137910`。离线重新生成反射多项式 `0xEDB88320` 的 4 组 × 256 项切片表，与 EXE 中的 **全部 1024 项完全相同**。用该表按字节复算上述四条路径，结果全部等于本机 Lumina 返回值的按位取反，包含实机观测到的 `17234180`。

## 重复验证

Python 3 标准库脚本 [verify-cn-texture-crc.py](../../scripts/verify-cn-texture-crc.py) 接受用户本机安装文件；它先核对上面的完整 EXE 哈希，版本变化直接失败，不把此处地址迁移到新版本。运行示例：

```powershell
python scripts/verify-cn-texture-crc.py --exe '<game-installation>'
```

脚本复核 PE 信息、公开签名唯一性、相关直接调用目标、零 seed 和前后取反、1024 项 CRC 表、高分辨率后缀及路径计算值。脚本只输出 JSON 证据，不复制客户端资源或指令字节，不执行游戏文件，也不写文件。路径构造和 digest 字段存储的完整控制流由上述离线解码检查确认；脚本不是完整 x64 仿真器。

实际运行结果：`OFFLINE_BINARY_AND_ALGORITHM_CHECK_PASSED`。这只是**离线验证通过**；修复后的牌面图标采集和用户逐张核对仍为**待验证**，原诊断里没有采到的 face IconId 无法事后恢复。
