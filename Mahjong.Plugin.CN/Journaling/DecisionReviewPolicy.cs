using System.Security.Cryptography;
using System.Text.Json;
using Mahjong.Core;
using Mahjong.Engine;
using Mahjong.Policy.Abstractions;
using Mahjong.Policy.Efficiency;

namespace Mahjong.Plugin.CN.Journaling;

/// <summary>Observer only: returns the same choice and preserves the independent-state marker.</summary>
internal class DecisionReviewPolicy : IPolicy, IRefreshablePolicy, IDisposable
{
    private readonly IPolicy inner;
    private readonly Action<string, StateSnapshot, ActionChoice, ScoredDiscard[]?> observe;
    private ScoredDiscard[]? candidates;
    private string? previous;
    private Action<double>? timing;
    internal DecisionReviewPolicy(IPolicy inner, Action<string, StateSnapshot, ActionChoice, ScoredDiscard[]?> observe)
    {
        this.inner=inner; this.observe=observe;
        if(inner is EfficiencyPolicy efficiency) efficiency.CandidatesScored += Capture;
    }
    internal static IPolicy Wrap(IPolicy inner, Action<string, StateSnapshot, ActionChoice, ScoredDiscard[]?> observe,Action<double>? timing=null)
    {
        DecisionReviewPolicy policy=inner is IIndependentPublicStatePolicy ? new Independent(inner,observe) : new DecisionReviewPolicy(inner,observe);
        policy.timing=timing;return policy;
    }
    private sealed class Independent(IPolicy inner, Action<string,StateSnapshot,ActionChoice,ScoredDiscard[]?> observe)
        : DecisionReviewPolicy(inner,observe), IIndependentPublicStatePolicy;
    private void Capture(ScoredDiscard[] value) => candidates=value.ToArray();
    public bool RequiresRefresh => inner is IRefreshablePolicy { RequiresRefresh:true };
    public ActionChoice Choose(StateSnapshot state)
    {
        candidates=null;
        long started=System.Diagnostics.Stopwatch.GetTimestamp();
        var choice=inner.Choose(state);
        double elapsed=System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        // Pending is already represented by the engine request; do not add a per-frame trace.
        if(choice.Reasoning?.StartsWith("AKOCHAN_PENDING:",StringComparison.Ordinal)==true)return choice;
        try
        {
            timing?.Invoke(elapsed);
            string input=InputHash(state);
            string key=input+JsonSerializer.Serialize(choice);
            if(key!=previous) { previous=key; observe(input,state,choice,candidates); }
        }
        catch { /* Review failures must not change the decision or arm/stop input. */ }
        return choice;
    }
    internal static string InputHash(StateSnapshot state) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(state with { UraDoraIndicators=[] })));
    public void Invalidate() { previous=null; (inner as IRefreshablePolicy)?.Invalidate(); }
    public void Dispose()
    {
        if(inner is EfficiencyPolicy efficiency)efficiency.CandidatesScored-=Capture;
        (inner as IDisposable)?.Dispose();
    }
}
