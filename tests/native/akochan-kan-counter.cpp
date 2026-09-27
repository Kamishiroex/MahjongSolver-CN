// Original test harness. Includes only the caller's locally obtained upstream headers.
// No original akochan implementation or private game record is distributed here.
#include "types.hpp"
#include <initializer_list>

struct Case {
    const char* name;
    std::initializer_list<const char*> events;
    int normal;
    int replacement;
    int baselineNormal;
    int baselineReplacement;
};

static Moves record(std::initializer_list<const char*> types) {
    Moves events;
    for (const auto type : types) events.push_back(json11::Json::object{{"type", type}});
    return events;
}

int main(int argc, char** argv) {
    _set_error_mode(_OUT_TO_STDERR);
    _set_abort_behavior(0, _WRITE_ABORT_MSG | _CALL_REPORTFAULT);
    SetErrorMode(SEM_NOGPFAULTERRORBOX | SEM_FAILCRITICALERRORS);
    const std::string mode = argc == 2 ? argv[1] : "";
    if (mode == "--invalid-ankan") {
        count_tsumo_num(record({"start_kyoku", "tsumo", "ankan", "tsumo"}));
        return 0; // Must not be reached: the original native assertion remains active.
    }
    if (mode != "--expect-fixed" && mode != "--expect-unpatched") return 2;
    const bool fixed = mode == "--expect-fixed";
    const Case cases[] = {
        {"ordinary", {"start_kyoku", "tsumo", "dahai", "tsumo"}, 2, 0, 2, 0},
        {"ankan_dora", {"start_kyoku", "tsumo", "ankan", "dora", "tsumo"}, 1, 1, 1, 1},
        {"daiminkan_direct", {"start_kyoku", "tsumo", "dahai", "daiminkan", "tsumo"}, 1, 1, 1, 1},
        {"kakan_direct", {"start_kyoku", "tsumo", "kakan", "tsumo"}, 1, 1, 1, 1},
        {"daiminkan_dora", {"start_kyoku", "tsumo", "dahai", "daiminkan", "dora", "tsumo"}, 1, 1, 2, 0},
        {"kakan_dora", {"start_kyoku", "tsumo", "kakan", "dora", "tsumo"}, 1, 1, 2, 0},
        {"mixed_kans", {"start_kyoku", "tsumo", "dahai", "daiminkan", "dora", "tsumo",
            "kakan", "dora", "tsumo", "ankan", "dora", "tsumo"}, 1, 3, 3, 1},
        {"round_boundary", {"start_kyoku", "tsumo", "ankan", "dora", "tsumo", "start_kyoku", "tsumo"}, 1, 0, 1, 0},
        {"late_dora_does_not_convert_later_draw", {"start_kyoku", "tsumo", "kakan", "tsumo", "dora", "dahai", "tsumo"}, 2, 1, 2, 1},
    };
    json11::Json::array results;
    int failures = 0;
    for (const auto& test : cases) {
        const auto actual = count_tsumo_num(record(test.events));
        const int normal = fixed ? test.normal : test.baselineNormal;
        const int replacement = fixed ? test.replacement : test.baselineReplacement;
        const bool passed = actual.first == normal && actual.second == replacement;
        if (!passed) failures++;
        results.push_back(json11::Json::object{{"case", test.name}, {"normal", actual.first},
            {"replacement", actual.second}, {"passed", passed}});
    }
    std::cout << json11::Json(json11::Json::object{{"fixed", fixed}, {"failures", failures}, {"cases", results}}).dump() << std::endl;
    return failures == 0 ? 0 : 1;
}
