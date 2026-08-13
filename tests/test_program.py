import re
import unittest

from cvnbot.ircclient import ChannelUser, IrcMessage, SendType
from cvnbot.listmanager import UserType
from cvnbot.rcreader import EventType, RCEvent

from .helpers import destroy_bot, make_bot

FEED = "#cvn-sandbox"
CONTROL = "#cvn-bots"
BROADCAST = "#cvn-broadcast"


def edit_event(**kwargs):
    event = RCEvent(
        project="en.wikipedia",
        title="Sandbox",
        url="https://en.wikipedia.org/w/index.php?diff=1",
        user="127.0.0.1",
        comment="test edit",
        eventtype=EventType.edit,
        szdiff=12,
    )
    for key, value in kwargs.items():
        setattr(event, key, value)
    return event


class BotTestCase(unittest.TestCase):
    def setUp(self):
        self.bot = make_bot()
        self.bot.config.feed_channel = FEED
        self.bot.config.control_channel = CONTROL

    def tearDown(self):
        destroy_bot(self.bot)

    @property
    def feed(self):
        return self.bot.irc.messages_to(FEED)

    def react(self, event):
        self.bot.react_to_rc_event(event)
        return self.feed


class ReactToEditTest(BotTestCase):
    def test_anon_edit_is_reported(self):
        messages = self.react(edit_event())
        self.assertEqual(len(messages), 1)
        self.assertIn("IP", messages[0])
        self.assertIn("[[en:User:127.0.0.1]]", messages[0])
        self.assertIn("[[en:Sandbox]]", messages[0])
        self.assertIn("(+12)", messages[0])

    def test_anon_edit_zero_szdiff_is_reported(self):
        messages = self.react(edit_event(szdiff=0))
        self.assertEqual(len(messages), 1)
        self.assertIn("IP", messages[0])
        self.assertIn("[[en:User:127.0.0.1]]", messages[0])
        self.assertIn("[[en:Sandbox]]", messages[0])
        self.assertIn("(+0)", messages[0])

    def test_small_edit_by_registered_user_is_ignored(self):
        self.assertEqual(self.react(edit_event(user="Tangotango")), [])

    def test_small_edit_by_blacklisted_user_is_reported(self):
        self.bot.listman.add_user_to_list(
            "Vandal", "", UserType.blacklisted, "op", "bad", 0
        )
        messages = self.react(edit_event(user="Vandal", szdiff=1))
        self.assertIn("Blacklist", messages[0])

    def test_small_edit_by_registered_to_watched_page_is_reported(self):
        self.bot.listman.add_page_to_watchlist("Sandbox", "", "op", "watch me", 0)
        messages = self.react(edit_event(user="Tangotango", szdiff=1))
        self.assertEqual(len(messages), 1)
        self.assertIn("watched", messages[0])

    def test_large_edit_by_registered_user_is_reported(self):
        messages = self.react(edit_event(user="Tangotango", szdiff=900))
        self.assertEqual(len(messages), 1)
        self.assertIn("User:Tangotango", messages[0])
        self.assertIn("(+900)", messages[0])

    def test_large_edit_by_whitelisted_user_is_ignored(self):
        self.bot.listman.add_user_to_list(
            "Tangotango", "", UserType.whitelisted, "op", "ok", 0
        )
        self.assertEqual(self.react(edit_event(user="Tangotango", szdiff=900)), [])

    def test_large_edit_by_admin_is_ignored(self):
        self.bot.listman.add_user_to_list(
            "Tangotango", "en.wikipedia", UserType.admin, "op", "ok", 0
        )
        self.assertEqual(self.react(edit_event(user="Tangotango", szdiff=900)), [])

    def test_large_edit_by_bot_is_ignored(self):
        self.bot.listman.add_user_to_list(
            "Tangotango", "en.wikipedia", UserType.bot, "op", "ok", 0
        )
        self.assertEqual(self.react(edit_event(user="Tangotango", szdiff=900)), [])

    def test_large_edit_with_botflag_is_ignored(self):
        self.assertEqual(self.react(edit_event(user="Tangotango", botflag=True, szdiff=900)), [])

    def test_blanking_by_registered_user_is_reported_and_greylists(self):
        messages = self.react(edit_event(user="Tangotango", comment="Blanked the page", szdiff=-20))
        self.assertEqual(len(messages), 1)
        self.assertIn("User:Tangotango", messages[0])
        self.assertIn("(-20)", messages[0])
        self.assertIn("blanked", messages[0])
        self.assertEqual(
            self.bot.listman.classify_editor("Tangotango", ""), UserType.greylisted
        )

    def test_hardhide_suppresses_output_but_keeps_autolisting(self):
        self.bot.config.feed_filter_event_edit = 3
        messages = self.react(edit_event(user="Tangotango", comment="Blanked the page", szdiff=-20))
        self.assertEqual(messages, [])
        self.assertEqual(
            self.bot.listman.classify_editor("Tangotango", ""), UserType.greylisted
        )

    def test_ignored_events_do_not_touch_the_database(self):
        self.bot.config.feed_filter_event_edit = 4
        messages = self.react(edit_event(user="Tangotango", comment="Blanked the page", szdiff=-20))
        self.assertEqual(messages, [])
        self.assertEqual(
            self.bot.listman.classify_editor("Tangotango", ""), UserType.user
        )

    def test_bes_by_registered_user_is_reported_and_greylists(self):
        self.bot.listman.add_item_to_list("badword", 20, "op", "vandalism", 0)
        messages = self.react(edit_event(user="Tangotango", comment="badword here"))
        self.assertIn("edit summary", messages[0])
        self.assertEqual(
            self.bot.listman.classify_editor("Tangotango", ""), UserType.greylisted
        )

    def test_replacing_report_mentions_the_content(self):
        messages = self.react(
            edit_event(user="Tangotango", comment="Replaced content with 'poop'")
        )
        self.assertIn("replaced", messages[0])
        self.assertIn("poop", messages[0])

    def test_replacing_report_for_autosumm_without_item1(self):
        self.bot.prjlist["en.wikipedia"].rautosumm_replace = re.compile("^Replaced all the content(?:: (?P<comment>.*?))?$")
        messages = self.react(
            edit_event(user="Tangotango", comment="Replaced all the content")
        )
        self.assertIn("replaced", messages[0])
        self.assertIn("with new text", messages[0])

    def test_cubbie_only_reports_uploads(self):
        self.bot.config.is_cubbie = True
        self.assertEqual(self.react(edit_event()), [])


