namespace Mahjong.Plugin.CN.Automation;

internal enum RecoveryCheck { Waiting, Resume, Abandon }

/// <summary>A bounded retry of an existing intent. Cannot create task/input authority.</summary>
internal sealed class TransientRecoveryGate
{
    private readonly Queue<double> attempts=[];
    private string? binding, candidate;
    private double started,stableSince;
    internal bool Pending => binding is not null;
    internal string Code { get; private set; }="RECOVERY_IDLE";
    internal int Attempts => attempts.Count;
    internal static bool Eligible(string reason)
    {
        string code=reason.Split([':', '：'],2)[0];
        if(code=="AKOCHAN_BLOCKED" && reason.Length>code.Length)
        {
            string detail=reason[(code.Length+1)..].Trim();
            return detail is "GLOBAL_AI_TIMEOUT" or "GLOBAL_AI_PROCESS_Timeout" or
                "GLOBAL_AI_PROCESS_EndOfStream" or "GLOBAL_AI_PROCESS_TransportFailure";
        }
        return code is "AUTO_SNAPSHOT_UNAVAILABLE" or "AUTO_STATE_INCONSISTENT" or
            "AUTO_QUEUE_READ_FAILED" or "TABLE_OBSERVATION_ERROR" or "AUTO_NEXT_HAND_TIMEOUT" ||
            code=="READ_OR_POLICY_ERROR" && reason.Contains("READ_FAILED",StringComparison.Ordinal) &&
                !reason.Contains("SCHEMA_MISMATCH",StringComparison.Ordinal);
    }
    internal bool Begin(string reason,string context,double now)
    {
        if(!Eligible(reason) || string.IsNullOrEmpty(context) || !double.IsFinite(now))return false;
        while(attempts.TryPeek(out double time) && now-time>=60)attempts.Dequeue();
        if(attempts.Count>=3){Code="RECOVERY_RETRY_LIMIT";return false;}
        attempts.Enqueue(now);binding=context;started=now;candidate=null;Code="RECOVERY_READING";return true;
    }
    internal RecoveryCheck Observe(string context,string? state,double now,bool ready,bool inputUncertain)
    {
        if(!Pending)return RecoveryCheck.Abandon;
        if(context!=binding || !double.IsFinite(now) || now<started)
        {Cancel("RECOVERY_AUTHORITY_CHANGED");return RecoveryCheck.Abandon;}
        if(now-started>8)
        {Cancel(inputUncertain?"RECOVERY_PREVIOUS_INPUT_UNCONFIRMED":"RECOVERY_READ_TIMEOUT");return RecoveryCheck.Abandon;}
        if(inputUncertain || !ready || state is null)
        {candidate=null;Code=inputUncertain?"RECOVERY_PREVIOUS_INPUT_UNCONFIRMED":"RECOVERY_READING";return RecoveryCheck.Waiting;}
        if(candidate!=state){candidate=state;stableSince=now;return RecoveryCheck.Waiting;}
        if(now-stableSince<.3)return RecoveryCheck.Waiting;
        Cancel("RECOVERY_READY");return RecoveryCheck.Resume;
    }
    internal void Cancel(string code="RECOVERY_CANCELLED") {binding=candidate=null;Code=code;}
}
