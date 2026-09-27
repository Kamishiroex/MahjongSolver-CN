// Current decision only; never add incident han to simulated future draws.
static json11::Json mjcn_win_snapshot(const Moves& record) {
    for (const auto& item : record)
        if (item["type"] == "start_kyoku" && item["mjcn_snapshot"].is_object())
            return item["mjcn_snapshot"];
    return json11::Json();
}
static int mjcn_own_ippatsu_han(const json11::Json& snapshot, int player) {
    const auto& p = snapshot["players"][player];
    return p["riichi_established"].bool_value() && p["ippatsu"].bool_value() ? 1 : 0;
}
// Upstream already counts one han for riichi. Double riichi adds exactly one,
// and unlike ippatsu it remains valid after later calls. Unknown is not true.
static int mjcn_own_double_riichi_extra_han(const json11::Json& snapshot, int player) {
    const auto& p = snapshot["players"][player];
    return p["riichi_established"].bool_value() && p["double_riichi"].bool_value() ? 1 : 0;
}
static int mjcn_tsumo_incident_han(const Moves& record, int player, int upstream_han) {
    const auto s = mjcn_win_snapshot(record);
    if (!s.is_object() || s["our_player"].int_value() != player || record.empty() ||
        record.back()["type"] != "tsumo" || record.back()["actor"].int_value() != player ||
        record.back()["mjcn_after_call"].bool_value()) return upstream_han;
    // Rinshan and haitei do not stack. Unknown origin keeps the documented
    // upstream assumption; the managed side reports the missing observation.
    const int draw_han = s["own_draw_kind"] == "rinshan" ? 1 : upstream_han;
    return draw_han + mjcn_own_ippatsu_han(s, player) + mjcn_own_double_riichi_extra_han(s, player);
}
static int mjcn_ron_incident_han(const Moves& record, int player, int upstream_han) {
    const auto s = mjcn_win_snapshot(record);
    if (!s.is_object() || s["our_player"].int_value() != player || record.empty() ||
        (record.back()["type"] != "dahai" && record.back()["type"] != "kakan")) return upstream_han;
    return upstream_han + mjcn_own_ippatsu_han(s, player) + mjcn_own_double_riichi_extra_han(s, player);
}
