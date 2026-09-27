// Original adapter entry point, compiled locally against the user's pinned engine.
#include "main.hpp"
#include "ai_src/selector.hpp"

int mjcn_upstream_main(int argc, char* argv[]);
Hai_Array mjcn_effective_visible(const Moves& record);

static json11::Json applied(const Moves& record) {
    const auto state = get_game_state(record);
    json11::Json::array players;
    for (int id = 0; id < 4; id++) {
        const auto& p = state.player_state[id];
        json11::Json::array river, melds, hand, furiten;
        const auto furiten_flags = get_furiten_flags(record,state,id,true);
        for (int tile = 1; tile < 38; tile++)
            if (furiten_flags[tile]) furiten.push_back(hai_int_to_str(tile));
        for (int tile = 1; tile < 38; tile++)
            for (int n = 0; n < p.tehai[tile]; n++) hand.push_back(hai_int_to_str(tile));
        for (const auto& r : p.kawa)
            river.push_back(json11::Json::object{{"pai",hai_int_to_str(r.hai)},{"tsumogiri",r.tsumogiri},{"riichi",r.is_reach}});
        for (const auto& m : p.fuuro) {
            json11::Json::array consumed;
            for (auto tile : m.consumed) consumed.push_back(hai_int_to_str(tile));
            melds.push_back(json11::Json::object{{"type",int(m.type)},{"pai",m.hai == 0 ? json11::Json() : json11::Json(hai_int_to_str(m.hai))},{"consumed",consumed},{"target_relative",m.target_relative}});
        }
        players.push_back(json11::Json::object{{"id",id},{"score",p.score},{"seat_wind",p.jikaze},{"riichi_declared",p.reach_declared},{"riichi_established",p.reach_accepted},{"river",river},{"melds",melds},{"hand",hand},{"furiten_tiles",furiten}});
    }
    json11::Json::array dora;
    for (auto tile : state.dora_marker) dora.push_back(hai_int_to_str(tile));
    int dealer = -1;
    for (int id = 0; id < 4; id++) if (state.player_state[id].jikaze == 0) dealer = id;
    json11::Json::array visible;
    const auto counts = mjcn_effective_visible(record);
    for (int tile = 1; tile < 38; tile++)
        for (int n = 0; n < counts[tile]; n++) visible.push_back(hai_int_to_str(tile));
    const auto snapshot = mjcn_win_snapshot(record);
    const auto furiten_context = json11::Json::object{{"temporary",snapshot["own_temporary_furiten"]},
        {"after_riichi",snapshot["own_riichi_furiten"]}};
    const int us = snapshot["our_player"].int_value();
    const int last_han = count_tsumo_num_all(record) == 70 ? 1 : 0;
    const auto win_context = json11::Json::object{{"own_draw_kind",snapshot["own_draw_kind"]},
        {"own_ippatsu",snapshot["players"][us]["ippatsu"]},
        {"own_double_riichi",snapshot["players"][us]["double_riichi"]},
        {"tsumo_incident_han",mjcn_tsumo_incident_han(record,us,last_han)},
        {"ron_incident_han",mjcn_ron_incident_han(record,us,last_han+(record.back()["type"] == "kakan" ? 1 : 0))}};
    return json11::Json::object{{"round_wind",state.bakaze},{"hand_number",state.kyoku},{"honba",state.honba},{"riichi_sticks",state.kyotaku},{"dealer",dealer},{"wall_remaining",70-count_tsumo_num_all(record)},{"dora",dora},{"players",players},{"public_visible",visible},{"win_context",win_context},{"furiten_context",furiten_context}};
}

int main(int argc, char* argv[]) {
    if (argc != 4 || std::string(argv[1]) != "snapshot") return mjcn_upstream_main(argc, argv);
    set_tactics_one(load_json_from_file(argv[2]));
    const int player = std::atoi(argv[3]);
    if (!is_valid_player(player)) return 3;
    std::string line, error;
    // Keep the pipe alive until the host closes it, just like upstream pipe mode.
    // Exiting immediately after a reply races the host's strict EOF monitor.
    while (std::getline(std::cin, line)) {
    if (line.size() > 1024*1024) return 4;
    error.clear();
    const auto request = json11::Json::parse(line, error);
    if (!error.empty() || !request["record"].is_array() || request["record"].array_items().size() < 3) return 5;
    Moves record = request["record"].array_items();
    if (record[1]["mjcn_snapshot"]["schema"] != 1 || record[1]["mjcn_snapshot"]["our_player"].int_value() != player) return 6;
    json11::Json::array candidates;
    for (const auto& choice : calc_moves_score(record, player))
        candidates.push_back(json11::Json::object{{"moves",choice.first},{"score",double(choice.second)}});
    std::cout << json11::Json(json11::Json::object{{"schema",1},{"input_sha256",request["input_sha256"]},{"candidates",candidates},{"applied",applied(record)}}).dump() << std::endl;
    }
    return 0;
}
