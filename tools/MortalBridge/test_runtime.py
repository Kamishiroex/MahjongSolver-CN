"""Protocol/privacy regressions; no torch or model needed."""
import copy
import unittest
from unittest.mock import Mock

from runtime import public_event, ReplaySession, prefer_available_win
from types import SimpleNamespace


class PublicProtocolTests(unittest.TestCase):
    def start(self):
        return dict(type="start_kyoku", bakaze="E", dora_marker="9m", kyoku=1,
                    honba=0, kyotaku=0, oya=0, scores=[25000] * 4,
                    tehais=[["1m", "2m", "3m", "4p", "5pr", "6p", "1s", "2s", "3s", "E", "E", "P", "C"] for _ in range(4)],
                    names=["private"] * 4, meta={"private": "must not reach engine"})

    def test_masks_opponents_for_every_pov_without_mutating_input(self):
        for player in range(4):
            event = self.start()
            before = copy.deepcopy(event)
            clean = public_event(event, player)
            self.assertEqual(before, event)
            self.assertNotIn("names", clean)
            self.assertNotIn("meta", clean)
            for i in range(4):
                self.assertEqual(clean["tehais"][i], before["tehais"][i] if i == player else ["?"] * 13)

    def test_only_own_draw_identity_survives(self):
        for actor in range(4):
            clean = public_event(dict(type="tsumo", actor=actor, pai="5mr"), 0)
            self.assertEqual(clean["pai"], "5mr" if actor == 0 else "?")

    def test_red_public_call_preserved(self):
        event = dict(type="pon", actor=1, target=2, pai="5pr", consumed=["5p", "5p"])
        self.assertEqual(event, public_event(event, 0))

    def test_partial_snapshot_is_not_mjai(self):
        with self.assertRaisesRegex(ValueError, "MORTAL_MJAI_EVENT_REQUIRED"):
            public_event(dict(OurPlayerId=0, Hand=[], HistoryComplete=False), 0)

    def test_start_requires_actual_thirteen_tile_hand(self):
        for hand in (["?"] * 13, ["1m"] * 14, []):
            event = self.start()
            event["tehais"][0] = hand
            with self.assertRaises(ValueError):
                public_event(event, 0)

    def test_unknown_tsumogiri_is_not_false(self):
        with self.assertRaisesRegex(ValueError, "MORTAL_TSUMOGIRI_UNKNOWN"):
            public_event(dict(type="dahai", actor=1, pai="1m", tsumogiri=None), 0)

    def test_missing_public_tile_and_invalid_actor_rejected(self):
        for event in (dict(type="dahai", actor=4, pai="1m", tsumogiri=True),
                      dict(type="dahai", actor=1, pai="?", tsumogiri=True)):
            with self.assertRaises(ValueError):
                public_event(event, 0)

    def test_wrong_meld_size_rejected(self):
        with self.assertRaisesRegex(ValueError, "MORTAL_CONSUMED_COUNT"):
            public_event(dict(type="ankan", actor=0, consumed=["1m"] * 3), 0)

    def test_scores_and_round_required(self):
        for key, value in (("scores", [25000]), ("honba", -1), ("kyoku", 0)):
            event = self.start()
            event[key] = value
            with self.assertRaises(ValueError):
                public_event(event, 0)

    def test_end_hands_not_forwarded(self):
        event = dict(type="hora", actor=1, target=0, tehais=["1m"], ura_markers=["2m"])
        self.assertEqual(public_event(event, 0), dict(type="hora", actor=1, target=0))

    def test_broken_session_never_reuses_native_state(self):
        session = ReplaySession.__new__(ReplaySession)
        session.failed, session.active, session.player = False, False, 0
        session.state, session.bot = Mock(), Mock()
        with self.assertRaisesRegex(ValueError, "MORTAL_START_KYOKU_REQUIRED"):
            session.feed(dict(type="tsumo", actor=0, pai="1m"))
        with self.assertRaisesRegex(ValueError, "MORTAL_SESSION_FAILED"):
            session.feed(self.start())
        session.state.update.assert_not_called()
        session.bot.react.assert_not_called()

    def test_tsumo_takes_priority_over_upstream_discard(self):
        cans = SimpleNamespace(can_tsumo_agari=True, can_ron_agari=False, target_actor=0)
        self.assertEqual(prefer_available_win({"type": "dahai"}, cans, 0),
                         dict(type="hora", actor=0, target=0))

    def test_ron_takes_priority_over_chi_and_targets_actual_discarder(self):
        cans = SimpleNamespace(can_tsumo_agari=False, can_ron_agari=True, target_actor=3)
        self.assertEqual(prefer_available_win({"type": "chi"}, cans, 0),
                         dict(type="hora", actor=0, target=3))

    def test_no_fabricated_win_when_furiten_or_no_yaku(self):
        cans = SimpleNamespace(can_tsumo_agari=False, can_ron_agari=False, target_actor=3)
        response = {"type": "none"}
        self.assertIs(prefer_available_win(response, cans, 0), response)


if __name__ == "__main__":
    unittest.main()
