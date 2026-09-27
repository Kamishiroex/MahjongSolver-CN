using System.Net.Http.Json;
using System.Text.Json;

namespace Mahjong.Cn.Statistics;

public sealed class OptionalPresenceClient(HttpClient http)
{
    private readonly Guid sessionId=Guid.NewGuid(); // Per load, never persisted.
    public static bool ValidEndpoint(Uri? uri)=>uri is {IsAbsoluteUri:true} && uri.UserInfo.Length==0 && uri.Query.Length==0 && uri.Fragment.Length==0 &&
        (uri.Scheme=="https" || (uri.Scheme=="http"&&uri.IsLoopback));
    public async Task<bool> HeartbeatAsync(Uri? endpoint,bool consent,CancellationToken cancellation)
    {
        if(!consent||!ValidEndpoint(endpoint))return false;
        using var response=await http.PostAsJsonAsync(new Uri(endpoint!,"v1/presence"),new {sessionId},cancellation);
        response.EnsureSuccessStatusCode();return true;
    }
    public async Task<int?> ReadCountAsync(Uri? endpoint,bool consent,CancellationToken cancellation)
    {
        if(!consent||!ValidEndpoint(endpoint))return null;
        using var response=await http.GetAsync(new Uri(endpoint!,"v1/public-stats"),HttpCompletionOption.ResponseHeadersRead,cancellation);
        response.EnsureSuccessStatusCode();
        await using var stream=await response.Content.ReadAsStreamAsync(cancellation);
        byte[] bytes=new byte[4097];int count=0,n;
        while(count<bytes.Length&&(n=await stream.ReadAsync(bytes.AsMemory(count),cancellation))>0)count+=n;
        if(count>4096)throw new InvalidDataException("PRESENCE_RESPONSE_LIMIT");
        using var doc=JsonDocument.Parse(bytes.AsMemory(0,count));
        var value=doc.RootElement.GetProperty("activeSessions");
        if(!value.TryGetInt32(out var active)||active<0||active>10000)throw new InvalidDataException("PRESENCE_COUNT_INVALID");
        return active;
    }
}
