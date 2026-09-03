import os
import tempfile
import unittest

from cvnbot import config as config_module

SAMPLE_INI = """# User
botnick=TestBot
botpass=TestBot:hunter2
botrealname=#cvn-test CVNBot
# Server
ircserver=irc.example.org
feedchannel=#cvn-test
controlchannel=None
# Feed
editbig=1234
feedFilterUsersReg=3
#Ignore=Comments
IsCubbie
"""


class ConfigTest(unittest.TestCase):
    def setUp(self):
        handle, self.path = tempfile.mkstemp(suffix=".ini")
        with os.fdopen(handle, "w") as out:
            out.write(SAMPLE_INI)

    def tearDown(self):
        os.unlink(self.path)

    def load(self):
        config = config_module.Config()
        return config_module.apply_from_file(config, self.path)

    def test_strings(self):
        config = self.load()
        self.assertEqual(config.bot_nick, "TestBot")
        self.assertEqual(config.bot_pass, "TestBot:hunter2")
        self.assertEqual(config.irc_server_name, "irc.example.org")
        self.assertEqual(config.feed_channel, "#cvn-test")

    def test_integers(self):
        config = self.load()
        self.assertEqual(config.edit_big, 1234)
        self.assertEqual(config.feed_filter_users_reg, 3)

    def test_booleans(self):
        config = self.load()
        self.assertTrue(config.is_cubbie, 'override')

    def test_defaults(self):
        config = self.load()
        self.assertEqual(config.broadcast_channel, "None")
        self.assertEqual(config.edit_blank, -500)
        self.assertEqual(config.irc_server_port, 6667)
        self.assertFalse(config.log_syslog)

    def test_ignore_comments(self):
        raw = config_module.read_raw_config(self.path)
        self.assertNotIn("#Ignore", raw)
        self.assertNotIn("Ignore", raw)
        self.assertEqual(len(raw), 9)

    def test_public_config(self):
        settings = config_module.public_config(self.load())
        names = [name for name in settings]
        for hidden in ("botpass", "botnick", "ircserver", "feedchannel", "lists"):
            self.assertNotIn(hidden, names)
        for shown in ("editbig", "IsCubbie", "feedFilterUsersAnon"):
            self.assertIn(shown, names)

        self.assertEqual(settings["editbig"], 1234)
        self.assertIs(settings["IsCubbie"], True)


if __name__ == "__main__":
    unittest.main()
