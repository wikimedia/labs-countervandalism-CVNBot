import os
import shutil
import socket
import tempfile
import threading
import time
import unittest
from unittest import mock

from cvnbot.program import CVNBot

TIMEOUT = 10


class FakeIrcServer(threading.Thread):
    """Accepts one client, welcomes it, and records everything it sends."""

    def __init__(self):
        super().__init__(daemon=True)
        self.socket = socket.socket()
        self.socket.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.socket.bind(("127.0.0.1", 0))
        self.socket.listen(1)
        self.port = self.socket.getsockname()[1]
        self.lines = []
        self.connection = None
        self.stopped = threading.Event()

    def run(self):
        connection, _ = self.socket.accept()
        connection.settimeout(0.5)
        self.connection = connection
        buffer = b""
        welcomed = False
        while not self.stopped.is_set():
            try:
                data = connection.recv(4096)
            except socket.timeout:
                continue
            except OSError:
                break
            if not data:
                break
            buffer += data
            while b"\n" in buffer:
                raw, buffer = buffer.split(b"\n", 1)
                self.lines.append(raw.decode("utf-8", "replace").strip())
            if not welcomed and any(l.startswith("USER") for l in self.lines):
                welcomed = True
                self.send(":srv 001 TestBot :Welcome")
        connection.close()

    def send(self, line):
        self.connection.sendall((line + "\r\n").encode("utf-8"))

    def wait_for(self, predicate, timeout=TIMEOUT):
        deadline = time.time() + timeout
        while time.time() < deadline:
            if any(predicate(line) for line in list(self.lines)):
                return True
            time.sleep(0.05)
        return False

    def shutdown(self):
        self.stopped.set()
        self.socket.close()


class IntegrationTest(unittest.TestCase):
    def setUp(self):
        self.server = FakeIrcServer()
        self.server.start()

        self.workdir = tempfile.mkdtemp()

        self.ini = os.path.join(self.workdir, "CVNBot.ini")
        with open(self.ini, "w") as out:
            out.write(
                "botnick=TestBot\n"
                "ircserver=127.0.0.1\n"
                "ircport={0}\n"
                "feedchannel=#cvn-sandbox\n"
                "controlchannel=#cvn-bots\n"
                "lists={1}/Lists.sqlite\n"
                "projects={1}/Projects.xml\n".format(self.server.port, self.workdir)
            )

        self.patchers = [
            mock.patch("cvnbot.rcreader.RCReader.initiate_connection"),
            mock.patch("sys.exit"),
            mock.patch.object(CVNBot, "VERSION", "0.0"),
        ]
        for patcher in self.patchers:
            patcher.start()

        self.bot = CVNBot(self.ini)
        threading.Thread(target=self.bot.run, args=(), daemon=True).start()
        self.assertTrue(self.server.wait_for(lambda l: l.startswith("USER")))

    def tearDown(self):
        self.bot.irc.disconnect()
        self.bot.listman.close_db_connection()
        self.server.shutdown()
        for patcher in self.patchers:
            patcher.stop()
        shutil.rmtree(self.workdir, ignore_errors=True)

    def command(self, text):
        self.server.send(
            ":boss!u@h PRIVMSG #cvn-bots :TestBot {0}".format(text)
        )

    def test_run_does_login_and_join(self):
        time.sleep(0.5)
        self.assertEqual([
            'CAP LS 302',
            'NICK TestBot',
            'USER TestBot 4 * :CVNBot 0.0',
            'JOIN #cvn-sandbox,#cvn-bots'], self.server.lines)

    def test_database_is_created(self):
        self.assertTrue(os.path.exists(os.path.join(self.workdir, "Lists.sqlite")))

    def test_command_from_an_op_is_answered(self):
        self.assertTrue(self.server.wait_for(lambda l: l.startswith("JOIN #cvn-sandbox")))
        self.server.send(":srv 353 TestBot = #cvn-bots :@boss TestBot")
        self.command("bl add Vandal x=1 r=bad")
        self.assertTrue(
            self.server.wait_for(
                lambda l: l.startswith("PRIVMSG #cvn-bots :")
            )
        )
        self.assertIn("Vandal is on global blacklist, added by boss",
                      [l for l in self.server.lines if l.startswith("PRIVMSG #cvn-bots :")][0])

    def test_command_from_an_unvoiced_user_is_refused(self):
        self.assertTrue(self.server.wait_for(lambda l: l.startswith("JOIN #cvn-sandbox")))
        self.server.send(":srv 353 TestBot = #cvn-bots :boss TestBot")
        self.command("list")
        self.assertTrue(
            self.server.wait_for(lambda l: l.startswith("NOTICE boss :"))
        )
        self.assertFalse(
            any(l.startswith("PRIVMSG #cvn-bots") for l in self.server.lines)
        )


if __name__ == "__main__":
    unittest.main()
