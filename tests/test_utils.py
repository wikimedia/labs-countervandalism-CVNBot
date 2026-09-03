import datetime
import re
import unittest

from cvnbot import utils


class ParseDateTimeLengthTest(unittest.TestCase):
    def test_indefinite(self):
        self.assertEqual(utils.parse_datetime_length("indefinite", 99), 0)
        self.assertEqual(utils.parse_datetime_length("infinite", 99), 0)

    def test_tomorrow(self):
        self.assertEqual(utils.parse_datetime_length("tomorrow", 99), 86400)

    def test_units(self):
        self.assertEqual(utils.parse_datetime_length("1 hour", 99), 3600)
        self.assertEqual(utils.parse_datetime_length("3 days", 99), 3 * 86400)
        self.assertEqual(utils.parse_datetime_length("2 weeks", 99), 14 * 86400)
        self.assertEqual(utils.parse_datetime_length("31 minutes", 99), 31 * 60)

    def test_combined(self):
        self.assertEqual(
            utils.parse_datetime_length("1 day and 2 hours", 99), 86400 + 7200
        )

    def test_unparseable_returns_default(self):
        self.assertEqual(utils.parse_datetime_length("06:21, 2 February 2019", 42), 42)
        self.assertEqual(utils.parse_datetime_length("", 42), 42)


class TicksTest(unittest.TestCase):
    def test_unix_epoch(self):
        epoch = datetime.datetime(1970, 1, 1, tzinfo=datetime.timezone.utc)
        self.assertEqual(utils.ticks_from_datetime(epoch), 621355968000000000)

    def test_round_trip(self):
        when = datetime.datetime(2024, 4, 5, 13, 37, 21, 500000, tzinfo=datetime.timezone.utc)
        self.assertEqual(utils.datetime_from_ticks(utils.ticks_from_datetime(when)), when)

    def test_ticks_now_moves_forward(self):
        self.assertLess(
            utils.ticks_from_datetime(
                datetime.datetime(2000, 1, 1, tzinfo=datetime.timezone.utc)
            ),
            utils.ticks_now()
        )

    def test_format_expiry(self):
        # Formatting is done in UTC, so build the input from a known UTC moment
        utc = datetime.datetime(2024, 4, 5, 13, 37, tzinfo=datetime.timezone.utc)
        self.assertEqual(
            utils.format_expiry(utils.ticks_from_datetime(utc)),
            "13:37, 5 April 2024",
        )


class ReplaceStrMaxTest(unittest.TestCase):
    def test_replaces_up_to_max(self):
        self.assertEqual(utils.replace_str_max("abracadabra", "a", "X", 2), "XbrXcadabra")

    def test_stops_when_exhausted(self):
        self.assertEqual(utils.replace_str_max("aa", "a", "X", 5), "XX")

    def test_no_match(self):
        self.assertEqual(utils.replace_str_max("xyz", "a", "X", 5), "xyz")


class WikiEncodeTest(unittest.TestCase):
    def test_spaces_become_underscores(self):
        self.assertEqual(utils.wiki_encode("Main Page"), "Main_Page")

    def test_brackets_and_bang(self):
        self.assertEqual(utils.wiki_encode("Foo (bar)!"), "Foo_%28bar%29%21")

    def test_non_ascii_is_lowercase_percent_encoded(self):
        self.assertEqual(utils.wiki_encode("Café"), "Caf%C3%A9")

    def test_slash_is_encoded(self):
        self.assertEqual(utils.wiki_encode("User:A/B & C"), "User:A/B_%26_C")


class CompileDotnetTest(unittest.TestCase):
    def test_named_groups_are_translated(self):
        pattern = utils.compile_dotnet(r"^blocked \[\[(?<item1>.+?)\]\]$")
        self.assertEqual(
            pattern.match("blocked [[User:Foo]]").group("item1"), "User:Foo"
        )
        pattern = utils.compile_dotnet(r"\b(?i)(alpaca|alpacas)\b", re.IGNORECASE)
        self.assertTrue(pattern.search("a million AlpacaS"))

    def test_lookbehind_is_left_alone(self):
        pattern = utils.compile_dotnet(r"(?<=a)b")
        self.assertTrue(pattern.search("ab"))


if __name__ == "__main__":
    unittest.main()
