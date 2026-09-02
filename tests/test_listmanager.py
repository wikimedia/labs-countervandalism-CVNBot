import datetime
import unittest

from cvnbot import utils
from cvnbot.listmanager import UserType, ListManager

from unittest import mock
from .helpers import destroy_bot, make_bot


class ListManagerTestCase(unittest.TestCase):
    def setUp(self):
        self.bot = make_bot()
        self.listman = self.bot.listman
        fake_now = datetime.datetime(2011, 4, 1, 0, 0, 0, tzinfo=datetime.timezone.utc)
        date_patcher = mock.patch('cvnbot.utils.ticks_now', return_value=utils.ticks_from_datetime(fake_now))
        date_patcher.start()
        self.addCleanup(date_patcher.stop)

    def tearDown(self):
        destroy_bot(self.bot)


class ListManagerHelpersTest(unittest.TestCase):
    def test_get_expiry_date_indefinite(self):
        self.assertEqual(ListManager.get_expiry_date(0), 0)

    def test_get_expiry_date_relative(self):
        before = utils.ticks_now()
        expiry = ListManager.get_expiry_date(3600)
        self.assertGreaterEqual(expiry - before, 3600 * 10**7)
        self.assertLess(expiry - before, 3601 * 10**7)

    def test_is_anon_anon(self):
        self.assertTrue(ListManager.is_anon("127.0.0.1"))
        self.assertTrue(ListManager.is_anon("2001:0DB8:0000:0000:0000:0000:1428:57AB"))
        self.assertTrue(ListManager.is_anon("~2025-12345"))

    def test_is_anon_registered(self):
        self.assertFalse(ListManager.is_anon("abc"))
        self.assertFalse(ListManager.is_anon("Tangotango"))
        self.assertFalse(ListManager.is_anon("127.0.0.xxx"))

    def test_is_anon_invalid(self):
        self.assertFalse(ListManager.is_anon(":"))
        self.assertFalse(ListManager.is_anon(".127.0.0.1"))
        self.assertFalse(ListManager.is_anon("fc:100:300"))
        self.assertFalse(ListManager.is_anon("X~2025-12345"))


