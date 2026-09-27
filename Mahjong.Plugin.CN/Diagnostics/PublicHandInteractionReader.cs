using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Mahjong.Plugin.CN.Diagnostics;

internal sealed record PublicHandButtonCandidate(string FacePath, int RootId, float ScreenX,
    bool SeparateSlotCandidate, bool? ButtonEnabled, string Code);
internal sealed record PublicHandInteractionCandidate(string Code, bool AllVisibleFacesBound,
    IReadOnlyList<PublicHandButtonCandidate> Buttons)
{
    public string MappingStatus => "Candidate";
    public bool CompleteLegalDiscards => false;
    public bool DrawIdentityVerified => false;
}

/// <summary>
/// Reads only the documented Enabled flag of the button owning each already verified,
/// public lower-hand face. The flag is not a complete Mahjong legality or turn contract.
/// Root 135 is reported only as the fixed separately laid out slot, never a proven draw.
/// </summary>
internal sealed unsafe class PublicHandInteractionReader(Func<nint, string, AtkResNode> node)
{
    internal PublicHandInteractionCandidate Read(IReadOnlyList<HandFaceCandidate>? faces,
        IReadOnlyDictionary<string, nint> addresses)
    {
        if (!LowerHandProfile.CheckLayout(faces).Eligible)
            return new("HAND_BUTTON_LAYOUT_UNVERIFIED", false, []);
        // A partial or transitioning hand cannot produce a purported full button set.
        var publicFaces = faces!;
        if (publicFaces.Any(f => f.DiagnosticStatus != LowerHandProfile.VerifiedIconStatus ||
            f.IconId is not uint icon || f.FacePathHash is not uint hash || !LowerTileCatalog.TryDecode(icon, hash, out _)))
            return new("HAND_BUTTON_FACES_UNVERIFIED", false, []);
        var buttons = new List<PublicHandButtonCandidate>(publicFaces.Count);
        foreach (var face in publicFaces)
        {
            LowerHandProfile.TryMatchPath(face.Path, out var route);
            var row = new PublicHandButtonCandidate(face.Path, route.RootId, face.X, route.RootId == 135,
                null, "HAND_BUTTON_OWNER_UNVERIFIED");
            string rootPath = "Emj/" + route.RootId;
            if (!addresses.TryGetValue(face.Path, out nint faceAddress) ||
                !addresses.TryGetValue(rootPath + "/9/5", out nint shellAddress) ||
                !addresses.TryGetValue(rootPath + "/9", out nint buttonAddress) ||
                !addresses.TryGetValue(rootPath, out nint rootAddress) ||
                !Collect(faceAddress, out var chain) ||
                !Owned(chain, faceAddress, buttonAddress, rootAddress, route.RootId, addresses))
            { buttons.Add(row); continue; }
            var shell = node(shellAddress, "handButton.publicShell");
            if (!Visible(shell) || shell.NodeId != 5 || shell.Type != NodeType.Image ||
                shell.Width != 42 || shell.Height != 55 || shell.ParentNode != chain[0].Node.ParentNode)
            { buttons.Add(row); continue; }
            var owner = chain.Single(x => x.Address == buttonAddress).Node;
            buttons.Add(row with { ButtonEnabled = (owner.NodeFlags & NodeFlags.Enabled) != 0,
                Code = "PUBLIC_HAND_BUTTON_CANDIDATE" });
        }
        bool complete = buttons.All(x => x.Code == "PUBLIC_HAND_BUTTON_CANDIDATE");
        return new(complete ? "PUBLIC_HAND_INTERACTION_CANDIDATE" : "HAND_BUTTON_PARTIAL_CANDIDATE",
            complete, buttons.OrderBy(x => x.ScreenX).ToArray());
    }

    private bool Collect(nint address, out List<(nint Address, AtkResNode Node)> chain)
    {
        chain = [];
        var seen = new HashSet<nint>();
        while (address != 0 && chain.Count < 16)
        {
            if (!seen.Add(address)) return false;
            var current = node(address, "handButton.parent");
            if (!Visible(current)) return false;
            chain.Add((address, current)); address = (nint)current.ParentNode;
        }
        return address == 0;
    }

    private static bool Owned(List<(nint Address, AtkResNode Node)> c, nint faceAddress, nint buttonAddress,
        nint rootAddress, int rootId, IReadOnlyDictionary<string, nint> addresses)
    {
        if (c.Count == 0 || c[0].Address != faceAddress || c[0].Node.NodeId != 4 ||
            c[0].Node.Type != NodeType.Image || c[0].Node.Width != 40 || c[0].Node.Height != 52) return false;
        int button = c.FindIndex(x => x.Address == buttonAddress), root = c.FindIndex(x => x.Address == rootAddress);
        if (button <= 0 || root <= button || root + 4 != c.Count ||
            c[button].Node.NodeId != 9 || (int)c[button].Node.Type != 1010 ||
            c[root].Node.NodeId != rootId || (int)c[root].Node.Type != 1055) return false;
        uint previous = 4;
        for (int i = 1; i < button; i++)
        {
            if (c[i].Node.Type != NodeType.Res || c[i].Node.NodeId is < 1 or > 3 || c[i].Node.NodeId >= previous)
                return false;
            previous = c[i].Node.NodeId;
        }
        // ULD template root 1 may be retained or elided by the live component manager.
        if (root > button + 2 || (root == button + 2 &&
            (c[button + 1].Node.NodeId != 1 || c[button + 1].Node.Type != NodeType.Res))) return false;
        uint[] ids = [133, 46, 1];
        for (int i = 0; i < ids.Length; i++)
            if (!addresses.TryGetValue("Emj/" + ids[i], out nint expected) || c[root + i + 1].Address != expected ||
                c[root + i + 1].Node.NodeId != ids[i] || c[root + i + 1].Node.Type != NodeType.Res) return false;
        return c[^1].Node.ParentNode == null;
    }

    private static bool Visible(AtkResNode n) => (n.NodeFlags & NodeFlags.Visible) != 0 && n.Color.A > 0 && !n.IsDrawDisabled &&
        float.IsFinite(n.ScaleX) && n.ScaleX > 0 && n.ScaleX == n.ScaleY &&
        float.IsFinite(n.Rotation) && Math.Abs(n.Rotation) < 0.0001f && float.IsFinite(n.ScreenX) && float.IsFinite(n.ScreenY);
}
