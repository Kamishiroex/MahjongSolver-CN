using System.Text;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Mahjong.Plugin.Dalamud.GameState.Variants;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class ActiveListLabelsTests
{
    [Theory]
    [InlineData("吃")]
    [InlineData("九种幺九倒牌")]
    public unsafe void Active_items_exclude_old_renderer_and_disable_incomplete_or_disabled_menus(string firstLabel)
    {
        byte[] chiBytes = Encoding.UTF8.GetBytes(firstLabel + "\0"), passBytes = Encoding.UTF8.GetBytes("放弃\0");
        fixed (byte* chi = chiBytes)
        fixed (byte* pass = passBytes)
        {
            AtkComponentList list = default;
            AtkComponentList.ListItem* entries = stackalloc AtkComponentList.ListItem[3];
            AtkComponentListItemRenderer* renderers = stackalloc AtkComponentListItemRenderer[3];
            AtkComponentNode* owners = stackalloc AtkComponentNode[3];
            AtkTextNode* texts = stackalloc AtkTextNode[3];
            AtkResNode** nodes = stackalloc AtkResNode*[3];
            new Span<AtkComponentList.ListItem>(entries, 3).Clear();
            new Span<AtkComponentListItemRenderer>(renderers, 3).Clear();
            new Span<AtkComponentNode>(owners, 3).Clear();
            new Span<AtkTextNode>(texts, 3).Clear();
            for (int i = 0; i < 3; i++)
            {
                entries[i].AtkComponentListItemRenderer = &renderers[i];
                renderers[i].ListItemIndex = i;
                renderers[i].OwnerNode = &owners[i];
                owners[i].NodeFlags = NodeFlags.Visible | NodeFlags.Enabled;
                texts[i].Type = NodeType.Text;
                texts[i].NodeText.StringPtr = i == 0 ? chi : pass;
                texts[i].NodeText.BufUsed = i == 0 ? chiBytes.Length : passBytes.Length;
                nodes[i] = (AtkResNode*)&texts[i];
                renderers[i].UldManager.NodeList = nodes + i;
                renderers[i].UldManager.NodeListCount = 1;
            }
            list.ListLength = 2; list.AllocatedItemRendererListLength = 3;
            list.ItemRendererList = entries; list.IsItemInteractionEnabled = true;
            Assert.Equal(new[] { firstLabel, "放弃" }, BaseEmjVariant.ReadActiveListLabels(&list));
            entries[1].IsDisabled = true;
            Assert.Empty(BaseEmjVariant.ReadActiveListLabels(&list));
            entries[1].IsDisabled = false; renderers[1].ListItemIndex = 2;
            Assert.Empty(BaseEmjVariant.ReadActiveListLabels(&list));
            renderers[1].ListItemIndex = 1; list.IsItemInteractionEnabled = false;
            Assert.Empty(BaseEmjVariant.ReadActiveListLabels(&list));
        }
    }
}
