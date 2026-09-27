namespace Mahjong.Plugin.CN.Access;

/// <summary>In-memory permission for one explicit task; never serialized or inferred from settings.</summary>
internal sealed record TaskOperationGrant(Guid RunId, string CharacterContext, int Generation)
{
    internal bool Allows(bool qualified, bool enabled, Guid runId, string context, int generation) =>
        qualified && enabled && RunId != Guid.Empty && RunId == runId && CharacterContext.Length > 0 &&
        CharacterContext == context && Generation == generation;
}
