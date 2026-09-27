using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mahjong.Cn.Statistics;

public sealed record DownloadObservation(long? Count,DateTimeOffset? ReadAtUtc,string? Error);
/// <summary>Anonymous, opt-in reader. Has no reference to game/task state.</summary>
public sealed class PublicDownloadStats(HttpClient http)
{
    public const string Repository="Kamishiroex/MahjongSolver-CN";
    private const string Api="https://api.github.com/repos/"+Repository;
    private readonly Dictionary<string,(string? Etag,string Json)> pages=[];
    private readonly SemaphoreSlim gate=new(1,1);
    private DownloadObservation cached=new(null,null,"尚未查询");
    private DateTimeOffset nextAttempt;
    public async Task<DownloadObservation> ReadAsync(DateTimeOffset now,CancellationToken cancellation=default)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if(now<nextAttempt)return cached;
            var assets=new HashSet<long>();long total=0;
            for(int page=1;;page++)
            {
                if(page>100)throw new InvalidDataException("PAGINATION_LIMIT");
                using var releases=await Page($"{Api}/releases?per_page=100&page={page}",cancellation);
                if(releases.RootElement.ValueKind!=JsonValueKind.Array)throw new InvalidDataException("RELEASE_SCHEMA");
                foreach(var r in releases.RootElement.EnumerateArray())
                {
                    if(r.GetProperty("draft").GetBoolean()||r.GetProperty("prerelease").GetBoolean())continue;
                    long id=r.GetProperty("id").GetInt64();if(id<=0)throw new InvalidDataException("RELEASE_ID");
                    for(int assetPage=1;;assetPage++)
                    {
                        if(assetPage>100)throw new InvalidDataException("ASSET_PAGINATION_LIMIT");
                        using var list=await Page($"{Api}/releases/{id}/assets?per_page=100&page={assetPage}",cancellation);
                        if(list.RootElement.ValueKind!=JsonValueKind.Array)throw new InvalidDataException("ASSET_SCHEMA");
                        foreach(var a in list.RootElement.EnumerateArray())
                        {
                            string? name=a.GetProperty("name").GetString();
                            if(name is null||!Regex.IsMatch(name,@"\AMahjong\.Plugin\.CN-(?:[0-9]+\.){2,3}[0-9]+\.zip\z"))continue;
                            long assetId=a.GetProperty("id").GetInt64(), count=a.GetProperty("download_count").GetInt64();
                            if(assetId<=0||count<0)throw new InvalidDataException("ASSET_COUNT");
                            if(assets.Add(assetId))total=checked(total+count);
                        }
                        if(list.RootElement.GetArrayLength()<100)break;
                    }
                }
                if(releases.RootElement.GetArrayLength()<100)break;
            }
            nextAttempt=now.AddHours(1);return cached=new(total,now,null);
        }
        catch(OperationCanceledException){throw;}
        catch(Exception ex) when(ex is HttpRequestException or JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException or OverflowException or FormatException)
        {nextAttempt=now.AddMinutes(15);return cached=cached with {Error="暂不可用："+ex.GetType().Name};}
        finally{gate.Release();}
    }
    private async Task<JsonDocument> Page(string url,CancellationToken cancellation)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,url);
        request.Headers.UserAgent.ParseAdd("Mahjong.Plugin.CN-public-statistics/1");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version","2026-03-10");
        if(pages.TryGetValue(url,out var old)&&old.Etag is not null)request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(old.Etag));
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,cancellation).ConfigureAwait(false);
        if(response.StatusCode==HttpStatusCode.NotModified&&old.Json is not null)return JsonDocument.Parse(old.Json);
        response.EnsureSuccessStatusCode();
        if(response.Content.Headers.ContentLength>2*1024*1024)throw new InvalidDataException("RESPONSE_LIMIT");
        await using var stream=await response.Content.ReadAsStreamAsync(cancellation);
        using var buffer=new MemoryStream();var chunk=new byte[8192];int n;
        while((n=await stream.ReadAsync(chunk,cancellation))>0)
        {if(buffer.Length+n>2*1024*1024)throw new InvalidDataException("RESPONSE_LIMIT");buffer.Write(chunk,0,n);}
        string json=System.Text.Encoding.UTF8.GetString(buffer.ToArray());var doc=JsonDocument.Parse(json);
        if(pages.Count>512)pages.Clear();pages[url]=(response.Headers.ETag?.ToString(),json);return doc;
    }
}
