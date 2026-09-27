"""Doman match metadata and the pinned v4 encoder's match-progress channel.

Only remaining scheduled hands are mapped onto the trained eight-hand progress scale.
Actual bakaze, jikaze and kyoku stay untouched. This is an input adaptation, not a
newly trained reward function, and does not encode every Doman termination rule.
"""
FEATURE_SCHEMA = 2
PROGRESS_ROW_V4 = 27  # pinned obs_repr.rs: 7 hand + 8 scores + 4 rank + 4 kyoku + 2 counters + 2 winds


def match_context(snapshot):
    first = snapshot.get("MatchFirstRound", 0)
    if type(first) is not int or first not in (0, 4):
        raise ValueError("MORTAL_MATCH_FIRST_ROUND")
    rules = snapshot.get("MatchRules")
    known = False
    if rules is not None:
        if rules.get("SchemaVersion") != 1 or rules.get("ProfileId") != "doman-public-v1":
            raise ValueError("MORTAL_RULE_PROFILE")
        kind, hands = rules.get("MatchType"), rules.get("ScheduledHands")
        if kind is not None:
            if kind not in ("east", "hanchan") or hands != (4 if kind == "east" else 8):
                raise ValueError("MORTAL_MATCH_LENGTH")
            if first != 8 - hands:
                raise ValueError("MORTAL_MATCH_RULE_CONFLICT")
            known = True
        elif hands is not None:
            raise ValueError("MORTAL_MATCH_LENGTH")
    else:
        hands = 8 - first  # Compatibility only; provenance stays unconfirmed.
        kind = "east" if first else "hanchan"
    ordinal = snapshot["RoundWind"] * 4 + snapshot["HandNumber"] - 1
    progress = min(7, max(0, first + ordinal)) / 7
    return {"match_type": kind, "scheduled_hands": hands, "confirmed_length": known,
            "progress": progress, "progress_row": PROGRESS_ROW_V4,
            "scheduled_all_last": ordinal + first >= 7,
            "actual_round_wind": snapshot["RoundWind"], "actual_hand_number": snapshot["HandNumber"],
            "profile": rules, "reward_retrained": False,
            "unencoded_rules": ["score-extension", "dealer-repeat", "leading-dealer-end",
                                "rating-reward", "time-limit-final-hand"]}


def encode_public_obs(state, snapshot, at_kan_select=False):
    import numpy as np
    obs, mask = state.encode_obs(4, at_kan_select)
    if obs.shape != (1012, 34) or mask.shape != (46,):
        raise ValueError("MORTAL_FEATURE_GEOMETRY")
    context = match_context(snapshot)
    obs = np.array(obs, copy=True)
    obs[PROGRESS_ROW_V4, :] = context["progress"]
    if not np.isfinite(obs).all():
        raise ValueError("MORTAL_FEATURE_NONFINITE")
    return obs, mask
