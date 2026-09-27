using Mahjong.Rules.Scoring;

namespace Mahjong.Rules.Rulesets;

/// <summary>
/// FFXIV Doman uses a one-yaku winning requirement, not a two-han minimum.
/// Official rules: https://na.finalfantasyxiv.com/lodestone/playguide/contentsguide/goldsaucer/doman-mahjong/
/// section 7 lists one-han wins and requires at least one yaku. The scorer checks that
/// requirement before adding dora; lowering the han threshold does not allow no-yaku wins.
/// </summary>
public sealed class DomanRuleSet : IRuleSet
{
    private readonly RiichiRuleSet riichi = new();

    public string Name => "Doman";

    public IReadOnlyList<IYakuRule> YakuRules => riichi.YakuRules;
    public IScoringRule ScoringRule => riichi.ScoringRule;
    public IDoraRule DoraRule => riichi.DoraRule;
    public IFuRule FuRule => riichi.FuRule;

    public bool AllowsRedDora => false;
    public bool AllowsKuitan => true;
    public int MinHan => 1;
    public int KazoeThreshold => ScoringConstants.KazoeYakumanHan;
    public int MaxYakuman => 2;
}
