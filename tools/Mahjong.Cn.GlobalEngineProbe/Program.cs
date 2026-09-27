using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mahjong.Cn;
using Mahjong.Cn.Engines;

// Deliberately synthetic adapter probes. These are not real CN game evidence.
if (args.Length is < 1 or > 2) throw new ArgumentException("GlobalEngineProbe <local-engine-directory> [captured-evidence.json]");
static VisibleTile T(string value) => MjaiTileCodec.TryDecode(value, out var tile) ? tile : throw new Exception(value);
static ImmutableArray<VisibleTile> Tiles(params string[] names) => names.Select(T).ToImmutableArray();
var installation = await AkochanInstallation.LoadAsync(Path.GetFullPath(args[0]));
if (args.Length == 2)
{
    using var file = JsonDocument.Parse(File.ReadAllText(args[1]));
    var input = file.RootElement.GetProperty("ActualPublicInput").Deserialize<AkochanGlobalSnapshot>(
        new JsonSerializerOptions { Converters={new JsonStringEnumConverter()} })!;
    var result = await new AkochanGlobalEngine().AnalyzeAsync(installation,input,TimeSpan.FromSeconds(60));
    if (file.RootElement.TryGetProperty("ExpectedMove",out var expected) &&
        !result.Candidates.Any(c=>c.Moves.Any(m=>m.Type==expected.GetString())))
        throw new Exception("Recorded regression did not return expected native move: " + expected.GetString());
    Console.WriteLine(JsonSerializer.Serialize(new { mode="offline-captured-public-table", input, result },
        new JsonSerializerOptions { WriteIndented=true }));
    return;
}
var players = Enumerable.Range(0, 4).Select(i => new AkochanGlobalPlayer(i,i,
    new[] { 27000, 22000, 26000, 24000 }[i],i == 1,i == 1,i == 1 ? 1 : null,
    new[] {
        new[] {"1m","2m"}, new[] {"9m","N"}, new[] {"8s","9s"}, new[] {"1p","2p"}
    }[i].Select((x,j) => new AkochanGlobalDiscard(T(x), j == 1, false)).ToImmutableArray(), [])).ToImmutableArray();
var hand = Tiles("3m","5mr","3m","7s","6m","9p","8m","3s","4s","N","1s","C","2s","6m");
var sample = new AkochanGlobalSnapshot(0,1,3,2,1,0,38,hand,Tiles("F","7p"),players,
    new("tsumo",0,T("6m")),["synthetic native snapshot bridge test"]) { ContextKey="native-global-probe" };
var engine = new AkochanGlobalEngine();
var reports = new List<object>();
async Task<AkochanGlobalDecision> Check(string name, AkochanGlobalSnapshot state, string? requiredMove = null)
{
    var result = await engine.AnalyzeAsync(installation,state,TimeSpan.FromSeconds(60));
    if (result.Candidates.Length == 0) throw new Exception("No candidates: " + name);
    if (requiredMove is not null && !result.Candidates.Any(c => c.Moves.Any(m => m.Type == requiredMove)))
        throw new Exception("Missing native action " + requiredMove + ": " + name);
    reports.Add(new { name, result.InputSha256, result.StartToResponseMilliseconds,
        best = result.Candidates[0], candidates = result.Candidates.Length,
        applied = JsonSerializer.Deserialize<JsonElement>(result.AppliedSnapshotJson) });
    return result;
}
await Check("closed-table-riichi-and-score",sample);
var openHand = Tiles("3m","5mr","3m","7s","6m","9p","8m","3s","4s","N","1s");
var ownPon = new AkochanGlobalMeld("pon",3,T("P"),Tiles("P","P","P"));
var openPlayers = players.SetItem(0,players[0] with { Melds=[ownPon] });
await Check("open-post-call-with-orphan-claimed-tile",sample with {
    Hand=openHand, Players=openPlayers, Trigger=new("discard",0,null) });
var markedPlayers = openPlayers.SetItem(3,players[3] with {
    River=players[3].River.Add(new(T("P"),false,true)) });
await Check("open-post-call-deduplicates-marked-claimed-tile",sample with {
    Hand=openHand, Players=markedPlayers, Trigger=new("discard",0,null) });