class ListManagerUsersTest(ListManagerTestCase):
    def test_classify_editor_unlisted(self):
        self.assertEqual(self.listman.classify_editor("Tango", ""), UserType.user)
        self.assertEqual(self.listman.classify_editor("10.0.0.5", ""), UserType.anon)

    def test_classify_editor_ignore_expired(self):
        self.listman.add_user_to_list("Tango", "", UserType.blacklisted, "op", "Vandal", 0)
        self.listman._execute("UPDATE users SET expiry = 1")
        self.assertEqual(self.listman.classify_editor("Tango", ""), UserType.user)

    def test_add_blacklisted(self):
        self.listman.add_user_to_list("Tango", "", UserType.blacklisted, "op", "Vandal", 3600)
        self.assertEqual(self.listman.classify_editor("Tango", ""), UserType.blacklisted)

    def test_add_whitelisted(self):
        result = self.listman.add_user_to_list(
            "Tango", "", UserType.whitelisted, "op", "Trusted", 0
        )
        self.assertIn("Tango", result)
        self.assertIn("whitelist", result)
        self.assertIn("Trusted", result)

    def test_add_admin_and_whitelisted(self):
        result = self.listman.add_user_to_list(
            "Tango", "en.wikipedia", UserType.admin, "op", "Vandal", 0
        )
        self.assertIn("Added", result)
        result = self.listman.add_user_to_list(
            "Tango", "", UserType.whitelisted, "op", "Trusted", 0
        )
        self.assertIn("Added", result)
        self.assertEqual(self.listman.classify_editor("Tango", "en.wikipedia"), UserType.admin)

    def test_add_whitelisted_and_admin(self):
        self.listman.add_user_to_list(
            "Tango", "", UserType.whitelisted, "op", "Trusted", 0
        )
        result = self.listman.add_user_to_list(
            "Tango", "en.wikipedia", UserType.admin, "op", "Vandal", 0
        )
        self.assertIn("Added", result)
        self.assertEqual(self.listman.classify_editor("Tango", "en.wikipedia"), UserType.admin)

    def test_add_whitelisted_and_bot(self):
        self.listman.add_user_to_list(
            "Tango", "", UserType.whitelisted, "op", "Trusted", 0
        )
        result = self.listman.add_user_to_list(
            "Tango", "en.wikipedia", UserType.bot, "op", "Vandal", 0
        )
        self.assertIn("Added", result)
        self.assertEqual(self.listman.classify_editor("Tango", "en.wikipedia"), UserType.bot)

    def test_add_whitelisted_and_blacklisted_conflict(self):
        self.listman.add_user_to_list(
            "Tango", "", UserType.whitelisted, "op", "Trusted", 0
        )
        result = self.listman.add_user_to_list(
            "Tango", "", UserType.blacklisted, "op", "Vandal", 0
        )
        self.assertIn("cannot add", result)
        self.assertEqual(self.listman.classify_editor("Tango", ""), UserType.whitelisted)

    def test_add_blacklisted_and_greylisted(self):
        self.listman.add_user_to_list(
            "Tango", "", UserType.greylisted, "CVNBot", "Matched something", 900
        )
        self.assertEqual(self.listman.classify_editor("Tango", ""), UserType.greylisted)
        self.listman.add_user_to_list(
            "Tango", "", UserType.blacklisted, "op", "vandal", 0
        )
        # Greylist takes precedence while it is still active
        self.assertEqual(self.listman.classify_editor("Tango", "en.wikipedia"), UserType.greylisted)

    def test_add_local_admin_beats_global_lists(self):
        self.listman.add_user_to_list(
            "Tango", "en.wikipedia", UserType.admin, "CVNBot", "Downloaded", 0
        )
        self.assertEqual(self.listman.classify_editor("Tango", "en.wikipedia"), UserType.admin)
        self.assertEqual(self.listman.classify_editor("Tango", ""), UserType.user)

        self.listman.add_user_to_list(
            "Tango", "", UserType.whitelisted, "op", "Trusted", 0
        )
        self.assertEqual(self.listman.classify_editor("Tango", "en.wikipedia"), UserType.admin)
        self.assertEqual(self.listman.classify_editor("Tango", ""), UserType.whitelisted)

    def test_add_update(self):
        self.listman.add_user_to_list("Tango", "", UserType.blacklisted, "op", "One", 0)
        result = self.listman.add_user_to_list(
            "Tango", "", UserType.blacklisted, "op", "Two", 0
        )
        self.assertIn("Tango", result)
        self.assertIn("Two", result)
        self.assertNotIn("One", result)

    def test_delete(self):
        self.listman.add_user_to_list("Tango", "", UserType.blacklisted, "op", "x", 0)
        result = self.listman.del_user_from_list("Tango", "", UserType.blacklisted)
        self.assertIn("Deleted", result)
        self.assertEqual(self.listman.classify_editor("Tango", ""), UserType.user)

    def test_delete_unlisted(self):
        result = self.listman.del_user_from_list("Tango", "", UserType.blacklisted)
        self.assertIn("is not on", result)

    def test_intel_listed(self):
        self.listman.add_user_to_list("Tango", "en.wikipedia", UserType.admin, "op", "Trusted", 0)
        self.listman.add_user_to_list("Tango", "", UserType.blacklisted, "op", "Vandal", 0)
        result = self.listman.global_intel("Tango")
        self.assertIn("blacklist", result)
        self.assertIn(" and ", result)
        self.assertIn("admin list", result)

    def test_intel_unlisted(self):
        self.assertIn("not on any lists", self.listman.global_intel("Tango"))