class ReactToNewPageTest(BotTestCase):
    def test_anon_creation_is_reported(self):
        messages = self.react(edit_event(newpage=True, szdiff=100))
        self.assertIn("created", messages[0])

    def test_large_creation_is_flagged(self):
        messages = self.react(edit_event(newpage=True, szdiff=5000))
        self.assertIn("Copyvio?", messages[0])

    def test_ordinary_creation_by_registered_user_is_ignored(self):
        self.assertEqual(
            self.react(edit_event(user="Tangotango", newpage=True, szdiff=100)), []
        )

    def test_title_matching_bna_is_reported(self):
        self.bot.listman.add_item_to_list("bad", 12, "op", "watch", 0)
        messages = self.react(
            edit_event(user="Tangotango", title="Somebad", newpage=True, szdiff=100)
        )
        self.assertIn("watch word", messages[0])


# See also:
#   test_rcreader.py#LogEventTest
class ReactToLogEventTest(BotTestCase):
    def test_newuser_create_is_reported(self):
        event = RCEvent(
            eventtype=EventType.newuser,
            project="en.wikipedia",
            user="Newbie",
        )
        messages = self.react(event)
        self.assertEqual(len(messages), 1)
        self.assertIn("Newbie", messages[0])

    def test_newuser_create2_is_reported(self):
        event = RCEvent(
            eventtype=EventType.newuser2,
            project="en.wikipedia",
            user="Creator",
            title="Newbie",
        )
        messages = self.react(event)
        self.assertEqual(len(messages), 1)
        self.assertIn("Newbie", messages[0])

    def test_newuser_create_matching_bnu_and_greylisted(self):
        self.bot.listman.add_item_to_list("vand.l", 11, "op", "some reason", 0)
        event = RCEvent(
            eventtype=EventType.newuser,
            project="en.wikipedia",
            user="Vandalman",
        )
        messages = self.react(event)
        self.assertIn("Vandalman", messages[0])
        self.assertIn("vand.l", messages[0])
        self.assertEqual(self.bot.listman.classify_editor("Vandalman", ""), UserType.greylisted)

    def test_newuser_create2_editor_matching_bnu_and_greylisted(self):
        self.bot.listman.add_item_to_list("vand.l", 11, "op", "some reason", 0)
        event = RCEvent(
            eventtype=EventType.newuser2,
            project="en.wikipedia",
            user="Creator",
            title="Vandalman",
        )
        messages = self.react(event)
        self.assertIn("Vandalman", messages[0])
        self.assertIn("vand.l", messages[0])
        self.assertEqual(self.bot.listman.classify_editor("Creator", ""), UserType.user)
        self.assertEqual(self.bot.listman.classify_editor("Vandalman", ""), UserType.greylisted)

    def test_newuser_create2_creator_matching_bnu_and_greylisted(self):
        self.bot.listman.add_item_to_list("vand.l", 11, "op", "some reason", 0)
        event = RCEvent(
            eventtype=EventType.newuser2,
            project="en.wikipedia",
            user="Vandalman",
            title="Sockpuppet",
        )
        messages = self.react(event)
        self.assertIn("Vandalman", messages[0])
        self.assertIn("Sockpuppet", messages[0])
        self.assertIn("vand.l", messages[0])
        self.assertEqual(self.bot.listman.classify_editor("Vandalman", ""), UserType.greylisted)
        self.assertEqual(self.bot.listman.classify_editor("Sockpuppet", ""), UserType.user)

    def test_block_with_length_is_reported_and_autoblacklisted(self):
        event = RCEvent(
            eventtype=EventType.block,
            project="en.wikipedia",
            title="User:Vandal",
            user="Admin",
            comment="spam",
            block_length="31 hours",
        )
        messages = self.react(event)
        self.assertIn("Block editor", messages[0])
        self.assertEqual(self.bot.listman.classify_editor("Vandal", ""), UserType.blacklisted)

    def test_block_indefinite_is_reported(self):
        event = RCEvent(
            eventtype=EventType.block,
            project="en.wikipedia",
            title="User:Vandal",
            user="Admin",
            comment="spam",
            block_length="indefinite",
        )
        messages = self.react(event)
        self.assertIn("Block editor", messages[0])
        self.assertEqual(self.bot.listman.classify_editor("Vandal", ""), UserType.user)

    def test_reblock_with_length_is_reported_and_autoblacklisted(self):
        event = RCEvent(
            eventtype=EventType.block,
            project="en.wikipedia",
            title="User:Vandal",
            user="Admin",
            comment="spammest",
            block_length="",
        )
        messages = self.react(event)
        self.assertIn("Block editor", messages[0])
        self.assertEqual(self.bot.listman.classify_editor("Vandal", ""), UserType.blacklisted)

    def test_unblock_is_reported(self):
        event = RCEvent(
            eventtype=EventType.unblock,
            project="en.wikipedia",
            title="User:Vandal",
            user="Admin",
            comment="mistake",
            block_length="31 hours",
        )
        messages = self.react(event)
        self.assertIn("unblock editor", messages[0].lower())
        self.assertEqual(self.bot.listman.classify_editor("Vandal", ""), UserType.user)

    def test_upload_by_user_is_reported(self):
        event = RCEvent(
            eventtype=EventType.upload,
            project="en.wikipedia",
            title="Image:A.png",
            user="Tango",
        )
        messages = self.react(event)
        self.assertIn("Tango", messages[0])
        self.assertIn("uploaded", messages[0])
        self.assertIn("Image:A.png", messages[0])

    def test_upload_by_admin_is_hidden(self):
        self.bot.listman.add_user_to_list("Tango", "en.wikipedia", UserType.admin, "op", "ok", 0)
        event = RCEvent(
            eventtype=EventType.upload,
            project="en.wikipedia",
            title="Image:A.png",
            user="Tango",
        )
        self.assertEqual(self.react(event), [])

    def test_move_is_reported(self):
        event = RCEvent(
            eventtype=EventType.move,
            project="en.wikipedia",
            user="Tango",
            title="A",
            moved_to="B",
            comment="better title",
            block_length="https://en.wikipedia.org/wiki/A",
        )
        messages = self.react(event)
        self.assertIn("User:Tango", messages[0])
        self.assertIn("[[en:A]] to [[en:B]]", messages[0])

    def test_delete_is_reported(self):
        event = RCEvent(
            eventtype=EventType.delete,
            project="en.wikipedia",
            title="Spam",
            user="Admin",
            comment="vandalism",
        )
        self.assertIn("deleted", self.react(event)[0].lower())

    def test_unhandled_event_type_sends_nothing(self):
        event = RCEvent(
            eventtype=EventType.restore,
            project="en.wikipedia",
            user="Admin",
        )
        self.assertEqual(self.react(event), [])


