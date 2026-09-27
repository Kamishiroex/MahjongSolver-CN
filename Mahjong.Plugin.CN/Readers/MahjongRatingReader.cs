using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Mahjong.Cn.Rating;

namespace Mahjong.Plugin.CN.Readers;

/// <summary>Pinned GSInfoEmj named fields only. Never reads names or opens a game window.</summary>
internal sealed class MahjongRatingReader(Func<string,nint> lookup, Func<nint,int,byte[]?> read)
{
    internal const string Profile = "CN-2026.09.15-GSInfoEmj-243dc41e4-r1";
    // Profile-page mapping was read locally and confirmed by the user, 2026-09-27.
    // The after-match server refresh association has NOT yet been proven.
    internal const bool MatchRefreshVerified = false;
    private long revision;
    private RatingObservation? last;
    internal RatingObservation Observe(string context, bool identityValid, DateTimeOffset now)
    {
        RatingObservation Unknown(string reason) => new(null,null,null,context,now,"GSInfoEmj",
            RatingMapping.Verified,RatingFreshness.Unknown,Profile,null,revision,reason);
        if(!identityValid || string.IsNullOrEmpty(context)) { last=null; return Unknown("版本或角色上下文不可用。"); }
        if(last?.LocalCharacterContext!=context)last=null;
        try
        {
            nint addon=lookup("GSInfoEmj");
            if(addon==0) return Cached("请打开本人金碟／麻将资料页。",context,now);
            var header=Bytes(addon,System.Runtime.CompilerServices.Unsafe.SizeOf<AtkUnitBase>());
            var unit=MemoryMarshal.Read<AtkUnitBase>(header);
            if(!unit.IsVisible || !unit.IsReady || unit.Alpha==0) return Cached("资料页未就绪，需刷新。",context,now);
            string Text(string field,uint id)
            {
                nint node=(nint)BitConverter.ToInt64(Bytes(addon+Offset<AddonGSInfoEmj>(field),8));
                if(node==0 || BitConverter.ToUInt32(Bytes(node+Offset<AtkResNode>(nameof(AtkResNode.NodeId)),4))!=id ||
                    BitConverter.ToUInt16(Bytes(node+Offset<AtkResNode>(nameof(AtkResNode.Type)),2))!=(ushort)NodeType.Text)
                    throw new InvalidDataException("RATING_NODE_IDENTITY");
                nint str=node+Offset<AtkTextNode>(nameof(AtkTextNode.NodeText));
                nint buffer=(nint)BitConverter.ToInt64(Bytes(str,8));
                long used=BitConverter.ToInt64(Bytes(str+Offset<Utf8String>(nameof(Utf8String.BufUsed)),8));
                if(buffer==0 || used is <2 or >65) throw new InvalidDataException("RATING_TEXT_BOUNDS");
                var bytes=Bytes(buffer,(int)used);
                if(bytes[^1]!=0 || bytes.AsSpan(0,bytes.Length-1).Contains((byte)0))throw new InvalidDataException("RATING_TEXT_INVALID");
                return new UTF8Encoding(false,true).GetString(bytes,0,bytes.Length-1);
            }
            int Number(string field,uint id)
            {
                string text=Text(field,id);
                if(text.Any(c=>c is <'0' or >'9') || !int.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out int value) || value>99999)
                    throw new InvalidDataException("RATING_NUMBER_REJECTED");
                return value;
            }
            int current=Number(nameof(AddonGSInfoEmj.CurrentRating),23);
            int highest=Number(nameof(AddonGSInfoEmj.HighestRating),24);
            string rank=Text(nameof(AddonGSInfoEmj.Rank),7);
            if(rank.Length>12 || rank.Any(c=>!(char.IsDigit(c) || "初段级級十百一二三四五六七八九天凤鳳位士聖圣".Contains(c))))
                rank="段位文本待核验";
            if(highest<current) throw new InvalidDataException("RATING_CONTRADICTION");
            last=new(current,highest,rank,context,now,"本人金碟／麻将资料页",RatingMapping.Verified,
                RatingFreshness.Fresh,Profile,null,++revision,null);
            return last;
        }
        catch(Exception ex) when(ex is InvalidDataException or ArgumentException or OverflowException)
        { return Cached("资料页读取失败："+(ex is InvalidDataException?ex.Message:ex.GetType().Name),context,now); }
    }
    private RatingObservation Cached(string reason,string context,DateTimeOffset now) =>
        last is { } value && value.LocalCharacterContext==context
            ? value with {Freshness=RatingFreshness.Cached,FailureReason=reason}
            : new(null,null,null,context,now,"GSInfoEmj",RatingMapping.Verified,RatingFreshness.Unknown,Profile,null,revision,reason);
    private byte[] Bytes(nint a,int size)=>a>=0x10000 && size is >0 and <=1024 && read(a,size) is { } b && b.Length==size ? b : throw new InvalidDataException("RATING_READ_FAILED");
    private static int Offset<T>(string name)=>typeof(T).GetField(name)!.GetCustomAttribute<FieldOffsetAttribute>()!.Value;
}