class ListManagerItemsTest(ListManagerTestCase):
    def test_add_and_match(self):
        self.listman.add_item_to_list("sh.t", 20, "op", "Some reason", 0)
        match = self.listman.matches_list("no shit sherlock", 20)
        self.assertTrue(match.success)
        self.assertEqual(match.matched_item, "sh.t")
        self.assertEqual(match.matched_reason, "Some reason")

        match = self.listman.matches_list("and he was bESHAtTed", 20)
        self.assertTrue(match.success)
        self.assertEqual(match.matched_item, "sh.t")
        self.assertEqual(match.matched_reason, "Some reason")

        self.assertFalse(self.listman.matches_list("a shower kit is harmless", 20).success)

    def test_match_separate(self):
        self.listman.add_item_to_list("sh.t", 20, "op", "Some reason", 0)
        self.assertFalse(self.listman.matches_list("no shit sherlock", 11).success)

    def test_add_invalid_regex(self):
        result = self.listman.add_item_to_list("*bad(", 20, "op", "oops", 0)
        self.assertIn("does not compile", result)

    def test_match_ignore_stored_invalid_regex(self):
        self.listman.add_item_to_list("sh.t", 20, "op", "Some reason", 0)
        self.listman._execute(
            "INSERT INTO items (item, itemtype, adder, reason, expiry)"
            " VALUES ('*bad(', 20, 'op', 'oops', 0)"
        )
        self.listman.add_item_to_list("sl.p", 20, "op", "Another reason", 0)

        self.assertFalse(self.listman.matches_list("anything", 20).success)
        self.assertTrue(self.listman.matches_list("shit", 20).success)
        self.assertTrue(self.listman.matches_list("slop", 20).success)

    def test_delete_item(self):
        self.listman.add_item_to_list("spam", 20, "op", "Some reason", 0)
        self.assertIn("Deleted", self.listman.del_item_from_list("spam", 20))
        self.assertFalse(self.listman.matches_list("spam", 20).success)

    def test_delete_unknown_item(self):
        self.assertIn("is not on", self.listman.del_item_from_list("spam", 20))

    def test_test_item(self):
        self.listman.add_item_to_list("spam", 20, "op", "Some reason", 0)
        self.assertIn("matches item", self.listman.test_item_on_list("spam here", 20))
        self.assertIn("does not match", self.listman.test_item_on_list("clean", 20))


class ListManagerWatchlistTest(ListManagerTestCase):
    def test_add_and_check(self):
        self.listman.add_page_to_watchlist("Sandbox", "", "op", "watched", 0)
        self.assertTrue(self.listman.is_watched_article("Sandbox", "en.wikipedia").success)

    def test_add_ucfirst(self):
        self.listman.add_page_to_watchlist("sandbox", "", "op", "watched", 0)
        self.assertTrue(self.listman.is_watched_article("Sandbox", "en.wikipedia").success)

    def test_add_local_watchlist(self):
        self.listman.add_page_to_watchlist("Sandbox", "en.wikipedia", "op", "watched", 0)
        self.assertFalse(self.listman.is_watched_article("Sandbox", "de.wikipedia").success)

    def test_add_translate_namespace(self):
        self.listman.add_page_to_watchlist("File:A.png", "en.wikipedia", "op", "watched", 0)
        self.assertTrue(self.listman.is_watched_article("Image:A.png", "en.wikipedia").success)

    def test_delete(self):
        self.listman.add_page_to_watchlist("Sandbox", "", "op", "watched", 0)
        self.assertIn("Deleted", self.listman.del_page_from_watchlist("Sandbox", ""))
        self.assertFalse(self.listman.is_watched_article("Sandbox", "en.wikipedia").success)
        self.assertFalse(self.listman.is_watched_article("Sandbox", "").success)


class GarbageCollectionTest(ListManagerTestCase):
    def test_collect_garbage(self):
        self.listman.add_user_to_list("Gone", "", UserType.blacklisted, "Tango", "meh", 0)
        self.listman.add_user_to_list("Stays", "", UserType.whitelisted, "Tango", "meh", 0)
        self.listman.add_page_to_watchlist("Template:Delete", "", "Tango", "meh", 0)
        self.listman.add_page_to_watchlist("Main Page", "", "Tango", "meh", 0)
        self.listman.add_item_to_list("spam", 20, "Tango", "meh", 0)
        self.listman.add_item_to_list("slop", 20, "Tango", "meh", 0)
        self.listman._execute("UPDATE users SET expiry = 1 WHERE name = 'Gone'")
        self.listman._execute("UPDATE watchlist SET expiry = 1 WHERE article = 'Main Page'")
        self.listman._execute("UPDATE items SET expiry = 1 WHERE item = 'spam'")

        self.assertEqual(self.listman.collect_garbage(), 3)
        self.assertEqual(self.listman.classify_editor("Gone", ""), UserType.user)
        self.assertEqual(self.listman.classify_editor("Stays", ""), UserType.whitelisted)
        self.assertFalse(self.listman.is_watched_article("Main Page", ""))
        self.assertTrue(self.listman.is_watched_article("Template:Delete", ""))
        self.assertFalse(self.listman.matches_list("spam", 20).success)
        self.assertTrue(self.listman.matches_list("slop", 20).success)


