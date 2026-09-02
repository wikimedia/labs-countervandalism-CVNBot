import enum
import logging
import os
import re
import sqlite3
import threading
import time

from . import utils
from .ircclient import Priority, SendType

logger = logging.getLogger("CVNBot.ListManager")


class UserType(enum.IntEnum):
    whitelisted = 0
    blacklisted = 1
    admin = 2
    anon = 3
    user = 4
    bot = 5
    greylisted = 6


class ListMatch:
    # Optimization: Faster access and less memory. https://stackoverflow.com/a/28059785/319266
    __slots__ = ("success", "matched_item", "matched_reason")

    def __init__(self, success=False, matched_item="", matched_reason=""):
        self.success = success
        self.matched_item = matched_item
        self.matched_reason = matched_reason

    def __bool__(self):
        return self.success


class ListManager:
    """Read and write the CVNBot database with user, page, and pattern information (Lists.sqlite)."""

    ipv4 = re.compile(
        r"(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}"
        r"(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)"
    )
    ipv6 = re.compile(r"(?:[0-9A-F]{1,4}:){7}[0-9A-F]{1,4}")

    # Support detection of temp accounts
    #
    # https://phabricator.wikimedia.org/T378530
    # https://gerrit.wikimedia.org/g/operations/mediawiki-config/+/d4afd6407a61cefb8a45817d5da9396f7e68178c/wmf-config/CommonSettings.php#4267
    # https://meta.wikimedia.org/w/api.php?format=jsonfm&formatversion=2&action=query&meta=siteinfo&siprop=autocreatetempuser
    temp_account = re.compile(r"^~2.+\b")

    rlist_cmd = re.compile(
        r"^(?P<cmd>add|del|show|test) +(?P<item>.+?)(?: +p=(?P<project>\S+?))?"
        r"(?: +x=(?P<len>\d{1,4}))?(?: +r=(?P<reason>.+?))?$",
        re.IGNORECASE,
    )

    # Default expiry for blacklist entries: 90 days (in seconds)
    BLACKLIST_DEFAULT_EXPIRY = 7776000

    def __init__(self, program):
        self.bot = program
        self.dbcon = None
        self.dbtoken = threading.RLock()
        self._gc_thread = None
        self._stopping = threading.Event()

    # -- Connection -------------------------------------------------------

    def init_db_connection(self, filename):
        already_exists = os.path.exists(filename)
        self.dbcon = sqlite3.connect(filename, check_same_thread=False, isolation_level=None)
        if already_exists:
            logger.info("Opening database from %s", filename)
        else:
            logger.info("Creating new database at %s", filename)
            # The file didn't exist before, so initialize tables
            with self.dbtoken:
                self.dbcon.execute(
                    "CREATE TABLE users ( name varchar(64), project varchar(32),"
                    " type integer(2), adder varchar(64), reason varchar(80),"
                    " expiry integer(32) )"
                )
                self.dbcon.execute(
                    "CREATE TABLE watchlist ( article varchar(64), project varchar(32),"
                    " adder varchar(64), reason varchar(80), expiry integer(32) )"
                )
                self.dbcon.execute(
                    "CREATE TABLE items ( item varchar(80), itemtype integer(2),"
                    " adder varchar(64), reason varchar(80), expiry integer(32) )"
                )

        # Start the expired item garbage collector
        self._gc_thread = threading.Thread(
            target=self._collect_garbage_thread,
            name="Tim",
            daemon=True
        )
        self._gc_thread.start()

    def close_db_connection(self):
        self._stopping.set()
        with self.dbtoken:
            if self.dbcon is not None:
                self.dbcon.close()
                self.dbcon = None

    def _collect_garbage_thread(self):
        # Run the expired item collector 10 seconds after startup, then every two hours
        #
        # We use `self._stopping` to cancel the timer and end this loop thread
        # when self.close_db_connection gets called on the main thread by Program.exit.
        GC_INITIAL_DELAY = 10
        GC_INTERVAL = 7200

        if self._stopping.wait(GC_INITIAL_DELAY):
            return

        while True:
            self.collect_garbage()
            if self._stopping.wait(GC_INTERVAL):
                return

    def collect_garbage(self):
        """Delete every entry whose expiry has passed."""
        total = 0
        now = utils.ticks_now()
        with self.dbtoken:
            cursor = self.dbcon.execute(
                "DELETE FROM users WHERE ((expiry < ?) AND (expiry != '0'))",
                (now,),
            )
            total += cursor.rowcount
            cursor = self.dbcon.execute(
                "DELETE FROM watchlist WHERE ((expiry < ?) AND (expiry != '0'))",
                (now,),
            )
            total += cursor.rowcount
            cursor = self.dbcon.execute(
                "DELETE FROM items WHERE ((expiry < ?) AND (expiry != '0'))",
                (now,),
            )
            total += cursor.rowcount
        logger.info("Tim threw away %d items", total)
        return total

    def _execute(self, sql, params=()):
        with self.dbtoken:
            return self.dbcon.execute(sql, params)

    def _query_one(self, sql, params=()):
        with self.dbtoken:
            return self.dbcon.execute(sql, params).fetchone()

    def _query_all(self, sql, params=()):
        with self.dbtoken:
            return self.dbcon.execute(sql, params).fetchall()

    # -- Helpers -----------------------------------------------

    @staticmethod
    def get_expiry_date(expiry):
        """
        Return an expiry timestamp in ticks

        See:
            handle_list_command: This is where the "x=" duration parameter
            from commands like "bl add" are converted from hours to seconds.

        Args:
            int expiry: number of seconds from now (0 for indefinite)
        """
        if expiry == 0:
            return 0
        return utils.ticks_now() + expiry * 10**7

    def parse_expiry_date(self, expiry):
        """
        Return a human-readable form for an expiry timestamp in ticks."""
        if expiry == 0:
            return self.bot.msgs["20006"]
        return utils.format_expiry(expiry)

    @staticmethod
    def ucfirst(text):
        return text[:1].upper() + text[1:]

    @staticmethod
    def friendly_project(project):
        if project == "":
            return "global"
        return project

    def friendly_list(self, list_type):
        return self.bot.msgs[str(17000 + int(list_type))]

    @staticmethod
    def is_anon(username):
        """Whether a username looks like an IP address or a temporary account."""

        # Optimization: Try temp_account first because IPs are no longer used on WMF wikis.
        return bool(
            ListManager.temp_account.search(username)
            or ListManager.ipv4.fullmatch(username)
            or ListManager.ipv6.fullmatch(username)
        )

    # -- Users ------------------------------------------------------------

    def add_user_to_list(self, name, project, utype, adder, reason, expiry):
        # Check if user is already on a list
        original_type = self.classify_editor(name, project)

        if original_type == utype:
            # Original type was same as new type; update reason and expiry
            self._execute(
                """
                UPDATE users SET adder = ?, reason = ?, expiry = ?
                WHERE name = ? AND project = ? AND type = ?
                """,
                (adder, reason, self.get_expiry_date(expiry), name, project, int(original_type)),
            )
            # Updated
            return self.bot.msgs.format(16104, self.show_user_on_list(name, project))

        if (
            # Unlisted
            original_type == UserType.anon or original_type == UserType.user
            # Allow temporary greylisting concurrent with any type
            # This takes precedence in show_user_on_list and classify_editor
            or utype == UserType.greylisted
            # Allow adding greylisted users to the blacklist (escalate without needing to wait)
            or (original_type == UserType.greylisted and utype == UserType.blacklisted)
            # Allow local admin/bot listing even if already on global whitelist (T327129)
            # This takes precedence in show_user_on_list and classify_editor
            or (original_type == UserType.whitelisted and utype in (UserType.admin, UserType.bot) and project != "")
        ):
            # User was originally unlisted or on a non-conflicting list
            self._execute(
                """
                INSERT INTO users (name, project, type, adder, reason, expiry)
                VALUES (?,?,?,?,?,?)
                """,
                (name, project, int(utype), adder, reason, self.get_expiry_date(expiry)),
            )
            # Added
            return self.bot.msgs.format(16103, self.show_user_on_list(name, project))

        # Cannot add, user was already on a conflicting list
        return self.bot.msgs.format(
            16102, name, self.friendly_list(original_type), self.friendly_list(utype)
        )

    def del_user_from_list(self, name, project, utype):
        original_type = self.classify_editor(name, project)

        if original_type != utype:
            return self.bot.msgs.format(
                16009, name, self.friendly_project(project), self.friendly_list(utype)
            )

        self._execute(
            "DELETE FROM users WHERE name = ? AND project = ? AND type = ?",
            (name, project, int(utype)),
        )

        # Deleted
        return self.bot.msgs.format(
            16101, name, self.friendly_project(project), self.friendly_list(original_type)
        )

    def show_user_on_list(self, username, project):
        now = utils.ticks_now()

        # First, check admin and bot list for this particular wiki
        if project != "":
            row = self._query_one(
                """
                SELECT type, adder, reason, expiry FROM users
                WHERE name = ? AND project = ?
                AND ((expiry > ?) OR (expiry = '0')) LIMIT 1
                """,
                (username, project, now),
            )
            if row is not None and row[0] in (int(UserType.admin), int(UserType.bot)):
                return self.bot.msgs.format(
                    16004, username, project, self.friendly_list(row[0]),
                    row[1], self.parse_expiry_date(row[3]), row[2],
                )

        # Is user globally greylisted? (This takes precedence)
        row = self._query_one(
            """
            SELECT reason, expiry FROM users
            WHERE name = ? AND project = '' AND type = ?
            AND ((expiry > ?) OR (expiry = '0')) LIMIT 1
            """,
            (username, int(UserType.greylisted), now),
        )
        if row is not None:
            return self.bot.msgs.format(
                16106, username, self.parse_expiry_date(row[1]), row[0]
            )

        # Next, check if user is globally whitelisted or blacklisted
        row = self._query_one(
            """
            SELECT type, adder, reason, expiry FROM users
            WHERE name = ? AND project = ?
            AND ((expiry > ?) OR (expiry = '0'))
            LIMIT 1
            """,
            (username, "", now),
        )
        if row is not None and row[0] in (int(UserType.blacklisted), int(UserType.whitelisted)):
            return self.bot.msgs.format(
                16004, username, self.friendly_project(""), self.friendly_list(row[0]),
                row[1], self.parse_expiry_date(row[3]), row[2],
            )

        # Finally, if we're still here, user is either user or anon
        if self.is_anon(username):
            return self.bot.msgs.format(16005, username)

        return self.bot.msgs.format(16006, username)

    def classify_editor(self, username, project):
        """
        Classify an editor on a wiki, or globally if project is empty.

        Order of precedence:
        1. Local admin or bot list
        2. Global greylist
        3. Global whitelist or blacklist
        4. Generic user or admin (is_anon)

        Args:
            username: Username or IP address
            project: Project name, or empty string to check global lists

        Returns:
            UserType
        """
        now = utils.ticks_now()

        # Optimization: Fetch in a batch instead of three separate inline queries
        rows = self._query_all(
            """
            SELECT project, type
            FROM users
            WHERE name = ?
              AND (project = ? OR project = '')
              AND (expiry > ? OR expiry = '0')
            """,
            (username, project, now),
        )

        local_utype = None
        global_greylisted = None
        global_utype = None

        for row_project, row_utype in rows:
            utype = UserType(row_utype)
            if row_project != "":
                local_utype = utype
            else:
                if utype == UserType.greylisted:
                    global_greylisted = utype
                else:
                    global_utype = utype

        # First, check if user is an admin or bot on this particular wiki
        if project != "" and local_utype == UserType.admin or local_utype == UserType.bot:
            return local_utype

        # Is user globally greylisted? (This takes precedence)
        if global_greylisted:
            return UserType.greylisted

        # Next, check if user is globally whitelisted or blacklisted
        if global_utype == UserType.whitelisted or global_utype == UserType.blacklisted:
            return global_utype

        # Finally, if we're still here, user is either user or anon
        if self.is_anon(username):
            return UserType.anon

        return UserType.user

    # -- Items (BNU, BNA, BES) --------------------------------------------

    def is_item_on_list(self, item, item_type):
        row = self._query_one(
            """
            SELECT item FROM items
            WHERE item = ? AND itemtype = ?
            AND ((expiry > ?) OR (expiry = '0'))
            LIMIT 1
            """,
            (item, item_type, utils.ticks_now()),
        )
        return row is not None

    def add_item_to_list(self, item, item_type, adder, reason, expiry):
        """Add an item to BNU (11), BNA (12), or BES (20).

        Args:
            string item
            int item_type
            string adder
            string reason
            int expiry: In seconds, 0 means indefinite.

        Returns:
            string: Response to IRC channel
        """
        try:
            utils.compile_dotnet(item, re.IGNORECASE).search('')
        except re.error as e:
            return "Error: Regex does not compile: " + str(e)

        if self.is_item_on_list(item, item_type):
            # Item is already on the same list, update reason and expiry
            self._execute(
                "UPDATE items SET adder = ?, reason = ?, expiry = ? WHERE item = ? AND itemtype = ?",
                (adder, reason, self.get_expiry_date(expiry), item, item_type),
            )
            # Updated
            return self.bot.msgs.format(16104, self.show_item_on_list(item, item_type))
        else:
            # Item is not on the list yet, can do simple insert
            self._execute(
                """
                INSERT INTO items (item, itemtype, adder, reason, expiry)
                VALUES(?, ?, ?, ?, ?)
                """,
                (item, item_type, adder, reason, self.get_expiry_date(expiry)),
            )
            # Added
            return self.bot.msgs.format(16103, self.show_item_on_list(item, item_type))

    def show_item_on_list(self, item, item_type):
        row = self._query_one(
            """
            SELECT adder, reason, expiry FROM items
            WHERE item = ? AND itemtype = ?
            AND ((expiry > ?) OR (expiry = '0'))
            LIMIT 1
            """,
            (item, item_type, utils.ticks_now()),
        )
        if row is not None:
            return self.bot.msgs.format(
                16007, item, self.friendly_list(item_type), row[0],
                self.parse_expiry_date(row[2]), row[1],
            )
        else:
            return self.bot.msgs.format(16008, item, self.friendly_list(item_type))

    def del_item_from_list(self, item, item_type):
        if self.is_item_on_list(item, item_type):
            self._execute(
                "DELETE FROM items WHERE item = ? AND itemtype = ?",
                (item, item_type)
            )
            return self.bot.msgs.format(16105, item, self.friendly_list(item_type))
        else:
            return self.bot.msgs.format(16008, item, self.friendly_list(item_type))

    def matches_list(self, title, list_type):
        rows = self._query_all(
            """
            SELECT item, reason FROM items
            WHERE itemtype = ?
            AND ((expiry > ?) OR (expiry = '0'))
            """,
            (list_type, utils.ticks_now()),
        )
        for item, reason in rows:
            try:
                pattern = utils.compile_dotnet(item, re.IGNORECASE)
                if re.search(pattern, title):
                    return ListMatch(True, item, reason)
            except re.error as e:
                logger.warning("Found invalid pattern: %s (%s)", item, e)
                self.bot.broadcast_dd("ERROR", "LMGNR_REGEX", str(e), title)

        # Obviously, did not match anything
        return ListMatch()

    def test_item_on_list(self, title, list_type):
        lm = self.matches_list(title, list_type)
        if lm.success:
            return self.bot.msgs.format(
                16200, title, lm.matched_item, self.friendly_list(list_type),
                lm.matched_reason,
            )
        else:
            return self.bot.msgs.format(16201, title, self.friendly_list(list_type))

    # -- Watchlist (CVP) --------------------------------------------------

    def _normalize_watchlist_item(self, item, project):
        # First, if this is not a Wiktionary, uppercase the first letter
        if not project.endswith("wiktionary"):
            item = self.ucfirst(item)

        # If this is a local watchlist, translate the namespace
        if project != "":
            item = self.bot.prjlist.translate_namespace(project, item)

        return item

    def add_page_to_watchlist(self, item, project, adder, reason, expiry):
        item = self._normalize_watchlist_item(item, project)

        # First, check if item is already on watchlist
        if self.is_watched_article(item, project).success:
            # Item is already on same watchlist, need to update
            self._execute(
                "UPDATE watchlist SET adder = ?, reason = ?, expiry = ? WHERE article = ? AND project = ?",
                (adder, reason, self.get_expiry_date(expiry), item, project),
            )
            return self.bot.msgs.format(
                16104, self.show_page_on_watchlist(item, project)
            )

        # Item is not on the watchlist yet, can do simple insert
        self._execute(
            "INSERT INTO watchlist (article, project, adder, reason, expiry) VALUES(?, ?, ?, ?, ?)",
            (item, project, adder, reason, self.get_expiry_date(expiry)),
        )
        return self.bot.msgs.format(16103, self.show_page_on_watchlist(item, project))

    def show_page_on_watchlist(self, item, project):
        item = self._normalize_watchlist_item(item, project)

        row = self._query_one(
            """
            SELECT adder, reason, expiry FROM watchlist
            WHERE article = ? AND project = ? AND ((expiry > ?) OR (expiry = '0'))
            LIMIT 1
            """,
            (item, project, utils.ticks_now()),
        )
        if row is not None:
            return self.bot.msgs.format(
                16004, item, self.friendly_project(project), self.friendly_list(10),
                row[0], self.parse_expiry_date(row[2]), row[1],
            )

        return self.bot.msgs.format(
            16009, item, self.friendly_project(project), self.friendly_list(10)
        )

    def del_page_from_watchlist(self, item, project):
        item = self._normalize_watchlist_item(item, project)

        if self.is_watched_article(item, project).success:
            self._execute(
                "DELETE FROM watchlist WHERE article = ? AND project = ?",
                (item, project),
            )
            return self.bot.msgs.format(
                16101, item, self.friendly_project(project), self.friendly_list(10)
            )
        else:
            return self.bot.msgs.format(
                16009, item, self.friendly_project(project), self.friendly_list(10)
            )

    def is_watched_article(self, title, project):
        row = self._query_one(
            """
            SELECT reason FROM watchlist
            WHERE article = ? AND (project = ? OR project = '')
            AND ((expiry > ?) OR (expiry = '0'))
            """,
            (title, project, utils.ticks_now()),
        )
        if row is not None:
            return ListMatch(True, "", row[0])
        else:
            # Did not match anything
            return ListMatch(False, "", "")

    # -- Commands ---------------------------------------------------------

    def handle_list_command(self, listtype, user, cmd_params):
        """
        Parse cmd_params and execute a given list add/del/show/test command.

        Args:
            int listtype: a number from UserType,
                or 10=Watchlist,
                or 11=BNU, 12=BNA and 20=BES
            string user: Nickname of user who gave this command
            string cmd_params: Rest of IRC command, given like so:
                - add Tangotango x=96 r=Terrible vandal
                - add Tangotango test account x=89
                - del Tangotango r=No longer needed (r is ignored and optional, but accepted anyway)

        Returns:
            string: Response to IRC channel
        """
        match = self.rlist_cmd.match(cmd_params)
        if not match:
            return self.bot.msgs["20000"]

        try:
            cmd = match.group("cmd").lower()
            item = match.group("item").strip()

            if listtype == 1:
                length = self.BLACKLIST_DEFAULT_EXPIRY
            else:
                # indefinite
                length = 0
            if match.group("len") is not None:
                # Convert input, in hours, to seconds
                length = int(match.group("len")) * 3600

            reason = "No reason given"
            if match.group("reason") is not None:
                reason = match.group("reason")

            project = ""
            if match.group("project") is not None:
                project = match.group("project")
                if project not in self.bot.prjlist:
                    return "Project " + project + " is unknown"

            if cmd == "add":
                return self._command_add(listtype, item, project, user, reason, length)
            if cmd == "del":
                return self._command_del(listtype, item, project, user, reason, length)
            if cmd == "show":
                return self._command_show(listtype, item, project)
            if cmd == "test":
                if listtype in (11, 12, 20):
                    return self.test_item_on_list(item, listtype)
                else:
                    return self.bot.msgs["20002"]

            logger.error("Unhandled command %s", cmd, stack_info=True)
            return "Error: Unhandled command"
        except Exception as e:
            logger.exception("Error while handling list command")
            return "Sorry, an error occured while handling the list command: " + str(e)

    def _command_add(self, listtype, item, project, user, reason, length):
        # Whitelist
        if listtype == 0:
            self.bot.broadcast("WL", "ADD", item, length, reason, user)
            return self.add_user_to_list(
                item, "", UserType.whitelisted, user, reason, length
            )
        # Blacklist
        if listtype == 1:
            self.bot.broadcast("BL", "ADD", item, length, reason, user)
            return self.add_user_to_list(
                item, "", UserType.blacklisted, user, reason, length
            )
        # Greylist
        if listtype == 6:
            return "You cannot directly add users to the greylist"
        # Adminlist
        if listtype == 2:
            if project == "":
                return self.bot.msgs["20001"]
            return self.add_user_to_list(
                item, project, UserType.admin, user, reason, length
            )
        # Botlist
        if listtype == 5:
            if project == "":
                return self.bot.msgs["20001"]
            return self.add_user_to_list(
                item, project, UserType.bot, user, reason, length
            )
        # Watchlist
        if listtype == 10:
            if project == "":
                self.bot.broadcast("CVP", "ADD", item, length, reason, user)
            return self.add_page_to_watchlist(item, project, user, reason, length)
        # BNU, BNA, BES
        if listtype in (11, 12, 20):
            self.bot.broadcast(
                {11: "BNU", 12: "BNA", 20: "BES"}[listtype],
                "ADD", item, length, reason, user,
            )
            return self.add_item_to_list(item, listtype, user, reason, length)

        return "Error: Unhandled add command for listtype"

    def _command_del(self, listtype, item, project, user, reason, length):
        # Whitelist
        if listtype == 0:
            self.bot.broadcast("WL", "DEL", item, 0, reason, user)
            return self.del_user_from_list(item, "", UserType.whitelisted)
        # Blacklist
        if listtype == 1:
            self.bot.broadcast("BL", "DEL", item, 0, reason, user)
            return self.del_user_from_list(item, "", UserType.blacklisted)
        # Greylist
        if listtype == 6:
            self.bot.broadcast("GL", "DEL", item, 0, reason, user)
            return self.del_user_from_list(item, "", UserType.greylisted)
        # Adminlist
        if listtype == 2:
            if project == "":
                return self.bot.msgs["20001"]
            return self.del_user_from_list(item, project, UserType.admin)
        # Botlist
        if listtype == 5:
            if project == "":
                return self.bot.msgs["20001"]
            return self.del_user_from_list(item, project, UserType.bot)
        # Watchlist
        if listtype == 10:
            if project == "":
                self.bot.broadcast("CVP", "DEL", item, length, reason, user)
            return self.del_page_from_watchlist(item, project)
        # BNU, BNA, BES
        if listtype in (11, 12, 20):
            self.bot.broadcast(
                {11: "BNU", 12: "BNA", 20: "BES"}[listtype],
                "DEL", item, 0, reason, user,
            )
            return self.del_item_from_list(item, listtype)

        return "Error: Unhandled del command for listtype"

    def _command_show(self, listtype, item, project):
        # Whitelist, blacklist, greylist
        if listtype in (0, 1, 6):
            return self.show_user_on_list(item, "")
        # Adminlist, botlist
        if listtype in (2, 5):
            if project == "":
                return self.bot.msgs["20001"]
            return self.show_user_on_list(item, project)
        # Watchlist
        if listtype == 10:
            return self.show_page_on_watchlist(item, project)
        # BNU, BNA, BES
        if listtype in (11, 12, 20):
            return self.show_item_on_list(item, listtype)

        return "Error: Unhandled show command for listtype"

    def global_intel(self, username):
        """Return user information by looking in all lists."""
        if username == "":
            return self.bot.msgs["20003"]

        try:
            rows = self._query_all(
                """
                SELECT project, type, adder, reason, expiry FROM users
                WHERE name = ? AND ((expiry > ?) OR (expiry = '0'))
                """,
                (username, utils.ticks_now()),
            )
            results = [
                self.bot.msgs.format(
                    16002, self.friendly_project(row[0]), self.friendly_list(row[1]),
                    row[2], self.parse_expiry_date(row[4]), row[3],
                )
                for row in rows
            ]

            if not results:
                return self.bot.msgs.format(16001, username)
            else:
                return self.bot.msgs.format(16000, username, " and ".join(results))
        except Exception as e:
            logger.exception("global_intel failed")
            return self.bot.msgs.format(16003, str(e))

    # -- Bulk operations --------------------------------------------------

    def fetch_group_and_add_to_list(self, project_name, group):
        """
        Download a list of admins/bots from the wiki and add them to the database.

        This should run in a separate thread.
        """
        if group == "sysop":
            group_type = UserType.admin
        elif group == "bot":
            group_type = UserType.bot
        else:
            raise Exception("Undefined group: " + group)

        logger.info("Fetching list of %s users from %s", group, project_name)

        if project_name not in self.bot.prjlist:
            raise Exception("Undefined project: " + project_name)
        project = self.bot.prjlist[project_name]

        resp = None
        try:
            resp = utils.get_raw_document(
                project.rooturl
                + "w/api.php?format=xml&action=query&list=allusers&augroup="
                + group
                + "&aulimit=max"
            )
            allusers = utils.parse_xml(resp, "allusers")
            if allusers is None:
                raise Exception("Missing allusers element in API response from " + project_name)
            total = 0
            for element in allusers:
                self.add_user_to_list(
                    element.get("name"),
                    project_name,
                    group_type,
                    "CVNBot",
                    "Auto-download from wiki",
                    0,
                )
                total += 1

            logger.info("Added %d %s users from %s", total, group, project_name)
        except Exception:
            if resp is not None:
                logger.info("Preview of failed user list fetch: %s", resp[:100])
            logger.exception("Unable to get list")

    def _start_group_fetch_thread(self, project_name, group, label):
        if project_name not in self.bot.prjlist:
            return "Project is unknown: " + project_name

        threading.Thread(
            target=self.fetch_group_and_add_to_list,
            args=(project_name, group),
            name="Get{0}@{1}".format(group, project_name),
            daemon=True,
        ).start()
        return "Started {0} userlist fetcher in the background".format(label)

    def config_get_admins(self, cmd_params):
        """
        Args:
            string cmd_params: Project name

        Returns:
            string: Response to IRC channel
        """
        return self._start_group_fetch_thread(cmd_params, "sysop", "admin")

    def config_get_bots(self, cmd_params):
        """
        Args:
            string cmd_params: Project name

        Returns:
            string: Response to IRC channel
        """
        return self._start_group_fetch_thread(cmd_params, "bot", "bot")

    def batch_get_all_admins_and_bots(self, origin_channel):
        """
        Download admin and bot lists for all monitored projects.

        This should run in a separate thread.
        """
        self.bot.send_message(
            SendType.MESSAGE,
            origin_channel,
            "Request to get admins and bots for all {0} wikis accepted.".format(
                len(self.bot.prjlist)
            ),
            Priority.HIGH,
        )

        for project_name in self.bot.prjlist.keys():
            for group in ("sysop", "bot"):
                self.fetch_group_and_add_to_list(project_name, group)
                time.sleep(0.5)

        self.bot.send_message(
            SendType.MESSAGE,
            origin_channel,
            "Done fetching all admins and bots. Phew, I'm tired :P",
            Priority.HIGH,
        )

    def purge_wiki_data(self, project_name):
        """
        Purge all local user and watchlist information for a project.

        This is irreversible and MUST NOT be called from
        ProjectList.delete_project or the "drop" command.
        It is harmless to keep around, and allows projects to be removed
        and re-added for whatever reason without losing this data.

        This function might called when a project is no longer
        monitored and thus may not rely on ProjectList containing
        information about the project.
        """
        if "'" in project_name:
            return "Sorry, invalid wiki name."

        total = 0
        with self.dbtoken:
            cursor = self.dbcon.execute(
                "DELETE FROM users WHERE project = ?",
                (project_name,)
            )
            total += cursor.rowcount
            cursor = self.dbcon.execute(
                "DELETE FROM watchlist WHERE project = ?",
                (project_name,)
            )
            total += cursor.rowcount

        result = "Threw away {0} items that were related to {1}".format(
            total, project_name
        )
        logger.info(result)
        return result
