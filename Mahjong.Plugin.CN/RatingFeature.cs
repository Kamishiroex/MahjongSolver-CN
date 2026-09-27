using System.Security.Cryptography;
using Mahjong.Cn.Rating;
using Mahjong.Plugin.CN.Readers;
using Dalamud.Game.ClientState.Conditions;

namespace Mahjong.Plugin.CN;

public sealed partial class Plugin
{
    private readonly byte[] ratingContextSalt=RandomNumberGenerator.GetBytes(32);
    private MahjongRatingReader? ratingReader;
    private double lastRatingPoll;
    private ulong ratingContentId;
    private string ratingContext="";
    private bool ratingReadingEnabled;
    private RatingRefresh? ratingRefresh;
    private CnRatingProfileAccess? ratingProfile;
    private int ratingOperationGeneration=-1;
    private string ratingOperationContext="";
    private string passiveRatingStatus="请手动打开金碟／方城战资料页，标准模式只读取已显示的评分。";
    internal RatingObservation? CurrentRating { get; private set; }
    internal bool RatingRefreshBusy => ratingRefresh?.Busy == true;
    internal string RatingRefreshStatus => ratingRefresh?.Status is {Length:>0} status ? status : passiveRatingStatus;
    internal void ReadOwnRating()
    {
        Identity=RuntimeIdentity.Read(Interface,Client);
        ratingReadingEnabled=true;
        passiveRatingStatus="标准模式不执行游戏操作，仅提供提示。请手动打开金碟／方城战资料页。";
        lastRatingPoll=0; UpdateRatingCore();
    }
    internal void ReadOwnRatingWithNavigation()
    {
        if(disposed || !RequireOperationCapability())return;
        Identity=RuntimeIdentity.Read(Interface,Client);
        RequestRatingRefresh(false,explicitRequest:true);
    }
    private bool RatingOperationsAuthorized => GameOperationsAvailable && ratingOperationGeneration==Volatile.Read(ref operationGeneration) &&
        ratingOperationContext.Length>0 && ratingOperationContext==CurrentCharacterContext();
    private void RequestRatingRefresh(bool afterMatch,bool explicitRequest=false)
    {
        if(!GameOperationsAvailable || !(explicitRequest || afterMatch && GameOperationsAuthorized))return;
        string context=CurrentCharacterContext();
        if(disposed || Identity.Error is not null || context.Length==0)return;
        ratingReadingEnabled=true;
        ratingOperationGeneration=Volatile.Read(ref operationGeneration);
        ratingOperationContext=context;
        ratingProfile??=new(name=>GameGui.GetAddonByName(name).Address,()=>RatingOperationsAuthorized);
        ratingRefresh??=new(ratingProfile,()=>RatingOperationsAuthorized);
        ratingRefresh.Request(context,AutomationNow,afterMatch,reviewRatingAnchor?.Before.MatchesPlayed);
        RecordJournalEvent("profile_operation_authorized",new {Scope="rating-page-only",AfterMatch=afterMatch,
            Source=explicitRequest?"explicit-user-click":"authorized-task-after-match"});
    }
    private bool CanRefreshRating() => Client.IsLoggedIn &&
        !Conditions[ConditionFlag.BetweenAreas] && !Conditions[ConditionFlag.BetweenAreas51] &&
        !Conditions[ConditionFlag.LoggingOut] && !Conditions[ConditionFlag.BoundByDuty] &&
        !Conditions[ConditionFlag.BoundByDuty56] && !Conditions[ConditionFlag.BoundByDuty95] &&
        !Conditions[ConditionFlag.InCombat] && !Conditions[ConditionFlag.Casting] &&
        !Conditions[ConditionFlag.OccupiedInCutSceneEvent] && !Conditions[ConditionFlag.OccupiedInQuestEvent] &&
        !Conditions[ConditionFlag.OccupiedInEvent] && !Conditions[ConditionFlag.TradeOpen] &&
        !Conditions[ConditionFlag.ExecutingCraftingAction] && !Conditions[ConditionFlag.ExecutingGatheringAction];
    private void CancelRatingRefresh()
    {
        ratingOperationGeneration=-1;
        ratingRefresh?.Cancel("全部读取已停止；已打开的窗口留给玩家处理。",false);
    }
    private string CurrentCharacterContext()
    {
        if(!Client.IsLoggedIn || PlayerState is null || !PlayerState.IsLoaded || PlayerState.ContentId==0) return "";
        if(ratingContentId!=PlayerState.ContentId)
        {
            ratingContentId=PlayerState.ContentId;
            ratingContext=Convert.ToHexString(HMACSHA256.HashData(ratingContextSalt,BitConverter.GetBytes(ratingContentId)));
        }
        return ratingContext;
    }
    private void UpdateRatingCore()
    {
        if(!ratingReadingEnabled && CurrentRating is null && !RatingRefreshBusy)return;
        double now=AutomationNow;
        if(now-lastRatingPoll<(RatingRefreshBusy?0.2:1))return;
        lastRatingPoll=now;
        string context=CurrentCharacterContext();
        if(CurrentRating?.LocalCharacterContext!=context || Identity.Error is not null)CurrentRating=null;
        if(!ratingReadingEnabled && !RatingRefreshBusy)return;
        ratingReader??=new(name=>GameGui.GetAddonByName(name).Address,
            (a,n)=>global::Dalamud.SafeMemory.ReadBytes(a,n,out var bytes)?bytes:null);
        if(RatingRefreshBusy)
        {
            try
            {
                ratingRefresh!.Tick(context,Identity.Error is null,CanRefreshRating(),now,
                    ()=>ratingReader.Observe(context,Identity.Error is null,DateTimeOffset.UtcNow));
                if(ratingRefresh.Result is { } value) { CurrentRating=value;RecordReviewRating(value); }
            }
            catch(Exception ex)
            {
                ratingRefresh!.Cancel("评分刷新失败："+ex.GetType().Name,false);
                Log.Warning("Rating refresh failed: {Type}",ex.GetType().Name);
            }
            return;
        }
        // Passive observation never opens a window; preserve the successful read's timestamp.
        var observed=ratingReader.Observe(context,Identity.Error is null,DateTimeOffset.UtcNow);
        if(observed.Freshness==RatingFreshness.Fresh) { CurrentRating=observed;RecordReviewRating(observed); }
    }
}
