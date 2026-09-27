using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Mahjong.Cn.Rating;
using Mahjong.Plugin.CN.Readers;
using Xunit;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed unsafe class RatingReaderTests
{
    private static readonly DateTimeOffset Now=new(2026,9,27,0,0,0,TimeSpan.Zero);
    private sealed class Fixture
    {
        internal byte[] Memory=new byte[0x10000];
        internal nint Address=0x10000;
        internal int Reads;
        internal readonly MahjongRatingReader Reader;
        internal Fixture()
        {
            Reader=new(_=>Address,(a,n)=> { Reads++; int i=checked((int)(a-0x10000));return i>=0&&i+n<=Memory.Length?Memory.AsSpan(i,n).ToArray():null; });
            AtkUnitBase unit=default; unit.IsVisible=true;unit.Flags1A1=1;unit.Alpha=255;Store(0,unit);
            Text(nameof(AddonGSInfoEmj.CurrentRating),23,"1940",0x1000);
            Text(nameof(AddonGSInfoEmj.HighestRating),24,"2100",0x2000);
            Text(nameof(AddonGSInfoEmj.Rank),7,"初段",0x3000);
        }
        internal void Store<T>(int offset,T value)where T:unmanaged=>MemoryMarshal.Write(Memory.AsSpan(offset),in value);
        internal void Text(string field,uint id,string value,int offset)
        {
            Store(Offset<AddonGSInfoEmj>(field),(long)(0x10000+offset));
            AtkTextNode node=default;node.AtkResNode.NodeId=id;node.AtkResNode.Type=NodeType.Text;Store(offset,node);
            var bytes=Encoding.UTF8.GetBytes(value+"\0");int str=offset+Offset<AtkTextNode>(nameof(AtkTextNode.NodeText));
            Store(str,(long)(0x10000+offset+0x500));Store(str+Offset<Utf8String>(nameof(Utf8String.BufUsed)),(long)bytes.Length);
            bytes.CopyTo(Memory,offset+0x500);
        }
    }
    private static int Offset<T>(string name)=>typeof(T).GetField(name)!.GetCustomAttribute<FieldOffsetAttribute>()!.Value;
    [Fact]public void Named_fields_keep_current_highest_separate_and_do_not_claim_match_refresh()
    {var f=new Fixture();var r=f.Reader.Observe("fixture",true,Now);Assert.Equal(1940,r.CurrentRating);Assert.Equal(2100,r.HighestRating);Assert.Equal("初段",r.Rank);Assert.Equal(RatingFreshness.Fresh,r.Freshness);Assert.Null(r.MatchAssociation);}
    [Fact]public void Closed_page_keeps_old_timestamp_and_context_switch_drops_cache()
    {var f=new Fixture();f.Reader.Observe("fixture",true,Now);f.Address=0;var cache=f.Reader.Observe("fixture",true,Now.AddMinutes(1));Assert.Equal(Now,cache.ReadAtUtc);Assert.Equal(RatingFreshness.Cached,cache.Freshness);Assert.Null(f.Reader.Observe("other",true,Now).CurrentRating);}
    [Fact]public void Invalid_identity_does_not_read_any_memory_or_retain_rating()
    {var f=new Fixture();Assert.Null(f.Reader.Observe("fixture",false,Now).CurrentRating);Assert.Equal(0,f.Reads);}
    [Theory][InlineData("25000",23)][InlineData("not-a-rating",23)][InlineData("1940",99)]
    public void Contradictory_or_mismatched_fields_never_become_valid(string value,uint id)
    {var f=new Fixture();f.Text(nameof(AddonGSInfoEmj.CurrentRating),id,value,0x1000);Assert.Null(f.Reader.Observe("fixture",true,Now).CurrentRating);}
    [Fact]public void Hidden_page_is_not_fresh()
    {var f=new Fixture();f.Reader.Observe("fixture",true,Now);AtkUnitBase unit=default;unit.IsVisible=false;f.Store(0,unit);Assert.Equal(RatingFreshness.Cached,f.Reader.Observe("fixture",true,Now.AddSeconds(1)).Freshness);}
}
