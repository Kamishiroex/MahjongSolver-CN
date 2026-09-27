using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Mahjong.Plugin.CN.Readers;

// Only finite tokens, numbers and an in-memory comparison with the local name leave
// this reader. Unknown strings (including names/worlds) are never retained or hashed.
internal sealed record ResultUiValue(string Path, int? Number, string? Token, bool IsSelf,
    uint? IconId=null,uint? TexturePathHash=null,ushort? U=null,ushort? V=null,ushort? Width=null,ushort? Height=null);
internal sealed record ResultUiSample(string Addon, string Profile, bool Visible,
    IReadOnlyList<ResultUiValue> Values, string? Error);

/// <summary>Read-only, bounded inspection of the fixed CN result ULDs. No AtkValues or callbacks.</summary>
internal sealed unsafe class ResultUiReader(Func<string,nint> lookup, Func<nint,int,byte[]?> read)
{
    internal const string Profile = "CN-2026.09.15-result-uld-r1";
    internal const string RankUldSha256 = "9683BCE92217300BE30B69159FA096D57012A99839A521EFAFC1959AB94DB148";
    private int budget;
    private readonly HashSet<nint> seen = [];
    private static readonly UTF8Encoding Utf8 = new(false,true);

    internal ResultUiSample Observe(string addonName, bool identityValid, string ownName)
    {
        var values=new List<ResultUiValue>();
        if(!identityValid || addonName is not ("Emj" or "EmjTotalResult"))
            return new(addonName,Profile,false,[],"RESULT_PROFILE_UNSUPPORTED");
        budget=8000; seen.Clear();
        try
        {
            nint addon=lookup(addonName);
            if(addon==0)return new(addonName,Profile,false,[],null);
            var unit=Read<AtkUnitBase>(addon);
            if(!unit.IsVisible || !unit.IsReady || unit.Alpha==0)return new(addonName,Profile,false,[],null);
            var roots=Nodes(addon+Offset<AtkUnitBase>(nameof(AtkUnitBase.UldManager)));
            nint root=roots.SingleOrDefault(p=> { var n=Read<AtkResNode>(p);return n.NodeId==1 && n.ParentNode==null; });
            if(root==0)throw new InvalidDataException("RESULT_ROOT_MISSING");
            var rn=Read<AtkResNode>(root);
            if(addonName=="EmjTotalResult" && (rn.Width!=460 || rn.Height!=512))
                throw new InvalidDataException("RESULT_LAYOUT_UNVERIFIED");
            foreach(nint address in roots)
            {
                var node=Read<AtkResNode>(address);
                // Emj root 46 is the result/transition overlay, 54 the hand detail.
                // Enumerate only fixed result nodes; no hands, chat or unrelated UI.
                bool allowed=addonName=="Emj" ? node.NodeId is 38 or 40 or 42 or 44 or 48 or 49 or 50 or 51 or 55 or 56 or 57 or 58 or 94 or 95
                    : node.NodeId is 2 or 20 or 21 or 22 or 23 or 25;
                if(allowed && Visible(address,root)) Visit(address,addonName+"/"+node.NodeId,0,root,ownName,values);
            }
            return new(addonName,Profile,true,values,null);
        }
        catch(Exception ex) when(ex is InvalidDataException or ArgumentException or OverflowException or InvalidOperationException)
        { return new(addonName,Profile,false,[],ex is InvalidDataException?ex.Message:"RESULT_READ_FAILED"); }
    }

    private void Visit(nint address,string path,int depth,nint root,string ownName,List<ResultUiValue> values)
    {
        if(depth>3 || values.Count>=64 || !seen.Add(address))return;
        var node=Read<AtkResNode>(address);
        if(node.Type==NodeType.Text || node.Type==NodeType.Counter)
        {
            if(Grammar(path) is null)return;
            nint str=address+(node.Type==NodeType.Text?Offset<AtkTextNode>(nameof(AtkTextNode.NodeText)):Offset<AtkCounterNode>(nameof(AtkCounterNode.NodeText)));
            nint buffer=(nint)BitConverter.ToInt64(Bytes(str,8));
            long used=BitConverter.ToInt64(Bytes(str+Offset<Utf8String>(nameof(Utf8String.BufUsed)),8));
            if(buffer==0 || used is <2 or >129)return;
            byte[] bytes=Bytes(buffer,(int)used);
            if(bytes[^1]!=0 || bytes.AsSpan(0,bytes.Length-1).Contains((byte)0))return;
            // Read the plain text fields used by the pinned UI. Do not call the
            // general SeString validator: this pinned build rejects plain "1234".
            // Formatting payloads remain unknown instead of dropping arbitrary bytes.
            var value=Parse(path,Utf8.GetString(bytes,0,bytes.Length-1),ownName);
            if(value is not null)values.Add(value);
            else if(Grammar(path)=="number")values.Add(new(path,null,"unrecognized-number",false));
        }
        else if(node.Type==NodeType.Image && ImagePath(path))
        {
            var image=Read<AtkImageNode>(address);
            if(image.PartsList==null)return;
            var list=Read<AtkUldPartsList>((nint)image.PartsList);
            if(list.PartCount is 0 or >64 || image.PartId>=list.PartCount || list.Parts==null)return;
            var part=Read<AtkUldPart>((nint)list.Parts+image.PartId*sizeof(AtkUldPart));
            if(part.UldAsset==null)return;
            var asset=Read<AtkUldAsset>((nint)part.UldAsset);
            if(asset.AtkTexture.TextureType!=TextureType.Resource || asset.AtkTexture.Resource==null)return;
            nint texture=(nint)asset.AtkTexture.Resource;
            uint icon=Read<uint>(texture+Offset<AtkTextureResource>(nameof(AtkTextureResource.IconId)));
            uint hash=Read<uint>(texture+Offset<AtkTextureResource>(nameof(AtkTextureResource.TexPathHash)));
            values.Add(new(path,null,null,false,icon,hash,part.U,part.V,part.Width,part.Height));
        }
        else if((int)node.Type>=1000)
        {
            nint component=(nint)BitConverter.ToInt64(Bytes(address+Offset<AtkComponentNode>(nameof(AtkComponentNode.Component)),8));
            foreach(nint child in Nodes(component+Offset<AtkComponentBase>(nameof(AtkComponentBase.UldManager))))
            {
                var n=Read<AtkResNode>(child);
                if(Visible(child,root))Visit(child,path+"/"+n.NodeId,depth+1,root,ownName,values);
            }
        }
    }

