"""Shared test fixtures: a bot with no network and no IRC connection."""

import os
import tempfile
import xml.etree.ElementTree as ElementTree

from cvnbot.program import CVNBot
from cvnbot.project import Project

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CONSOLE_MSGS = os.path.join(REPO_ROOT, "cvnbot", "Console.msgs")

EXAMPLE_PROJECT_XML = r"""
<project>
<projectName>en.wikipedia</projectName>
<interwikiLink>en:</interwikiLink>
<rooturl>https://en.wikipedia.org/</rooturl>
<speciallog>Special:.+?/(.+)</speciallog>
<namespaces>&lt;?xml version="1.0"?&gt;&lt;api&gt;&lt;query&gt;&lt;namespaces&gt;
&lt;ns id="-2"&gt;Media&lt;/ns&gt;
&lt;ns id="-1"&gt;Special&lt;/ns&gt;
&lt;ns id="0" /&gt;
&lt;ns id="1"&gt;Talk&lt;/ns&gt;
&lt;ns id="2"&gt;User&lt;/ns&gt;
&lt;ns id="3"&gt;User talk&lt;/ns&gt;
&lt;ns id="4"&gt;Wikipedia&lt;/ns&gt;
&lt;ns id="6"&gt;File&lt;/ns&gt;
&lt;ns id="10"&gt;Template&lt;/ns&gt;
&lt;ns id="14"&gt;Category&lt;/ns&gt;
&lt;/namespaces&gt;&lt;/query&gt;&lt;/api&gt;</namespaces>
<restoreRegex>^restored "\[\[(?&lt;item1&gt;.+?)\]\]"(?:: (?&lt;comment&gt;.*?))?$</restoreRegex>
<deleteRegex>^deleted "\[\[(?&lt;item1&gt;.+?)\]\]"(?:: (?&lt;comment&gt;.*?))?$</deleteRegex>
<protectRegex>^protected "\[\[(?&lt;item1&gt;.+?)\]\]"(?:: (?&lt;comment&gt;.*?))?$</protectRegex>
<unprotectRegex>^removed protection from "\[\[(?&lt;item1&gt;.+?)\]\]"(?:: (?&lt;comment&gt;.*?))?$</unprotectRegex>
<modifyprotectRegex>^changed protection settings for "\[\[(?&lt;item1&gt;.+?)\]\]"(?:: (?&lt;comment&gt;.*?))?$</modifyprotectRegex>
<uploadRegex>^uploaded "\[\[(?&lt;item1&gt;.+?)\]\]"(?:: (?&lt;comment&gt;.*?))?$</uploadRegex>
<moveRegex>^moved \[\[(?&lt;item1&gt;.+?)\]\] to \[\[(?&lt;item2&gt;.+?)\]\](?:: (?&lt;comment&gt;.*?))?$</moveRegex>
<moveredirRegex>^moved \[\[(?&lt;item1&gt;.+?)\]\] to \[\[(?&lt;item2&gt;.+?)\]\] over redirect(?:: (?&lt;comment&gt;.*?))?$</moveredirRegex>
<blockRegex>^blocked \[\[(?&lt;item1&gt;.+?)\]\] with an expiration time of (?&lt;item2&gt;.+?) \((?&lt;item3&gt;.+?)\)(?:: (?&lt;comment&gt;.*?))?$</blockRegex>
<unblockRegex>^unblocked (?&lt;item1&gt;.+?)(?:: (?&lt;comment&gt;.*?))?$</unblockRegex>
<reblockRegex>^changed block settings for \[\[(?&lt;item1&gt;.+?)\]\] with an expiration time of (?&lt;item2&gt;.+?) (?&lt;item3&gt;.+?)(?:: (?&lt;comment&gt;.*?))?$</reblockRegex>
<autosummBlank>^Blanked the page(?:: (?&lt;comment&gt;.*?))?$</autosummBlank>
<autosummReplace>^Replaced content with '(?&lt;item1&gt;.+?)'(?:: (?&lt;comment&gt;.*?))?$</autosummReplace>
</project>
"""


class FakeIrc:
    """Records everything the bot tries to send."""

    def __init__(self):
        self.sent = []
        self.users = {}
        self.joined = []
        self.parted = []

    def send_message(self, send_type, destination, message, priority=None):
        self.sent.append((send_type, destination, message))

    def rfc_join(self, channel):
        self.joined.append(channel)

    def rfc_part(self, channel, reason=""):
        self.parted.append(channel)

    def rfc_quit(self, reason=""):
        pass

    def get_channel_user(self, channel, nick):
        return self.users.get((channel, nick))

    def messages_to(self, destination):
        return [m for _, dest, m in self.sent if dest == destination]


def make_project(name="en.wikipedia", interwiki="en:"):
    """Create a Project object without any file or network."""
    project = Project()
    project.read_project_details(ElementTree.fromstring(EXAMPLE_PROJECT_XML))
    project.project_name = name
    project.interwiki_link = interwiki
    return project


def make_bot(with_project=True):
    """Build a bot with messages loaded, a temp database and a fake IRC."""
    bot = CVNBot('Unused.ini')

    # The rest of this is a replacement for calling CVNBot.run()
    #
    # Intentionally omitted:
    # - config: Don't call config_module.apply_from_file()
    #           You can change bot.config directly instead.
    # - prjlist: Don't call bot.prjlist.load_from_file,
    #            unless your test sets its own bot.prjlist.fn_projects_xml first.

    bot.msgs.read(CONSOLE_MSGS)

    if with_project:
        project = make_project()
        bot.prjlist.projects[project.project_name] = project

    lists_file_handle, lists_file_path = tempfile.mkstemp(suffix=".sqlite")
    os.close(lists_file_handle)
    os.unlink(lists_file_path)
    bot.config.lists_file = lists_file_path
    bot.listman.init_db_connection(lists_file_path)

    bot.irc = FakeIrc()

    bot.rcreader.rcirc = FakeIrc()

    return bot


def destroy_bot(bot):
    path = bot.config.lists_file
    bot.listman.close_db_connection()
    if os.path.exists(path):
        os.unlink(path)
