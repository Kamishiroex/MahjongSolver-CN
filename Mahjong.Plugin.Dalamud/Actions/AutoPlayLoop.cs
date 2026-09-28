using System;
using System.Collections.Generic;
using System.Text.Json;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Mahjong.Engine;
using Mahjong.Plugin.Dalamud.GameState;
using Mahjong.Plugin.Dalamud.GameState.Variants;
using Mahjong.Policy;
using AtkValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;

namespace Mahjong.Plugin.Dalamud.Actions;

public sealed class AutoPlayLoop : IDisposable
{
    private const int ChiVariantSelectStateCode = 25;
    /// <summary>Agari / draw result modal between hands; carries Legal=None and a single "Next" button.</summary>
    private const int HandResultStateCode = 29;
    private static readonly TimeSpan RetryCooldown = TimeSpan.FromSeconds(3.0);
    private static readonly TimeSpan DispatchTimeout = TimeSpan.FromSeconds(10.0);
    private static readonly TimeSpan PolicyPendingPollInterval = TimeSpan.FromMilliseconds(250);

    private const int VariantAcceptDelayMs = 500;
    private const int CallDecisionDelayMs = 700;
    private const int RiichiTsumogiriDelayMs = 700;
    private const int HandResultAdvanceDelayMs = 300;
    /// <summary>State-29 must persist this long before we fire the Next click. Firing during the result-modal animation phase landed the addon in a stuck state-32 with no inputs accepted (2026-05-26).</summary>
    private static readonly TimeSpan HandResultStabilityWindow = TimeSpan.FromSeconds(3.5);

    private const ActionFlags CallPromptFlags =
        ActionFlags.Pon | ActionFlags.Chi |
        ActionFlags.MinKan | ActionFlags.ShouMinKan |
        ActionFlags.Ron | ActionFlags.Riichi | ActionFlags.Tsumo | ActionFlags.Kyushukyuhai;

    private readonly Plugin plugin;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly MahjongAddon addon;
#if MAHJONG_CN
    private DateTime? kyushuSubmittedAt;
    private nint kyushuOwner;
    private bool kyushuConfirmed;
#endif
    private readonly ActionStateMachine fsm = new(DispatchTimeout, RetryCooldown);
    private readonly QueuedActionGate queuedActions = new();
    private bool disposed;
    private DateTime policyRetryNotBefore;
    private string? lastSkipReason;
    private DateTime? handResultFirstSeenAt;
    private bool handResultDispatchedThisInstance;
    private DateTime? handResultSubmittedAt;
    private DateTime? nextHandWaitStartedAt;
    private static readonly TimeSpan NextHandWaitTimeout = TimeSpan.FromSeconds(30);
    private MeldCandidate? pendingAiChiVariant;
    private bool? riichiDiscardRed;
    private bool? riichiDiscardTsumogiri;
    private Guid? reviewDecision;

    public string LastActionDescription { get; private set; } = "(none)";

    public int LastObservedState { get; private set; } = -1;

    public int LastObservedHandCount { get; private set; } = -1;

#if MAHJONG_CN
    internal AutoPlayLoop(Plugin plugin, IFramework framework, IPluginLog log, MahjongAddon addon)
#else
    public AutoPlayLoop(Plugin plugin, IFramework framework, IPluginLog log, MahjongAddon addon)
#endif
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(framework);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(addon);
        this.plugin = plugin;
        this.framework = framework;
        this.log = log;
        this.addon = addon;
        framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        Stop();
        framework.Update -= OnUpdate;
    }

    /// <summary>Cancels every previously queued action, including actions waiting for a humanized delay.</summary>
    public void CancelPendingActions() => queuedActions.Invalidate();

    /// <summary>Framework-thread reset; CancelPendingActions can be called from another thread.</summary>
    public void Stop()
    {
        CancelPendingActions();
        fsm.CompleteDispatch();
        fsm.ClearContext();
        fsm.ClearRiichiConfirm();
        pendingAiChiVariant = null;
#if MAHJONG_CN
        kyushuSubmittedAt = null;
        kyushuOwner = 0;
        kyushuConfirmed = false;
#endif
        riichiDiscardRed = null;
        riichiDiscardTsumogiri = null;
        pendingOutcome = null;
        policyRetryNotBefore = default;
        handResultFirstSeenAt = null;
        handResultDispatchedThisInstance = false;
        handResultSubmittedAt = null;
        nextHandWaitStartedAt = null;
        stuckStateCode = null;
        stuckHandCount = null;
        stuckLegal = null;
        stuckEmitted = false;
    }

    private void StopForError(string reason)
    {
        if (disposed) return; // Preserve the stop reason already recorded by the runtime.
        LastActionDescription = reason;
        Stop();
        StopRuntime(reason);
        log.Warning($"[AutoPlayLoop] stopped: {reason}");
    }

    // The upstream entry remains independently compilable; only the CN facade owns
    // the exact-version/runtime gate and table-session lifecycle used by this port.
#if MAHJONG_CN
    private bool RuntimeCanOperate => plugin.CanOperate;
    private bool RuntimeHasObservedTable => plugin.HasObservedTable;
    private void StopRuntime(string reason) => plugin.PauseAutomation(reason);
#else
    private bool RuntimeCanOperate => true;
    private bool RuntimeHasObservedTable => true;
    private void StopRuntime(string reason) => plugin.ConfigService.Update(config => config with
    {
        AutomationArmed = false,
        SuggestionOnly = true,
    });
