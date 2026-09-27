using FFXIVClientStructs.FFXIV.Component.GUI;
using Mahjong.Plugin.CN.Diagnostics;
using Xunit;

namespace Mahjong.Plugin.CN.Tests;

public sealed unsafe class PublicHandInteractionReaderTests
{
    [Theory]
    [InlineData(134, true)]
    [InlineData(135, true)]
    [InlineData(1340001, true)]
    [InlineData(1340016, false)]
    public void Current_own_public_button_flag_is_only_a_candidate(int root, bool enabled)
    {
        var f = new Fixture(root); f.SetEnabled(enabled);
        var result = f.Read(); var button = Assert.Single(result.Buttons);
        Assert.True(result.AllVisibleFacesBound); Assert.Equal(enabled, button.ButtonEnabled);
        Assert.Equal(root == 135, button.SeparateSlotCandidate);
        Assert.Equal("Candidate", result.MappingStatus); Assert.False(result.CompleteLegalDiscards);
        Assert.False(result.DrawIdentityVerified);
    }

    [Theory]
    [InlineData("opponent-face")]
    [InlineData("unknown-resource")]
    [InlineData("unverified-face")]
    [InlineData("duplicate-face")]
    [InlineData("transition-count")]
    public void Unproven_or_transitioning_hand_does_not_even_read_button_nodes(string variation)
    {
        var f = new Fixture(134);
        switch (variation)
        {
            case "opponent-face": f.Faces[0] = f.Faces[0] with { Path = "Emj/137/9/4" }; break;
            case "unknown-resource": f.Faces[0] = f.Faces[0] with { FacePathHash = 0 }; break;
            case "unverified-face": f.Faces[0] = f.Faces[0] with { DiagnosticStatus = "LAYOUT_ONLY" }; break;
            case "duplicate-face": f.Faces.Add(f.Faces[0]); break;
            case "transition-count": for (int i = 0; i < 14; i++) f.Faces.Add(f.Faces[0]); break;
        }
        var result = f.Read(); Assert.False(result.AllVisibleFacesBound); Assert.Empty(result.Buttons); Assert.Equal(0, f.Reads);
    }

    [Theory]
    [InlineData("hidden-button")]
    [InlineData("hidden-parent")]
    [InlineData("alpha")]
    [InlineData("wrong-button-template")]
    [InlineData("wrong-root-template")]
    [InlineData("wrong-root-address")]
    [InlineData("shell-not-sibling")]
    [InlineData("shell-hidden")]
    [InlineData("cycle")]
    [InlineData("rotation")]
    [InlineData("nan")]
    [InlineData("foreign-panel")]
    public void Public_parent_and_sibling_proof_is_required_before_enabled_result(string variation)
    {
        var f = new Fixture(134); var button = f.Nodes[Fixture.Button]; var owner = f.Nodes[Fixture.Root];
        switch (variation)
        {
            case "hidden-button": button.NodeFlags &= ~NodeFlags.Visible; break;
            case "hidden-parent": owner.NodeFlags &= ~NodeFlags.Visible; break;
            case "alpha": button.Color.A = 0; break;
            case "wrong-button-template": button.Type = (NodeType)1029; break;
            case "wrong-root-template": owner.Type = (NodeType)1058; break;
            case "wrong-root-address": f.Addresses["Emj/134"] += 8; break;
            case "shell-not-sibling": var shell = f.Nodes[Fixture.Shell]; shell.ParentNode = (AtkResNode*)Fixture.Root; f.Nodes[Fixture.Shell] = shell; break;
            case "shell-hidden": var hidden = f.Nodes[Fixture.Shell]; hidden.NodeFlags &= ~NodeFlags.Visible; f.Nodes[Fixture.Shell] = hidden; break;
            case "cycle": owner.ParentNode = (AtkResNode*)Fixture.Button; break;
            case "rotation": button.Rotation = 1; break;
            case "nan": button.ScreenX = float.NaN; break;
            case "foreign-panel": owner.ParentNode = (AtkResNode*)Fixture.Table; break;
        }
        f.Nodes[Fixture.Button] = button; f.Nodes[Fixture.Root] = owner;
        var result = f.Read(); Assert.False(result.AllVisibleFacesBound); Assert.Null(Assert.Single(result.Buttons).ButtonEnabled);
        Assert.InRange(f.Reads, 1, 16);
    }

    [Fact]
    public void Hidden_or_missing_shell_is_not_an_enabled_button()
    {
        var f = new Fixture(135); f.Addresses.Remove("Emj/135/9/5");
        Assert.Null(Assert.Single(f.Read().Buttons).ButtonEnabled); Assert.Equal(0, f.Reads);
    }

    [Fact]
    public void No_faces_does_not_establish_no_legal_discards()
    {
        var reader = new PublicHandInteractionReader((_, _) => throw new Exception("No read allowed"));
        var result = reader.Read(null, new Dictionary<string, nint>());
        Assert.False(result.AllVisibleFacesBound); Assert.False(result.CompleteLegalDiscards); Assert.Empty(result.Buttons);
    }

    private sealed class Fixture
    {
        internal const nint Face = 0x1000, Shell = 0x2000, Wrapper3 = 0x3000, Wrapper2 = 0x4000,
            Button = 0x5000, Root = 0x6000, Lower = 0x7000, Table = 0x8000, Addon = 0x9000;
        internal readonly Dictionary<nint, AtkResNode> Nodes = [];
        internal readonly Dictionary<string, nint> Addresses = [];
        internal readonly List<HandFaceCandidate> Faces = [];
        internal int Reads;
        internal Fixture(int root)
        {
            Add("Emj/" + root + "/9/4", Face, 4, 2, Wrapper3, 40, 52);
            Add("Emj/" + root + "/9/5", Shell, 5, 2, Wrapper3, 42, 55);
            Add("internal3", Wrapper3, 3, 1, Wrapper2, 42, 55);
            Add("internal2", Wrapper2, 2, 1, Button, 42, 55);
            Add("Emj/" + root + "/9", Button, 9, 1010, Root, 42, 55);
            Add("Emj/" + root, Root, (uint)root, 1055, Lower, 50, 60);
            Add("Emj/133", Lower, 133, 1, Table, 620, 80);
            Add("Emj/46", Table, 46, 1, Addon, 620, 560);
            Add("Emj/1", Addon, 1, 1, 0, 1260, 700);
            Faces.Add(new("Emj/" + root + "/9/4", 100, 200, 40, 52, 76028,
                LowerHandProfile.VerifiedIconStatus, LowerHandImageReader.ClientTexturePathHash("ui/icon/076000/076028_hr1.tex")));
        }
        internal void SetEnabled(bool enabled)
        { var b = Nodes[Button]; if (enabled) b.NodeFlags |= NodeFlags.Enabled; else b.NodeFlags &= ~NodeFlags.Enabled; Nodes[Button] = b; }
        internal PublicHandInteractionCandidate Read() => new PublicHandInteractionReader((p, _) => { Reads++; return Nodes[p]; }).Read(Faces, Addresses);
        private void Add(string path, nint address, uint id, int type, nint parent, ushort width, ushort height)
        {
            var n = new AtkResNode { NodeId = id, Type = (NodeType)type, ParentNode = (AtkResNode*)parent,
                Width = width, Height = height, ScaleX = 1, ScaleY = 1, NodeFlags = NodeFlags.Visible };
            n.Color.A = 255; Nodes[address] = n; Addresses[path] = address;
        }
    }
}
