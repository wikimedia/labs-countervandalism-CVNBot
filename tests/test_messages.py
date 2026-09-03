import logging
import os
import tempfile
import unittest

from cvnbot.messages import Messages

from .helpers import CONSOLE_MSGS


class MessagesTest(unittest.TestCase):
    def setUp(self):
        self.msgs = Messages()
        self.read_returned = self.msgs.read(CONSOLE_MSGS)

    def test_read_return(self):
        self.assertTrue(self.read_returned)

    def test_read_colour_and_bold_placeholders_expanded(self):
        self.assertEqual(self.msgs["00100"], "\x02\x0307")

    def test_read_comments_and_blank_lines_ignored(self):
        self.assertNotIn("", self.msgs)

    def test_read_set(self):
        self.assertEqual(self.msgs["17010"], "watchlist")

    def test_contains(self):
        for key in ("00000", "20005", "17010"):
            self.assertIn(key, self.msgs)

    def test_read_replaces_previous_messages(self):
        handle, path = tempfile.mkstemp()
        with os.fdopen(handle, "w") as out:
            out.write("# comment\n\n00000=2.03\n12345=hello\n")
        try:
            self.msgs.read(path)
        finally:
            os.unlink(path)
        self.assertEqual(len(self.msgs), 2)
        self.assertEqual(self.msgs["12345"], "hello")

    def test_read_missing_file_preserves_previous_messages(self):
        logging.disable(logging.ERROR)
        try:
            self.assertFalse(self.msgs.read("/nonexistent/Console.msgs"))
            self.assertGreater(len(self.msgs), 100)
        finally:
            logging.disable(logging.WARNING)

        self.assertEqual(self.msgs["17010"], "watchlist")

    def test_get_subst(self):
        message = self.msgs.subst(5003, {
            "editor": "en:User:127.0.0.1",
            "ceditor": "127.0.0.1",
            "article": "en:Sandbox",
            "carticle": "Sandbox",
            "size": "+12",
            "sizeattrib": "",
            "sizereset": "",
            "url": "https://example.org/diff",
            "reason": "test",
        })
        self.assertIn("[[en:User:127.0.0.1]]", message)
        self.assertIn("[[en:Sandbox]]", message)
        self.assertIn("(+12)", message)
        self.assertNotIn("${", message)

    def test_get_format(self):
        self.assertEqual(
            self.msgs.format(16001, "Tangotango"), "Tangotango is not on any lists"
        )

    def test_get_unknown_code(self):
        logging.disable(logging.ERROR)
        try:
            self.assertEqual(self.msgs.format(99999, "x"), Messages.ERROR_MESSAGE)
            self.assertEqual(self.msgs.subst(99999, {}), Messages.ERROR_MESSAGE)
        finally:
            logging.disable(logging.WARNING)


if __name__ == "__main__":
    unittest.main()
