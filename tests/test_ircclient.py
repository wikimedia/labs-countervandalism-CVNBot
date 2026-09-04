import heapq
import logging
import unittest

from cvnbot.ircclient import (
    IrcClient,
    IrcMessage,
    Priority,
    SendType,
)


class IrcMessageTest(unittest.TestCase):
    def test_channel_message(self):
        message = IrcMessage(":nick!user@host PRIVMSG #chan :hello world")
        self.assertEqual(message.command, "PRIVMSG")
        self.assertEqual(message.nick, "nick")
        self.assertEqual(message.channel, "#chan")
        self.assertEqual(message.message, "hello world")

    def test_message_with_colon_in_text(self):
        message = IrcMessage(":n!u@h PRIVMSG #chan :https://example.org : ok")
        self.assertEqual(message.message, "https://example.org : ok")

    def test_private_message_has_no_channel(self):
        message = IrcMessage(":n!u@h PRIVMSG CVNBot :hi")
        self.assertEqual(message.channel, "")

    def test_ping(self):
        message = IrcMessage("PING :server.example.org")
        self.assertEqual(message.command, "PING")
        self.assertEqual(message.message, "server.example.org")

    def test_numeric_with_params(self):
        message = IrcMessage(":server 353 me = #chan :@op +voice plain")
        self.assertEqual(message.command, "353")
        self.assertEqual(message.params[:3], ["me", "=", "#chan"])
        self.assertEqual(message.message, "@op +voice plain")

    def test_error(self):
        message = IrcMessage("ERROR :Closing Link: nick (Excess Flood)")
        self.assertEqual(message.command, "ERROR")
        self.assertIn("Excess Flood", message.message)


class ChannelTrackingTest(unittest.TestCase):
    def setUp(self):
        self.client = IrcClient()

    def feed(self, line):
        self.client._handle_line(line)

    def test_names_reply_sets_modes(self):
        self.feed(":server 353 me = #chan :@opper +voiced plain")
        self.assertTrue(self.client.get_channel_user("#chan", "opper").is_op)
        self.assertTrue(self.client.get_channel_user("#chan", "voiced").is_voice)
        self.assertFalse(self.client.get_channel_user("#chan", "plain").is_op)
        self.assertFalse(self.client.get_channel_user("#chan", "plain").is_voice)

    def test_unknown_user(self):
        self.assertIsNone(self.client.get_channel_user("#chan", "nobody"))

    def test_join_and_part(self):
        self.feed(":joiner!u@h JOIN :#chan")
        self.assertIsNotNone(self.client.get_channel_user("#chan", "joiner"))
        self.feed(":joiner!u@h PART #chan :bye")
        self.assertIsNone(self.client.get_channel_user("#chan", "joiner"))

    def test_quit_removes_from_all_channels(self):
        self.feed(":server 353 me = #chan :quitter")
        self.feed(":quitter!u@h QUIT :leaving")
        self.assertIsNone(self.client.get_channel_user("#chan", "quitter"))

    def test_nick_change(self):
        self.feed(":server 353 me = #chan :@old")
        self.feed(":old!u@h NICK :new")
        self.assertIsNone(self.client.get_channel_user("#chan", "old"))
        self.assertTrue(self.client.get_channel_user("#chan", "new").is_op)

    def test_mode_grants_and_revokes_op(self):
        self.feed(":server 353 me = #chan :someone")
        self.feed(":op!u@h MODE #chan +o someone")
        self.assertTrue(self.client.get_channel_user("#chan", "someone").is_op)
        self.feed(":op!u@h MODE #chan -o someone")
        self.assertFalse(self.client.get_channel_user("#chan", "someone").is_op)

    def test_mode_with_multiple_targets(self):
        self.feed(":op!u@h MODE #chan +vv one two")
        self.assertTrue(self.client.get_channel_user("#chan", "one").is_voice)
        self.assertTrue(self.client.get_channel_user("#chan", "two").is_voice)

    def test_mode_without_user_target_is_ignored(self):
        self.feed(":server 353 me = #chan :someone")
        self.feed(":op!u@h MODE #chan +b *!*@spam.example")
        self.assertFalse(self.client.get_channel_user("#chan", "someone").is_op)

    def test_kick(self):
        self.feed(":server 353 me = #chan :victim")
        self.feed(":op!u@h KICK #chan victim :out")
        self.assertIsNone(self.client.get_channel_user("#chan", "victim"))


