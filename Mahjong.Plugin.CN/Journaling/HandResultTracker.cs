using System.Text.Json;
using Mahjong.Plugin.CN.Readers;

namespace Mahjong.Plugin.CN.Journaling;

internal sealed record HandResultReading(string RoundId,string Kind,bool? SelfWon,bool? SelfDealtIn,
    string Code,int[]? Payments=null,int[]? ScoresBefore=null,int[]? ScoresAfter=null)
{
    public bool Complete => SelfWon.HasValue && SelfDealtIn.HasValue;
    public string Source => ResultUiReader.Profile;
}
internal sealed record PendingHandResult(string RoundId,ResultUiValue Banner,int[] ScoresBefore);

/// <summary>Only explicit result banners plus all four displayed settlement payments.
/// Ordinary score changes, requests to win, and table teardown are not outcomes.</summary>
internal sealed class HandResultTracker
{
    private string? round, banner, stableHash, emittedHash;
    private DateTimeOffset bannerTime,resultSurfaceAt,stableSince;
    private int[]? lastScores,before;
    private DateTimeOffset lastScoresAt;
    private bool sawResult,confirmed;
    private ResultUiValue? bannerValue;
    internal bool Pending => banner is not null && sawResult;
    internal PendingHandResult? Evidence=>round is not null && sawResult && bannerValue is not null && before is {Length:4}
        ? new(round,bannerValue,before.ToArray()):null;
    internal void Reset() { round=banner=stableHash=emittedHash=null;bannerValue=null;bannerTime=resultSurfaceAt=stableSince=default;lastScores=before=null;sawResult=confirmed=false; }

    internal HandResultReading? Observe(string? roundId,bool resultSurface,ResultUiSample sample,DateTimeOffset now)
    {
        if(roundId!=round) {Reset();round=roundId;}
        if(roundId is null)return null;
        if(!sample.Visible || sample.Error is not null || sample.Profile!=ResultUiReader.Profile ||
            sample.Values is null || sample.Values.Count>64 || sample.Values.Any(v=>v is null))
        {stableHash=null;return null;}
        foreach(var value in sample.Values)
        {
            string? current=ResultResourceCatalog.Banner(value);
            if(current is not null)
            {
                if(banner is null && now-lastScoresAt<=TimeSpan.FromSeconds(2))before=lastScores;
                banner=current;bannerTime=now;bannerValue=value;
            }
        }
        int[]? scores=Scores(sample);
        if(banner is null && !resultSurface && scores is not null){lastScores=scores;lastScoresAt=now;}
        // The live CN sample updates scores only AFTER the hand-detail dialog is
        // dismissed. Keep this round's proof through that payment animation.
        if(resultSurface || sample.Values.Any(v=>v.Path=="Emj/95/3" && v.Number is >=0))
        { sawResult=true;resultSurfaceAt=now; }
        if(!sawResult) { stableHash=null;return null; }
        // Players may leave the hand-result dialog open while waiting for others.
        // Its continued visibility keeps this round's banner proof alive.
        bool recent=now-(resultSurfaceAt>bannerTime?resultSurfaceAt:bannerTime)<=TimeSpan.FromSeconds(30);
        if(emittedHash is not null && !recent)return null;
        var reading=Read(roundId,recent?banner:null,sample,before,scores);
        // Teardown hides score panels. Missing later widgets cannot erase a
        // settled result; a different complete result is still emitted as conflict.
        if(confirmed && !reading.Complete)return null;
        string hash=JsonSerializer.Serialize(reading);
        if(stableHash!=hash) { stableHash=hash;stableSince=now;return null; }
        if(now-stableSince<TimeSpan.FromMilliseconds(600) || emittedHash==hash)return null;
        emittedHash=hash;
        confirmed|=reading.Complete;
        return reading;
    }

