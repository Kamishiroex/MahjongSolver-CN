using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Mahjong.Plugin.CN.Readers;
using Mahjong.Plugin.CN.Journaling;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed unsafe class ResultUiReaderTests
{
    private sealed class Fixture
    {
        private const int Base=0x100000;
        private readonly byte[] memory=new byte[0x40000];
        private int used=0x2000;
        internal readonly nint Root;
        internal readonly nint[] Rows=new nint[4];
        internal int Reads;
        internal readonly ResultUiReader Reader;
        internal Fixture()
        {
            Reader=new(name=>name=="EmjTotalResult"?Base:0,(address,size)=>
            { Reads++;long p=address-Base;return p>=0 && p+size<=memory.Length?memory.AsSpan((int)p,size).ToArray():null; });
            Root=Alloc(0x200);SetNode(Root,1,NodeType.Res,0,460,512);
            var roots=new List<nint> {Root};
            for(int i=0;i<4;i++)
            {
                nint row=Rows[i]=Alloc(0x200),component=Alloc(0x200),rank=Alloc(0x300),name=Alloc(0x300);
                SetNode(row,(uint)(20+i),(NodeType)1002,Root,400,92);
                Store(row+Offset<AtkComponentNode>(nameof(AtkComponentNode.Component)),(long)component);
                Text(rank,10,row,(i+1).ToString());Text(name,15,row,i==2?"synthetic-self":i==0?"1234":"荣和");
                var manager=Manager([rank,name]);Store(component+Offset<AtkComponentBase>(nameof(AtkComponentBase.UldManager)),manager);
                roots.Add(row);
            }
            AtkUnitBase unit=default;unit.IsVisible=true;unit.Flags1A1=1;unit.Alpha=255;unit.UldManager=Manager(roots);
            Store(Base,unit);
        }
        private nint Alloc(int size){nint p=Base+used;used+=size;return p;}
        internal void Store<T>(nint address,T value)where T:unmanaged=>MemoryMarshal.Write(memory.AsSpan((int)address-Base),in value);
        private AtkUldManager Manager(IReadOnlyList<nint> nodes)
        {
            nint array=Alloc(nodes.Count*8);
            for(int i=0;i<nodes.Count;i++)Store(array+i*8,(long)nodes[i]);
            AtkUldManager m=default;m.NodeList=(AtkResNode**)array;m.NodeListCount=(ushort)nodes.Count;return m;
        }
        internal void SetNode(nint address,uint id,NodeType type,nint parent,ushort w,ushort h,bool visible=true)
        {
            AtkResNode node=default;node.NodeId=id;node.Type=type;node.Width=w;node.Height=h;node.ParentNode=(AtkResNode*)parent;
            node.NodeFlags=visible?NodeFlags.Visible:0;node.Color.A=255;Store(address,node);
        }
        private void Text(nint address,uint id,nint parent,string text)
        {
            SetNode(address,id,NodeType.Text,parent,64,24);
            byte[] bytes=Encoding.UTF8.GetBytes(text+"\0");nint buffer=Alloc(bytes.Length);
            bytes.CopyTo(memory,(int)buffer-Base);
            nint str=address+Offset<AtkTextNode>(nameof(AtkTextNode.NodeText));
            Store(str,(long)buffer);Store(str+Offset<Utf8String>(nameof(Utf8String.BufUsed)),(long)bytes.Length);
        }
    }
    private static int Offset<T>(string name)=>typeof(T).GetField(name)!.GetCustomAttribute<FieldOffsetAttribute>()!.Value;
    [Fact]public void Actual_native_text_reader_keeps_plain_digits_and_only_boolean_self_identity()
    {
        var f=new Fixture();var read=f.Reader.Observe("EmjTotalResult",true,"synthetic-self");
        Assert.Null(read.Error);Assert.Equal(3,FinalResultEvidence.Read(read).Placement);
        Assert.Equal(4,read.Values.Count(v=>v.Number.HasValue));Assert.Single(read.Values,v=>v.IsSelf);
        string json=JsonSerializer.Serialize(read);Assert.DoesNotContain("synthetic-self",json);Assert.DoesNotContain("1234",json);Assert.DoesNotContain("荣和",json);
    }
    [Fact]public void Hidden_ancestor_prevents_a_complete_rank_and_unsupported_profile_reads_nothing()
    {
        var f=new Fixture();Assert.Empty(f.Reader.Observe("EmjTotalResult",false,"synthetic-self").Values);Assert.Equal(0,f.Reads);
        f.SetNode(f.Rows[0],20,(NodeType)1002,f.Root,400,92,false);
        Assert.Null(FinalResultEvidence.Read(f.Reader.Observe("EmjTotalResult",true,"synthetic-self")).Placement);
    }
    [Fact]public void Wrong_layout_and_broken_pointer_fail_without_partial_results()
    {
        var f=new Fixture();f.SetNode(f.Root,1,NodeType.Res,0,461,512);
        Assert.Equal("RESULT_LAYOUT_UNVERIFIED",f.Reader.Observe("EmjTotalResult",true,"synthetic-self").Error);
        f.SetNode(f.Root,1,NodeType.Res,0,460,512);f.Store(f.Rows[0]+Offset<AtkComponentNode>(nameof(AtkComponentNode.Component)),0L);
        var read=f.Reader.Observe("EmjTotalResult",true,"synthetic-self");Assert.NotNull(read.Error);Assert.Empty(read.Values);
    }
}
