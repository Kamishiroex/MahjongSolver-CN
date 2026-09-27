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
    internal RatingObservation? CurrentRating { get; private set; }
    internal bool RatingRefreshBusy => ratingRefresh?.Busy == true;
    internal string RatingRefreshStatus => ratingRefresh?.Status ?? "";
    internal void ReadOwnRating()
    {
        Identity=RuntimeIdentity.Read(Interface,Client);
        RequestRatingRefresh(false);
        lastRatingPoll=0; UpdateRatingCore();
    }
    private void RequestRatingRefresh(bool afterMatch)
    {
        string context=CurrentCharacterContext();
        if(disposed || Identity.Error is not null || context.Length==0)return;
        ratingReadingEnabled=true;
        ratingProfile??=new(name=>GameGui.GetAddonByName(name).Address);
        ratingRefresh??=new(ratingProfile);
        ratingRefresh.Request(context,AutomationNow,afterMatch);
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
        ratingRefresh?.Cancel("全部读取已停止。", !disposed && Framework.IsInFrameworkUpdateThread &&
            Identity.Error is null && CanRefreshRating());
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
                if(ratingRefresh.Result is { } value)CurrentRating=value;
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
        if(observed.Freshness==RatingFreshness.Fresh)CurrentRating=observed;
    }
}
