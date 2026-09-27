// Original MJCN adapter. Included in the locally generated upstream types.cpp.
// The snapshot is current public state, never a claimed complete event replay.
static json11::Json mjcn_current_snapshot;

static json11::Json mjcn_find_snapshot(const Moves& record) {
    for (const auto& item : record)
        if (item["type"] == "start_kyoku" && item["mjcn_snapshot"].is_object())
            return item["mjcn_snapshot"];
    return json11::Json();
}

static Game_State mjcn_read_state(const json11::Json& s) {
    Game_State result;
    result.bakaze = s["round_wind"].int_value();
    result.kyoku = s["hand_number"].int_value();
    result.honba = s["honba"].int_value();
    result.kyotaku = s["riichi_sticks"].int_value();
    for (const auto& tile : s["dora"].array_items()) result.dora_marker.push_back(hai_str_to_int(tile.string_value()));
    for (const auto& p : s["players"].array_items()) {
        auto& dst = result.player_state[p["id"].int_value()];
        dst.tehai.fill(0);
        dst.score = p["score"].int_value();
        dst.jikaze = p["seat_wind"].int_value();
        dst.reach_declared = p["riichi_declared"].bool_value();
        dst.reach_accepted = p["riichi_established"].bool_value();
        for (const auto& r : p["river"].array_items()) {
            Sutehai river;
            river.hai = hai_str_to_int(r["pai"].string_value());
            river.tsumogiri = r["tsumogiri"].bool_value();
            river.is_reach = r["riichi"].bool_value();
            dst.kawa.push_back(river);
        }
        for (const auto& m : p["melds"].array_items()) {
            Fuuro_Elem meld;
            const auto type = m["type"].string_value();
            meld.type = type == "chi" ? FT_CHI : type == "pon" ? FT_PON : type == "ankan" ? FT_ANKAN : type == "kakan" ? FT_KAKAN : FT_DAIMINKAN;
            meld.hai = m["pai"].is_null() ? 0 : hai_str_to_int(m["pai"].string_value());
            meld.target_relative = m["from"].is_null() ? 0 : (4 + m["from"].int_value() - p["id"].int_value()) % 4;
            bool removed_claim = false;
            for (const auto& tile : m["tiles"].array_items()) {
                const int h = hai_str_to_int(tile.string_value());
                if (meld.type != FT_ANKAN && !removed_claim && h == meld.hai) removed_claim = true;
                else meld.consumed.push_back(h);
            }
            dst.fuuro.push_back(meld);
        }
    }
    for (const auto& tile : s["hand"].array_items())
        result.player_state[s["our_player"].int_value()].tehai[hai_str_to_int(tile.string_value())]++;
    return result;
}

// Upstream counts each claimed tile through its original river. Mid-game recovery
// can have a visible meld but lack that covered river slot. Add only those missing
// physical copies, without inventing a discard or its order.
Hai_Array mjcn_missing_claimed_tiles() {
    Hai_Array extra = {};
    if (!mjcn_current_snapshot.is_object()) return extra;
    Hai_Array marked = {};
    for (const auto& p : mjcn_current_snapshot["players"].array_items())
        for (const auto& r : p["river"].array_items())
            if (r["claimed"].bool_value()) marked[hai_str_to_int(r["pai"].string_value())]++;
    for (const auto& p : mjcn_current_snapshot["players"].array_items())
        for (const auto& m : p["melds"].array_items()) {
            if (m["type"] == "ankan") continue;
            const int h = hai_str_to_int(m["pai"].string_value());
            if (marked[h] > 0) marked[h]--; else extra[h]++;
        }
    return extra;
}
