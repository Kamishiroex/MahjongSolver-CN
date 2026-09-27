# 动作菜单：活动列表项与预分配渲染节点

适用国服客户端 `2026.09.15.0000.0000`、ClientStructs 提交 `f824354f4a6a2b1cd16cc8fcb670c7a66bf64880`。此次修改只读取公开动作菜单，不更改操作回调、自动操作或 AI 就绪条件。

## 实际发现

`mjcn-live-20260924-091538` 连续采集的第 2 帧（UTC `2026-09-24T09:15:39.2507222+00:00`）输出了 7 个节点候选：自摸、放弃，以及 5 个文字不可解码的克隆节点。原始最小输出、来源时间与完整事件文件 SHA-256 保存在 [action-menu-preallocated-20260924.json](evidence/action-menu-preallocated-20260924.json)。没有保存地址、账号或聊天内容。

后续旧输出还出现 `Ron,null,null,null,null,Pass,Pass` 等组合，其中有效文字也可能来自复用的旧渲染节点。相同 Y 坐标、空文字、可见标志或正确中文标签，都不能单独证明该节点当前属于菜单。旧代码将 `Emj/104/3` 下所有可见克隆当成活动项，因而报告虚假的额外行并使菜单一直处于 Partial。

**这批旧记录没有列表长度、活动项与 renderer 的绑定元数据，不能离线补出这些值，也不能据它声称新读取器已通过国服实测。** 最小样本是旧缺陷的真实复现依据，不是新逻辑的实测验收。

## 固定源码依据

使用本地已核对的同一 ClientStructs 提交声明，通过 `Marshal.OffsetOf` 和声明类型读取；没有新增猜测偏移或调用游戏原生函数。

| 来源 | 此次使用的已声明字段及含义 |
|---|---|
| [AtkComponentNode](https://github.com/ottercorp/FFXIVClientStructs/blob/f824354f4a6a2b1cd16cc8fcb670c7a66bf64880/FFXIVClientStructs/FFXIV/Component/GUI/AtkComponentNode.cs) | `Component` 指向该组件节点对应的组件实例 |
| [AtkComponentBase](https://github.com/ottercorp/FFXIVClientStructs/blob/f824354f4a6a2b1cd16cc8fcb670c7a66bf64880/FFXIVClientStructs/FFXIV/Component/GUI/AtkComponentBase.cs) | `OwnerNode` 反向指向所属组件节点 |
| [AtkComponentList](https://github.com/ottercorp/FFXIVClientStructs/blob/f824354f4a6a2b1cd16cc8fcb670c7a66bf64880/FFXIVClientStructs/FFXIV/Component/GUI/AtkComponentList.cs) | `ListLength`、`AllocatedItemRendererListLength`、`ItemRendererList`、`FirstVisibleItemIndex`、`NumVisibleItems`、更新/滚动刷新标志；每项 `ListItem` 含 renderer 指针及 `IsDisabled`；`IsItemInteractionEnabled` 注释明确控制项目悬停/点击 |
| [AtkComponentListItemRenderer](https://github.com/ottercorp/FFXIVClientStructs/blob/f824354f4a6a2b1cd16cc8fcb670c7a66bf64880/FFXIVClientStructs/FFXIV/Component/GUI/AtkComponentListItemRenderer.cs) | `ListItemIndex` 表示该 renderer 当前绑定的逻辑项目；列表 `UpdateListItems` 注释明确更新 renderer 位置、可见性和 `ListItemIndex` |
| [AtkComponentButton](https://github.com/ottercorp/FFXIVClientStructs/blob/f824354f4a6a2b1cd16cc8fcb670c7a66bf64880/FFXIVClientStructs/FFXIV/Component/GUI/AtkComponentButton.cs) | `IsEnabled` 使用 `OwnerNode.NodeFlags.Enabled`；读取器复制此标志，不执行属性中的原生指针解引用 |

固定 `Emj/104(1052)/3(1030)/row(1029)/4(Text)` 父链、尺寸及可见性检查继续保留。新增处理顺序：

1. 验证列表组件及 OwnerNode 双向关系，要求 `1 <= ListLength <= 8` 且长度不超过已分配容量；容量上限 64。
2. 暂不接受更新中、滚动刷新中或首可见项不是 0 的列表；不会将可能未完整绘制的菜单宣布为完整。
3. 只遍历 `ItemRendererList[0..ListLength)`；要求每项 renderer 唯一、`ListItemIndex` 等于项目索引、OwnerNode 存在于已验证的公开行节点中，且该行的 `Component` 反向匹配 renderer。
4. 只有上述活动项才进入原有文本白名单解析。其余预分配节点不会被读取文字；即使它们仍保留有效动作标签，也不进入菜单。
5. 逐项启用状态为 `!ListItem.IsDisabled && renderer Enabled && IsItemInteractionEnabled`；诊断中分别记录三个来源。
6. 完成后重读列表元数据、活动项绑定及禁用状态。变化即返回 `ACTION_MENU_LIST_CHANGED_DURING_READ` 并丢弃本次候选动作。

新增 `ListState` 以及每行的 `ListItemIndex`、`ListItemDisabled`、`RendererEnabled`、`ListInteractionEnabled` 均为无地址标量。索引只描述当前 UI 列表绑定，**不解释为游戏 callback 参数**。`CompleteLegalActions` 和 `ActionOccurred` 仍为 false；菜单出现并不证明执行成功，也不提供吃牌组合所消耗的精确牌。

## 验证结果与范围

执行命令：

```powershell
& ./.work/dotnet/dotnet.exe test tests/Mahjong.Plugin.CN.Tests/Mahjong.Plugin.CN.Tests.csproj -c Release --filter 'FullyQualifiedName~PublicActionMenuReaderTests' --nologo
& ./.work/dotnet/dotnet.exe test tests/Mahjong.Plugin.CN.Tests/Mahjong.Plugin.CN.Tests.csproj -c Release --no-restore --nologo
```

实际结果：动作菜单定向测试 **37 通过**；整个国服读取器测试集 **875 通过，0 失败、0 跳过**。回归包含：7 个预分配 renderer 只有 2 个活动项（其余 5 行故意填入有效“碰”标签）、列表/项目禁用覆盖节点 Enabled、索引/OwnerNode/Component 不匹配、重复 renderer、更新或滚动中、容量错误、读到一半绑定改变，以及原有父链/隐藏/标签约束。

状态：**已实现、离线验证通过；新的活动 renderer 元数据及完整菜单仍待国服实机验证。** 此修复不重写旧日志中的未知字段，不发布安装包，不打开实时 AI 或未验证操作。
