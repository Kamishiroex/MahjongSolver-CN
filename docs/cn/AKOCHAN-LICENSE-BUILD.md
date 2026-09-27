# akochan 本机构建、协议与许可核查

> 0.6.0.0 更新：实局 AI 已改用[当前公开牌局快照入口](GLOBAL-AI.md)，旧离线／手牌路径仅保留历史说明和自检。原生引擎仍独立本机构建，不随插件分发。下文的旧版本范围与验证数字不能替代新版本证据。

本页记录真实上游 CPU 引擎的本地构建和运行证据。它不能证明国服牌局读取正确，也不能证明建议质量达到某个段位。国服的公开事件历史尚未验证前，不得填造手牌、规则、场况或事件来调用引擎。

## 固定来源与许可

- 上游：[critter-mj/akochan](https://github.com/critter-mj/akochan)。开发计划 P5/P6 的 S8 确实指向这个仓库；本次选择它自己的 `main.cpp` 的 `pipe` 入口，不把别的 mjai 实现的协议套进来。
- 固定提交：`53188a0b926fbab38177f88c3cd87d554cf412af`，提交日期 `2022-07-05 17:40:37 +0900`，提交说明 `[bug fix] fixed legal chi and dahai judge`。
- 上游检出路径 `.work/akochan`；构建前后 `git status --porcelain=v1` 为空。没有改动上游源码、AI 算法和参数。
- [LICENSE 原文件](https://github.com/critter-mj/akochan/blob/53188a0b926fbab38177f88c3cd87d554cf412af/LICENSE) 为 CP932 日文自定义使用条款，**不是 AGPL/MIT 等标准开源许可证**。本地 SHA256 为 `73d32d37160eceee5893b00ada91a3ca63eaa2f550ca5bb827c571043c0af910a`。

条款第 4–5 行把仓库的全部文件定义为“本程序”，把 `ai_src` 内文件定义为“AI 部分”。第 8–10 行允许非公开研究、探讨，非营利使用输出需说明使用了该程序，其他利用需作者许可。第 13–14 行规定非营利再分发需署名、继承其条款、且不得改动 AI 部分；AI 部分改动后的再分发原则上禁止。第 17–18 行另有知识产权与修改前协商约定。应阅读原文，不能将其自动改授 AGPL，也不能从 GitHub 可下载推导出无限制使用或分发权。

本项目只交付自己编写的桥接代码及本机构建脚本。akochan 源码、模型参数、原生二进制和微软运行库均不进入插件发布 ZIP；脚本从固定上游下载到使用者本机并保留原始 LICENSE。这里的本地编译实验不扩大该条款许可范围。

另有 Boost Software License 1.0 的 Boost 头文件，以及仓库内 json11 的 MIT 声明（`share/json11.hpp` 文件开头）。本地安装复制这两份声明。微软 CRT/OpenMP DLL 来自本机已有 VS 的 redist 目录，脚本没有安装或修改系统运行时。

## 可重复执行的 Windows 构建

从本项目根目录执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/setup-akochan.ps1
```

默认路径：

- 上游源码：`.work/akochan`。
- 构建与日志：`.work/akochan-build/build-<随机ID>/`。每次运行创建独立目录。
- Boost 缓存：`.work/akochan-tools/boost_1_70_0`。
- 本机安装：`%APPDATA%\XIVLauncherCN\pluginConfigs\Mahjong.Plugin.CN\engines\akochan`。

可以传 `-SourceDirectory`、`-BuildDirectory`、`-Destination`。源码和构建路径必须位于本项目工作区内，路径链不得经过 reparse point。已有源码必须处于上述固定提交且无改动。目标目录非空时明确报错，不覆盖、不删除任何现有引擎文件；再次构建验证时选一个新的 `-Destination`。脚本没有递归删除或移动操作。

脚本只下载缺少的公开源码/依赖，发现可用的现有 VS C++ x64 工具链和 Windows SDK；缺失时明确失败，不安装全局开发环境。本次实际使用：

- Visual Studio 2019 Build Tools，MSVC `14.29.30133` 工具集，实际 `cl.exe` 文件版本 `19.29.30159.0`。
- Windows SDK `10.0.19041.0`。
- [官方 Boost 1.70.0 下载](https://archives.boost.io/release/1.70.0/source/boost_1_70_0.tar.bz2)，压缩包 SHA256 `430ae8354789de4fd19ee52f3b1f739e1fba576f0aded0897c3c2bc00fb38778`，只提取头文件和许可。

主要编译参数为 `/std:c++14 /EHsc /MD /O2 /openmp /utf-8 /DWINSTD /DNPROCS=2 /DNOMINMAX /D_WIN32_WINNT=0x0601 /DBOOST_ALL_NO_LIB /DBOOST_ERROR_CODE_HEADER_ONLY`，链接 `ws2_32.lib`、`mswsock.lib`，EXE 链接本机构建的 `ai.lib`。Boost.System 使用该版本支持的头文件实现，无需另行全局安装 Boost 二进制库。完整参数会保存在构建目录的 `ai.arguments.json` 与 `main.arguments.json`。

上游 [Windows README](https://github.com/critter-mj/akochan/blob/53188a0b926fbab38177f88c3cd87d554cf412af/README.md#build-with-windows) 使用 MinGW。本次 MSVC 编译暴露了上游跨翻译单元将同一个全局变量分别声明为 const / non-const 的链接差异。脚本显式加两个链接别名，把已有引用绑定到已有定义，不修改源码：

```text
/ALTERNATENAME:?out_console@@3_NB=?out_console@@3_NA
/ALTERNATENAME:?tactics_json@@3V?$array@VJson@json11@@$03@std@@B=?tactics_json@@3V?$array@VJson@json11@@$03@std@@A
```

定义见 `ai_src/selector.cpp:4` 和 `:6`。这项链接适配已通过下列样本运行，但未做 MSVC 与 MinGW 全决策逐项一致性评估。

安装目录包含 `system.exe`、`ai.dll`、`setup_mjai.json`、`params/`、原始 `LICENSE`、第三方声明及 `msvcp140.dll`、`vcruntime140.dll`、`vcruntime140_1.dll`、`vcomp140.dll`。Windows 系统自带 API/UCRT 文件不从系统目录复制。`akochan-installation.json` 的格式为：

```json
{
  "schema": 1,
  "sourceRepository": "https://github.com/critter-mj/akochan",
  "sourceCommit": "53188a0b926fbab38177f88c3cd87d554cf412af",
  "executable": "system.exe",
  "tactics": "setup_mjai.json",
  "threads": 2,
  "files": { "system.exe": "实际构建文件的SHA256", "params/实际相对路径": "实际参数文件的SHA256" }
}
```

这里只示意 `files` 格式，不是可以直接使用的清单。实际生成文件逐一记录所有依赖、许可证与参数的 POSIX 相对路径及 SHA256；清单自身不做递归自哈希。

2026-09-24 已实际运行上面的完整脚本，返回代码 0，安装到默认目录；安装后的原生首摸样本耗时 3.124 秒，输出弃 `N`，stderr 为空。实际 manifest 包含 719 项文件，逐项复核 SHA256 为 0 项不匹配。再次执行同一命令会在任何编译或复制之前明确拒绝非空目标目录，已实测返回代码 1 且保留原有文件。完整日志和安装验证报告位于 `.work/akochan-build/build-8ab40f97e91c43ecba0bbde804fe4a2b/`。

## 原生运行契约

[入口：main.cpp:179–218](https://github.com/critter-mj/akochan/blob/53188a0b926fbab38177f88c3cd87d554cf412af/main.cpp#L179)：

```text
system.exe pipe setup_mjai.json 0
```

`0` 是本机玩家固定 actor 座位，范围 0–3。工作目录必须包含 `params/`，因为 [mjutil.cpp:29](https://github.com/critter-mj/akochan/blob/53188a0b926fbab38177f88c3cd87d554cf412af/ai_src/mjutil.cpp#L29) 按相对路径读取参数。`setup_mjai.json` 是真实上游配置文件，不是模型权重文件的替代品。该提交参数目录实际有 709 个文件，共 86,405 字节。

本次编译 `NPROCS=2`。启动进程必须设置 `OMP_NUM_THREADS=2`、`OMP_THREAD_LIMIT=2`，建议 `OMP_DYNAMIC=FALSE`。上游用 `omp_get_thread_num()` 索引按 `NPROCS` 大小编译的数组；放任 OpenMP 使用更多线程存在越界风险。这是本机引擎契约，不是性能建议。

stdin 每行一个 **UTF-8 无 BOM** JSON 事件。`pipe` 没有启动欢迎信息、握手或逐事件 ACK。它先记录事件，然后在 `can_act:false` 时停止本次响应。因此桥接器可以发送完整真实历史，把所有中间事件标为 false，仅最后的有效触发事件设为 true。以下事件才触发响应：

- 本机 actor 的 `tsumo`。
- 其他 actor 的 `dahai` 或 `kakan`。

其他事件不输出结果。`error` 事件被忽略。stdin EOF 时退出，退出不会额外返回一条动作。实测空输入和所有事件 `can_act:false` 均以代码 0 退出，stdout/stderr 为空。`pipe_detailed` 是不同入口，不检查 `can_act:false`，不能拿它替代同样的历史重放协议。

`start_kyoku` 会清空历史但保留原来的第一个 `start_game`。缺少第一个事件会触及非法迭代器范围。改变规则、玩家身份、场次时应启动新的原生进程；不得只向旧进程追加新 `start_game` 期待其替换旧头部。原生程序也不会把“建议”自动插入历史，后续只能提交实际发生的事件。

stdout 是一个动作数组，并非单个对象。依据 [selector.cpp:19](https://github.com/critter-mj/akochan/blob/53188a0b926fbab38177f88c3cd87d554cf412af/ai_src/selector.cpp#L19) 与 [:84](https://github.com/critter-mj/akochan/blob/53188a0b926fbab38177f88c3cd87d554cf412af/ai_src/selector.cpp#L84)，可能出现 `reach + dahai` 或 `chi/pon + dahai` 两步动作；自摸用 `hora` 且 actor 与 target 相同；不鸣牌用 `none`。桥接层必须保留完整数组。普通 `pipe` 没有候选排名、概率或文本理由，不得自行把旧策略生成的指标标成 akochan 的理由。

规则与历史的最小要求来自 [types.cpp:412–633](https://github.com/critter-mj/akochan/blob/53188a0b926fbab38177f88c3cd87d554cf412af/share/types.cpp#L412)：

- `start_game` 的 `kyoku_first`、`aka_flag` 是必填项。样本是 `kyoku_first:4`、赤牌开启；这不能替代 FFXIV 规则确认。
- `start_kyoku` 包含 `bakaze`、`kyoku`、`honba`、`kyotaku`、`oya`、`scores[4]`、`dora_marker`、`tehais[4]`。本机配牌是真实 13 张牌；其他三家用 13 个 `"?"`。
- 本机摸牌必须已识别；其他家的摸牌是 `"?"`，公开弃牌包含正确 `tsumogiri`。
- 副露需真实 `actor`、`target`、`pai`、`consumed`；立直声明与 `reach_accepted` 分开记录，后者实际扣 1000 分并增加供托；宝牌增加等事件不可遗漏。
- actor 是整个牌局固定座位编号，不能每次根据当前东南西北重新编号。

使用者正常可见的 UI 不能确认的字段必须使实时分析停止。当前国服 49 字段来源与真实事件时间顺序尚未完成验证，不能把上述契约等同于当前插件已能从国服采集全部字段。

## 已实际运行的公开样本

样本来自固定提交的 [haifu_log_sample.json](https://github.com/critter-mj/akochan/blob/53188a0b926fbab38177f88c3cd87d554cf412af/haifu_log_sample.json)。原文件包含完整信息，本次仅把其他三家起手牌和摸牌改成 `"?"` 来隐藏非公开信息，保留其余真实已发生的事件；没有伪造动作，没有使用旧策略输出代替原生结果。样本输出由 akochan 产生。

| 截至上游文件行号 | 场景 | 实际原生输出 | 本机单次耗时 |
|---|---|---|---:|
| 3 | 本机首摸 `6m` | 弃 `N`，非摸切 | 3.041 s |
| 6 | 对手首弃 `F` | `none` | 2.109 s |
| 35 | 本机摸 `6p` | 弃 `6p`，摸切 | 0.714 s |
| 82 | 上家弃 `C` | 碰 `C`（消耗两个 `C`），然后弃 `3m` | 0.814 s |
| 91 | 本机摸 `7m` | `hora`，actor=target=0 | 0.301 s |
| 185 | 跨局后已有对手立直及接受事件 | 弃 `3m`，摸切 | 0.192 s |

六个实际分析均退出代码 0，stderr 为空。这些是独立进程的单次测量，不能当成 P50/P95、游戏实时时限或国服帧时间测量。尚未用这个样本验证杠、荣和、立直推荐、候选分数质量或全部复杂规则。

本机开发证据位于 `.work/akochan-build/run-results.json`、`public-history-results.json`、`run-sample.py`、`run-public-history.py`。安装脚本会独立重建并运行首摸样本，在各次构建目录写 `upstream-first-draw-public.jsonl`、`selftest-result.json`、`installation-result.json` 与完整编译日志。上述临时上游数据和二进制不放入发布 ZIP。

状态：原生 CPU 构建 **构建通过**；上述公开样本与 pipe 行为 **离线验证通过**；国服场况完整读取、真实历史重建、国服建议及游戏动作 **待验证**。没有国服实机牌局样本，不能标为“国服实机验证通过”。