    internal static ResultUiValue? Parse(string path,string text,string ownName)
    {
        string? grammar=Grammar(path);
        if(grammar is null)return null;
        // The name region never uses the numeric/token grammar, even for an all-
        // digit name or a name that happens to be one of the allowed result words.
        if(grammar=="self")return ownName.Length>0 && text==ownName?new(path,null,null,true):null;
        if(text.Length>32 || text.Any(char.IsControl))return null;
        if(grammar=="token")return text is "荣和" or "自摸" or "和牌" or "放铳" or "流局" or "荒牌流局" or "中途流局" or
            "九种九牌" or "四风连打" or "四杠散了" or "四家立直" or "三家和了" or "流局满贯" or "听牌" or "未听牌"
            ? new(path,null,text,false):null;
        string number=text.Trim(' ');
        if(number.Length>0 && number[0] is '+' or '-')number=number[1..];
        if(number.Length is 0 or >7)return null;
        if(number.Contains(','))
        {
            var groups=number.Split(',');
            if(groups.Length!=2 || groups[0].Length is <1 or >3 || groups[1].Length!=3)return null;
            number=string.Concat(groups);
        }
        if(!number.All(char.IsAsciiDigit) || !int.TryParse(number,NumberStyles.None,CultureInfo.InvariantCulture,out int n) || n>200000)return null;
        if(text.TrimStart(' ').StartsWith('-'))n=-n;
        return new(path,n,null,false);
    }

    private static string? Grammar(string path)
    {
        if(path=="Emj/56")return "self";
        if(path is "Emj/55" or "Emj/48/2" or "Emj/50/2")return "token";
        if(path=="Emj/95/3")return "number";
        string[] p=path.Split('/');
        if(p.Length==4 && p[0]=="Emj" && p[3] is "2" or "3" &&
            (p[1]=="38" && p[2] is "11" or "12" || p[1] is "40" or "42" or "44" && p[2] is "12" or "13"))return "number";
        if(p.Length!=3 || p[0]!="EmjTotalResult" || p[1] is not ("20" or "21" or "22" or "23"))return null;
        return p[2] switch {"15"=>"self", "2" or "3" or "4" or "5" or "8" or "10"=>"number",_=>null};
    }

    private static bool ImagePath(string path)=>path is "Emj/48/3" or "Emj/49/2" or "Emj/51/2" or
        "Emj/38/22/2" or "Emj/40/25/2" or "Emj/42/25/2" or "Emj/44/25/2";

    private bool Visible(nint address,nint root)
    {
        var parents=new HashSet<nint>();
        for(int i=0;address!=0 && i<24;i++)
        {
            if(!parents.Add(address))return false;
            var n=Read<AtkResNode>(address);
            if((n.NodeFlags&NodeFlags.Visible)==0 || n.Color.A==0 || n.IsDrawDisabled)return false;
            if(address==root)return true;
            address=(nint)n.ParentNode;
        }
        return false;
    }
    private nint[] Nodes(nint uld)
    {
        int count=BitConverter.ToUInt16(Bytes(uld+Offset<AtkUldManager>(nameof(AtkUldManager.NodeListCount)),2));
        if(count>2048)throw new InvalidDataException("RESULT_NODE_LIMIT");
        nint list=(nint)BitConverter.ToInt64(Bytes(uld+Offset<AtkUldManager>(nameof(AtkUldManager.NodeList)),8));
        if(count==0)return [];
        byte[] pointers=Bytes(list,count*8);
        return Enumerable.Range(0,count).Select(i=>(nint)BitConverter.ToInt64(pointers,i*8)).Where(p=>p!=0).ToArray();
    }
    private T Read<T>(nint a) where T:unmanaged=>MemoryMarshal.Read<T>(Bytes(a,sizeof(T)));
    private byte[] Bytes(nint a,int size)=>--budget>=0 && a>=0x10000 && size is >0 and <=16384 && read(a,size) is { } b && b.Length==size
        ? b:throw new InvalidDataException("RESULT_READ_BOUNDS");
    private static int Offset<T>(string name)=>typeof(T).GetField(name)!.GetCustomAttribute<FieldOffsetAttribute>()!.Value;
}
