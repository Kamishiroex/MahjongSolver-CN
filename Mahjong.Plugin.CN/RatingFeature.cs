using System.Security.Cryptography;
using Mahjong.Cn.Rating;
using Mahjong.Plugin.CN.Readers;

namespace Mahjong.Plugin.CN;

public sealed partial class Plugin
{
    private readonly byte[] ratingContextSalt=RandomNumberGenerator.GetBytes(32);
    private MahjongRatingReader? ratingReader;
    private double lastRatingPoll;
    private ulong ratingContentId;
    private string ratingContext="";
    private bool ratingReadingEnabled;
    internal RatingObservation? CurrentRating { get; private set; }
    internal void ReadOwnRating()
    {
        Identity=RuntimeIdentity.Read(Interface,Client);
        ratingReadingEnabled=true; lastRatingPoll=0; UpdateRatingCore();
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
        if(!ratingReadingEnabled)return;
        double now=AutomationNow;
        if(now-lastRatingPoll<1)return;
        lastRatingPoll=now;
        ratingReader??=new(name=>GameGui.GetAddonByName(name).Address,
            (a,n)=>global::Dalamud.SafeMemory.ReadBytes(a,n,out var bytes)?bytes:null);
        CurrentRating=ratingReader.Observe(CurrentCharacterContext(),Identity.Error is null,DateTimeOffset.UtcNow);
    }
}
