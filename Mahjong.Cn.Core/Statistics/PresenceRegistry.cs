namespace Mahjong.Cn.Statistics;

/// <summary>Server-receive monotonic time, random sessions, bounded volatile state. No identity fields.</summary>
public sealed class PresenceRegistry
{
    private readonly Dictionary<Guid,double> sessions=[];
    private readonly object gate=new();
    public const int TtlSeconds=180;
    public int Touch(Guid id,double receivedSeconds)
    {
        lock(gate)
        {
            Prune(receivedSeconds);
            if(id==Guid.Empty||!double.IsFinite(receivedSeconds))return 400;
            if(sessions.TryGetValue(id,out var old)&&receivedSeconds-old<30)return 429;
            if(!sessions.ContainsKey(id)&&sessions.Count>=10000)return 503;
            sessions[id]=receivedSeconds;return 204;
        }
    }
    public void Remove(Guid id){lock(gate)sessions.Remove(id);}
    public int Count(double now){lock(gate){Prune(now);return sessions.Count;}}
    private void Prune(double now){foreach(var id in sessions.Where(p=>now-p.Value>=TtlSeconds).Select(p=>p.Key).ToArray())sessions.Remove(id);}
}