var response = sample with { Hand=Tiles("1m","2m","3m","4m","5m","6m","2p","3p","4p","7s","8s","9s","P"),
    Trigger=new("dahai",3,T("P")), Players=players.SetItem(3,players[3] with { River=players[3].River.Add(new(T("P"),false,false)) }) };
await Check("opponent-discard-response",response with { Players=response.Players.SetItem(0,response.Players[0] with {
    RiichiDeclared=true,RiichiEstablished=true,RiichiDiscardIndex=1 }) },"hora");
await Check("self-draw-win",sample with { Hand=response.Hand.Add(T("P")),Trigger=new("tsumo",0,T("P")) },"hora");
await Check("closed-kan-options",sample with { Hand=Tiles("C","C","C","C","1m","2m","3m","4p","5p","6p","1s","1s","1s","N"),
    Trigger=new("tsumo",0,T("C")) },"ankan");
await Check("added-kan-options",sample with { Hand=Tiles("1m","2m","3m","4m","5m","6m","7s","8s","N","N","P"),
    Players=openPlayers, Trigger=new("tsumo",0,T("P")) },"kakan");
if (installation.HasWinContextBridge)
{
    var cleanPlayers=Enumerable.Range(0,4).Select(i=>new AkochanGlobalPlayer(i,i,25000,false,false,null,[],[])).ToImmutableArray();
    var noYaku=sample with { Honba=0,RiichiSticks=0,DoraIndicators=Tiles("F"),WallRemaining=20,
        Players=cleanPlayers.SetItem(0,cleanPlayers[0] with { Melds=[new("daiminkan",1,T("1m"),Tiles("1m","1m","1m","1m"))] }),
        Hand=Tiles("2m","3m","4m","4p","5p","6p","7s","8s","9s","2p","2p"),
        Trigger=new("tsumo",0,T("2p")),OwnDrawKind="normal" };
    var baseline=await Check("open-no-yaku-normal-draw",noYaku);
    if(baseline.Candidates.Any(c=>c.Moves.Any(m=>m.Type=="hora")))throw new Exception("Normal no-yaku draw gained a win.");
    await Check("rinshan-only-yaku-enables-native-win",noYaku with {OwnDrawKind="rinshan"},"hora");
    await Check("rinshan-at-zero-wall-does-not-stack-haitei",noYaku with {OwnDrawKind="rinshan",WallRemaining=0},"hora");
    var riichi=sample with {Honba=0,RiichiSticks=0,DoraIndicators=Tiles("F"),WallRemaining=20,
        Hand=Tiles("1m","2m","3m","4m","5m","6m","2p","3p","4p","7s","8s","9s","P","P"),
        Trigger=new("tsumo",0,T("P")),OwnDrawKind="normal",
        Players=cleanPlayers.SetItem(0,cleanPlayers[0] with {RiichiDeclared=true,RiichiEstablished=true,Ippatsu=false})};
    static double HoraScore(AkochanGlobalDecision r)=>r.Candidates.Where(c=>c.Moves.Any(m=>m.Type=="hora")).Max(c=>c.Score);
    var ordinary=await Check("riichi-tsumo-without-ippatsu",riichi,"hora");
    var withIppatsu=await Check("riichi-tsumo-with-ippatsu",riichi with {Players=riichi.Players.SetItem(0,riichi.Players[0] with {Ippatsu=true})},"hora");
    if(HoraScore(withIppatsu)<=HoraScore(ordinary))throw new Exception("Own ippatsu did not improve actual native tsumo score.");
    var ron=riichi with {OwnDrawKind=null,Hand=riichi.Hand.RemoveAt(13),Trigger=new("dahai",3,T("P")),
        Players=riichi.Players.SetItem(3,riichi.Players[3] with {River=[new(T("P"),false,false)]})};
    ordinary=await Check("riichi-ron-without-ippatsu",ron,"hora");
    withIppatsu=await Check("riichi-ron-with-ippatsu",ron with {Players=ron.Players.SetItem(0,ron.Players[0] with {Ippatsu=true})},"hora");
    if(HoraScore(withIppatsu)<=HoraScore(ordinary))throw new Exception("Own ippatsu did not improve actual native ron score.");
    if (installation.HasDoubleRiichiBridge)
    {
        foreach(var state in new[]{riichi,ron}) {
            var normal=await Check("ordinary-riichi-"+state.Trigger.Type,state,"hora");
            var doubled=await Check("double-riichi-"+state.Trigger.Type,state with {
                Players=state.Players.SetItem(0,state.Players[0] with {DoubleRiichi=true})},"hora");
            if(HoraScore(doubled)<=HoraScore(normal))throw new Exception("Double riichi did not improve actual native win score.");
            var combined=await Check("double-riichi-and-ippatsu-"+state.Trigger.Type,state with {
                Players=state.Players.SetItem(0,state.Players[0] with {DoubleRiichi=true,Ippatsu=true})},"hora");
            if(HoraScore(combined)<=HoraScore(doubled))throw new Exception("Ippatsu did not stack with double riichi.");
        }
    }
}
if (installation.HasPartialHistoryFuritenFix)
{
    var ron = response with { Players=response.Players.SetItem(0,response.Players[0] with {
        RiichiDeclared=true,RiichiEstablished=true,RiichiDiscardIndex=1 }),
        KnownEvents=[new(10,"dahai",1,null,T("P"),[]),new(20,"dahai",0,null,T("2m"),[])] };
    await Check("partial-pre-riichi-opponent-discard-is-not-pass-furiten",ron,"hora");
    var ownFuriten = await Check("partial-history-retains-own-river-furiten",ron with {
        Players=ron.Players.SetItem(0,ron.Players[0] with {River=ron.Players[0].River.Add(new(T("P"),false,false))}) });
    if(ownFuriten.Candidates.Any(c=>c.Moves.Any(m=>m.Type=="hora")))throw new Exception("Own-river furiten was erased.");
    var complete = await Check("complete-history-still-detects-riichi-pass",ron with { HistoryComplete=true,
        KnownEvents=[new(5,"reach_accepted",0,null,null,[]),..ron.KnownEvents] });
    if(complete.Candidates.Any(c=>c.Moves.Any(m=>m.Type=="hora")))throw new Exception("Complete-history furiten was erased.");
    await Check("partial-red-five-own-river-normalizes-kind",sample with { Players=sample.Players.SetItem(0,
        sample.Players[0] with {River=sample.Players[0].River.Add(new(T("5mr"),true,false))}),
        Hand=sample.Hand.Select(t=>t.Red ? new VisibleTile(t.Id,false) : t).ToImmutableArray() });
}
if (installation.HasObservedFuritenBridge)
{
    var ron=response with {Players=response.Players.SetItem(0,response.Players[0] with {RiichiDeclared=true,RiichiEstablished=true})};
    async Task NoRon(string name,AkochanGlobalSnapshot state) {
        var result=await Check(name,state);
        if(result.Candidates.Any(c=>c.Moves.Any(m=>m.Type=="hora")))throw new Exception("Observed furiten did not block Ron: "+name);
    }
    await NoRon("observed-temporary-furiten-blocks-ron",ron with {OwnTemporaryFuriten=true});
    await NoRon("observed-riichi-furiten-blocks-ron",ron with {OwnRiichiFuriten=true});
    await Check("unknown-pass-history-does-not-fabricate-furiten",ron,"hora");
    await Check("cleared-temporary-furiten-restores-ron",ron with {OwnTemporaryFuriten=false,OwnRiichiFuriten=false},"hora");
    var tsumo=ron with {Hand=ron.Hand.Add(T("P")),Trigger=new("tsumo",0,T("P"))};
    await Check("temporary-furiten-does-not-block-tsumo",tsumo with {OwnTemporaryFuriten=true},"hora");
    await Check("riichi-furiten-does-not-block-tsumo",tsumo with {OwnRiichiFuriten=true},"hora");
    await NoRon("negative-pass-flags-never-clear-own-river-furiten",ron with {OwnTemporaryFuriten=false,OwnRiichiFuriten=false,
        Players=ron.Players.SetItem(0,ron.Players[0] with {River=ron.Players[0].River.Add(new(T("P"),false,false))})});
}
Console.WriteLine(JsonSerializer.Serialize(new { native=true, synthetic=true, winContextBridge=installation.HasWinContextBridge,
    partialHistoryFuritenFix=installation.HasPartialHistoryFuritenFix,
    doubleRiichiBridge=installation.HasDoubleRiichiBridge,
    observedFuritenBridge=installation.HasObservedFuritenBridge,
    source=installation.SourceCommit, reports },new JsonSerializerOptions { WriteIndented=true }));
