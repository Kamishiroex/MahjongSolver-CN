using System.Text;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Mahjong.Plugin.CN.Readers;

/// <summary>CN 2026.09.15 / ClientStructs 243dc41e4. Framework thread only.
/// Uses the GoldSaucer agent and the actual labelled radio button; no guessed tab callback.</summary>
internal sealed unsafe class CnRatingProfileAccess(Func<string, nint> lookup) : IRatingProfileAccess
{
    private bool ownsWindow;
    private nint ownedAddress;
    private ushort ownedId;
    private short lastSelectedTab;
    private AtkUnitBase* Root => (AtkUnitBase*)lookup("GSInfo");
    public bool IsOpen { get { var root = Root; return root != null && root->IsVisible; } }
    public bool IsMahjongSelected
    {
        get
        {
            RememberWindow();
            var page = (AtkUnitBase*)lookup("GSInfoEmj");
            var tab = FindMahjongTab(Root);
            bool selected = IsOpen && page != null && page->IsVisible && page->IsReady && page->Alpha > 0 &&
                tab != null && tab->IsSelected;
            var agent = AgentGoldSaucer.Instance();
            if (selected && ownsWindow && agent != null) lastSelectedTab = agent->GoldSaucerSelectedTab;
            return selected;
        }
    }
    public bool Open()
    {
        ForgetOwnership();
        var agent = AgentGoldSaucer.Instance();
        if (agent == null || IsOpen || agent->IsAgentActive() || !agent->IsActivatable()) return false;
        lastSelectedTab = agent->GoldSaucerSelectedTab;
        ownsWindow = true;
        agent->Show();
        RememberWindow();
        return true;
    }
    private void RememberWindow()
    {
        if (!ownsWindow || ownedAddress != 0) return;
        var root = Root; var agent = AgentGoldSaucer.Instance();
        if (root == null || agent == null || root->Id == 0 || root->Id != agent->AddonId) return;
        ownedAddress = (nint)root; ownedId = root->Id;
    }
    public bool SelectMahjong()
    {
        RememberWindow();
        var root = Root;
        var tab = FindMahjongTab(root);
        if (tab == null) return false;
        if (!tab->IsSelected && !tab->SetActive()) return false;
        var agent = AgentGoldSaucer.Instance();
        if (agent != null) lastSelectedTab = agent->GoldSaucerSelectedTab;
        return true;
    }
    public void CloseOwned()
    {
        var root = Root; var agent = AgentGoldSaucer.Instance();
        bool close = ownsWindow && ownedAddress != 0 && root != null && (nint)root == ownedAddress &&
            root->Id == ownedId && root->IsVisible && agent != null && agent->AddonId == ownedId &&
            agent->GoldSaucerSelectedTab == lastSelectedTab;
        ForgetOwnership();
        if (close) agent->Hide();
    }
    public void ForgetOwnership() { ownsWindow = false; ownedAddress = 0; ownedId = 0; }

    internal static AtkComponentRadioButton* FindMahjongTab(AtkUnitBase* root)
    {
        if (root == null || !root->IsVisible || !root->IsReady || root->Alpha == 0 ||
            root->UldManager.NodeList == null || root->UldManager.NodeListCount > 256) return null;
        AtkComponentRadioButton* found = null;
        for (int i = 0; i < root->UldManager.NodeListCount; i++)
        {
            var node = root->UldManager.NodeList[i];
            if (node == null || (ushort)node->Type < 1000 || !Visible(node)) continue;
            var component = ((AtkComponentNode*)node)->Component;
            if (component == null || component->GetComponentType() != ComponentType.RadioButton) continue;
            var button = (AtkComponentRadioButton*)component;
            if (!button->IsEnabled || button->ButtonTextNode == null) continue;
            var text = &button->ButtonTextNode->NodeText;
            if ((byte*)text->StringPtr == null || text->BufUsed is < 2 or > 65) continue;
            var bytes = new ReadOnlySpan<byte>(text->StringPtr, (int)text->BufUsed);
            if (bytes[^1] != 0) continue;
            string label = Encoding.UTF8.GetString(bytes[..^1]);
            if (label is not ("麻将" or "多玛方城" or "多玛方城战")) continue;
            if (found != null) return null;
            found = button;
        }
        return found;
    }
    private static bool Visible(AtkResNode* node)
    {
        for (int depth = 0; node != null; node = node->ParentNode)
            if (++depth > 32 || !node->NodeFlags.HasFlag(NodeFlags.Visible) || node->Color.A == 0 || node->IsDrawDisabled) return false;
        return true;
    }
}