    internal static HandResultReading Read(string roundId,string? kind,ResultUiSample sample,int[]? before=null,int[]? after=null)
    {
        HandResultReading Unknown(string code)=>new(roundId,kind??"Unknown",null,null,code);
        if(sample.Profile!=ResultUiReader.Profile || sample.Addon!="Emj" || !sample.Visible || sample.Error is not null)
            return Unknown("RESULT_READING_UNAVAILABLE");
        if(kind is not ("Ron" or "Tsumo" or "Draw"))return Unknown("RESULT_BANNER_MISSING");
        // These are the already mapped four public scores, frozen immediately
        // before the result banner and re-read on the result surface. Do not use
        // the still unverified 1036 numeric labels as payment fields.
        if(before is not {Length:4} || after is not {Length:4} || before.Concat(after).Any(n=>n is <-200000 or >200000))
            return Unknown("RESULT_PAYMENTS_INCOMPLETE");
        var payments=after.Zip(before,(a,b)=>a-b).ToArray();
        if(payments.Any(n=>n%100!=0))return Unknown("RESULT_SCORE_ANIMATION");
        int positive=payments.Count(n=>n>0),negative=payments.Count(n=>n<0);
        long sum=payments.Sum(n=>(long)n);
        if(sum<0 || sum>100000)return Unknown("RESULT_PAYMENTS_CONTRADICTORY");
        if(kind=="Draw")
        {
            // This intentionally excludes nagashi-mangan and unrecognized special
            // draw payments. A generic draw banner alone cannot certify their rules.
            if(sum!=0 || payments.Any(n=>Math.Abs(n)>3000))return Unknown("SPECIAL_DRAW_UNVERIFIED");
            return new(roundId,kind,false,false,"RESULT_CONFIRMED",payments.ToArray(),before,after);
        }
        if(kind=="Tsumo" && (positive!=1 || negative<1) || kind=="Ron" && (positive<1 || negative!=1))
            return Unknown("RESULT_WIN_PAYER_AMBIGUOUS");
        return new(roundId,kind,payments[0]>0,kind=="Ron" && payments[0]<0,"RESULT_CONFIRMED",payments.ToArray(),before,after);
    }

    internal static bool Valid(HandResultReading value)
    {
        if(value.Code!="RESULT_CONFIRMED" || value.Payments is not {Length:4})return false;
        var reconstructed=Read(value.RoundId,value.Kind,new("Emj",ResultUiReader.Profile,true,[],null),value.ScoresBefore,value.ScoresAfter);
        return reconstructed.Complete && reconstructed.SelfWon==value.SelfWon && reconstructed.SelfDealtIn==value.SelfDealtIn &&
            reconstructed.Payments!.SequenceEqual(value.Payments);
    }

    private static int[]? Scores(ResultUiSample sample)
    {
        var scores=new List<int>();
        foreach(string path in new[]{"Emj/38/12","Emj/40/13","Emj/42/13","Emj/44/13"})
        {
            var a=sample.Values.Where(v=>v.Path==path+"/2").ToArray();
            var b=sample.Values.Where(v=>v.Path==path+"/3").ToArray();
            if(a.Length!=1 || b.Length!=1 || a[0].Number is not { } n || b[0].Number!=n)return null;
            scores.Add(n);
        }
        return scores.ToArray();
    }
}

internal static class ResultResourceCatalog
{
    // Only checked Chinese resources, normal and high-resolution. Pixel data stays
    // in the game; docs/cn/evidence/result-resource-catalog.json records provenance.
    internal static string? Banner(ResultUiValue value)
    {
        if(value.Path is not ("Emj/48/3" or "Emj/49/2" or "Emj/51/2") || value.U!=0 || value.V!=0 ||
            value.Width!=640 || value.Height!=80)return null;
        return value.IconId switch
        {
            121486 when Hash(value.TexturePathHash,121486)=>"Draw",
            121488 when Hash(value.TexturePathHash,121488)=>"Ron",
            121489 when Hash(value.TexturePathHash,121489)=>"Tsumo",
            _=>null,
        };
    }
    private static bool Hash(uint? hash,uint icon)=>hash==~Lumina.Misc.Crc32.Get($"ui/icon/121000/chs/{icon}.tex") ||
        hash==~Lumina.Misc.Crc32.Get($"ui/icon/121000/chs/{icon}_hr1.tex");
}
