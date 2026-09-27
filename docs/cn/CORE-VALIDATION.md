# 维护基线与关键回归

国服入口与 RuntimeIdentity 限定客户端及框架版本；读取器在游戏线程复制公开字段；观察器维护来源、稳定性和局界；策略异步调用本地引擎；自动循环只在当前合法窗口执行带上下文检查的操作。CN 不启用上游遥测。推理、读牌、动作效果分别验证。

| 必须保留的回归 | 位置与防止的问题 |
|---|---|
| 区域、资源、缩放与排序 | `tests/Mahjong.Plugin.CN.Tests`、`PublicMonitorSessionTests`：壳与牌面、赤五、四家区域；未知不当空或 false |
| 事件连续性 | `PublicRoundEventTrackerTests`、`PublicRiverEventTrackerTests` 及 own-hand/call/riichi/furiten 测试：跨局重置，不把过渡与缺口编成完整牌谱 |
| AI 输入与协议 | `AkochanGlobalPolicyTests`、`MortalMoveProtocolTests` 和原生 probes：过期请求不动作，分别验证凡夫单步吃碰/放弃与 akochan 批次 |
| 和牌及分步动作 | `AutoPlayPendingPolicyTests`、`DispatchProgressTests`：和牌不被吃碰/放弃覆盖；立直关菜单后完成原弃牌，防重复提交 |
| 局间等待与退出 | `NextHandTransitionTests`、`RuntimeModeLifecycleTests`：持续等待仅限已观察到的结算/换局，手动暂停、退出与实际错误撤销输入 |
| 模型初始化 | `AkochanGlobalPolicyTests`、`tools/Mahjong.Cn.MortalProbe`：提前预热，取消旧请求不杀热模型，不接收旧响应 |
| 日志与恢复 | journal/rotation/recovery 测试与 `tools/Mahjong.Cn.JournalReplay`：详细错误码不能破坏日志，按整场清理，恢复与桌面一致 |
| 出包完整性 | `tests/test_source_package.py`、实际源码包重建：未审核文件不入包，源码与构建提交一致 |

`docs/cn/evidence` 及测试样例保留资源目录、脱敏公开输入和关键状态序列，不是原始内存转储。日期和公开资源哈希用于追溯版本；单次观测不能证明其他状态。源码/样例直接引用的证据名称保持稳定。

任务书、阶段进度与重复发布叙述不再维护。当前行为见 [README-CN.md](../../README-CN.md)，边界见 [公开输入缺口](PUBLIC-INPUT-GAPS-20260925.md)、[完整历史契约](LIVE-AI-INPUT-COMPLETENESS.md)。近似风险分数、历史缺失及国服规则差异不得被写成已解决。

验证入口 `scripts/build-cn.ps1` 记录本次实际执行的 .NET/Python 测试、可选原生样例、日志重放和源码包重建。`-VerifyOnly` 不生成分发包；`-SkipTests` 不是全部回归通过。实机流程见 [ACCEPTANCE.md](ACCEPTANCE.md)，读牌正确与建议质量分开评价。

## 界面、权限与任务

五页为总览、任务、战绩与记录、设置、诊断。经典布局仅由外观设置选择，不因未验证退回；窗口持久化 ID、命令、配置和 InternalName 保持兼容。任务配置使用不可变快照，暂停撤销操作与排队许可，继续同一任务保留计数和时间预算；正常完成与异常停止分开。

测试权限只约束可选测试后端。验证、导入、开窗和主题切换均不授予运行许可；标准策略行为、零测试工厂调用、测试到期及迟到结果隔离必须持续回归。导出隐私测试与日志哈希链验证保留，脱敏派生文件不能冒称原始记录。

本人评分来源为锁定版本 `AddonGSInfoEmj` 的 CurrentRating/HighestRating/Rank，偏移 0x240/0x248/0x250、文本节点 23/24/7。资料页三字段曾人工核对；整场后刷新、最终名次与评分目标禁止当作已验证。换角色、退出及不可见页面使用明确未知/缓存状态。

评分刷新使用同一固定提交的 [AgentGoldSaucer](https://github.com/Dalamud-DailyRoutines/FFXIVClientStructs/blob/243dc41e4d71f350cd80aa5eba8c75517f3d5154/FFXIVClientStructs/FFXIV/Client/UI/Agent/AgentGoldSaucer.cs) 与 AgentInterface.Show/Hide。2026-09-27 在该国服客户端实际核对：主窗口为 `GoldSaucerInfo`（不是 `GSInfo`），分页文字为“方城战”（不是“多玛方城战”），节点 8 为 RadioButton，注册 ButtonClick 事件的 Listener 指向主窗口、Target 指向按钮，采样 Param 为 6。实现按唯一标签和组件类型定位并复制当前注册事件，**不硬编码节点号或事件参数**，也不以 SetActive 高亮当作分页切换成功；最终必须看到 `GSInfoEmj` 就绪并获得稳定读数。`RatingProfileAccessTests` 保留这些静态导航事实，并覆盖缺少事件、重复、错误 Listener/Target、隐藏/禁用按钮和损坏指针关联。

只读当前/最高评分与段位，导航只检查金碟页静态按钮标签。请求在框架线程逐帧运行、两次读数稳定才接受；只关闭自有窗口，换场景/角色、全停、超时会取消。2026-09-27 用户实机确认旧入口“刷新本人评分”成功切到方城战页、读到评分并关闭窗口；这是导航实现的历史证据。当前标准入口仅读已打开的页，自动导航改为测试版操作能力下的明确授权。整场后台刷新还要求同一任务的有效授权，仅由麻将 `IDutyState.DutyCompleted` 触发，退桌后才尝试打开；不会将刷新视为服务器评分已结算的证明。`RatingRefreshTests` 覆盖等待、取消、权限撤销、迟到结果、窗口归属和读数一致性；新权限流程及整场后评分关联仍须按验收步骤实测。

界面支持自然高度卡片、窄窗和紧凑模式；100/150/200%由真实 ImGui 测试宿主演练，宿主不是实机。旧基线曾取得未验证、无模型的国服界面截图，不等于本版完整对局验收。截图、现场记录和个人配置不随源码公开。完整验证以本次 build-manifest.json 和测试结果为准，不能沿用旧次数。

战绩摘要只读取有依据的整场事件，不虚构名次、评分变化或胜率。下载统计与自愿在线客户端默认关闭，失败不影响本地任务；在线服务未部署。普通摘要使用中性来源，真实后端留在本地技术详情。
