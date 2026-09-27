using Mahjong.Cn.Rating;

namespace Mahjong.Plugin.CN.Readers;

internal interface IRatingProfileAccess
{
    bool IsOpen { get; }
    bool IsMahjongSelected { get; }
    bool Open();
    bool SelectMahjong();
    void CloseOwned();
    void ForgetOwnership();
}

/// <summary>Bounded navigation with a live permission check, distinct from passive reading.</summary>
internal sealed class RatingRefresh(IRatingProfileAccess profile, Func<bool> operationAllowed)
{
    private string context = "";
    private double requested, openedAt;
    private bool automatic, attemptedOpen, selected;
    private RatingObservation? candidate;
    private int? previousMatches;
    internal bool Busy { get; private set; }
    internal string Status { get; private set; } = "";
    internal RatingObservation? Result { get; private set; }

    internal void Request(string character, double now, bool afterMatch, int? beforeMatches = null)
    {
        if (Busy) return; // Repeated clicks/events never reopen or extend the deadline.
        context = character; requested = now; openedAt = 0;
        automatic = afterMatch; attemptedOpen = selected = false; candidate = Result = null;
        previousMatches = afterMatch ? beforeMatches : null;
        Busy = true; Status = afterMatch ? "整场已结束，等待退桌后刷新评分…" : "正在读取本人评分…";
    }

    internal void Tick(string character, bool identityValid, bool available, double now,
        Func<RatingObservation> observe)
    {
        if (!Busy) return;
        if(!operationAllowed())
        { Cancel("评分窗口操作授权已失效，未继续切页或关闭。",false);return; }
        if (!identityValid || character.Length == 0 || context != character)
        { Cancel("评分刷新已取消：版本或角色上下文已改变。", false); return; }
        if (now - requested > (automatic ? 120 : 10) || attemptedOpen && now - openedAt > 5)
        { Cancel("评分刷新超时，请稍后重试；上次读数不会被覆盖。", available); return; }
        if (!available)
        {
            if (attemptedOpen) Cancel("评分刷新已取消：游戏正在切换场景或进入对局。", false);
            return;
        }
        // The duty-complete event is not proof that the server has published a new rating.
        if (automatic && now - requested < 3) return;
        if (!profile.IsMahjongSelected)
        {
            if (!attemptedOpen)
            {
                // A background refresh must not navigate away from the user's profile page.
                if (automatic && profile.IsOpen) { Status = "等待金碟资料页空闲后刷新评分…"; return; }
                attemptedOpen = true; openedAt = now;
                if (!profile.IsOpen && !profile.Open())
                { Cancel("当前无法打开金碟资料页，请稍后重试。", true); return; }
            }
            if (selected) return; // Page setup can span several framework frames.
            selected = profile.SelectMahjong();
            return; // Wait for the selected page to finish loading on a later frame.
        }
        var value = observe();
        if(!operationAllowed())
        { Cancel("评分窗口操作授权已失效，未接受迟到结果。",false);return; }
        if (value.Freshness != RatingFreshness.Fresh || value.CurrentRating is null ||
            value.LocalCharacterContext != context)
        { candidate = null; return; }
        if (automatic && previousMatches is { } before && (value.MatchesPlayed is null || value.MatchesPlayed == before))
        {
            candidate = null; Status = "等待资料页总场数更新，尚未确认本场评分…";
            return; // Keep the existing bounded deadline; never reopen or extend it.
        }
        if (candidate is { } previous && previous.CurrentRating == value.CurrentRating &&
            previous.HighestRating == value.HighestRating && previous.Rank == value.Rank &&
            previous.MatchesPlayed == value.MatchesPlayed &&
            value.ReadAtUtc - previous.ReadAtUtc >= TimeSpan.FromMilliseconds(150))
        {
            Result = value; Busy = false; Status = automatic ? "整场后已读取资料页评分。" : "本人评分已刷新。";
            profile.CloseOwned();
        }
        else if (candidate is null || candidate.CurrentRating != value.CurrentRating ||
            candidate.HighestRating != value.HighestRating || candidate.Rank != value.Rank || candidate.MatchesPlayed != value.MatchesPlayed) candidate = value;
    }

    internal void Cancel(string reason, bool canClose)
    {
        Busy = false; candidate = null; Result = null; Status = reason;
        if (canClose && operationAllowed()) profile.CloseOwned(); else profile.ForgetOwnership();
    }
}
