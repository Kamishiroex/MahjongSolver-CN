using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using Mahjong.Plugin.CN.Automation;
using Mahjong.Plugin.CN.Diagnostics;

namespace Mahjong.Plugin.CN;

public sealed partial class Plugin
{
    internal static AddonProbe AttachCurrentMatchRules(AddonProbe addon, Func<PublicMatchRulesCandidate?> read) =>
        addon is { Name: "Emj", Present: true, Visible: true, Ready: true, Error: null }
            ? addon with { PublicMatchRules = read() } : addon with { PublicMatchRules = null };

    // Framework thread, after the fixed runtime gate. Never substitute the user's queue preference.
    private static unsafe PublicMatchRulesCandidate? ReadCurrentMatchRules()
    {
        var game = GameMain.Instance();
        if (game == null || game->TerritoryLoadState != 2 || game->CurrentTerritoryTypeId != 831)
            return null;
        uint id = game->CurrentContentFinderConditionId;
        if (MahjongDuties.Find(id) is not { } duty ||
            !DataManager.GetExcelSheet<ContentFinderCondition>().TryGetRow(id, out var row) ||
            !CnMatchmakingAdapter.MatchesSheet(duty, row)) return null;
        return new(id, id is >= 766 and <= 769 ? "east" : "hanchan",
            id is not (650 or 769), "PUBLIC_CURRENT_MAHJONG_DUTY_VERIFIED");
    }
}
