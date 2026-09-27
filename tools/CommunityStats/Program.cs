using System.Diagnostics;
using System.Text.Json;
using Mahjong.Cn.Statistics;

var builder=WebApplication.CreateBuilder(args);
// Local protocol host only. No implicit public deployment or external telemetry.
builder.WebHost.UseUrls("http://127.0.0.1:57831");
builder.WebHost.ConfigureKestrel(o=>o.Limits.MaxRequestBodySize=256);
var app=builder.Build();var registry=new PresenceRegistry();
double Now()=>Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency;
app.MapGet("/v1/public-stats",()=>Results.Json(new {activeSessions=registry.Count(Now()),updatedUtc=DateTimeOffset.UtcNow,ttlSeconds=180,scope="opt-in sessions"}));
app.MapMethods("/v1/presence",["POST","DELETE"],async (HttpRequest request)=>
{
    try
    {
        using var body=await JsonDocument.ParseAsync(request.Body,new JsonDocumentOptions{MaxDepth=2},request.HttpContext.RequestAborted);
        if(body.RootElement.ValueKind!=JsonValueKind.Object || body.RootElement.EnumerateObject().Count()!=1 ||
            !body.RootElement.TryGetProperty("sessionId",out var value)||value.ValueKind!=JsonValueKind.String ||
            !Guid.TryParse(value.GetString(),out var id)||id==Guid.Empty)return Results.BadRequest();
        if(request.Method=="DELETE"){registry.Remove(id);return Results.NoContent();}
        return Results.StatusCode(registry.Touch(id,Now()));
    }
    catch(JsonException){return Results.BadRequest();}
});
app.Run();