class HandleListCommandTest(ListManagerTestCase):
    def test_add_with_duration_and_reason(self):
        result = self.listman.handle_list_command(1, "op", "add Tango x=48 r=Terrible vandal")
        self.assertIn("Tango", result)
        self.assertIn("Terrible vandal", result)
        self.assertIn("until 00:00, 3 April 2011", result)
        self.assertEqual(self.listman.classify_editor("Tango", ""), UserType.blacklisted)

    def test_add_broadcasts(self):
        self.bot.config.broadcast_channel = "#cvn-broadcast"
        self.listman.handle_list_command(1, "op", "add Tango x=48 r=Terrible vandal")
        broadcasts = self.bot.irc.messages_to("#cvn-broadcast")
        self.assertEqual(len(broadcasts), 1)
        self.assertIn("BL", broadcasts[0])
        self.assertIn("Tango", broadcasts[0])

    def test_add_multiword_item(self):
        result = self.listman.handle_list_command(1, "op", "add Tango tango x=1")
        self.assertIn("until 01:00, 1 April 2011", result)
        self.assertEqual(self.listman.classify_editor("Tango tango", ""), UserType.blacklisted)

    def test_show(self):
        self.listman.handle_list_command(1, "op", "add Tango tango r=bad")
        self.assertIn("bad", self.listman.handle_list_command(1, "op", "show Tango tango"))

    def test_delete(self):
        self.listman.handle_list_command(1, "op", "add Tango tango r=bad")
        self.listman.handle_list_command(1, "op", "del Tango tango")
        self.assertEqual(self.listman.classify_editor("Tango tango", ""), UserType.user)

    def test_test_command(self):
        self.listman.handle_list_command(20, "op", "add spam r=spammy")
        self.assertIn(
            "matches item",
            self.listman.handle_list_command(20, "op", "test spam here")
        )

    def test_test_command_rejected_for_user_lists(self):
        self.assertEqual(
            self.listman.handle_list_command(1, "op", "test Tango"),
            self.bot.msgs["20002"],
        )

    def test_add_with_unknown_project(self):
        self.assertEqual(
            "Project xx.wikipedia is unknown",
            self.listman.handle_list_command(2, "op", "add Tango p=xx.wikipedia")
        )

    def test_adminlist_requires_project(self):
        self.assertIn(
            "no global admin",
            self.listman.handle_list_command(2, "op", "add Tango")
        )

    def test_add_rejected_for_greylist(self):
        self.assertIn(
            "cannot directly add", self.listman.handle_list_command(6, "op", "add Tango")
        )

    def test_invalid_syntax(self):
        self.assertEqual(
            self.listman.handle_list_command(1, "op", "something Tango"),
            self.bot.msgs["20000"],
        )


class ListManagerPurgeTest(ListManagerTestCase):
    def test_purge_removes_project_data_only(self):
        self.listman.add_user_to_list("Tango", "en.wikipedia", UserType.admin, "op", "x", 0)
        self.listman.add_user_to_list("Krinkle", "", UserType.blacklisted, "op", "x", 0)
        self.listman.add_page_to_watchlist("Main Page", "", "op", "x", 0)
        self.listman.add_page_to_watchlist("Sandbox", "en.wikipedia", "op", "x", 0)

        result = self.listman.purge_wiki_data("en.wikipedia")
        self.assertIn("Threw away 2 items", result)
        self.assertEqual(self.listman.classify_editor("Tango", "en.wikipedia"), UserType.user)
        self.assertEqual(self.listman.classify_editor("Krinkle", ""), UserType.blacklisted)
        self.assertFalse(self.listman.is_watched_article("Sandbox", "en.wikipedia").success)
        self.assertTrue(self.listman.is_watched_article("Main Page", "en.wikipedia").success)

    def test_invalid_name_is_refused(self):
        self.assertIn("invalid wiki name", self.listman.purge_wiki_data("en'wikipedia"))


if __name__ == "__main__":
    unittest.main()