class CommandTest(BotTestCase):
    def setUp(self):
        import re
        super().setUp()
        self.bot.config.bot_nick = "CVNBot"
        self.bot.bot_cmd = re.compile(
            r"^CVNBot (\s*(?P<command>\S*))(\s(?P<params>.*))?$",
            re.IGNORECASE
        )
        self.bot.irc.users[(CONTROL, "op")] = self._user(op=True)
        self.bot.irc.users[(CONTROL, "voiced")] = self._user(voice=True)
        self.bot.irc.users[(CONTROL, "plain")] = self._user()

    @staticmethod
    def _user(op=False, voice=False):
        user = ChannelUser("someone")
        if op:
            user.modes.add("o")
        if voice:
            user.modes.add("v")
        return user

    def command(self, nick, text):
        event = IrcMessage(
            ":{0}!u@h PRIVMSG {1} :{2}".format(nick, CONTROL, text)
        )
        self.bot._on_channel_message(self.bot.irc, event)
        return self.bot.irc.messages_to(CONTROL)

    def test_help_from_unvoiced_user_is_refused(self):
        self.assertEqual(self.command("plain", "CVNBot help"), [])
        # Access denied message
        self.assertEqual(len(self.bot.irc.messages_to("plain")), 1)

    def test_help_from_voiced_user_is_answered(self):
        self.assertEqual(len(self.command("voiced", "CVNBot help")), 1)

    def test_help_from_op_user_is_answered(self):
        self.assertEqual(len(self.command("op", "CVNBot help")), 1)

    def test_purge_from_unvoiced_user_is_refused(self):
        self.assertEqual(self.command("plain", "CVNBot purge en.wikipedia"), [])
        # Access denied message
        self.assertEqual(len(self.bot.irc.messages_to("plain")), 1)

    def test_purge_from_voiced_user_is_refused(self):
        self.assertEqual(self.command("voiced", "CVNBot purge en.wikipedia"), [])
        # Access denied message
        self.assertEqual(len(self.bot.irc.messages_to("voiced")), 1)

    def test_purge_from_op_user_is_answered(self):
        messages = self.command("op", "CVNBot purge en.wikipedia")
        self.assertIn("Threw away", messages[0])

    def test_list_command(self):
        messages = self.command("op", "CVNBot list")
        self.assertIn("en.wikipedia", messages[0])
        self.assertIn("Total: 1 wikis", messages[0])

    def test_count_command(self):
        messages = self.command("voiced", "CVNBot count")
        self.assertIn("owns 1 wikis", messages[0])
        self.assertIn(self.bot.VERSION, messages[0])

    def test_config_command_hides_the_password(self):
        self.bot.config.bot_pass = "hunter2"
        messages = self.command("voiced", "CVNBot config")
        self.assertIn("runs CVNBot " + self.bot.VERSION, messages[0])
        self.assertIn("editbig", messages[0])
        self.assertNotIn("hunter2", " ".join(messages))

    def test_blacklist_command(self):
        messages = self.command("voiced", "CVNBot bl add Vandal x=1 r=bad")
        self.assertIn("Vandal", messages[0])
        self.assertEqual(
            self.bot.listman.classify_editor("Vandal", ""), UserType.blacklisted
        )

    def test_intel_command(self):
        self.command("voiced", "CVNBot bl add Vandal x=1 r=bad")
        messages = self.command("voiced", "CVNBot intel Vandal")
        self.assertIn("blacklist", messages[-1])

    def test_unknown_command_is_ignored(self):
        self.assertEqual(self.command("op", "CVNBot something"), [])

    def test_non_command_message_is_ignored(self):
        self.assertEqual(self.command("op", "hello world"), [])

    def test_status_command(self):
        self.assertIn("Last message", self.command("voiced", "CVNBot status")[0])


