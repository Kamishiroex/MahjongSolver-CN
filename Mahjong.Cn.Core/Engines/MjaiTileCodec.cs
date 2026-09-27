namespace Mahjong.Cn.Engines;

/// <summary>Strict mjai tile notation for known visible tiles; no game or process services.</summary>
/// <remarks>
/// Protocol evidence: critter-mj/akochan, commit 53188a0b926fbab38177f88c3cd87d554cf412af,
/// share/types.cpp:315-399 (hai_str_to_int / hai_int_to_str), :209-230 (dora conversions).
/// https://github.com/critter-mj/akochan/blob/53188a0b926fbab38177f88c3cd87d554cf412af/share/types.cpp
/// That source treats '?' as an unknown sentinel, never a known tile. P/F/C are white/green/red dragons.
/// </remarks>
public static class MjaiTileCodec
{
    /// <summary>Identifies only the exact protocol unknown marker, without constructing a tile.</summary>
    public static bool IsUnknownMarker(string? token) => token == "?";

    /// <summary>Encodes one of the 34 tile kinds, optionally one of the three red fives.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The kind is outside 0..33.</exception>
    /// <exception cref="ArgumentException">A non-five kind is marked red.</exception>
    public static string Encode(VisibleTile tile)
    {
        Validate(tile);
        if (tile.Id >= 27) return "ESWNPFC"[tile.Id - 27].ToString();
        string token = string.Create(2, tile.Id, static (chars, id) =>
        {
            chars[0] = (char)('1' + id % 9);
            chars[1] = "mps"[id / 9];
        });
        return tile.Red ? token + "r" : token;
    }

    /// <summary>
    /// Decodes exact, case-sensitive mjai notation. Unknown markers, whitespace and aliases fail.
    /// On failure the out value has no tile meaning and must not be consumed.
    /// </summary>
    public static bool TryDecode(string? token, out VisibleTile tile)
    {
        tile = default;
        if (token is null) return false;
        if (token.Length == 1)
        {
            int honor = "ESWNPFC".IndexOf(token[0]);
            if (honor < 0) return false;
            tile = new VisibleTile(27 + honor, false);
            return true;
        }
        if (token.Length is not (2 or 3) || token[0] is < '1' or > '9') return false;
        int suit = token[1] switch { 'm' => 0, 'p' => 1, 's' => 2, _ => -1 };
        if (suit < 0) return false;
        bool red = token.Length == 3;
        if (red && (token[0] != '5' || token[2] != 'r')) return false;
        tile = new VisibleTile(suit * 9 + token[0] - '1', red);
        return true;
    }

    /// <summary>
    /// Converts a known actual-dora tile kind to its preceding indicator kind.
    /// Callers must independently establish that the UI displays actual dora, not indicators.
    /// A valid red five is normalized by kind; the returned indicator is always ordinary.
    /// The red flag alone never establishes that a tile is actual dora.
    /// </summary>
    public static VisibleTile DoraIndicatorForActualDora(VisibleTile actualDora)
    {
        Validate(actualDora);
        int id = actualDora.Id;
        int indicator = id switch
        {
            < 27 => id / 9 * 9 + (id % 9 + 8) % 9,
            < 31 => 27 + (id - 27 + 3) % 4,
            _ => 31 + (id - 31 + 2) % 3,
        };
        return new VisibleTile(indicator, false);
    }

    private static void Validate(VisibleTile tile)
    {
        if (tile.Id is < 0 or > 33)
            throw new ArgumentOutOfRangeException(nameof(tile), tile, "A known tile kind must be within 0..33.");
        if (tile.Red && tile.Id is not (4 or 13 or 22))
            throw new ArgumentException("Only the five of a numbered suit can be red.", nameof(tile));
    }
}
