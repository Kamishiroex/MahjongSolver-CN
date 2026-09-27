# 本地 akochan 杠后补牌计数修复

2026-09-24 实际完成：固定上游 `53188a0b926fbab38177f88c3cd87d554cf412af` 的本地修复、原生对照测试、完整 `ai.dll/system.exe` 构建，以及官方公开开局样本运行。没有替换当前插件配置目录中的旧引擎，没有发布原生源码、参数或二进制。

## 问题与改动

`share/types.cpp` 的 `count_tsumo_num` 原来只将以下紧邻顺序算作补牌：大明杠→摸牌、加杠→摸牌、暗杠→宝牌→摸牌。大明杠或加杠后先公开宝牌再补牌，会被错误计入普通牌山摸牌，进而影响原生引擎的剩余摸牌估计。

[patch-akochan-local.ps1](../../scripts/patch-akochan-local.ps1) 只扩展该条件，使 `daiminkan/kakan → dora → tsumo` 也计入补牌，并增加前两条记录存在的边界检查。原来的暗杠直接接摸牌断言保持不变；没有伪造、插入或重排输入事件，没有修改模型参数。

该脚本要求精确上游 HEAD，以及 `types.cpp` 规范化为 LF 后的 SHA-256。它在独立构建目录生成一个本地 `types.cpp`，不改原 checkout；重复生成同一内容不会重写文件，不同的已有输出会被拒绝。生成文件及原生构建产物都留在被排除的 `.work`。仓库只包含原创补丁生成逻辑和原创测试调用器。

`setup-akochan.ps1` 让 `ai.dll` 与 `system.exe` 都编译这一个生成文件，并将补丁 ID `mjcn-kan-dora-rinshan-v1`、原始/补丁后哈希和脚本哈希写进 `akochan-installation.json.localPatches`。上游提交号仍表示基底源码，不能单靠它辨别旧安装是否包含此修复。

## 实际验证

C#加载器独立核对补丁ID、来源文件、提交及原始／补丁后源码哈希，保留 `HasKanDoraCounterFix`。旧安装缺少补丁时仍可运行不受影响的离线样例，但含明杠或加杠→宝牌→补牌的序列会在启动进程前返回 `AKOCHAN_KAN_DORA_PATCH_REQUIRED`。决策结果单独记录 `KanDoraCounterFixApplied`；文件摘要核对本地构建库存，不是第三方签名。

[原创 C++ 调用器](../../tests/native/akochan-kan-counter.cpp) 与本机真实上游共享源码链接，直接执行原生 `count_tsumo_num`，不是 C# 模拟器。输入是针对计数函数的合成事件类型序列，不声称这些简化序列是完整合法牌谱。

| 检查 | 实际结果 |
| --- | --- |
| 原版用修复后的预期检查 9 个场景 | 3 个失败：大明杠后宝牌、加杠后宝牌、连续混合杠 |
| 补丁版相同 9 个场景 | 9/9 通过 |
| 暗杠不经过宝牌直接摸牌 | 原生断言仍触发，实际退出码 3 |
| 重复应用、不同输出、改动过的源、已打补丁的源、错误提交、原 checkout 不变 | 6/6 通过 |
| 完整原生构建 | `ai.dll` 和 `system.exe` 均成功；AI 编译 0 个 warning，主程序 2 个既有 warning（C4005、C4244） |
| 新安装的官方公开开局样本 | 退出码 0，stderr 为空，返回弃北 `dahai N`，本次 3.319 秒 |

编译器：MSVC `19.29.30159.0`，工具集 `14.29.30133`，Windows SDK `10.0.19041.0`，Boost `1.70.0`。本次使用 x64、2 个 OpenMP 线程。

| 文件/身份 | SHA-256 |
| --- | --- |
| 上游 `types.cpp`，LF 规范化 | `a0bac9e40b13e99b9b9e30dd5fd43b71de6225505a118a538cf0915b3a243076` |
| 本地生成 `types.cpp` | `dd77887b85aa7409a3141011e43012b2c1b85ac7d9b5fc70062e1c7089ac05f4` |
| 本次本地 `system.exe` | `a48ec299b8b23752ff79667b1e15d8da28397cbef61b3a08c0f2953bf5975d22` |
| 本次本地 `ai.dll` | `c5dfdd99805e77815e4a166a4a8f7a0c88bb23b8f171f3ec127bff96a14ad906` |

MSVC 产物可能含构建时间相关信息，重复构建以新 manifest 的实际哈希为准，不要求二进制哈希逐次一致。

本机完整证据位于 `.work/akochan-native-counter-build/build-bf8c32c6e736479ebff0fc4c4e85bfa5/`：`native-kan-counter-results.json`、`local-patch.json`、`installation-result.json` 和编译日志。补丁阻断测试位于 `.work/akochan-patch-tests/test-03d5a7c09fa04d66a0e24c7588deec84/result.json`。这些目录不随插件包分发。

## 重复执行

在仓库根目录使用 PowerShell，沿用已有的本机 VS C++ 工具链：

```powershell
./scripts/test-akochan-local-patch.ps1
./scripts/setup-akochan.ps1 -NativeCounterTestsOnly -BuildDirectory .work/akochan-native-counter-build
./scripts/setup-akochan.ps1 -BuildDirectory .work/akochan-native-counter-build -Destination .work/akochan-patched-local
```

最后一个命令的安装目标必须不存在或为空；已有内容不会被覆盖。正式构建仍先运行原生计数对照，再构建引擎和运行官方样本。所有 native 文件只用于用户本机构建，遵守上游独立许可证，禁止将本地产物混入插件 ZIP。

这修复并验证了原生计数缺陷；它不证明国服事件读取完整，也不启用实时 AI。当前正常安装目录中的旧引擎仍需在将来切换时明确核对 `localPatches`，不能将本次 `.work` 验证说成已在游戏中使用。
