using Mahjong.Cn.Rating;
using Mahjong.Plugin.CN.Readers;
using Xunit;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class RatingRefreshTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);
    private sealed class Profile : IRatingProfileAccess
    {
        public bool IsOpen { get; set; }
        public bool IsMahjongSelected { get; set; }
        internal bool Owned, CanOpen = true;
        internal int Opens, Selections, Closes;
        public bool Open() { Opens++; if (!CanOpen) return false; IsOpen = Owned = true; return true; }
        public bool SelectMahjong() { Selections++; return IsOpen; }
        public void CloseOwned() { if (Owned) { Closes++; IsOpen = IsMahjongSelected = false; } ForgetOwnership(); }
        public void ForgetOwnership() => Owned = false;
    }
    private static RatingObservation Value(double now, int current = 1800, string context = "synthetic") =>
        new(current, 2000, "初段", context, Start.AddSeconds(now), "fixture",
            RatingMapping.Verified, RatingFreshness.Fresh, MahjongRatingReader.Profile, null, 1, null);
    private static void Tick(RatingRefresh refresh, double now, bool available = true, string context = "synthetic", bool valid = true) =>
        refresh.Tick(context, valid, available, now, () => Value(now));

    [Fact] public void One_click_opens_selects_waits_for_stable_read_then_closes_only_owned_page()
    {
        var p = new Profile(); var r = new RatingRefresh(p,()=>true); r.Request("synthetic", 0, false);
        Tick(r, 0); Tick(r, .2); // Page still loading: no repeated selection or cancellation.
        Assert.True(r.Busy); Assert.Equal(1, p.Opens); Assert.Equal(1, p.Selections);
        p.IsMahjongSelected = true; Tick(r, .4); Assert.True(r.Busy);
        Tick(r, .6); Assert.False(r.Busy); Assert.Equal(1800, r.Result!.CurrentRating);
        Assert.Equal(1, p.Closes); Assert.Null(r.Result.MatchAssociation);
        Tick(r, 1); Assert.Equal(1, p.Opens);
    }
    [Fact] public void Existing_user_page_is_never_closed()
    {
        var p = new Profile { IsOpen = true, IsMahjongSelected = true }; var r = new RatingRefresh(p,()=>true);
        r.Request("synthetic", 0, false); Tick(r, 0); Tick(r, .2);
        Assert.NotNull(r.Result); Assert.True(p.IsOpen); Assert.Equal(0, p.Opens); Assert.Equal(0, p.Closes);
    }
    [Fact] public void Automatic_refresh_waits_for_exit_and_never_navigates_away_from_user_page()
    {
        var p = new Profile(); var r = new RatingRefresh(p,()=>true); r.Request("synthetic", 0, true);
        Tick(r, 1); Tick(r, 10, false); Assert.Equal(0, p.Opens);
        p.IsOpen = true; Tick(r, 12); Assert.Equal(0, p.Selections);
        p.IsOpen = false; Tick(r, 13); Assert.Equal(1, p.Opens);
        p.IsMahjongSelected = true; Tick(r, 14); Tick(r, 14.2); Assert.NotNull(r.Result);
    }
    [Fact] public void Repeated_requests_do_not_extend_deadline_or_reopen()
    {
        var p = new Profile(); var r = new RatingRefresh(p,()=>true); r.Request("synthetic", 0, false);
        Tick(r, 0); r.Request("synthetic", 4, false); Tick(r, 6);
        Assert.False(r.Busy); Assert.Null(r.Result); Assert.Equal(1, p.Opens); Assert.Equal(1, p.Closes);
    }
    [Fact] public void Busy_game_timeout_does_not_require_or_block_gameplay()
    {
        var p = new Profile(); var r = new RatingRefresh(p,()=>true); r.Request("synthetic", 0, true);
        Tick(r, 121, false); Assert.False(r.Busy); Assert.Equal(0, p.Opens); Assert.Contains("超时", r.Status);
    }
    [Theory] [InlineData("other", true)] [InlineData("", true)] [InlineData("synthetic", false)]
    public void Changed_context_or_version_discards_pending_without_touching_game(string context, bool valid)
    {
        var p = new Profile(); var r = new RatingRefresh(p,()=>true); r.Request("synthetic", 0, false); Tick(r, 0);
        Tick(r, .2, context: context, valid: valid); Assert.False(r.Busy); Assert.Null(r.Result); Assert.Equal(0, p.Closes);
    }
    [Fact] public void Scene_transition_cancels_and_never_reopens_on_return()
    {
        var p = new Profile(); var r = new RatingRefresh(p,()=>true); r.Request("synthetic", 0, false);
        Tick(r, 0); Tick(r, .2, false); Tick(r, 1); Assert.False(r.Busy); Assert.Equal(1, p.Opens);
    }
    [Fact] public void Stale_or_foreign_read_is_not_accepted()
    {
        var p = new Profile { IsOpen = true, IsMahjongSelected = true }; var r = new RatingRefresh(p,()=>true);
        r.Request("synthetic", 0, false);
        r.Tick("synthetic", true, true, 1, () => Value(1) with { Freshness = RatingFreshness.Cached });
        r.Tick("synthetic", true, true, 2, () => Value(2, context: "other"));
        Assert.True(r.Busy); Assert.Null(r.Result);
        Tick(r, 3); Tick(r, 3.2); Assert.NotNull(r.Result);
    }
    [Fact] public void Changing_values_require_new_stable_pair()
    {
        var p = new Profile { IsOpen = true, IsMahjongSelected = true }; var r = new RatingRefresh(p,()=>true);
        r.Request("synthetic", 0, false); Tick(r, 0);
        r.Tick("synthetic", true, true, .2, () => Value(.2, 1810)); Assert.True(r.Busy);
        r.Tick("synthetic", true, true, .4, () => Value(.4, 1810)); Assert.Equal(1810, r.Result!.CurrentRating);
    }
    [Fact] public void Changed_match_counter_requires_its_own_stable_pair_even_when_rating_is_unchanged()
    {
        var p = new Profile { IsOpen = true, IsMahjongSelected = true }; var r = new RatingRefresh(p,()=>true);
        r.Request("synthetic", 0, false);
        r.Tick("synthetic",true,true,0,()=>Value(0) with {MatchesPlayed=42});
        r.Tick("synthetic",true,true,.2,()=>Value(.2) with {MatchesPlayed=43});
        Assert.True(r.Busy);
        r.Tick("synthetic",true,true,.4,()=>Value(.4) with {MatchesPlayed=43});
        Assert.Equal(43,r.Result!.MatchesPlayed);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void After_match_waits_for_counter_advance_or_times_out_without_accepting_stale_rating(bool advance)
    {
        var p=new Profile();var r=new RatingRefresh(p,()=>true);r.Request("synthetic",0,true,42);
        Tick(r,3);p.IsMahjongSelected=true;
        r.Tick("synthetic",true,true,3.2,()=>Value(3.2) with {MatchesPlayed=42});
        r.Tick("synthetic",true,true,3.4,()=>Value(3.4) with {MatchesPlayed=42});
        Assert.True(r.Busy);Assert.Null(r.Result);Assert.Equal(0,p.Closes);
        if(advance)
        {
            r.Tick("synthetic",true,true,4,()=>Value(4) with {MatchesPlayed=43});
            r.Tick("synthetic",true,true,4.2,()=>Value(4.2) with {MatchesPlayed=43});
            Assert.Equal(43,r.Result!.MatchesPlayed);
        }
        else { Tick(r,9);Assert.Null(r.Result); }
        Assert.False(r.Busy);Assert.Equal(1,p.Closes);Assert.Equal(1,p.Opens);
    }
    [Fact] public void Stop_cancels_late_reads_and_closes_own_page()
    {
        var p = new Profile(); var r = new RatingRefresh(p,()=>true); r.Request("synthetic", 0, false); Tick(r, 0);
        r.Cancel("stopped", true); Tick(r, 1); Assert.Null(r.Result); Assert.Equal(1, p.Closes);
    }
    [Fact] public void Missing_counter_does_not_finish_after_match_refresh_before_valid_counter_arrives()
    {
        var p=new Profile {IsOpen=true,IsMahjongSelected=true};var r=new RatingRefresh(p,()=>true);
        r.Request("synthetic",0,true,42);
        Tick(r,3);Tick(r,3.2);Assert.True(r.Busy);Assert.Null(r.Result);
        r.Tick("synthetic",true,true,3.4,()=>Value(3.4) with {MatchesPlayed=43});
        r.Tick("synthetic",true,true,3.6,()=>Value(3.6) with {MatchesPlayed=43});
        Assert.Equal(43,r.Result!.MatchesPlayed);Assert.True(p.IsOpen);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void Permission_revocation_prevents_open_selection_close_and_late_results(bool initiallyAllowed)
    {
        bool allowed=initiallyAllowed;
        var p=new Profile();var r=new RatingRefresh(p,()=>allowed);
        r.Request("synthetic",0,false);Tick(r,0);
        int opens=p.Opens,selections=p.Selections;
        allowed=false;Tick(r,.2);
        Assert.False(r.Busy);Assert.Null(r.Result);Assert.Equal(0,p.Closes);
        allowed=true;p.IsMahjongSelected=true;Tick(r,1);Tick(r,2);
        Assert.Equal(opens,p.Opens);Assert.Equal(selections,p.Selections);
        Assert.Equal(0,p.Closes);Assert.Null(r.Result);
    }
    [Fact] public void Cancel_never_closes_without_live_permission()
    {
        bool allowed=true;
        var p=new Profile();var r=new RatingRefresh(p,()=>allowed);
        r.Request("synthetic",0,false);Tick(r,0);allowed=false;
        r.Cancel("revoked",true);Assert.Equal(0,p.Closes);Assert.True(p.IsOpen);
    }
    [Fact] public void Permission_lost_during_read_discards_value_and_does_not_close()
    {
        bool allowed=true;
        var p=new Profile {IsOpen=true,IsMahjongSelected=true,Owned=true};
        var r=new RatingRefresh(p,()=>allowed);r.Request("synthetic",0,false);Tick(r,0);
        r.Tick("synthetic",true,true,.2,()=>{allowed=false;return Value(.2);});
        Assert.False(r.Busy);Assert.Null(r.Result);Assert.Equal(0,p.Closes);
    }
}
