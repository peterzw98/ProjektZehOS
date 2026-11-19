"""Zuweisung der verantwortlichen Person beim Übernehmen und Freigeben (scripts/board.py)."""
import importlib.util
import pathlib
import unittest
from unittest import mock

SPEC = importlib.util.spec_from_file_location("board", pathlib.Path(__file__).resolve().parents[1] / "board.py")
board = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(board)


class SoftAssigneeTests(unittest.TestCase):
    def test_add_and_remove_use_team_person(self):
        with mock.patch.object(board, "gh") as gh:
            board.soft_assignee(381, "peter", True)
            board.soft_assignee(381, "buzz", False)
        self.assertEqual(gh.call_args_list[0].args[-2:], ("--add-assignee", "peterzw98"))
        self.assertEqual(gh.call_args_list[1].args[-2:], ("--remove-assignee", "larsernst"))

    def test_unknown_team_changes_nothing(self):
        with mock.patch.object(board, "gh") as gh:
            board.soft_assignee(381, "neu", True)
        gh.assert_not_called()

    def test_failure_only_warns(self):
        with mock.patch.object(board, "gh", side_effect=SystemExit("kein Recht")):
            board.soft_assignee(381, "peter", True)   # darf die Übernahme nicht abbrechen


if __name__ == "__main__":
    unittest.main()
