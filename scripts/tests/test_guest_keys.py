import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import guest_keys  # noqa: E402


def keys(text):
    return [guest_keys.key_for(character) for character in text]


class GuestKeysTests(unittest.TestCase):
    def test_y_and_z_are_swapped(self):
        self.assertEqual(keys('security'), [(False, 's'), (False, 'e'), (False, 'c'), (False, 'u'), (False, 'r'),
                                            (False, 'i'), (False, 't'), (False, 'z')])
        self.assertEqual(keys('yz'), [(False, 'z'), (False, 'y')])
        self.assertEqual(keys('Zeile')[0], (True, 'y'))
        self.assertEqual(keys('Y'), [(True, 'z')])

    def test_url_characters(self):
        self.assertEqual(keys(':/_'), [(True, 'dot'), (True, '7'), (True, 'slash')])
        self.assertEqual(keys('=?,;+'), [(True, '0'), (True, 'minus'), (False, 'comma'), (True, 'comma'), (False, 'bracket_right')])
        self.assertEqual(keys('.- 1'), [(False, 'dot'), (False, 'slash'), (False, 'spc'), (False, '1')])

    def test_unknown_punctuation_fails_instead_of_wrong_key(self):
        for bad in ('@', '~', 'ab'):
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                guest_keys.key_for(bad)


if __name__ == '__main__':
    unittest.main()
