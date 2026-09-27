namespace Mahjong.Plugin.CN.Access;

/// <summary>In-memory permission for one explicit task; never serialized or inferred from settings.</summary>
internal sealed record TaskOperationGrant(Guid RunId, string CharacterContext, int Generation)
{
    internal bool Allows(bool qualified, Guid runId, string context, int generation) =>
        qualified && RunId != Guid.Empty && RunId == runId && CharacterContext.Length > 0 &&
        CharacterContext == context && Generation == generation;
}

/// <summary>A task started while qualified may finish, including an unlimited loop.
/// This in-memory entitlement never starts work and cannot transfer to a new task.</summary>
internal sealed record QualifiedTaskAccess(Guid RunId, string CharacterContext,
    Mahjong.Cn.Tasks.TaskPlan Plan, string Source, int SourceGeneration)
{
    internal bool Matches(Mahjong.Cn.Tasks.TaskRun? run, string source, int generation) =>
        run is not null && RunId != Guid.Empty && run.RunId == RunId && CharacterContext.Length > 0 &&
        run.CharacterContext == CharacterContext && run.Code != "USER_ENDED" &&
        run.Plan == Plan && Source == source && SourceGeneration == generation;
}
