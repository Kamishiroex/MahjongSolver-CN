using System.Text.Json;

namespace Mahjong.Cn.Engines;

public static class AkochanSamples
{
    /// <summary>Three events from the pinned upstream example, with all opponent concealed tiles removed.</summary>
    public static AkochanReplay PublicOpening()
    {
        using var stream = typeof(AkochanSamples).Assembly.GetManifestResourceStream("akochan-public-opening.json")
            ?? throw new AkochanException("AKOCHAN_SAMPLE_MISSING");
        using var json = JsonDocument.Parse(stream);
        return AkochanReplay.Create(json.RootElement.GetProperty("events").EnumerateArray().Select(x => x.GetRawText()),
            0, "akochan-public-opening", false);
    }
}
