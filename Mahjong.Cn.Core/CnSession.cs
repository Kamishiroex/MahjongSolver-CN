using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace Mahjong.Cn;

/// <summary>Only a compiled, version-specific profile may assert ReadVerified. Configuration must not set it.</summary>
public sealed record CnCompatibilityProfile(string GameVersion, int DalamudApi,
    string? ReadEvidence = null, bool ReadVerified = false, string Region = "CN");

public sealed record SessionStatus(string Code, string Message, int StableSamples,
    ImmutableArray<ValidationIssue> Issues)
{
    public bool Ready => Code == "ready";
}

/// <summary>No memory access. A failed gate always invalidates the previous suggestion.</summary>
public sealed class CnSession(CnCompatibilityProfile profile, string gameVersion, int dalamudApi)
{
    private long lastCapture = -1;
    private string? fingerprint;
    private int stableSamples;
    private bool stopped;
    private string? lastRound;
    private string? lastRoundMetadata;
    private int lastTurn;
    private int lastWall;
    private long lastPointsWithDeposits;
    private readonly HashSet<string> retiredRounds = [];
    public SessionStatus Status { get; private set; } = new("idle", "等待牌局；自动操作关闭", 0, []);
    public VisibleSnapshot? CurrentSnapshot { get; private set; }
    public CnSuggestion? CurrentSuggestion { get; private set; }
    public bool AutomationEnabled => false;

    public SessionStatus Observe(ReadObservation observation)
    {
        CurrentSuggestion = null;
        CurrentSnapshot = null;
        if (stopped) return Reset("stopped", "已停止；需手动恢复只读诊断");
        if (observation.CaptureId <= lastCapture) return Reset("stale-capture", "采样序号未递增；已清除旧建议");
        lastCapture = observation.CaptureId;
        if (profile.Region != "CN" || string.IsNullOrWhiteSpace(gameVersion) ||
            profile.GameVersion != gameVersion || profile.DalamudApi != dalamudApi)
            return Reset("version-mismatch", "客户端版本或 Dalamud API 与国服适配档案不匹配");
        if (observation.Phase != ObservationPhase.Active)
        {
            if (observation.Phase == ObservationPhase.OutsideTable) ClearRoundHistory();
            return Reset(observation.Phase switch
            {
                ObservationPhase.OutsideTable => "outside-table",
                ObservationPhase.Transition => "transition",
                _ => "read-error",
            }, observation.Phase switch
            {
                ObservationPhase.OutsideTable => "已离开牌桌；牌局和建议已清除",
                ObservationPhase.Transition => "动画或牌局过渡；等待完整稳定状态",
                _ => observation.Error ?? "牌局读取异常，位置未知",
            });
        }
        if (observation.Snapshot is not { } snapshot) return Reset("read-error", "Active.Snapshot 缺失");
        ValidationResult validation;
        try { validation = SnapshotValidator.Validate(snapshot); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NullReferenceException)
        { return Reset("validation-error", $"SnapshotValidator: {ex.GetType().Name}"); }
        if (!validation.IsValid)
            return Reset("invalid-state", $"{validation.Issues[0].Path}: {validation.Issues[0].Message}", validation.Issues);
        string metadata = $"{snapshot.RoundWind}:{snapshot.HandNumber}:{snapshot.Honba}";
        if (retiredRounds.Contains(snapshot.RoundId!)) return Reset("retired-round", "读取到已结束的旧局；已清除建议");
        if (lastRound == snapshot.RoundId && (metadata != lastRoundMetadata ||
            snapshot.TurnIndex < lastTurn || snapshot.WallRemaining > lastWall))
            return Reset("state-regression", "同一局的局数、巡数或余牌数发生回退；可能是过渡期旧数据");
        // Upstream HandSimulator debits/awards exactly 1000 points per riichi deposit.
        // Use Int64, and observe the initial total rather than assuming a starting score.
        long pointsWithDeposits = snapshot.Seats.Sum(x => (long)x.Score!.Value) + snapshot.RiichiSticks!.Value * 1000L;
        if (lastRound == snapshot.RoundId && pointsWithDeposits != lastPointsWithDeposits)
            return Reset("score-inconsistent", "四家点数总和加立直供托发生矛盾；可能读取到尚未完整更新的点数");
        if (lastRound != snapshot.RoundId && lastRound is not null) retiredRounds.Add(lastRound);
        lastRound = snapshot.RoundId;
        lastRoundMetadata = metadata;
        lastTurn = snapshot.TurnIndex!.Value;
        lastWall = snapshot.WallRemaining!.Value;
        lastPointsWithDeposits = pointsWithDeposits;
        CurrentSnapshot = snapshot;
        string next = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(snapshot)));
        stableSamples = next == fingerprint ? Math.Min(stableSamples + 1, 2) : 1;
        fingerprint = next;
        if (!profile.ReadVerified || string.IsNullOrWhiteSpace(profile.ReadEvidence))
            return Status = new("profile-unverified", "缺少对应国服版本的读取实测证据；建议和所有自动操作禁用", stableSamples, []);
        if (stableSamples < 2)
            return Status = new("stabilizing", "等待第二次独立一致采样；旧建议已清除", stableSamples, []);
        try
        {
            CurrentSuggestion = CnSuggestionBridge.Evaluate(snapshot);
            return Status = new("ready", "读取状态稳定；仅显示建议，自动操作关闭", stableSamples, []);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return Reset("engine-unavailable", $"建议引擎 {ex.GetType().Name}: {ex.Message}"); }
    }

    public void Stop(string reason = "用户停止")
    {
        stopped = true;
        CurrentSnapshot = null;
        CurrentSuggestion = null;
        Reset("stopped", reason);
    }

    public void ResumeReadOnly()
    {
        stopped = false;
        lastCapture = -1;
        CurrentSnapshot = null;
        ClearRoundHistory();
        Reset("idle", "已恢复只读诊断；自动操作仍关闭");
    }

    private void ClearRoundHistory()
    {
        lastRound = null;
        lastRoundMetadata = null;
        retiredRounds.Clear();
    }

    private SessionStatus Reset(string code, string message, ImmutableArray<ValidationIssue> issues = default)
    {
        fingerprint = null;
        stableSamples = 0;
        CurrentSnapshot = null;
        CurrentSuggestion = null;
        return Status = new(code, message, 0, issues.IsDefault ? [] : issues);
    }
}

public enum CnAction { Discard, Chi, Pon, OpenKan, ClosedKan, AddedKan, Riichi, Ron, Tsumo, Pass }
public sealed record ActionCapability(CnAction Action, bool Enabled, string Reason);
public sealed record ActionResult(bool Executed, string Reason);
public interface ICnActionAdapter
{
    ImmutableArray<ActionCapability> Capabilities { get; }
    ActionResult Execute(CnAction action, VisibleSnapshot snapshot, int? handSlot = null);
    void Stop();
}

/// <summary>Intentionally contains no game-operation implementation or callback parameters.</summary>
public sealed class DisabledActionAdapter : ICnActionAdapter
{
    public ImmutableArray<ActionCapability> Capabilities { get; } = Enum.GetValues<CnAction>()
        .Select(x => new ActionCapability(x, false, "国服回调、参数与时序尚无对应版本验证；操作禁用"))
        .ToImmutableArray();
    public ActionResult Execute(CnAction action, VisibleSnapshot snapshot, int? handSlot = null) =>
        new(false, $"{action} 未执行：国服操作能力未验证");
    public void Stop() { }
}