class BroadcastTest(BotTestCase):
    def setUp(self):
        super().setUp()
        self.bot.config.broadcast_channel = BROADCAST

    def notice(self, message):
        event = IrcMessage(
            ":other!u@h NOTICE {0} :{1}".format(BROADCAST, message)
        )
        self.bot._on_channel_notice(self.bot.irc, event)

    def broadcast_line(self, list_name, action, item, expiry, reason, adder):
        self.bot.irc.sent = []
        self.bot.broadcast(list_name, action, item, expiry, reason, adder)
        return self.bot.irc.messages_to(BROADCAST)[0]

    def test_broadcast_sent(self):
        line = self.broadcast_line("BL", "ADD", "Vandal", 900, "bad", "op")
        self.assertIn("B/1.1", line)
        self.assertIn("Vandal", line)

    def test_broadcast_sent_nothing(self):
        self.bot.config.broadcast_channel = "None"
        self.bot.irc.sent = []
        self.bot.broadcast("BL", "ADD", "Vandal", 900, "bad", "op")
        self.assertEqual(self.bot.irc.sent, [])

    def test_user_add(self):
        line = self.broadcast_line("BL", "ADD", "Vandal", 900, "bad", "op")
        self.bot.irc.sent = []
        self.notice(line)
        self.assertEqual(
            self.bot.listman.classify_editor("Vandal", ""), UserType.blacklisted
        )

    def test_user_delete(self):
        self.bot.listman.add_user_to_list(
            "Vandal", "", UserType.blacklisted, "op", "bad", 0
        )
        self.notice(self.broadcast_line("BL", "DEL", "Vandal", 0, "sorry", "op"))
        self.assertEqual(self.bot.listman.classify_editor("Vandal", ""), UserType.user)

    def test_watchlist_add(self):
        self.notice(self.broadcast_line("CVP", "ADD", "Sandbox", 0, "watch", "op"))
        self.assertTrue(self.bot.listman.is_watched_article("Sandbox", "").success)

    def test_item_add(self):
        self.notice(self.broadcast_line("BES", "ADD", "badword", 0, "spam", "op"))
        self.assertTrue(self.bot.listman.matches_list("a badword", 20).success)

    def test_bleep_count(self):
        line = self.broadcast_line("BLEEP", "COUNT", "BLEEP", 0, CONTROL, "op")
        self.bot.irc.sent = []
        self.notice(line)
        self.assertIn("owns 1 wikis", self.bot.irc.messages_to(CONTROL)[0])

    def test_bleep_find_known(self):
        line = self.broadcast_line("BLEEP", "FIND", "en.wikipedia", 0, CONTROL, "op")
        self.bot.irc.sent = []
        self.notice(line)
        self.assertIn("has en.wikipedia", self.bot.irc.messages_to(CONTROL)[0])

    def test_bleep_find_unknown_is_ignored(self):
        line = self.broadcast_line("BLEEP", "FIND", "de.wikipedia", 0, CONTROL, "op")
        self.bot.irc.sent = []
        self.notice(line)
        self.assertEqual(self.bot.irc.sent, [])

    def test_unknown_add_is_ignored(self):
        self.notice(self.broadcast_line("XX", "ADD", "thing", 0, "why", "op"))

    def test_invalid_format_is_ignored(self):
        self.notice("hello world")


class SendMessageMultiTest(BotTestCase):
    def test_split_on_newlines(self):
        self.bot.send_message_multi(SendType.MESSAGE, FEED, "one\ntwo")
        self.assertEqual(self.feed, ["one", "two"])

    def test_split_long_lines(self):
        self.bot.send_message_multi(SendType.MESSAGE, FEED, "x" * 1000)
        self.assertEqual(len(self.feed), 3)

    def test_skip_empty_and_quote_only_lines(self):
        self.bot.send_message_multi(SendType.MESSAGE, FEED, 'real\n""\n"')
        self.assertEqual(self.feed, ["real"])

    def test_empty_message_sends_nothing(self):
        self.bot.send_message_multi(SendType.MESSAGE, FEED, "")
        self.assertEqual(self.feed, [])


if __name__ == "__main__":
    unittest.main()