#endif

    private void OnUpdate(IFramework fw)
    {
        try { OnUpdateCore(); }
        catch (Exception ex)
        {
            if (disposed) return;
            log.Error($"[AutoPlayLoop] update error: {ex}");
            StopForError($"AUTO_READ_OR_POLICY_ERROR: {ex.GetType().Name}");
        }
    }

    private unsafe void OnUpdateCore()
    {
        if (disposed)
            return;

        if (!IsAutomationArmed())
        {
            var cfg = plugin.Configuration;
            if (fsm.IsDispatchInFlight || pendingOutcome is not null) Stop();
            if (cfg.AutomationArmed && !cfg.SuggestionOnly && !RuntimeCanOperate)
                StopForError("AUTO_RUNTIME_GATE_CLOSED: 当前版本、界面或读取状态不允许自动操作。");
            EmitSkipReason($"gate: tos={cfg.TosAccepted} armed={cfg.AutomationArmed} suggest_only={cfg.SuggestionOnly}",
                state: -1, hand: -1, flags: 0);
            return;
        }

        if (!RuntimeHasObservedTable)
        {
            EmitSkipReason("waiting for first visible Mahjong table", state: -1, hand: -1, flags: 0);
            return;
        }

#if MAHJONG_CN
        // A disposed addon is a scene exit, not a corrupt in-game snapshot. The
        // runtime's later Framework callback used to lose this race to this loop.
        if (!addon.TryGet(out _, out _))
        {
            plugin.StopAutomation("SCENE_EXIT：牌桌已关闭，自动操作已停止；等待确认整场完成与下一次入桌。");
            return;
        }
#endif

        if (!ContinueAfterStuckRecovery())
        {
            EmitSkipReason("dispatch in flight (still within timeout)",
                state: -1, hand: -1, flags: 0);
            return;
        }

        // Runs before the snapshot guard: the result modal has no hand-array, so TryBuildSnapshot returns null and the post-snapshot state checks never fire.
        int earlyState = ReadStateCode();
        if (TryHandleHandResult(earlyState))
            return;

#if MAHJONG_CN
        if (TryWaitForKyushu(DateTime.UtcNow)) return;
#endif

        var snap = plugin.AddonReader.TryBuildSnapshot();

        if (TryWaitForNextHand(earlyState, snap, DateTime.UtcNow)) return;

        // Observe changes separately from submission; a callback result is not a game acknowledgement.
        CheckPendingDispatchOutcome(snap);
        if (!IsAutomationArmed()) return;
        if (snap is null)
        {
            StopForError("AUTO_SNAPSHOT_UNAVAILABLE: 牌局读取失败或已退出牌桌，已取消排队动作。");
            return;
        }
        // Do not resend an unacknowledged submission when the old retry cooldown elapses.
        // Riichi is a two-step upstream route: its first submission can leave the
        // same preview popup visible until the latched discard is submitted.
        if (pendingOutcome is { } pending && !CanContinuePendingRiichi(pending.Label, snap)) return;
        CheckStuckStateAndEmit(snap);
        if (!IsAutomationArmed()) return;

        int state = ReadStateCode();
        if (state < 0 || snap.AddonStateCode != state)
        {
            StopForError("AUTO_STATE_INCONSISTENT: 当前界面状态与牌局快照不一致。");
            return;
        }
        var context = new DispatchContext(state, snap.Hand.Count);
        LastObservedState = state;
        LastObservedHandCount = context.Hand;

        if (state == ChiVariantSelectStateCode)
        {
            EmitProgressing();
            HandleChiVariantSelect(context);
            return;
        }

        bool isCallPrompt = (snap.Legal.Flags & CallPromptFlags) != 0;
        bool isDiscardTurn = snap.Legal.Can(ActionFlags.Discard);
        int flags = (int)snap.Legal.Flags;

        // Riichi-confirm latch is hand-scoped via ObserveWall — popup signature drops mid-hand and clearing per-tick would let the loop redeclare riichi 20+ times in one hand.
        fsm.ObserveWall(snap.WallRemaining);

        if (!isCallPrompt && !isDiscardTurn)
        {
            // Do not clear FSM context on transient "not actionable" ticks — discard-animation gaps drop the Discard flag mid-commit and clearing here permits a duplicate dispatch.
            EmitSkipReason($"not actionable (state={state} hand={snap.Hand.Count} legal={snap.Legal.Flags})",
                state: state, hand: snap.Hand.Count, flags: flags);
            return;
        }

        if (IsPolicyRefreshDeferred(DateTime.UtcNow))
        {
            EmitSkipReason("policy calculation pending; waiting for a fresh framework tick",
                state: state, hand: snap.Hand.Count, flags: flags);
            return;
        }

        if (fsm.ShouldSuppressForContext(context, DateTime.UtcNow))
        {
            EmitSkipReason($"suppressed for context (state={context.State} hand={context.Hand})",
                state: state, hand: snap.Hand.Count, flags: flags);
            return;
        }

        if (TryHandleRiichiConfirmTsumogiri(snap, context, isCallPrompt))
        {
            EmitProgressing();
            return;
        }

        EmitProgressing();
        if (isCallPrompt)
            ScheduleCallDecision(context);
        else
            ScheduleDiscard(context);
    }

    /// <summary>Dedup by exact reason string — loop ticks 60x/sec, so emit only on transitions.</summary>
    private void EmitSkipReason(string reason, int state, int hand, int flags)
    {
        if (lastSkipReason == reason)
            return;
        lastSkipReason = reason;
        log.Info($"[AutoPlayLoop] skip: {reason}");
        plugin.FindingsLog?.Record("hand_state_paused", new Dictionary<string, object?>
        {
            ["reason"] = reason,
            ["state"] = state,
            ["hand"] = hand,
            ["flags"] = flags,
        });
    }

    private void EmitProgressing()
    {
        if (lastSkipReason is null)
            return;
        log.Info($"[AutoPlayLoop] resumed (was: {lastSkipReason})");
        lastSkipReason = null;
    }

    private void EmitDecisionFinding(string source, StateSnapshot snap, ActionChoice choice)
    {
#if MAHJONG_CN
        if (!choice.Reasoning.StartsWith("AVAILABLE_WIN_GUARD:", StringComparison.Ordinal))
            reviewDecision=plugin.ReviewDecisionId(snap);
#endif
        plugin.FindingsLog?.Record("decision", new Dictionary<string, object?>
        {
            ["source"] = source,
            ["kind"] = choice.Kind.ToString(),
            ["tile"] = choice.DiscardTile?.ToString(),
            ["hand_count"] = snap.Hand.Count,
            ["flags"] = (int)snap.Legal.Flags,
            ["pon_candidates"] = snap.Legal.PonCandidates.Count,
            ["chi_candidates"] = snap.Legal.ChiCandidates.Count,
            ["kan_candidates"] = snap.Legal.KanCandidates.Count,
            ["wall"] = snap.WallRemaining,
            ["reasoning"] = choice.Reasoning,
        });
    }

    private void EmitDispatchFinding(
        string label, InputDispatcher.DispatchResult result,
        int? option = null, Tile? tile = null, int? slot = null, int? state = null,
        StateSnapshot? snap = null, bool callInput = false)
    {
        Guid? reviewSubmission=null;
#if MAHJONG_CN
        // A callback can synchronously tear down the addon. Preserve its managed submission result.
        string recordedPath = (callInput ? plugin.ActiveCallDispatch?.Route : plugin.ActiveDiscardPath)
            ?? "(dispatch-route-unavailable-after-teardown)";
        var actualCall = callInput ? plugin.ActiveCallDispatch ?? plugin.LastStoppedDispatch : null;
        if (actualCall is not null) { option = actualCall.Option; state = actualCall.StateCode; }
        reviewSubmission=plugin.RecordActionSubmission(label, result, option, tile, slot, state, recordedPath, reviewDecision);
        reviewDecision=null;
#endif
        // Native input can synchronously close the addon and dispose this loop.
        if (disposed || !IsAutomationArmed()) return;
        string dispatchPath = callInput
            ? plugin.Dispatcher.LastCallDispatch?.Route ?? "(call-route-unavailable)"
            : plugin.Dispatcher.LastDiscardPath;
        plugin.FindingsLog?.Record("dispatch_attempted", new Dictionary<string, object?>
        {
            ["label"] = label,
            ["result"] = InputDispatcher.DescribeResult(result),
            ["action_confirmed"] = false,
            ["option"] = option,
            ["tile"] = tile?.ToString(),
            ["slot"] = slot,
            ["state"] = state,
            ["path"] = dispatchPath,
            ["cur_state"] = snap?.AddonStateCode,
            ["cur_hand"] = snap?.Hand.Count,
            ["cur_melds"] = snap?.OurMelds.Count,
            ["cur_legal"] = snap?.Legal.Flags.ToString(),
        });

        if (result != InputDispatcher.DispatchResult.Submitted)
        {
            StopForError($"AUTO_DISPATCH_REJECTED: {label}: {InputDispatcher.DescribeResult(result)}");
            return;
        }

        if (snap is not null)
        {
            pendingOutcome = new PendingDispatchOutcome(
                Label: label,
                DispatchedAt: DateTime.UtcNow,
                StateAtDispatch: snap.AddonStateCode,
                HandAtDispatch: snap.Hand.Count,
                MeldsAtDispatch: snap.OurMelds.Count,
                LegalAtDispatch: snap.Legal.Flags,
                LastDispatchPath: dispatchPath,
                ClosedHandAtDispatch: snap.Hand.ToArray(),
                WallAtDispatch: snap.WallRemaining) { ReviewSubmission=reviewSubmission };
        }
    }

    private PendingDispatchOutcome? pendingOutcome;

    private static readonly TimeSpan DispatchOutcomeWindow = DispatchTimeout;

    private readonly record struct PendingDispatchOutcome(
        string Label,
        DateTime DispatchedAt,
        int StateAtDispatch,
        int HandAtDispatch,
        int MeldsAtDispatch,
        ActionFlags LegalAtDispatch,
        string LastDispatchPath,
        IReadOnlyList<Tile> ClosedHandAtDispatch,
        int WallAtDispatch)
    { internal Guid? ReviewSubmission { get; init; } }

    private static readonly TimeSpan StuckStateThreshold = TimeSpan.FromSeconds(10);

    private int? stuckStateCode;
    private int? stuckHandCount;
    private ActionFlags? stuckLegal;
    private DateTime stuckSince;
    private bool stuckEmitted;

    private void CheckStuckStateAndEmit(StateSnapshot? snap)
    {
        if (snap is null)
            return;

        var legal = snap.Legal.Flags;
        if (stuckStateCode != snap.AddonStateCode
            || stuckHandCount != snap.Hand.Count
            || stuckLegal != legal)
        {
            stuckStateCode = snap.AddonStateCode;
            stuckHandCount = snap.Hand.Count;
            stuckLegal = legal;
            stuckSince = DateTime.UtcNow;
            stuckEmitted = false;
            return;
        }

        if (stuckEmitted)
            return;

        var elapsed = DateTime.UtcNow - stuckSince;
        if (elapsed < StuckStateThreshold)
            return;

        int[]? rawSlots = plugin.AddonReader.DumpHandArrayRaw();
        string handDump = rawSlots is null ? "(no raw)" : FormatHandArrayDump(rawSlots);
        int? activeTextureBase = plugin.AddonReader.ActiveLayout?.TileTextureBase;

        plugin.FindingsLog?.Record("stuck_state", new Dictionary<string, object?>
        {
            ["state"] = snap.AddonStateCode,
            ["hand"] = snap.Hand.Count,
            ["melds"] = snap.OurMelds.Count,
            ["legal"] = legal.ToString(),
            ["elapsed_ms"] = (int)elapsed.TotalMilliseconds,
            ["last_dispatch_path"] = plugin.Dispatcher.LastDiscardPath,
            ["last_action"] = LastActionDescription,
            ["hand_raw"] = rawSlots,
            ["tile_texture_base"] = activeTextureBase,
        });
        log.Warning(
            $"[AutoPlayLoop] STUCK at state={snap.AddonStateCode} hand={snap.Hand.Count} " +
            $"melds={snap.OurMelds.Count} legal={legal} for {(int)elapsed.TotalSeconds}s. " +
            $"Last dispatch: {LastActionDescription} (path={plugin.Dispatcher.LastDiscardPath}). " +
            $"Manual click required.");
        log.Warning($"[AutoPlayLoop] STUCK hand-array dump: {handDump}");
        stuckEmitted = true;
    }

    private string FormatHandArrayDump(int[] slots)
    {
        int textureBase = plugin.AddonReader.ActiveLayout?.TileTextureBase ?? 0;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < slots.Length; i++)
        {
            int raw = slots[i];
            string decoded;
            if (raw == 0)
                decoded = "  -";
            else
            {
                int idx = raw - textureBase;
                decoded = idx switch
                {
                    >= 0 and < 34 => Tile.FromId(idx).ToString(),
                    34 => "5m*",
                    35 => "5p*",
                    36 => "5s*",
                    _ => "??",
                };
            }
            sb.Append($"[{i:00}]={raw,5}({decoded,3})");
            if (i == 6)
                sb.Append(" | ");
            else if (i < slots.Length - 1)
                sb.Append(' ');
        }
        return sb.ToString();
    }

    private void CheckPendingDispatchOutcome(StateSnapshot? snap)
    {
        if (pendingOutcome is not { } pending)
            return;

        bool stateChanged = snap is not null &&
            (snap.AddonStateCode != pending.StateAtDispatch
             || snap.Hand.Count != pending.HandAtDispatch
             || snap.OurMelds.Count != pending.MeldsAtDispatch);
        // Passing a call does not change our hand or melds, and the addon can keep
        // state 15 while the menu closes. LastCallMenu comes from the snapshot read
        // immediately above this check; absent/stale/visible menu evidence is not enough.
        bool passPromptClosed = IsDismissedPassPrompt(pending.Label, pending.LegalAtDispatch,
            snap, plugin.AddonReader.LastCallMenu);
        bool skippedDrawTransition = ObservedLaterDraw(pending.Label, pending.ClosedHandAtDispatch,
            pending.WallAtDispatch, snap);
        bool windowExpired = DateTime.UtcNow - pending.DispatchedAt > DispatchOutcomeWindow;

        if (stateChanged || passPromptClosed || skippedDrawTransition)
        {
#if MAHJONG_CN
            plugin.RecordReview("review_action_observation", new { SubmissionId=pending.ReviewSubmission,
                pending.Label,StateTransitionObserved=true,PassPromptClosed=passPromptClosed,
                LaterDrawObserved=skippedDrawTransition,ActionConfirmed=false,
                ElapsedMs=(int)(DateTime.UtcNow-pending.DispatchedAt).TotalMilliseconds });
#endif
            plugin.FindingsLog?.Record("dispatch_outcome", new Dictionary<string, object?>
            {
                ["label"] = pending.Label,
                ["state_transition_observed"] = true,
                ["pass_prompt_closed"] = passPromptClosed,
                ["later_draw_observed"] = skippedDrawTransition,
                ["action_confirmed"] = false,
                ["path"] = pending.LastDispatchPath,
                ["state_at_dispatch"] = pending.StateAtDispatch,
                ["hand_at_dispatch"] = pending.HandAtDispatch,
                ["melds_at_dispatch"] = pending.MeldsAtDispatch,
                ["state_after"] = snap?.AddonStateCode,
                ["hand_after"] = snap?.Hand.Count,
                ["melds_after"] = snap?.OurMelds.Count,
                ["elapsed_ms"] = (int)(DateTime.UtcNow - pending.DispatchedAt).TotalMilliseconds,
            });
            pendingOutcome = null;
        }
        else if (windowExpired)
        {
#if MAHJONG_CN
            plugin.RecordReview("review_action_observation", new { SubmissionId=pending.ReviewSubmission,
                pending.Label,StateTransitionObserved=false,ActionConfirmed=false,TimedOut=true });
#endif
            plugin.FindingsLog?.Record("dispatch_outcome", new Dictionary<string, object?>
            {
                ["label"] = pending.Label,
                ["state_transition_observed"] = false,
                ["action_confirmed"] = false,
                ["path"] = pending.LastDispatchPath,
                ["state_at_dispatch"] = pending.StateAtDispatch,
                ["hand_at_dispatch"] = pending.HandAtDispatch,
                ["melds_at_dispatch"] = pending.MeldsAtDispatch,
                ["state_after"] = snap?.AddonStateCode,
                ["hand_after"] = snap?.Hand.Count,
                ["melds_after"] = snap?.OurMelds.Count,
                ["elapsed_ms"] = (int)(DateTime.UtcNow - pending.DispatchedAt).TotalMilliseconds,
            });
            pendingOutcome = null;
            StopForError($"AUTO_OUTCOME_TIMEOUT: {pending.Label} 已发送，但未观察到牌局变化；不会盲目重复发送。");
        }
    }

    /// <summary>
    /// A slow frame can miss the intermediate discard/kan animation and return to
    /// the same state/count. Require BOTH a smaller wall and changed tile inventory,
    /// not sorting, a closed menu, or an optimistic local MeldTracker update alone.
    /// This observes progress, never confirms which input caused it.
    /// </summary>
    internal static bool ObservedLaterDraw(string label, IReadOnlyList<Tile> before, int wallBefore,
        StateSnapshot? after) => label is "discard" or "riichi-tsumogiri" or "ankan" or "minkan" or "shouminkan" &&
        after is not null && before.Count > 0 && before.Count == after.Hand.Count &&
        wallBefore > after.WallRemaining && after.WallRemaining >= 0 &&
        !before.OrderBy(t => t.Id).SequenceEqual(after.Hand.OrderBy(t => t.Id));

    /// <summary>
    /// Narrow completion evidence for Pass only. A closed prompt is observed progress,
    /// not proof that our submitted native input caused it. Other actions retain their
    /// state/count checks and a still-open or unreadable prompt retains its timeout.
    /// </summary>
    internal static bool IsDismissedPassPrompt(string label, ActionFlags legalAtDispatch,
        StateSnapshot? current, CallMenuObservation? currentMenu) =>
        label == "pass"
        && (legalAtDispatch & CallPromptFlags) != 0
        && current is not null
        && (current.Legal.Flags & CallPromptFlags) == 0
        && currentMenu is { CallModalVisible: false }
        && currentMenu.StateCode == current.AddonStateCode;

    private bool IsAutomationArmed()
    {
        var cfg = plugin.Configuration;
        return !disposed && cfg.TosAccepted && cfg.AutomationArmed && !cfg.SuggestionOnly && RuntimeCanOperate;
    }

