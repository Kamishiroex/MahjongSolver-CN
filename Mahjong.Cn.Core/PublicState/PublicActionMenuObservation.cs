using System.Collections.Immutable;

namespace Mahjong.Cn.PublicState;

public enum PublicMenuAction { Unknown, Pon, Chi, Kan, Ron, Riichi, Tsumo, Pass, Cancel, Kyushukyuhai }

/// <summary>Current visible renderer only; path/order is not a callback index or submitted game action.</summary>
public sealed record PublicActionMenuOption(string Path, float ScreenY, long StateRevision,
    PublicMenuAction Action, bool? Enabled, string Code);

public sealed record PublicActionMenuObservation(bool Visible, bool AllVisibleRowsDecoded,
    ImmutableArray<PublicActionMenuOption> Rows, string Code)
{
    public bool CompleteLegalActions => false;
    public bool ActionOccurred => false;
}
