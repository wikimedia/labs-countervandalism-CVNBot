import dataclasses
import datetime
import enum
import logging
import re
import threading

from . import utils
from .ircclient import IrcClient, IrcConnectionError

logger = logging.getLogger("CVNBot.RCReader")


class EventType(enum.Enum):
    delete = "delete"
    restore = "restore"
    upload = "upload"
    block = "block"
    unblock = "unblock"
    edit = "edit"
    protect = "protect"
    unprotect = "unprotect"
    move = "move"
    rollback = "rollback"
    newuser = "newuser"
    imported = "import"
    unknown = "unknown"
    newuser2 = "newuser2"
    autocreate = "autocreate"
    modifyprotect = "modifyprotect"


@dataclasses.dataclass
class RCEvent:
    project: str = ""
    title: str = ""
    url: str = ""
    user: str = ""
    minor: bool = False
    newpage: bool = False
    botflag: bool = False
    szdiff: int = 0
    comment: str = ""
    eventtype: EventType = EventType.unknown
    block_length: str = ""
    moved_to: str = ""


class RCReader:
    """Connect to the Wikimedia RC feed and forward events to Program."""

    # RC parsing regexen
    strip_colours = re.compile(r"\x04\d{0,2}\*?")
    strip_colours2 = re.compile(r"\x03\d{0,2}")
    strip_bold = re.compile(r"\x02")
    rsz_diff = re.compile(r"\(([\+\-])([0-9]+)\)")

    SERVER_NAME = "irc.wikimedia.org"

    def __init__(self, program):
        self.bot = program
        self.rcirc = IrcClient("RCReader",
                               auto_reconnect=True,
                               auto_rejoin=True,
                               )
        self.last_message = datetime.datetime.now()

    def initiate_connection(self):
        """Connect, join every monitored wiki's channel, and read forever."""
        threading.current_thread().name = "RCReader"

        logger.info("Thread started")

        # Set up RCReader
        self.rcirc.on_connected = self._on_connected
        self.rcirc.on_channel_message = self._on_channel_message

        try:
            self.rcirc.connect(self.SERVER_NAME, 6667)
        except IrcConnectionError as e:
            logger.warning("Could not connect: %s", e)
            return

        self.rcirc.login(self.bot.config.bot_nick, "CVNBot", 4, "CVNBot")

        logger.info("Joining %d channels", len(self.bot.prjlist))
        for project_name in self.bot.prjlist.keys():
            self.rcirc.rfc_join("#" + project_name)

        # Enter loop
        self.rcirc.listen()
        # When listen() returns the IRC session is over
        self.rcirc.disconnect()

    def _on_connected(self, client):
        logger.info("Connected to %s", self.SERVER_NAME)

    def _on_channel_message(self, client, event):
        self.last_message = datetime.datetime.now()

        rce = self.parse_message(event.channel, event.message)
        if rce is None:
            return

        try:
            self.bot.react_to_rc_event(rce)
        except Exception as e:
            logger.exception("Failed to handle RCEvent")
            self.bot.broadcast_dd(
                "ERROR", "ReactorException", str(e),
                event.channel + " " + event.message,
            )

    def parse_message(self, channel, message):
        """
        Parse one raw line into an RCEvent, or None to ignore it.

        Sample from #en.wikipedia on 2017-10-13:
          01> #00314 [[
          02> #00307 Special:Log/newusers
          03> #00314 ]]
          04> #0034   create2
          05> #00310
          06> #00302
          07> #003
          08> #0035  *
          09> #003
          10> #00303 Ujju.19788
          11> #003
          12> #0035  *
          13> #003
          14> #00310 created new account User:Upendhare
          15> #003
        """
        stripped = message
        stripped = utils.replace_str_max(stripped, "\x03", "\x04", 14)
        stripped = self.strip_colours.sub("\x03", stripped)
        stripped = self.strip_bold.sub("", stripped)
        fields = stripped.split("\x03", 14)
        if len(fields) == 15:
            if fields[14].endswith("\x03"):
                fields[14] = fields[14][:-1]
        else:
            # Probably really long article title or something that got cut off;
            # we can't handle these
            return None

        # "#en.wikipedia" > "en.wikipedia"
        project_name = channel[1:]
        if project_name not in self.bot.prjlist:
            logger.warning("Failed to process incoming from %s\n%s\n%s", channel, message, fields)
            self.bot.broadcast_dd(
                "ERROR", "RCR_AOORE", "Message for unmonitored project",
                "{0} {1}".format(channel, stripped),
            )
            # Ignore
            return None
        project = self.bot.prjlist[project_name]

        rce = RCEvent(
            project=project_name,
            title=project.translate_namespace(fields[2]),
            url=fields[6],
            user=fields[10],
        )

        # At the moment, fields[14] contains IRC colour codes.
        # For plain edits, remove just the \x03's.
        # For logs, remove using the regex.
        titlemo = project.rspecial_log_regex.search(fields[2])
        if not titlemo:
            # This is a regular edit
            rce.minor = "M" in fields[4]
            rce.newpage = "N" in fields[4]
            rce.botflag = "B" in fields[4]
            rce.eventtype = EventType.edit
            rce.comment = fields[14].replace("\x03", "")
        else:
            # This is a log action
            log_type = titlemo.group(1)
            # Fix comments
            rce.comment = self.strip_colours2.sub("", fields[14])
            # Check logevent type
            if not self._parse_log_event(rce, project, log_type, fields, message):
                return None

            # These flags don't apply to log events, but must be initialized
            rce.minor = False
            rce.newpage = False
            rce.botflag = False

        # Deal with the diff size
        diff = self.rsz_diff.search(fields[13])
        if diff:
            rce.szdiff = int(diff.group(2))
            if diff.group(1) == "-":
                rce.szdiff = -rce.szdiff
        else:
            rce.szdiff = 0

        return rce

    def _parse_log_event(self, rce, project, log_type, fields, message):
        """Set rce.eventtype and possibly other fields; returns False when the event must be ignored."""
        if log_type == "newusers":
            return self._parse_newusers(rce, project, fields, message)
        if log_type == "block":
            return self._parse_block(rce, project, fields, message)
        if log_type == "protect":
            return self._parse_protect(rce, project, message)
        if log_type == "delete":
            return self._parse_delete(rce, project)
        if log_type == "upload":
            return self._parse_upload(rce, project)
        if log_type == "move":
            return self._parse_move(rce, project, message)

        # Ignore "rights", "import", and any other log event
        return False

    # Could be a user creating their own account, or a user creating a sockpuppet
    #
    # Sample from #nl.wikipedia in 2016 (with comment)
    #   [[Speciaal:Log/newusers]] create2  * BRPots *  created new account Gebruiker:BRPwiki: eerder fout gemaakt
    #
    # Sample from #nl.wikipedia in 2016 (without comment)
    #   [[Speciaal:Log/newusers]] create2  * Sherani koster *  created new account Gebruiker:Rani farah koster
    #
    # Sample from #en.wikipedia in 2017:
    #   [[Special:Log/newusers]] create2  * Ujju.19788 *  created new account User:Upendhare
    #
    # Sample from #en.wikipedia in 2022:
    #   [[Special:Log/newusers]] byemail  * Mdaniels5757 *  created new ccount User:Hannahco12: Requested account
    #
    # Treat newusers/byemail the same as newusers/create2.
    # MediaWiki internally re-uses the "create2" message for "byemail" as well.
    # Ref mediawiki-core.git:/LogFormatter.php#getIRCActionText
    # Ref https://phabricator.wikimedia.org/T327126
    #
    # See also Program._react_newuser2, which formats creator=user, and editor=title
    def _parse_newusers(self, rce, project, fields, message):
        if "create2" in fields[4] or "byemail" in fields[4]:
            match = project.rcreate2_regex.search(rce.comment)
            if not match:
                logger.warning("Unmatched create2 event in %s: %s", rce.project, message)
                return False
            rce.title = match.group(1)
            rce.eventtype = EventType.newuser2
            return True

        if "autocreate" in fields[4]:
            rce.eventtype = EventType.autocreate
        else:
            rce.eventtype = EventType.newuser
        return True

    # Sample from #test.wikipedia in August 2026:
    # [[Special:Log/block]] block  * Krinkle *  blocked [[User:KrinkleBot]] with an expiration time of 5 minutes (autoblock disabled)
    # [[Special:Log/block]] block  * Krinkle *  blocked [[User:KrinkleBot]] with an expiration time of 00:00, 25 August 2026 (account creation disabled)
    # [[Special:Log/block]] reblock  * Krinkle *  changed block settings for [[User:KrinkleBot]] with an expiration time of 1 second (autoblock disabled)
    # [[Special:Log/block]] reblock  * Krinkle *  changed block settings for [[User:KrinkleBot]] with an expiration time of 1 second (autoblock disabled): example {{here}}
    # [[Special:Log/block]] reblock  * Krinkle *  changed block settings for [[User:KrinkleBot]] with an expiration time of 20:00, 24 August 2026 (autoblock disabled, email disabled, cannot edit own talk page)
    # [[Special:Log/block]] reblock  * Krinkle *  changed block settings for [[User:KrinkleBot]] with an expiration time of 20:00, 24 August 2026 (autoblock disabled, email disabled, cannot edit own talk page): example
    # [[Special:Log/block]] unblock  * Krinkle *  unblocked User:KrinkleBot
    def _parse_block(self, rce, project, fields, message):
        if "unblock" in fields[4]:
            match = project.runblock_regex.search(rce.comment)
            if not match:
                logger.warning("Unmatched block/unblock in %s: %s", rce.project, message)
                return False
            rce.eventtype = EventType.unblock
            rce.title = match.group("item1")
            # This project regex might not have a "comment" match group
            rce.comment = match.groupdict(default="").get("comment", "")
            return True

        if "reblock" in fields[4]:
            match = project.rreblock_regex.search(rce.comment)
            if not match:
                logger.warning("Unmatched block/reblock in %s: %s", rce.project, message)
                return False
            # Treat reblock the same as a new block for simplicity
            rce.eventtype = EventType.block
            rce.title = match.group("item1")
            return True

        match = project.rblock_regex.search(rce.comment)
        if not match:
            logger.warning("Unmatched block type in %s: %s", rce.project, message)
            return False

        rce.eventtype = EventType.block
        rce.title = match.group("item1")
        # Assume default value of 24 hours in case the on-wiki message override
        # is missing expiry ($2) from its interface message
        item2 = match.groupdict().get("item2")
        rce.block_length = item2 if item2 is not None else "24 hours"
        # This project regex might not have a "comment" match group
        rce.comment = match.groupdict(default="").get("comment", "")
        return True

    # Sample from #en.wikipedia in August 2026:
    # [[Special:Log/protect]] protect  * Codename Noreste *  protected "[[Module:Message box/tmbox.css ‎[edit=sysop] (indefinite)‎[move=sysop] (indefinite)]]": Highly visible template
    #
    # Sample from #he.wikipedia in August 2026:
    # [[מיוחד:Log/protect]] protect  * Krinkle *  protected "[[ויקיפדיה:ארגז חול ‏[edit=autoconfirmed] (פגה ב־05:43, 25 באוגוסט 2026 (UTC))‏[move=autoconfirmed] (פגה ב־05:43, 25 באוגוסט 2026 (UTC))]]"
    def _parse_protect(self, rce, project, message):
        # TODO: Fix rce.title for protect and modifyprotect events
        # This wrongly contains "[move=sysop] (indefinite)" etc.
        # In mediawiki-core/LogFormatter.php#getIRCActionText it uses 'protectedarticle'
        # ('protected "[[$1]]"') with `$target . ' ' . $parameters['4::description']` as parameter.
        #
        # The good news is that mediawiki-core/ProtectLogFormatter.php prepends
        # the restrictions addendum with Language::getDirMark, i.e. 0x200e (U+200E LEFT-TO-RIGHT MARK)
        # or 0x200f (U+200F RIGHT-TO-LEFT MARK).
        #
        # See test_rcreader.py#test_protect_real_ltr_comment
        for regex, eventtype in (
            (project.rprotect_regex, EventType.protect),
            (project.rmodifyprotect_regex, EventType.modifyprotect),
            (project.runprotect_regex, EventType.unprotect),
        ):
            match = regex.search(rce.comment)
            if match:
                rce.eventtype = eventtype
                rce.title = project.translate_namespace(match.group("item1"))
                # This project regex might not have a "comment" match group
                rce.comment = match.groupdict(default="").get("comment", "")
                return True

        logger.warning("Unmatched protect type in %s: %s", rce.project, message)
        return False

    def _parse_delete(self, rce, project):
        for regex, eventtype in (
            (project.rdelete_regex, EventType.delete),
            (project.rrestore_regex, EventType.restore),
        ):
            match = regex.search(rce.comment)
            if match:
                rce.eventtype = eventtype
                rce.title = project.translate_namespace(match.group("item1"))
                # This project regex might not have a "comment" match group
                rce.comment = match.groupdict(default="").get("comment", "")
                return True

        # Ignore "delete/revision" (change visibility of revision) and any other deletion events
        return False

    def _parse_upload(self, rce, project):
        match = project.rupload_regex.search(rce.comment)
        if not match:
            # Ignore "upload/overwrite" (upload new version) and any other upload event
            return False
        rce.eventtype = EventType.upload
        rce.title = project.translate_namespace(match.group("item1"))
        # This project regex might not have a "comment" match group
        rce.comment = match.groupdict(default="").get("comment", "")
        return True

    def _parse_move(self, rce, project, message):
        rce.eventtype = EventType.move
        # First check "move over redirect", because it uses a longer format,
        # whereas plain "move" may match both.
        for regex in (project.rmoveredir_regex, project.rmove_regex):
            match = regex.search(rce.comment)
            if match:
                rce.title = project.translate_namespace(match.group("item1"))
                rce.moved_to = project.translate_namespace(match.group("item2"))
                # We use the unused block_length field to store our "moved from" URL
                rce.block_length = project.rooturl + "wiki/" + utils.wiki_encode(match.group("item1"))
                # This project regex might not have a "comment" match group
                rce.comment = match.groupdict(default="").get("comment", "")
                return True

        logger.warning("Unmatched move type in %s: %s", rce.project, message)
        return False