class DispatchTest(unittest.TestCase):
    def setUp(self):
        self.client = IrcClient()
        self.events = []
        self.client.on_channel_message = lambda c, e, ts=None: self.events.append(("msg", e))
        self.client.on_channel_notice = lambda c, e: self.events.append(("notice", e))
        self.client.on_connected = lambda c: self.events.append(("connected", None))
        self.client.on_error = lambda c, m: self.events.append(("error", m))

    def test_dispatches_channel_message(self):
        self.client._handle_line(":n!u@h PRIVMSG #chan :hi")
        self.assertEqual(self.events[0][0], "msg")

    def test_ignores_private_message(self):
        self.client._handle_line(":n!u@h PRIVMSG me :hi")
        self.assertEqual(self.events, [])

    def test_dispatches_notice(self):
        self.client._handle_line(":n!u@h NOTICE #chan :broadcast")
        self.assertEqual(self.events[0][0], "notice")

    def test_dispatches_connected_on_welcome(self):
        self.client._handle_line(":server 001 me :Welcome")
        self.assertEqual(self.events[0][0], "connected")

    def test_dispatches_error(self):
        self.client._handle_line("ERROR :Closing Link: (Excess Flood)")
        self.assertEqual(self.events[0][0], "error")
        self.assertIn("Excess Flood", self.events[0][1])

    def test_numeric_errors_are_reported(self):
        self.client._handle_line(":server 471 me #chan :Cannot join channel")
        self.assertEqual(self.events[0][0], "error")

    def test_handler_exceptions_do_not_propagate(self):
        def boom(client, event):
            raise ValueError("boom")

        self.client.on_channel_message = boom

        logging.disable(logging.ERROR)
        try:
            self.client._handle_line(":n!u@h PRIVMSG #chan :hi")
        finally:
            logging.disable(logging.WARNING)


class SendTest(unittest.TestCase):
    def setUp(self):
        self.client = IrcClient()

    @property
    def lines(self):
        ret = []
        while self.client._send_queue._heap:
            ret.append(heapq.heappop(self.client._send_queue._heap)[2])
        return ret

    def test_fifo_and_priority(self):
        self.client.send_message(SendType.MESSAGE, "#chan", "B3", Priority.LOW)
        self.client.send_message(SendType.MESSAGE, "#chan", "A3", Priority.HIGH)
        self.client.send_message(SendType.MESSAGE, "#chan", "B1", Priority.LOW)
        self.client.send_message(SendType.MESSAGE, "#chan", "A1", Priority.HIGH)
        self.client.send_message(SendType.MESSAGE, "#chan", "B2", Priority.LOW)
        self.client.send_message(SendType.MESSAGE, "#chan", "A2", Priority.HIGH)
        self.assertEqual(self.lines, ["PRIVMSG #chan :A3",
                                      "PRIVMSG #chan :A1",
                                      "PRIVMSG #chan :A2",
                                      "PRIVMSG #chan :B3",
                                      "PRIVMSG #chan :B1",
                                      "PRIVMSG #chan :B2"])

    def test_privmsg_hello(self):
        self.client.send_message(SendType.MESSAGE, "#chan", "hello")
        self.assertEqual(self.lines[0], "PRIVMSG #chan :hello")

    def test_privmsg_empty_string_is_ignored(self):
        self.client.send_message(SendType.MESSAGE, "#chan", "")
        self.assertEqual(self.lines, [])

    def test_privmsg_whitespace_is_ignored(self):
        self.client.send_message(SendType.MESSAGE, "#chan", "  ")
        self.assertEqual(self.lines, [])

    def test_privmsg_long_messages_are_split(self):
        self.client.send_message(SendType.MESSAGE, "#chan", "x" * 900)
        self.assertEqual(self.lines, [
            "PRIVMSG #chan :" + ("x" * 485),
            "PRIVMSG #chan :" + ("x" * 415),
        ])

    def test_privmsg_long_message_trailing_space_is_ignored(self):
        self.client.send_message(SendType.MESSAGE, "#chan", ("x" * 484) + "   ")
        self.assertEqual(self.lines, [
            "PRIVMSG #chan :" + ("x" * 484) + " ",
            "PRIVMSG #chan :  ",
        ])

    def test_privmsg_multiline_message(self):
        self.client.send_message(SendType.MESSAGE, "#chan", "foo\nbar")
        self.assertEqual(self.lines, [
            "PRIVMSG #chan :foo",
            "PRIVMSG #chan :bar",
        ])

    def test_privmsg_empty_lines_are_ignored(self):
        self.client.send_message(SendType.MESSAGE, "#chan", "foo\n \nbar")
        self.assertEqual(self.lines, [
            "PRIVMSG #chan :foo",
            "PRIVMSG #chan :bar",
        ])

    def test_split_does_not_break_multibyte_characters(self):
        chunks = self.client._split_for_wire("é" * 400, 20)
        self.assertGreater(len(chunks), 1)
        self.assertEqual("".join(chunks), "é" * 400)

    def test_action(self):
        self.client.send_message(SendType.ACTION, "#chan", "waves")
        self.assertEqual(self.lines[0], "PRIVMSG #chan :\x01ACTION waves\x01")

    def test_notice(self):
        self.client.send_message(SendType.NOTICE, "#chan", "psst", Priority.HIGH)
        self.assertEqual(self.lines[0], "NOTICE #chan :psst")


if __name__ == "__main__":
    unittest.main()