#if MAHJONG_CN
    internal bool TryWaitForKyushu(DateTime now)
    {
        if (disposed || !IsAutomationArmed() || kyushuSubmittedAt is not { } submitted) return false;
        if (now - submitted >= TimeSpan.FromSeconds(30))
            StopForError("AUTO_KYUSHU_RESULT_TIMEOUT：已提交九种幺九倒牌，但30秒内未见结算；请检查确认窗口。");
        else
        {
            if (!kyushuConfirmed && now - submitted >= TimeSpan.FromSeconds(1))
                kyushuConfirmed = plugin.TryConfirmKyushu(kyushuOwner);
            LastActionDescription = "九种幺九倒牌已提交，等待流局结算；不会重复点击。";
        }
        return true;
    }
#endif

    private bool ContinueAfterStuckRecovery()
    {
        if (!fsm.IsDispatchInFlight)
            return true;
        if (fsm.TryRecoverFromStuckDispatch(DateTime.UtcNow))
        {
            StopForError("AUTO_QUEUE_TIMEOUT: 排队动作超时，已取消且不会重复发送。");
            return false;
        }
        return false;
    }

    private void HandleChiVariantSelect(DispatchContext context)
    {
        if (fsm.ShouldSuppressForContext(context, DateTime.UtcNow))
            return;
        ScheduleVariantAccept(context);
    }

    private bool TryHandleHandResult(int state)
    {
        if (state != HandResultStateCode)
        {
            // Keep the observed boundary until a new hand is readable. CN's recorded
            // Next transition is 29 -> 35 (empty hand) -> 27 (null snapshot).
            return false;
        }

        LastObservedState = state;
        LastObservedHandCount = -1;
#if MAHJONG_CN
        kyushuSubmittedAt = null;
        kyushuOwner = 0;
        kyushuConfirmed = false;
#endif

        var now = DateTime.UtcNow;
        if (handResultFirstSeenAt is null)
        {
            // An early win may advance the derived wall counter by <= 5 next hand.
            // The existing result-state boundary must clear old hand-scoped latches too.
            fsm.ClearRiichiConfirm();
            pendingAiChiVariant = null;
            riichiDiscardRed = null;
            riichiDiscardTsumogiri = null;
            fsm.ClearContext();
            pendingOutcome = null;
            handResultFirstSeenAt = now;
        }
        if (!plugin.Configuration.AutoAdvanceAfterHand) return true;

        if (handResultDispatchedThisInstance)
        {
            LastActionDescription = plugin.Configuration.KeepAutomaticBetweenHands
                ? "已提交下一局，持续等待其他玩家确认；自动模式保持开启。"
                : "已提交下一局，等待结算窗口关闭。";
            if (!plugin.Configuration.KeepAutomaticBetweenHands && handResultSubmittedAt is { } submittedAt && now - submittedAt > DispatchTimeout)
                StopForError("AUTO_RESULT_ADVANCE_TIMEOUT: 下一局操作已发送但结果窗口未关闭，已停止。");
            return true;
        }
        if (now - handResultFirstSeenAt < HandResultStabilityWindow)
            return true;

        var context = new DispatchContext(state, -1);
        if (fsm.ShouldSuppressForContext(context, now))
            return true;

        EmitProgressing();
        handResultDispatchedThisInstance = true;
        ScheduleHandResultAdvance(context);
        return true;
    }

    /// <summary>Only an observed result surface permits an input-free next-hand wait.</summary>
    internal bool TryWaitForNextHand(int state, StateSnapshot? snapshot, DateTime now)
    {
        if (handResultFirstSeenAt is null || state < 0) return false;
        if (nextHandWaitStartedAt is null)
        {
            nextHandWaitStartedAt = now;
            CancelPendingActions();
            fsm.CompleteDispatch();
            fsm.ClearContext();
            fsm.ClearRiichiConfirm();
            pendingOutcome = null;
            (plugin.Policy as Mahjong.Policy.Abstractions.IRefreshablePolicy)?.Invalidate();
        }
        if (state is not (27 or 35) && snapshot is not null && snapshot.AddonStateCode == state &&
            snapshot.Hand.Count > 0 && snapshot.Hand.Count + 3 * snapshot.OurMelds.Count is 13 or 14)
        {
            handResultFirstSeenAt = null;
            handResultDispatchedThisInstance = false;
            handResultSubmittedAt = null;
            nextHandWaitStartedAt = null;
            policyRetryNotBefore = default;
            LastActionDescription = "新一局手牌已读取，继续自动打牌。";
            return false;
        }
        // Only the recorded result-transition states can wait indefinitely. An
        // unrelated missing/contradictory hand must still alert and stop.
        bool keepWaiting = plugin.Configuration.KeepAutomaticBetweenHands && state is 27 or 35;
        if (!keepWaiting && now - nextHandWaitStartedAt >= NextHandWaitTimeout)
        {
            StopForError("AUTO_NEXT_HAND_TIMEOUT: 结算后30秒仍未读到新的完整手牌，已停止；可重新选择模式。");
            return true;
        }
        LastActionDescription = $"结算后等待新一局手牌（界面状态 {state}），自动模式保持开启。";
        return true;
    }

    /// <summary>Post-declaration Riichi: complete via tsumogiri instead of re-clicking the list (the list click no-ops at this point).</summary>
    internal bool CanContinuePendingRiichi(string label, StateSnapshot snap) =>
        label is "riichi" or "riichi-confirm" && fsm.NeedsRiichiDiscard &&
        (fsm.ShouldScheduleRiichiDiscard(snap.Legal.Flags, snap.Hand.Count) ||
         (snap.Legal.Flags & (ActionFlags.Ron | ActionFlags.Tsumo | ActionFlags.AnKan |
             ActionFlags.MinKan | ActionFlags.ShouMinKan)) != 0);

    private bool TryHandleRiichiConfirmTsumogiri(StateSnapshot snap, DispatchContext context, bool isCallPrompt)
    {
        if (!fsm.IsRiichiConfirmPending)
            return false;
        // CN can close the Riichi/Pass menu and retain state 6 with Discard only.
        // Complete the already submitted declaration using its latched AI tile.
        // A later policy result must not replace that tile or click Riichi again.
        bool declarationMenu = isCallPrompt && snap.Legal.Can(ActionFlags.Riichi);
        bool selectedDiscardWindow = fsm.NeedsRiichiDiscard && fsm.RiichiConfirmTile is not null &&
            snap.Legal.Can(ActionFlags.Discard);
        if (!declarationMenu && !selectedDiscardWindow)
            return false;
        // A surviving declaration/preview flag must never outrank an actual win or kan.
        if ((snap.Legal.Flags & (ActionFlags.Ron | ActionFlags.Tsumo | ActionFlags.AnKan |
                ActionFlags.MinKan | ActionFlags.ShouMinKan)) != 0) return false;
        if (context.Hand <= 0 || context.Hand % 3 != 2)
            return false;
        if (fsm.ShouldScheduleRiichiDiscard(snap.Legal.Flags, context.Hand)) ScheduleRiichiTsumogiri(context);
        else LastActionDescription = "立直弃牌已提交；等待游戏摸切或新的和牌/杠牌窗口。";
        return true;
    }

    private unsafe bool TryReadQueuedContext(DispatchContext expected, out nint address, out string identity)
    {
        address = 0;
        identity = string.Empty;
        if (!addon.TryGet(out var unit, out _) || !unit->IsVisible) return false;
        address = (nint)unit;
        int state = ReadStateCode();
        if (state < 0) return false;
        if (expected.State == HandResultStateCode && expected.Hand == -1)
        {
            identity = state.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        var snapshot = plugin.AddonReader.TryBuildSnapshot();
        if (snapshot is null || snapshot.AddonStateCode != state) return false;
        // State+count alone can repeat on another turn. Compare the complete immutable
        // public snapshot, including tile order, legal offers, melds and discard counts.
        identity = JsonSerializer.Serialize(snapshot);
        return true;
    }

    private void ScheduleAction(string label, DispatchContext context, int medianDelayMs, Action body)
    {
        reviewDecision=null;
        if (!IsAutomationArmed()) return;
        if (!TryReadQueuedContext(context, out var expectedAddress, out var expectedIdentity))
        {
            StopForError($"AUTO_QUEUE_READ_FAILED: {label}: 无法建立排队动作的当前状态。");
            return;
        }
        long generation = queuedActions.Capture();
        fsm.BeginDispatch(DateTime.UtcNow, context);
        var delay = HumanTiming.RandomDelay(medianMs: medianDelayMs);
        _ = framework.RunOnTick(() =>
        {
            try
            {
                bool ran = queuedActions.TryExecute(generation, () =>
                {
                    if (disposed || !IsAutomationArmed()) return false;
                    if (!TryReadQueuedContext(context, out var address, out var identity))
                    {
                        StopForError($"AUTO_QUEUE_READ_FAILED: {label}: 延迟期间界面退出或读取失败。");
                        return false;
                    }
                    if (address != expectedAddress || identity != expectedIdentity)
                    {
#if MAHJONG_CN
                        plugin.RecordReview("review_window_cancelled",new { WindowId=Guid.NewGuid(),Label=label,
                            Reason="PUBLIC_STATE_CHANGED_DURING_DELAY",RequiredActionMissed=(bool?)null });
#endif
                        LastActionDescription = $"{label} canceled: public state changed during delay";
                        fsm.ClearContext();
                        return false;
                    }
                    return true;
                }, body);
                if (!ran && queuedActions.IsCurrent(generation) && !IsAutomationArmed()) Stop();
            }
            catch (Exception ex)
            {
                if (disposed || !queuedActions.IsCurrent(generation)) return;
                log.Error($"AutoPlayLoop {label} error: {ex}");
                StopForError($"AUTO_DISPATCH_EXCEPTION: {label}: {ex.GetType().Name}");
            }
            finally
            {
                // A stale task must never clear the in-flight flag of a newer generation.
                if (queuedActions.IsCurrent(generation)) fsm.CompleteDispatch();
            }
        }, delay);
    }

    private void ScheduleHandResultAdvance(DispatchContext context)
    {
        ScheduleAction("hand-result-next", context, HandResultAdvanceDelayMs, () =>
        {
            if (!plugin.Configuration.AutoAdvanceAfterHand)
            {
                handResultDispatchedThisInstance = false;
                return;
            }
            int currentState = ReadStateCode();
            if (currentState != HandResultStateCode)
            {
                LastActionDescription = $"hand-result-next aborted: state moved {HandResultStateCode}→{currentState}";
                return;
            }

            var snap = plugin.AddonReader.TryBuildSnapshot();
            var result = plugin.Dispatcher.DispatchHandResultNext();
            if (result == InputDispatcher.DispatchResult.Submitted)
                handResultSubmittedAt = DateTime.UtcNow;
            LastActionDescription = $"auto-hand-result-next → {InputDispatcher.DescribeResult(result)}";
            log.Info($"[AutoPlayLoop] hand-result-next dispatch: {LastActionDescription}");
            EmitDispatchFinding("hand-result-next", result, state: currentState, snap: snap);
            ClearRetryDebounceIfHookFailed(result);
        });
    }

    private void ScheduleVariantAccept(DispatchContext context)
    {
        ScheduleAction("variant", context, VariantAcceptDelayMs, () =>
        {
            // Modal can close during the humanized delay — re-check at dispatch time.
            int currentState = ReadStateCode();
            if (currentState != ChiVariantSelectStateCode)
            {
                LastActionDescription = $"variant aborted: state moved {ChiVariantSelectStateCode}→{currentState}";
                return;
            }

            int bestIdx = 0;
            string scoreNote = "default(opt=0)";
            var variants = TryReadChiVariants();
            var snap = plugin.AddonReader.TryBuildSnapshot();
            if (variants is null || snap is null)
            {
                StopForError("AUTO_CHI_VARIANTS_UNAVAILABLE: 吃牌候选读取失败，未发送默认选项。");
                return;
            }
            if (pendingAiChiVariant is { } selected)
            {
                bestIdx = MatchSelectedChiVariantIndex(variants, selected);
                scoreNote = "AI selected chi combination";
            }
            else if (plugin.Policy is IIndependentPublicStatePolicy)
            {
                StopForError("AKOCHAN_BLOCKED: CHI_VARIANT_SELECTION_MISSING");
                return;
            }
            else bestIdx = PickBestChiVariantIndex(variants, snap, out scoreNote);
            if (bestIdx < 0)
            {
                StopForError("AUTO_CHI_VARIANTS_INCONSISTENT: 没有与手牌一致的吃牌组合。");
                return;
            }

            var result = plugin.Dispatcher.DispatchChiVariant(bestIdx);
            LastActionDescription = $"auto-variant[opt={bestIdx}] → {InputDispatcher.DescribeResult(result)} ({scoreNote})";
            log.Info($"[AutoPlayLoop] variant dispatch: {LastActionDescription}");
            EmitDispatchFinding("chi-variant", result, option: bestIdx, state: currentState, snap: snap);
            if (result == InputDispatcher.DispatchResult.Submitted) pendingAiChiVariant = null;
            ClearRetryDebounceIfHookFailed(result);
        });
    }

    /// <summary>Do not run a different evaluator after AI has already chosen the chi shape.</summary>
    internal static int MatchSelectedChiVariantIndex(IReadOnlyList<int[]> variants, MeldCandidate selected)
    {
        if (selected.Kind != MeldKind.Chi || selected.HandTiles is not { Length: 2 }) return -1;
        var expected = selected.HandTiles.Select(t => (int)t.Id).Append(selected.ClaimedTile.Id).Order().ToArray();
        int match = -1;
        for (int i = 0; i < variants.Count; i++)
        {
            if (!variants[i].Order().SequenceEqual(expected)) continue;
            if (match >= 0) return -1; // Duplicate kind-only variants may hide a red identity distinction.
            match = i;
        }
        return match;
    }

    /// <summary>Reads the chi-variant tile triples from AtkValues at the chi-variant-select popup. Capture 2026-05-25: atk[3]=variant_count, then 4 ints per variant (3 tile-IDs + 1 sentinel = textureBase).</summary>
    private unsafe IReadOnlyList<int[]>? TryReadChiVariants()
    {
        if (!addon.TryGet(out var unit, out _))
            return null;
        if (!unit->IsVisible || unit->AtkValues == null)
            return null;

        int atkCount = unit->AtkValuesCount;
        if (atkCount < 4)
            return null;

        var atk = unit->AtkValues;
        if (atk[3].Type != AtkValueType.Int)
            return null;
        int variantCount = atk[3].Int;
        if (variantCount is < 1 or > 8)
            return null;

        int textureBase = plugin.AddonReader.ActiveLayout?.TileTextureBase ?? 0;
        if (textureBase == 0)
            return null;

        int needed = 4 + variantCount * 4;
        if (atkCount < needed)
            return null;

        var variants = new List<int[]>(variantCount);
        for (int i = 0; i < variantCount; i++)
        {
            int baseIdx = 4 + i * 4;
            var tileIds = new int[3];
            for (int j = 0; j < 3; j++)
            {
                if (atk[baseIdx + j].Type != AtkValueType.Int)
                    return null;
                int id = HandArrayDecoder.DecodeTileId(atk[baseIdx + j].Int, textureBase, out _);
                if (id < 0)
                    return null;
                tileIds[j] = id;
            }
            variants.Add(tileIds);
        }
        return variants;
    }

    /// <summary>Picks the chi variant whose post-call closed hand has the lowest shanten. Tries all 3 (claim, hand-pair) splits per variant since the claimed tile isn't explicitly marked in AtkValues. Ties resolve to the lower variant index.</summary>
    private static int PickBestChiVariantIndex(IReadOnlyList<int[]> variants, StateSnapshot snap, out string note)
    {
        var counts = new int[Mahjong.Core.Tile.Count34];
        foreach (var t in snap.Hand)
            counts[t.Id]++;
        int meldsAfter = snap.OurMelds.Count + 1;

        int bestIdx = 0;
        int bestShanten = int.MaxValue;
        for (int v = 0; v < variants.Count; v++)
        {
            var tiles = variants[v];
            int? variantShanten = null;
            for (int claimSlot = 0; claimSlot < 3; claimSlot++)
            {
                int h1 = tiles[(claimSlot + 1) % 3];
                int h2 = tiles[(claimSlot + 2) % 3];
                if (counts[h1] < 1) continue;
                counts[h1]--;
                if (counts[h2] < 1) { counts[h1]++; continue; }
                counts[h2]--;
                int sh = Mahjong.Engine.ShantenCalculator.Standard(counts, meldsAfter);
                counts[h1]++;
                counts[h2]++;
                if (variantShanten is null || sh < variantShanten)
                    variantShanten = sh;
            }
            if (variantShanten is null) continue;
            if (variantShanten < bestShanten)
            {
                bestShanten = variantShanten.Value;
                bestIdx = v;
            }
        }

        note = bestShanten == int.MaxValue
            ? $"no formable variant among {variants.Count}"
            : $"shanten={bestShanten} across {variants.Count} variants";
        return bestShanten == int.MaxValue ? -1 : bestIdx;
    }

    private void ScheduleDiscard(DispatchContext context)
    {
        ScheduleAction("discard", context, plugin.Configuration.HumanizedDelayMs, () =>
        {
            var snap = plugin.AddonReader.TryBuildSnapshot();
            int currentState = ReadStateCode();
            if (snap is null || !snap.Legal.Can(ActionFlags.Discard))
            {
                if (snap is null)
                {
                    StopForError("AUTO_SNAPSHOT_UNAVAILABLE: 弃牌前无法读取当前牌局。");
                    return;
                }
                LastActionDescription = $"discard aborted: not a discard state (state={currentState} hand={snap?.Hand.Count ?? -1} flags={snap?.Legal.Flags.ToString() ?? "null"})";
                log.Info($"[AutoPlayLoop] {LastActionDescription}");
                return;
            }

            var choice = ChooseCurrentAction(ref snap);
            if (DeferPendingPolicyChoice(choice, DateTime.UtcNow)) return;
            log.Info(
                $"[AutoPlayLoop] discard body: schedState={context.State} curState={currentState} " +
                $"hand={snap.Hand.Count} melds={snap.OurMelds.Count} flags={snap.Legal.Flags} " +
                $"choice={choice.Kind} tile={choice.DiscardTile}");
            EmitDecisionFinding("discard", snap, choice);
            DispatchPolicyChoice(snap, choice);
            if (disposed || !IsAutomationArmed()) return;
            log.Info(
                $"[AutoPlayLoop] discard body done: {LastActionDescription} " +
                $"path={plugin.Dispatcher.LastDiscardPath}");
        });
    }

    /// <summary>A refused dispatch stops automation; it is not a signal to retry immediately.</summary>
    private void ClearRetryDebounceIfHookFailed(InputDispatcher.DispatchResult result)
    {
        if (result != InputDispatcher.DispatchResult.Submitted)
            StopForError($"AUTO_DISPATCH_REJECTED: {InputDispatcher.DescribeResult(result)}");
    }

    private void ScheduleCallDecision(DispatchContext context)
    {
        ScheduleAction("call", context, CallDecisionDelayMs, () =>
        {
            var snap = plugin.AddonReader.TryBuildSnapshot();
            int currentState = ReadStateCode();
            if (snap is null)
            {
                StopForError($"AUTO_SNAPSHOT_UNAVAILABLE: call, state={currentState}");
                return;
            }
            var choice = ChooseCurrentAction(ref snap);
            if (DeferPendingPolicyChoice(choice, DateTime.UtcNow)) return;
            log.Info(
                $"[AutoPlayLoop] call body: schedState={context.State} curState={currentState} " +
                $"hand={snap.Hand.Count} melds={snap.OurMelds.Count} flags={snap.Legal.Flags} " +
                $"choice={choice.Kind} tile={choice.DiscardTile}");
            EmitDecisionFinding("call", snap, choice);
            DispatchCallChoice(snap, choice);
            // LastDiscardPath belongs to DispatchDiscard — do not print it on the call path; it would be stale.
            log.Info(
                $"[AutoPlayLoop] call body done: {LastActionDescription} " +
                $"pon={snap.Legal.PonCandidates.Count} chi={snap.Legal.ChiCandidates.Count} " +
                $"kan={snap.Legal.KanCandidates.Count}");
        });
    }

    private void ScheduleRiichiTsumogiri(DispatchContext context)
    {
        ScheduleAction("riichi-tsumogiri", context, RiichiTsumogiriDelayMs, () =>
        {
            var snap = plugin.AddonReader.TryBuildSnapshot();
#if MAHJONG_CN
            if (snap is not null && TryChooseVisibleWin(ref snap, out var win))
            {
                EmitDecisionFinding("riichi-win-guard", snap, win);
                DispatchCallChoice(snap, win);
                return;
            }
#endif
            if (!fsm.NeedsRiichiDiscard) return;
            if (snap is not null && !fsm.ShouldScheduleRiichiDiscard(snap.Legal.Flags, snap.Hand.Count)) return;
            if (snap is null || snap.Hand.Count < 14)
            {
                StopForError($"AUTO_RIICHI_HAND_INVALID: hand={snap?.Hand.Count ?? -1}");
                return;
            }

            // Latch carries the policy-chosen tile; fall back to slot 13 only when no tile was latched (post-confirm yaku-preview popup).
            int slot;
            Tile tile;
            if (fsm.RiichiConfirmTile is { } target)
            {
                slot = plugin.AddonReader.FindAddonSlotOfTile(target, riichiDiscardRed, riichiDiscardTsumogiri);
                if (slot < 0)
                {
                    StopForError("AUTO_RIICHI_TILE_MISSING: 立直选择的牌已不在当前手牌。");
                    return;
                }
                tile = target;
            }
            else
            {
                slot = 13;
                tile = snap.Hand[13];
            }

            var result = plugin.Dispatcher.DispatchDiscard(slot);
            if (result == InputDispatcher.DispatchResult.Submitted && IsAutomationArmed()) fsm.MarkRiichiDiscardSubmitted();
            LastActionDescription = $"auto-riichi-tsumogiri {tile} slot={slot} → {InputDispatcher.DescribeResult(result)}";
            log.Info($"[AutoPlayLoop] riichi-tsumogiri dispatch: {LastActionDescription}");
            EmitDispatchFinding("riichi-tsumogiri", result, tile: tile, slot: slot, snap: snap);
            ClearRetryDebounceIfHookFailed(result);
            // Keep declaration suppression until the round boundary, but consume this
            // discard separately. A later draw may not search for the old chosen tile.
        });
    }

    private void DispatchPolicyChoice(StateSnapshot snap, ActionChoice choice)
    {
        if (DeferPendingPolicyChoice(choice, DateTime.UtcNow)) return;

        if (choice.Kind is ActionKind.Tsumo or ActionKind.Ron ||
            IsGlobalAiChoice(choice) && choice.Kind is ActionKind.ShouMinKan or ActionKind.MinKan or ActionKind.Kyushukyuhai)
        {
            DispatchCallChoice(snap, choice);
            return;
        }

        if (choice.Kind == ActionKind.AnKan && choice.DiscardTile is { } kanTile)
        {
            DispatchAnkan(snap, choice, kanTile);
            return;
        }

        if (choice.Kind == ActionKind.Pass && IsHandOutOfSyncReason(choice.Reasoning)
            && snap.Legal.Can(ActionFlags.Discard))
        {
            StopForError($"AUTO_HAND_OUT_OF_SYNC: {choice.Reasoning}");
            return;
        }

        if (choice.Kind != ActionKind.Discard && choice.Kind != ActionKind.Riichi)
        {
            LastActionDescription = $"policy returned {choice.Kind} — not dispatching";
            return;
        }
        if (choice.DiscardTile is null)
        {
            StopForError($"AUTO_POLICY_TILE_MISSING: {choice.Kind}");
            return;
        }

        DispatchDiscardOrRiichi(snap, choice);
    }

    private ActionChoice ChooseCurrentAction(ref StateSnapshot snap)
    {
#if MAHJONG_CN
        if (TryChooseVisibleWin(ref snap, out var win)) return win;
#endif
        return plugin.Policy.Choose(snap);
    }

#if MAHJONG_CN
    private bool TryChooseVisibleWin(ref StateSnapshot snap, out ActionChoice choice)
    {
        choice = null!;
        if (!plugin.Dispatcher.TryGetAvailableWin(out var offered)) return false;
        // Execution protection, explicitly attributed rather than reported as a model decision.
        // Revalidated again by DispatchCallOption before any native input.
        var originalFlags = snap.Legal.Flags;
        string inputHash = Mahjong.Plugin.CN.Journaling.DecisionReviewPolicy.InputHash(snap);
        var flag = offered == ActionKind.Ron ? ActionFlags.Ron : ActionFlags.Tsumo;
        snap = snap with { Legal = snap.Legal with { Flags = snap.Legal.Flags | flag } };
        choice = new(offered, Reasoning: "AVAILABLE_WIN_GUARD: 当前游戏菜单提供和牌，执行层优先和牌；未调用求解器。");
        reviewDecision = Guid.NewGuid();
        plugin.RecordReview("review_decision", new
        {
            DecisionId = reviewDecision, Backend = "game-visible-win-guard", InputSha256 = inputHash,
            Choice = choice, BackendOverride = "AVAILABLE_WIN_GUARD: 当前可用和牌优先，求解器未调用",
            CandidateSource = "visible-enabled-game-menu", SolverCalled = false,
            OriginalSnapshotFlags = (int)originalFlags, VisibleWin = offered.ToString(), ActionConfirmed = false,
        });
        return true;
    }
#endif

    /// <summary>
    /// An asynchronous policy's pending result is not a request to press Pass. No input was
    /// submitted, so release only its unused context debounce. A later framework tick must
    /// rebuild and check the snapshot, runtime gates, delayed queue, and policy decision.
    /// The policy completion never schedules input or supplies a cached action to this loop.
    /// </summary>
    internal bool DeferPendingPolicyChoice(ActionChoice choice, DateTime now)
    {
        if (choice.Kind == ActionKind.Pass && choice.Reasoning?.StartsWith("AKOCHAN_BLOCKED:", StringComparison.Ordinal) == true)
        {
            StopForError(choice.Reasoning);
            return true;
        }
        if (choice.Kind != ActionKind.Pass ||
            choice.Reasoning?.StartsWith("AKOCHAN_PENDING:", StringComparison.Ordinal) != true)
            return false;

        LastActionDescription = choice.Reasoning;
        fsm.ClearContext();
        policyRetryNotBefore = now + PolicyPendingPollInterval;
        return true;
    }

    internal bool IsPolicyRefreshDeferred(DateTime now) => now < policyRetryNotBefore;

    /// <summary>
    /// Matches <c>EfficiencyPolicy.TsumogiriFallback</c>'s pause reason. When the meld tracker
    /// has fallen behind (typically post-ShouMinKan or a missed pon/chi inference race), the
    /// policy refuses to score a hand whose closed+meld arithmetic ≠ 14. Without a recovery
    /// path the autoplay loop debounces forever on the same Pass and the bot softlocks.
    /// </summary>
    private static bool IsHandOutOfSyncReason(string? reasoning) =>
        reasoning is not null
        && reasoning.StartsWith("hand state out of sync", StringComparison.Ordinal);

    private void DispatchAnkan(StateSnapshot snap, ActionChoice choice, Tile kanTile)
    {
        // Route AnKan through opcode 11 (call-prompt button-row) — the speculative opcode-12 path was a no-op in the addon.
        int acceptIndex = ComputeAcceptIndex(ActionKind.AnKan, RefineAiKanLegal(snap.Legal, choice), null);
        var result = plugin.Dispatcher.DispatchCallOption(acceptIndex, ActionKind.AnKan);
        LastActionDescription = $"auto-ankan {kanTile} opt={acceptIndex} → {InputDispatcher.DescribeResult(result)}";
        EmitDispatchFinding("ankan", result, option: acceptIndex, tile: kanTile, snap: snap, callInput: true);
        ClearRetryDebounceIfHookFailed(result);

        // Experimental upstream inference after submission, NOT a game acknowledgement.
        // Self-declared kans produce no opp-discard signal; MeldTracker.ObserveSnapshot cannot infer them, so record here to preserve the 14-tile invariant.
        if (result == InputDispatcher.DispatchResult.Submitted && IsAutomationArmed())
            plugin.MeldTracker.Record(Meld.AnKan(kanTile));
    }

    private void DispatchDiscardOrRiichi(StateSnapshot snap, ActionChoice choice)
    {
        var tile = choice.DiscardTile!.Value;
        int slot = plugin.AddonReader.FindAddonSlotOfTile(tile, choice.DiscardRed, choice.DiscardTsumogiri);
        if (slot < 0)
        {
            StopForError("AUTO_POLICY_TILE_NOT_IN_HAND: 决策牌不存在于当前手牌槽。");
            return;
        }

        // Riichi at state-6: click via opcode-11 and latch the policy's chosen tile so the next-tick tsumogiri commits the ukeire-max tile, not slot 13.
        if (choice.Kind == ActionKind.Riichi && snap.Legal.Can(ActionFlags.Riichi))
        {
            int riichiIdx = ComputeAcceptIndex(ActionKind.Riichi, snap.Legal, null);
            var rResult = plugin.Dispatcher.DispatchCallOption(riichiIdx, ActionKind.Riichi);
            LastActionDescription = $"auto-riichi[opt={riichiIdx}] (tile={tile}) → {InputDispatcher.DescribeResult(rResult)}";
            EmitDispatchFinding("riichi", rResult, option: riichiIdx, tile: tile, snap: snap, callInput: true);
            if (rResult == InputDispatcher.DispatchResult.Submitted && IsAutomationArmed())
            {
                fsm.LatchRiichiConfirm(tile);
                riichiDiscardRed = choice.DiscardRed;
                riichiDiscardTsumogiri = choice.DiscardTsumogiri;
            }
            ClearRetryDebounceIfHookFailed(rResult);
            return;
        }

        var result = plugin.Dispatcher.DispatchDiscard(slot);
        LastActionDescription = $"auto-discard {tile} slot={slot} → {InputDispatcher.DescribeResult(result)}";
        EmitDispatchFinding("discard", result, tile: tile, slot: slot, snap: snap);
        ClearRetryDebounceIfHookFailed(result);
    }

    private void DispatchCallChoice(StateSnapshot snap, ActionChoice choice)
    {
        if (DeferPendingPolicyChoice(choice, DateTime.UtcNow)) return;

        if (IsHandOutOfSyncReason(choice.Reasoning))
        {
            StopForError($"AUTO_HAND_OUT_OF_SYNC: {choice.Reasoning}");
            return;
        }
        var legal = RefineAiKanLegal(snap.Legal, choice);

        // State-6 popup is dual-use: it offers Riichi/Tsumo/AnKan and lists discardable tiles — route Discard/Riichi through the list-widget path, not Pass.
        if (choice.Kind is ActionKind.Discard or ActionKind.Riichi
            && choice.DiscardTile.HasValue)
        {
            DispatchPolicyChoice(snap, choice);
            log.Info($"[AutoPlayLoop] discard-from-call-popup dispatch: {LastActionDescription}");
            return;
        }

        bool acceptRiichiPopup = ResolveRiichiPopupAcceptance(snap, choice, out var riichiProbeTile, out var riichiReason, out bool policyPending);
        if (policyPending) return;

        bool shouldAccept = acceptRiichiPopup || choice.Kind is
            ActionKind.Ron or ActionKind.Tsumo or
            ActionKind.Pon or ActionKind.Chi or
            ActionKind.AnKan or ActionKind.MinKan or ActionKind.ShouMinKan or ActionKind.Kyushukyuhai;

        if (shouldAccept)
            DispatchAccept(snap, choice, legal, acceptRiichiPopup, riichiProbeTile, riichiReason!);
        else
            DispatchPass(snap, choice, legal, riichiReason);

        log.Info($"[AutoPlayLoop] call-prompt dispatch: {LastActionDescription}");
    }

    /// <summary>For an initial Riichi popup, re-run policy against a synthetic Discard|Riichi snapshot so RiichiPolicy actually fires — the standard call branch skips Riichi.</summary>
    private bool ResolveRiichiPopupAcceptance(StateSnapshot snap, ActionChoice choice, out Tile? probeTile, out string? probeReason, out bool policyPending)
    {
        probeReason = null;
        probeTile = null;
        policyPending = false;
        if (choice.Kind != ActionKind.Pass || !snap.Legal.Can(ActionFlags.Riichi))
            return false;

        // A native none decision already evaluated reach; never replace it with the
        // old strategy's fabricated Discard|Riichi probe or accept it as confirmation.
        if (IsGlobalAiChoice(choice)) return false;

        if (fsm.IsRiichiConfirmPending || snap.Hand.Count == 0 || snap.Hand.Count % 3 != 2)
        {
            probeReason = "riichi-confirm";
            return true;
        }

        var probe = snap with
        {
            Legal = snap.Legal with
            {
                Flags = ActionFlags.Discard | ActionFlags.Riichi,
            },
        };
        var verdict = plugin.Policy.Choose(probe);
        if (DeferPendingPolicyChoice(verdict, DateTime.UtcNow))
        {
            policyPending = true;
            return false;
        }
        if (verdict.Kind != ActionKind.Riichi)
        {
            probeReason = string.IsNullOrEmpty(verdict.Reasoning)
                ? "riichi declined by policy"
                : $"riichi declined: {verdict.Reasoning}";
            return false;
        }

        probeTile = verdict.DiscardTile;
        probeReason = string.IsNullOrEmpty(verdict.Reasoning) ? "riichi-accept" : verdict.Reasoning;
        return true;
    }

    private void DispatchAccept(StateSnapshot snap, ActionChoice choice, LegalActions legal, bool acceptRiichiPopup, Tile? riichiProbeTile, string riichiReason)
    {
        // Every accept flows through opcode 11 / SelectItem (DispatchCallOption auto-routes by popup shape). The dedicated Tsumo opcode-9 path no-opped at state-6 SelfDeclareList because that popup is a list widget — the corpus capture of opcode 9 was the addon's internal callback fired *after* SelectItem ran, not a click-equivalent payload.
        int acceptIndex = acceptRiichiPopup
            ? ComputeAcceptIndex(ActionKind.Riichi, legal, choice.Call)
            : ComputeAcceptIndex(choice.Kind, legal, choice.Call);
        var result2 = plugin.Dispatcher.DispatchCallOption(acceptIndex, acceptRiichiPopup ? ActionKind.Riichi : choice.Kind);
        string label = acceptRiichiPopup ? "riichi-confirm" : choice.Kind.ToString().ToLowerInvariant();
        LastActionDescription = $"auto-{label}[opt={acceptIndex}] → {InputDispatcher.DescribeResult(result2)}";
        EmitDispatchFinding(label, result2, option: acceptIndex, snap: snap, callInput: true);
        ClearRetryDebounceIfHookFailed(result2);
#if MAHJONG_CN
        if (choice.Kind == ActionKind.Kyushukyuhai && result2 == InputDispatcher.DispatchResult.Submitted && IsAutomationArmed())
        {
            kyushuSubmittedAt = DateTime.UtcNow;
            kyushuConfirmed = false;
            kyushuOwner = CurrentAddonAddress();
            pendingOutcome = null; // Dedicated, bounded wait covers an optional confirmation then state29.
        }
#endif

        // Yaku-preview confirm popup shares the Riichi-flag signature — latch to prevent retry-dispatch and carry the probe's chosen discard.
        if (acceptRiichiPopup && result2 == InputDispatcher.DispatchResult.Submitted && IsAutomationArmed())
            fsm.LatchRiichiConfirm(riichiProbeTile);

        if (result2 == InputDispatcher.DispatchResult.Submitted && IsAutomationArmed() &&
            choice.Kind == ActionKind.Chi && IsGlobalAiChoice(choice)) pendingAiChiVariant = choice.Call;

        // Experimental upstream inference after submission, NOT a game acknowledgement.
        // ShouMinKan: addon shrinks the closed hand by 1 and the existing pon ought to grow to a kan,
        // but ObserveSnapshot can't infer that from a delta=1. Upgrade the meld in-place so meld-tile
        // arithmetic stays at 14 and the policy doesn't fall into out-of-sync Pass.
        if (result2 == InputDispatcher.DispatchResult.Submitted && IsAutomationArmed()
            && choice.Kind == ActionKind.ShouMinKan
            && choice.Call is { } shouCand)
        {
            plugin.MeldTracker.UpgradeToShouMinKan(shouCand.ClaimedTile);
        }
    }

    private void DispatchPass(StateSnapshot snap, ActionChoice choice, LegalActions legal, string? reasonOverride = null)
    {
        // Pass index = count of accept buttons (multi-chi adds one slot per chi candidate).
        int passIndex = ComputePassIndex(legal);
        var result = plugin.Dispatcher.DispatchCallOption(passIndex, ActionKind.Pass);
        LastActionDescription = $"auto-pass[opt={passIndex}] → {InputDispatcher.DescribeResult(result)}";
        EmitDispatchFinding("pass", result, option: passIndex, snap: snap, callInput: true);
    }

    private static bool IsGlobalAiChoice(ActionChoice choice) =>
        choice.Reasoning?.StartsWith("AKOCHAN_GLOBAL:", StringComparison.Ordinal) == true;

#if MAHJONG_CN
    private unsafe nint CurrentAddonAddress() => addon.TryGet(out var unit, out _) ? (nint)unit : 0;
#endif

    private static LegalActions RefineAiKanLegal(LegalActions legal, ActionChoice choice)
    {
        if (!IsGlobalAiChoice(choice) || choice.Kind is not (ActionKind.AnKan or ActionKind.ShouMinKan) ||
            !legal.Can(ActionFlags.MinKan)) return legal;
        var flag = choice.Kind == ActionKind.AnKan ? ActionFlags.AnKan : ActionFlags.ShouMinKan;
        return legal with { Flags = (legal.Flags & ~ActionFlags.MinKan) | flag };
    }

    /// <summary>Call-row button order Pon, Chi, AnKan, MinKan, ShouMinKan, Ron, Riichi, Tsumo, Pass; Chi is one slot regardless of ChiCandidates.Count (variant picked in state-25 sub-popup).</summary>
    internal static int ComputeAcceptIndex(ActionKind kind, LegalActions legal, MeldCandidate? chosenCall)
    {
        int idx = 0;

        if (kind == ActionKind.Pon)
            return idx;
        if (legal.Can(ActionFlags.Pon))
            idx++;

        if (kind == ActionKind.Chi)
            return idx;
        if (legal.Can(ActionFlags.Chi))
            idx++;

        if (kind == ActionKind.AnKan)
            return idx;
        if (legal.Can(ActionFlags.AnKan))
            idx++;

        if (kind == ActionKind.MinKan)
            return idx;
        if (legal.Can(ActionFlags.MinKan))
            idx++;

        if (kind == ActionKind.ShouMinKan)
            return idx;
        if (legal.Can(ActionFlags.ShouMinKan))
            idx++;

        if (kind == ActionKind.Ron)
            return idx;
        if (legal.Can(ActionFlags.Ron))
            idx++;

        if (kind == ActionKind.Riichi)
            return idx;
        if (legal.Can(ActionFlags.Riichi))
            idx++;

        if (kind == ActionKind.Tsumo)
            return idx;

        return 0;
    }

    /// <summary>Index of the Pass button on a call-prompt row: one slot per offered accept action (see <see cref="ComputeAcceptIndex"/>), Pass closes the row.</summary>
    internal static int ComputePassIndex(LegalActions legal)
    {
        int idx = 0;
        if (legal.Can(ActionFlags.Pon))
            idx++;
        if (legal.Can(ActionFlags.Chi))
            idx++;
        if (legal.Can(ActionFlags.AnKan))
            idx++;
        if (legal.Can(ActionFlags.MinKan))
            idx++;
        if (legal.Can(ActionFlags.ShouMinKan))
            idx++;
        if (legal.Can(ActionFlags.Ron))
            idx++;
        if (legal.Can(ActionFlags.Riichi))
            idx++;
        if (legal.Can(ActionFlags.Tsumo))
            idx++;
        return idx;
    }

    private unsafe int ReadStateCode()
    {
        if (!addon.TryGet(out var unit, out _))
            return -1;
        if (!unit->IsVisible || unit->AtkValues == null || unit->AtkValuesCount == 0)
            return -1;
        var v = unit->AtkValues[0];
        return v.Type == FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int ? v.Int : -1;
    }
}
