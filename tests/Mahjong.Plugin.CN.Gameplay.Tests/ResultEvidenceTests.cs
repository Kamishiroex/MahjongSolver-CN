using System.Collections.Immutable;
using System.Text.Json;
using Mahjong.Plugin.CN.Readers;
using Mahjong.Plugin.CN.Journaling;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class ResultEvidenceTests
{
    [Fact]public void Live_final_tsumo_maps_all_four_rank_rows_to_visible_table_seats_without_storing_names()
    {
        using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"fixtures","result-final-tsumo-20260927.json")));
        var sample=doc.RootElement;
        var before=sample.GetProperty("Before").Deserialize<ResultUiSample>()!;
        var banner=sample.GetProperty("Banner").Deserialize<ResultUiSample>()!;
        var final=sample.GetProperty("Final").Deserialize<ResultUiSample>()!;
        int[] scores=new[]{"Emj/38/12/2","Emj/40/13/2","Emj/42/13/2","Emj/44/13/2"}
            .Select(p=>before.Values.Single(v=>v.Path==p).Number!.Value).ToArray();
        Assert.Contains(banner.Values,v=>ResultResourceCatalog.Banner(v)=="Tsumo");
        var mapped=final.Values.Where(v=>v.RelativeSeat.HasValue).OrderBy(v=>v.RelativeSeat).ToArray();
        Assert.Equal(new[]{0,1,2,3},mapped.Select(v=>v.RelativeSeat!.Value));
        Assert.Equal(1,FinalResultEvidence.Read(final).Placement);
        var hand=HandResultTracker.Read("recorded-final-hand","Tsumo",before,scores,mapped.Select(v=>v.Number!.Value).ToArray());
        Assert.True(hand.SelfWon);Assert.False(hand.SelfDealtIn);Assert.Equal(new[]{7800,-2600,-2600,-2600},hand.Payments);
    }
    private static readonly DateTimeOffset Start=new(2026,9,27,0,0,0,TimeSpan.Zero);
    private static readonly Guid Match=Guid.NewGuid();
    private static MatchRatingAnchor Anchor()=>new(Match,Guid.NewGuid(),Start,
        new(1800,42,Start.AddSeconds(-5),MahjongRatingReader.Profile,10));
    private static RatingPageEvidence After(int rating=1810,int count=43)=>new(rating,count,Start.AddMinutes(31),MahjongRatingReader.Profile,12);

    [Theory][InlineData(1810,10)][InlineData(1800,0)][InlineData(1760,-40)]
    public void Single_counter_increment_correlates_positive_zero_and_negative_delta(int rating,int delta)
    {Assert.Equal(delta,MatchRatingEvidence.Delta(Anchor(),After(rating),Start.AddMinutes(30)));}
    [Theory][InlineData(42)][InlineData(41)][InlineData(44)]
    public void Stale_reset_and_multiple_matches_cannot_supply_delta(int count)
    {Assert.Null(MatchRatingEvidence.Delta(Anchor(),After(count:count),Start.AddMinutes(30)));}
    [Fact]public void Time_order_refresh_revision_and_mapping_are_checked()
    {
        var a=Anchor();var b=After();var end=Start.AddMinutes(30);
        Assert.Null(MatchRatingEvidence.Delta(a with {Before=a.Before with {ReadAtUtc=Start.AddMinutes(-31)}},b,end));
        Assert.Equal(10,MatchRatingEvidence.Delta(a with {Before=a.Before with {ReadAtUtc=Start.AddMinutes(-5)}},b,end));
        Assert.Null(MatchRatingEvidence.Delta(a,b with {ReadAtUtc=end},end));
        Assert.Null(MatchRatingEvidence.Delta(a,b with {ReadAtUtc=end.AddMinutes(6)},end));
        Assert.Null(MatchRatingEvidence.Delta(a,b with {EvidenceRevision=10},end));
        Assert.Null(MatchRatingEvidence.Delta(a,b with {ProfileVersion="other"},end));
        Assert.Null(MatchRatingEvidence.Delta(a,b,Start.AddMinutes(-1)));
    }
    [Fact]public async Task Supplement_is_bound_to_closed_journal_and_survives_archive_review()
    {
        string root=Path.Combine(Path.GetTempPath(),"mjcn-outcome-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var journal=new GameJournal(root);var a=Anchor() with {MatchId=journal.SessionId};
            void Event(string kind,object data,DateTimeOffset time)=>journal.AppendRecorded(kind,JsonSerializer.SerializeToElement(data),time);
            Event("review_rating_anchor",a,Start);
            Event("synthetic_private_fixture",new {CharacterName="synthetic-private-player"},Start.AddSeconds(1));
            Event("match_result",new {MatchId=journal.SessionId,Source="IDutyState.DutyCompleted"},Start.AddMinutes(30));
            Event("session_stopped",new {Reason="table_exit"},Start.AddMinutes(30).AddSeconds(1));
            Assert.False(journal.Finalized.IsCompleted);
            await journal.CompleteAsync();await journal.Finalized;
            var read=await GameJournal.ReadAsync(Path.Combine(journal.DirectoryPath,"events.jsonl"),summaryOnly:true);
            var supplement=new MatchRatingSupplement(2,journal.SessionId,a.ContextToken,read.Lines[^1].Sha256,After());
            var rows=MatchSummaryBuilder.Build(read,journal.DirectoryPath);
            Assert.Equal(10,Assert.Single(MatchRatingEvidence.Apply(read,rows,supplement)).RatingDelta);
            Assert.Null(Assert.Single(MatchRatingEvidence.Apply(read,rows,supplement with {ContextToken=Guid.NewGuid()})).RatingDelta);
            Assert.Null(Assert.Single(MatchRatingEvidence.Apply(read,rows,supplement with {JournalTailSha256="wrong"})).RatingDelta);
            Assert.Null(Assert.Single(MatchRatingEvidence.Apply(read with {IncompleteTail=true},rows,supplement)).RatingDelta);
            var final=new FinalResultSupplement(1,journal.SessionId,read.Lines[^1].Sha256,Start.AddMinutes(30).AddSeconds(2),Ranking());
            Assert.Equal(3,Assert.Single(FinalResultEvidence.Apply(read,rows,final)).Placement);
            Assert.Null(Assert.Single(FinalResultEvidence.Apply(read,rows,final with {JournalTailSha256="wrong"})).Placement);
            Assert.Null(Assert.Single(FinalResultEvidence.Apply(read,rows,final with {MatchId=Guid.NewGuid()})).Placement);
            Assert.Null(Assert.Single(FinalResultEvidence.Apply(read,rows,final with {ReadAtUtc=Start.AddMinutes(36)})).Placement);
            await File.WriteAllBytesAsync(Path.Combine(journal.DirectoryPath,"final-result.json"),JsonSerializer.SerializeToUtf8Bytes(final));
            await File.WriteAllBytesAsync(Path.Combine(journal.DirectoryPath,"rating-after.json"),JsonSerializer.SerializeToUtf8Bytes(supplement));
            Assert.Equal(10,Assert.Single(await MatchSummaryBuilder.LoadAsync(journal.DirectoryPath)).RatingDelta);
            string zip=Path.Combine(root,"archive.zip");System.IO.Compression.ZipFile.CreateFromDirectory(journal.DirectoryPath,zip);
            Assert.Equal(10,Assert.Single(await JournalReviewStore.SummariesAsync(zip)).RatingDelta);
            Assert.Equal(3,Assert.Single(await JournalReviewStore.SummariesAsync(zip)).Placement);
            string exported=Path.Combine(root,"sanitized.zip");await journal.ExportAsync(exported);
            var exportedRow=Assert.Single(await JournalReviewStore.SummariesAsync(exported));
            Assert.Equal(10,exportedRow.RatingDelta);Assert.Equal(3,exportedRow.Placement);
            var derived=await JournalReviewStore.EventsAsync(exported);
            Assert.True(derived.IntegrityPassed);Assert.NotEqual(read.Lines[^1].Sha256,derived.Lines[^1].Sha256);
            Assert.DoesNotContain("synthetic-private-player",JsonSerializer.Serialize(derived));
            await File.WriteAllBytesAsync(Path.Combine(journal.DirectoryPath,"final-result.json"),JsonSerializer.SerializeToUtf8Bytes(final with {JournalTailSha256="stale"}));
            string stale=Path.Combine(root,"stale.zip");await journal.ExportAsync(stale);
            Assert.Null(Assert.Single(await JournalReviewStore.SummariesAsync(stale)).Placement);
        }
        finally{foreach(string f in Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories))File.Delete(f);
            foreach(string d in Directory.EnumerateDirectories(root).Reverse())Directory.Delete(d);Directory.Delete(root);}
    }

    private static HandResultReading Payments(string? kind,params int[] payments)=>HandResultTracker.Read("round",kind,
        new("Emj",ResultUiReader.Profile,true,[],null),[25000,25000,25000,25000],payments.Select(n=>25000+n).ToArray());
    [Theory]
    [InlineData("Ron",8000,-8000,0,0,true,false)]
    [InlineData("Ron",-8000,8000,0,0,false,true)]
    [InlineData("Ron",0,-8000,8000,0,false,false)]
    [InlineData("Ron",-16000,8000,8000,0,false,true)]
    [InlineData("Tsumo",8000,-4000,-2000,-2000,true,false)]
    [InlineData("Tsumo",-4000,8000,-2000,-2000,false,false)]
    [InlineData("Draw",-3000,1000,1000,1000,false,false)]
    public void Explicit_outcome_and_complete_payments_distinguish_win_dealin_tsumo_and_draw(string kind,int a,int b,int c,int d,bool win,bool deal)
    {var h=Payments(kind,a,b,c,d);Assert.True(h.Complete);Assert.Equal(win,h.SelfWon);Assert.Equal(deal,h.SelfDealtIn);}
    [Fact]public void Missing_banner_incomplete_payments_pao_and_special_draw_remain_unknown()
    {
        Assert.False(Payments(null,8000,-8000,0,0).Complete);
        Assert.False(Payments("Ron",8000,-8000).Complete);
        Assert.False(Payments("Ron",8000,-4000,-4000,0).Complete);
        Assert.False(Payments("Draw",8000,-4000,-2000,-2000).Complete);
        Assert.False(Payments("Ron",0,0,0,0).Complete);
    }
    [Fact]public void Score_animation_pair_or_without_pre_result_baseline_does_not_invent_winner()
    {
        var sample=new ResultUiSample("Emj",ResultUiReader.Profile,true,[],null);
        Assert.False(HandResultTracker.Read("r","Ron",sample).Complete);
        Assert.False(HandResultTracker.Read("r","Ron",sample,[25000,25000,25000,25000],[25123,24877,25000,25000]).Complete);
        var result=HandResultTracker.Read("r","Ron",sample,[25000,25000,25000,25000],[33000,17000,25000,25000]);
        Assert.True(result.SelfWon);Assert.False(result.SelfDealtIn);
    }
    [Fact]public void Private_names_never_pass_number_or_token_grammar_in_name_regions()
    {
        Assert.Null(ResultUiReader.Parse("EmjTotalResult/20/15","1234","self"));
        Assert.Null(ResultUiReader.Parse("EmjTotalResult/20/15","荣和","self"));
        var self=ResultUiReader.Parse("EmjTotalResult/20/15","self","self");Assert.True(self!.IsSelf);Assert.Null(self.Number);Assert.Null(self.Token);
        Assert.Null(ResultUiReader.Parse("EmjTotalResult/20/17","1234","self"));
        Assert.Equal(12500,ResultUiReader.Parse("EmjTotalResult/20/4","+12,500","self")!.Number);
        Assert.Null(ResultUiReader.Parse("EmjTotalResult/20/4","12,50","self"));
    }
    private static ResultUiSample Ranking(int ours=3)=>new("EmjTotalResult",ResultUiReader.Profile,true,
        Enumerable.Range(1,4).SelectMany(n=>new[]{new ResultUiValue($"EmjTotalResult/{19+n}/10",n,null,false),
            new ResultUiValue($"EmjTotalResult/{19+n}/15",null,null,n==ours)}).ToArray(),null);
    [Fact]public void Last_hand_can_use_final_scores_only_when_all_four_names_were_uniquely_matched_to_seats()
    {
        Guid match=Guid.NewGuid();var now=DateTimeOffset.UtcNow;
        var banner=new ResultUiValue("Emj/51/2",null,null,false,121488,~Lumina.Misc.Crc32.Get("ui/icon/121000/chs/121488.tex"),0,0,640,80);
        var events=new (string,object)[]{("review_hand_started",new {MatchId=match,RoundId="r1"}),
            ("review_hand_pending",new {MatchId=match,Evidence=new PendingHandResult("r1",banner,[25000,25000,25000,25000])}),
            ("match_result",new {MatchId=match,Source="IDutyState.DutyCompleted"}),("session_stopped",new {})};
        var read=new JournalReadResult(events.Select((e,i)=>new JournalLine(new(2,match,i+1,now.AddSeconds(i),e.Item1,
            JsonSerializer.SerializeToElement(e.Item2),""),"tail")).ToImmutableArray(),false,null);
        var original=MatchSummaryBuilder.Build(read,"fixture");
        var rank=Ranking(1);int[] scores=[33000,25000,25000,17000];int[] mapping=[0,2,3,1];
        var sample=rank with {Values=rank.Values.Concat(Enumerable.Range(0,4).Select(i=>new ResultUiValue(
            $"EmjTotalResult/{20+i}/4",scores[i],null,false,RelativeSeat:mapping[i]))).ToArray()};
        var supplement=new FinalResultSupplement(1,match,"tail",now.AddSeconds(4),sample);
        var row=Assert.Single(FinalResultEvidence.Apply(read,original,supplement));
        Assert.Equal(1,row.Wins);Assert.Equal(0,row.DealIns);Assert.Equal(1,row.ObservedHands);
        Assert.Single(row.Hands);Assert.Equal(new[]{8000,-8000,0,0},row.Hands[0].Payments);
        var twice=Assert.Single(FinalResultEvidence.Apply(read,[row],supplement));Assert.Equal(1,twice.Wins);Assert.Single(twice.Hands);
        foreach(var missing in new[]{sample with {Values=sample.Values.Select(v=>v with {RelativeSeat=null}).ToArray()},
            sample with {Values=sample.Values.Select(v=>v.RelativeSeat.HasValue?v with {RelativeSeat=0}:v).ToArray()}})
            Assert.Null(Assert.Single(FinalResultEvidence.Apply(read,original,supplement with {Sample=missing})).Wins);
    }
    [Fact]public void Name_equality_bridge_handles_rank_reorder_but_rejects_duplicates_missing_and_foreign_tables()
    {
        string[] names=["fixture-self","fixture-right","fixture-top","fixture-left"];
        Assert.Equal(new[]{2,0,3,1},ResultUiReader.MatchSeats(names,[names[2],names[0],names[3],names[1]],names[0]));
        Assert.Null(ResultUiReader.MatchSeats(names,[names[0],names[0],names[2],names[3]],names[0]));
        Assert.Null(ResultUiReader.MatchSeats(names,[names[0],names[1],null,names[3]],names[0]));
        Assert.Null(ResultUiReader.MatchSeats(names,[names[0],names[1],"foreign",names[3]],names[0]));
        Assert.Null(ResultUiReader.MatchSeats(names,names,"foreign-self"));
    }
    [Fact]public void Final_rank_requires_complete_unique_order_and_exactly_one_self()
    {
        Assert.Equal(3,FinalResultEvidence.Read(Ranking()).Placement);
        Assert.Null(FinalResultEvidence.Read(Ranking(0)).Placement);
        Assert.Null(FinalResultEvidence.Read(Ranking() with {Visible=false}).Placement);
        Assert.Null(FinalResultEvidence.Read(Ranking() with {Values=Ranking().Values.Skip(1).ToArray()}).Placement);
        Assert.Null(FinalResultEvidence.Read(Ranking() with {Values=Ranking().Values.Select(v=>v.Path.EndsWith("/10")?v with {Number=1}:v).ToArray()}).Placement);
        Assert.Null(FinalResultEvidence.Read(Ranking() with {Values=Ranking().Values.Select(v=>v.Path.EndsWith("/15")?v with {IsSelf=true}:v).ToArray()}).Placement);
    }
    [Fact]public void Live_cn_ron_sample_rejects_intermediate_payment_animation_then_reads_2300_transfer()
    {
        using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"fixtures","result-payment-transition-20260927.json")));
        var frames=doc.RootElement.GetProperty("Frames").EnumerateArray().ToArray();
        int[] Scores(JsonElement f)=>f.GetProperty("Scores").EnumerateArray().Select(x=>x.GetInt32()).ToArray();
        var first=frames[0];var before=Scores(first);
        var icon=first.GetProperty("ResultIcons")[0].Deserialize<ResultUiValue>()!;
        Assert.Equal("Ron",ResultResourceCatalog.Banner(icon));
        Assert.Null(ResultResourceCatalog.Banner(icon with {IconId=121490}));
        Assert.Null(ResultResourceCatalog.Banner(icon with {TexturePathHash=0}));
        var sample=new ResultUiSample("Emj",ResultUiReader.Profile,true,[],null);
        foreach(var f in frames.Where(f=>Scores(f).Any(n=>n%100!=0)))
            Assert.Equal("RESULT_SCORE_ANIMATION",HandResultTracker.Read("anonymized-round","Ron",sample,before,Scores(f)).Code);
        var result=HandResultTracker.Read("anonymized-round","Ron",sample,before,Scores(frames[^1]));
        Assert.Equal(new[]{2300,0,-2300,0},result.Payments);Assert.True(result.SelfWon);Assert.False(result.SelfDealtIn);Assert.True(HandResultTracker.Valid(result));
    }

    [Theory][InlineData(2)][InlineData(90)]
    public void Result_dialog_closes_before_payment_animation_finishes_then_counts_once_and_resets_next_hand(int delay)
    {
        // Synthetic timing exercises the live ordering: banner -> detail -> payment
        // animation -> stable scores. It does not invent extra captured frames.
        ResultUiSample Sample(int own,int opposite,bool banner=false,bool detail=false)
        {
            var v=new List<ResultUiValue>();
            foreach(var (path,score) in new[]{("Emj/38/12",own),("Emj/40/13",25000),("Emj/42/13",opposite),("Emj/44/13",25000)})
                foreach(int n in new[]{2,3})v.Add(new(path+"/"+n,score,null,false));
            if(banner)v.Add(new("Emj/51/2",null,null,false,121488,~Lumina.Misc.Crc32.Get("ui/icon/121000/chs/121488.tex"),0,0,640,80));
            if(detail)v.Add(new("Emj/95/3",2000,null,false));
            return new("Emj",ResultUiReader.Profile,true,v,null);
        }
        var tracker=new HandResultTracker();
        tracker.Observe("r1",false,Sample(25000,25000),Start);
        tracker.Observe("r1",false,Sample(25000,25000,true),Start.AddSeconds(.2));
        tracker.Observe("r1",true,Sample(25000,25000,detail:true),Start.AddSeconds(1));
        tracker.Observe("r1",true,Sample(25000,25000,detail:true),Start.AddSeconds(delay-.1));
        tracker.Observe("r1",false,Sample(26010,23990),Start.AddSeconds(delay));
        tracker.Observe("r1",false,Sample(27000,23000),Start.AddSeconds(delay+1));
        var final=tracker.Observe("r1",false,Sample(27000,23000),Start.AddSeconds(delay+1.7));
        Assert.True(final!.SelfWon);Assert.Null(tracker.Observe("r1",false,Sample(27000,23000),Start.AddSeconds(delay+2)));
        var hiddenPanels=new ResultUiSample("Emj",ResultUiReader.Profile,true,[],null);
        Assert.Null(tracker.Observe("r1",false,hiddenPanels,Start.AddSeconds(delay+3)));
        Assert.Null(tracker.Observe("r1",false,hiddenPanels,Start.AddSeconds(delay+4)));
        Assert.Null(tracker.Observe("r1",true,Sample(27000,23000),Start.AddMinutes(2)));
        Assert.Null(tracker.Observe("r2",false,Sample(27000,23000),Start.AddMinutes(3)));Assert.False(tracker.Pending);
    }

    [Fact]public void Live_final_result_maps_own_first_place_and_normalized_profile_pair_preserves_25_delta()
    {
        using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"fixtures","final-result-rating-20260927.json")));
        var r=doc.RootElement;var sample=r.GetProperty("FinalSample").Deserialize<ResultUiSample>()!;
        Assert.Equal(r.GetProperty("ExpectedPlacement").GetInt32(),FinalResultEvidence.Read(sample).Placement);
        var a=Anchor() with {Before=Anchor().Before with {CurrentRating=r.GetProperty("BeforeRating").GetInt32(),MatchesPlayed=r.GetProperty("BeforeMatches").GetInt32()}};
        var after=After(r.GetProperty("AfterRating").GetInt32(),r.GetProperty("AfterMatches").GetInt32());
        Assert.Equal(25,MatchRatingEvidence.Delta(a,after,Start.AddMinutes(30)));
    }

    [Fact]public void Duplicate_hand_and_late_after_duty_completion_are_counted_once_without_inventing_unknown_hands()
    {
        var hand=Payments("Ron",8000,-8000,0,0);
        var data=new (string,object)[]{("review_hand_started",new {MatchId=Match,RoundId="round"}),
            ("review_hand_started",new {MatchId=Match,RoundId="unseen-result"}),
            ("task_match_completed",new {MatchId=Guid.NewGuid(),ReviewMatchId=Match}),
            ("review_hand_result",new {MatchId=Match,Result=hand}),
            ("review_hand_result",new {MatchId=Match,Result=hand})};
        JournalReadResult Read((string,object)[] entries)=>new(entries.Select((d,i)=>new JournalLine(new(2,Match,i+1,
            Start.AddSeconds(i),d.Item1,JsonSerializer.SerializeToElement(d.Item2),""),"fixture")).ToImmutableArray(),false,null);
        var row=Assert.Single(MatchSummaryBuilder.Build(Read(data),"fixture"));
        Assert.Equal(2,row.ObservedHands);Assert.Equal(1,row.HandsWithVerifiedOutcome);Assert.Equal(1,row.Wins);Assert.Equal(0,row.DealIns);
        var conflicting=data.Append(("review_hand_result",(object)new {MatchId=Match,Result=Payments("Ron",-8000,8000,0,0)})).ToArray();
        Assert.Null(Assert.Single(MatchSummaryBuilder.Build(Read(conflicting),"fixture")).Wins);
        Assert.Null(Assert.Single(MatchSummaryBuilder.Build(Read(data) with {IncompleteTail=true},"fixture")).Wins);
    }
}
