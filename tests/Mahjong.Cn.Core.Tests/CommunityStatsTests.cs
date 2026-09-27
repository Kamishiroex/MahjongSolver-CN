using System.Net;
using System.Text;
using System.Text.Json;
using Mahjong.Cn.Statistics;
using Xunit;

namespace Mahjong.Cn.Tests;
public sealed class CommunityStatsTests
{
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> reply):HttpMessageHandler
    {
        internal int Calls;
        internal List<string> Bodies=[];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {Calls++;Assert.Null(request.Headers.Authorization);if(request.Content is not null)Bodies.Add(await request.Content.ReadAsStringAsync(token));return reply(request);}
    }
    private static HttpResponseMessage Json(object value)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(value),Encoding.UTF8,"application/json")};
    private static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-09-27T00:00:00Z");
    [Fact]public async Task All_release_and_asset_pages_are_counted_deduplicated_and_cached()
    {
        var handler=new Handler(r=>
        {
            string url=r.RequestUri!.PathAndQuery;
            if(url.Contains("/releases/1/assets"))
                return url.EndsWith("page=1")?Json(Enumerable.Range(1,100).Select(i=>new {id=i,name=i==100?"Mahjong.Plugin.CN-0.6.4.5.zip":"source.zip",download_count=4})):
                    Json(new[]{new{id=101,name="Mahjong.Plugin.CN-0.6.4.4.zip",download_count=3}});
            if(url.Contains("/releases/2/assets"))return Json(new[]{new{id=100,name="Mahjong.Plugin.CN-0.6.4.5.zip",download_count=4},new{id=102,name="models.zip",download_count=999}});
            return url.EndsWith("page=1")?Json(Enumerable.Range(1,100).Select(i=>new{id=i,draft=i>1,prerelease=false})):
                Json(new[]{new{id=2,draft=false,prerelease=false},new{id=3,draft=false,prerelease=true}});
        });
        var client=new PublicDownloadStats(new HttpClient(handler));
        Assert.Equal(7,(await client.ReadAsync(Now)).Count);int calls=handler.Calls;
        Assert.Equal(7,(await client.ReadAsync(Now.AddMinutes(59))).Count);Assert.Equal(calls,handler.Calls);
    }
    [Fact]public async Task New_repository_counts_three_part_packages_but_not_source_or_models()
    {
        var handler=new Handler(r=>
        {
            Assert.StartsWith("https://api.github.com/repos/Kamishiroex/MahjongSolver-CN/",r.RequestUri!.AbsoluteUri);
            return r.RequestUri.AbsolutePath.EndsWith("/assets")
                ?Json(new[]{new{id=1,name="Mahjong.Plugin.CN-4.1.4.zip",download_count=2},new{id=2,name="Mahjong.Plugin.CN-4.1.4-source.zip",download_count=50},new{id=3,name="models.zip",download_count=100}})
                :Json(new[]{new{id=1,draft=false,prerelease=false}});
        });
        Assert.Equal(2,(await new PublicDownloadStats(new HttpClient(handler)).ReadAsync(Now)).Count);
    }
    [Theory][InlineData(429,"[]")][InlineData(500,"[]")][InlineData(200,"{")][InlineData(200,"{}")]
    public async Task Failure_is_unknown_not_zero_and_backs_off(int status,string body)
    {var h=new Handler(_=>new((HttpStatusCode)status){Content=new StringContent(body)});var c=new PublicDownloadStats(new HttpClient(h));var result=await c.ReadAsync(Now);Assert.Null(result.Count);Assert.NotNull(result.Error);await c.ReadAsync(Now.AddMinutes(1));Assert.Equal(1,h.Calls);}
    [Fact]public async Task Conditional_pages_reuse_only_validated_cached_content()
    {
        var h=new Handler(r=>{if(r.Headers.IfNoneMatch.Count>0)return new(HttpStatusCode.NotModified);var response=Json(Array.Empty<object>());response.Headers.ETag=new("\"fixture\"");return response;});
        var c=new PublicDownloadStats(new HttpClient(h));Assert.Equal(0,(await c.ReadAsync(Now)).Count);Assert.Equal(0,(await c.ReadAsync(Now.AddHours(2))).Count);Assert.Equal(2,h.Calls);
    }
    [Fact]public async Task No_consent_means_no_request_and_heartbeat_payload_has_only_random_session()
    {
        var h=new Handler(_=>new(HttpStatusCode.NoContent));var client=new OptionalPresenceClient(new HttpClient(h));var uri=new Uri("http://127.0.0.1:57831/");
        Assert.False(await client.HeartbeatAsync(uri,false,default));Assert.Null(await client.ReadCountAsync(uri,false,default));Assert.Equal(0,h.Calls);
        await client.HeartbeatAsync(uri,true,default);await client.HeartbeatAsync(uri,true,default);
        using var body=JsonDocument.Parse(h.Bodies[0]);Assert.Single(body.RootElement.EnumerateObject());Assert.True(Guid.TryParse(body.RootElement.GetProperty("sessionId").GetString(),out _));Assert.Equal(h.Bodies[0],h.Bodies[1]);
    }
    [Fact]public void Presence_uses_receive_time_deduplicates_and_expires()
    {var registry=new PresenceRegistry();var id=Guid.NewGuid();Assert.Equal(204,registry.Touch(id,0));Assert.Equal(429,registry.Touch(id,1));Assert.Equal(1,registry.Count(179));Assert.Equal(0,registry.Count(180));Assert.Equal(204,registry.Touch(id,181));registry.Remove(id);Assert.Equal(0,registry.Count(181));}
    [Theory][InlineData("http://example.org")][InlineData("https://user:secret@example.org")][InlineData("https://example.org/?key=example")]
    public void Invalid_endpoint_never_accepted(string uri)=>Assert.False(OptionalPresenceClient.ValidEndpoint(new(uri)));
}
