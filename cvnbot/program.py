import datetime
import logging
import os
import re
import subprocess
import sys
import threading
import time

from . import config as config_module
from . import utils
from .ircclient import IrcClient, IrcConnectionError, Priority, SendType
from .listmanager import ListManager, UserType
from .log import setup_logger
from .messages import Messages
from .projectlist import ProjectList
from .rcreader import EventType, RCReader

logger = logging.getLogger("CVNBot.Program")


class CVNBot:
    """The CVNBot program."""

    VERSION = "5.0.0-alpha.6"

    # Maximum length of a single line sent to IRC
    CHUNK_SIZE = 400

    broadcast_msg = re.compile(
        r"""\*\x02B/1.1\x02\*(?P<list>.+?)\*(?P<action>.+?)\*\x03
            07\x02(?P<item>.+?)\x02\x03\*\x03
            13(?P<len>\d+?)\x03\*\x03
            09\x02(?P<reason>.*?)\x02\x03\*\x03
            11\x02(?P<adder>.*?)\x03\x02\*""",
        re.VERBOSE
    )

    def __init__(self, config_filename):
        self.irc = IrcClient("Main",
                             auto_reconnect=True,
                             auto_rejoin=True,
                             )
        self.msgs = Messages()
        self.rcreader = RCReader(self)
        self.prjlist = ProjectList(self)
        self.listman = ListManager(self)
        self.config = config_module.Config()

        self.bot_cmd = None
        self.config_filename = config_filename

    def run(self):
        """Start the bot. Does not return until the program is ready to exit."""

        threading.current_thread().name = "Main"

        config_module.apply_from_file(self.config, self.config_filename)

        setup_logger(self.config)
        logger.info("Loaded main configuration from %s", self.config_filename)

        # Start global catcher right after setting up syslog
        # Any failure after this will auto restart.
        # We have a 10s delay to avoid hot bootloops.
        # If you see an error locally, Ctrl-C within 10s to avoid replacement with
        # a new background process.
        # If you're moving this, we must not move this below init_db_connection,
        # because that's when we first start other threads, and failures there
        # should always stop the main thread.
        sys.excepthook = self._on_application_unhandled_error
        threading.excepthook = lambda args: self._on_application_unhandled_error(
            args.exc_type, args.exc_value, args.exc_traceback
        )

        self.bot_cmd = re.compile(
            "^"
            + re.escape(self.config.bot_nick)
            + r" (\s*(?P<command>\S*))(\s(?P<params>.*))?$",
            re.IGNORECASE,
        )

        if not self.msgs.read(self.config.messages_file):
            self.exit()

        # Read projects
        self.prjlist.fn_projects_xml = self.config.projects_file
        self.prjlist.load_from_file()

        self.listman.init_db_connection(self.config.lists_file)

        # Set up IRC client
        self.irc.on_channel_message = self._on_channel_message
        self.irc.on_channel_notice = self._on_channel_notice
        self.irc.on_connected = self._on_connected
        self.irc.on_error = self._on_error
        self.irc.on_connection_error = self._on_connection_error

        try:
            self.irc.connect(self.config.irc_server_name, self.config.irc_server_port)
        except IrcConnectionError as e:
            logger.critical("Could not connect: %s", e)
            self.exit()

        try:
            self.irc.login(
                self.config.bot_nick,
                self.config.bot_real_name + " " + self.VERSION,
                4,
                self.config.bot_nick,
                self.config.bot_pass,
            )

            for label, channel in (
                ("feed", self.config.feed_channel),
                ("control", self.config.control_channel),
                ("broadcast", self.config.broadcast_channel),
            ):
                if channel != "None":
                    logger.info("Joining %s channel: %s", label, channel)
                    self.irc.rfc_join(channel)

            # Now connect the RCReader to channels
            threading.Thread(
                target=self.rcreader.initiate_connection, name="RCReader", daemon=True
            ).start()

            # Blocking read and dispatch messages (including reconnects) until the session is over
            self.irc.listen()

            # When listen() returns, our IRC session is over, let's disconnect
            self.irc.disconnect()
        except Exception:
            # This should not happen, but just in case, we handle it nicely
            logger.critical("Error occurred in Main IRC try clause!", exc_info=True)
            self.exit()

    def _on_application_unhandled_error(self, exc_type, exc_value, exc_traceback):
        """Catch all unhandled exceptions, in any thread."""
        if not issubclass(exc_type, Exception):
            # Ignore KeyboardInterrupt, which should quit, not restart
            return

        try:
            logger.error(
                "Unhandled exception in global catcher. Restarting in 10 seconds...",
                exc_info=(exc_type, exc_value, exc_traceback),
            )
        except Exception:
            # Logging failed
            print("Unhandled exception, and logging failed: " + str(exc_value))
            sys.exit(24)

        try:
            self.quit_irc("Unhandled exception in global catcher. Restarting...")
            time.sleep(10)
            self.restart()
        except Exception:
            print("Restart failed.")
            sys.exit(24)

    # -- IRC events -------------------------------------------------------

    def _on_connected(self, client):
        logger.info("Connected to %s", self.config.irc_server_name)

    def _on_connection_error(self, client):
        logger.error("Program lost connection to IRC, restarting...")
        self.restart()

    def _on_error(self, client, error_message):
        if "Excess Flood" in error_message:  # Do not localize
            # Oops, we were flooded off
            logger.warning("Received excess flood error, restarting...")
            self.restart()

    def _on_channel_notice(self, client, event):
        """Detect and handle incoming broadcast messages."""
        if event.channel != self.config.broadcast_channel:
            # Just in case
            return
        if not event.message:
            # Prevent empty messages from crashing the bot
            return

        match = self.broadcast_msg.search(event.message)
        if not match:
            return

        try:
            self._handle_broadcast(
                match.group("action"),
                match.group("list"),
                match.group("item"),
                int(match.group("len")),
                match.group("reason"),
                match.group("adder"),
            )
        except Exception as e:
            logger.exception("Failed to handle broadcast command")
            self.broadcast_dd("ERROR", "BC_ERROR", str(e), event.message)

    BROADCAST_USER_LISTS = {
        "WL": UserType.whitelisted,
        "BL": UserType.blacklisted,
        "GL": UserType.greylisted,
    }
    BROADCAST_ITEM_LISTS = {
        "BNU": 11,
        "BNA": 12,
        "BES": 20
    }

    def _handle_broadcast(self, action, list_name, item, length, reason, adder):
        # Similar to ListManager.handle_list_command
        if action == "ADD":
            if list_name in self.BROADCAST_USER_LISTS:
                self.listman.add_user_to_list(
                    item, "", self.BROADCAST_USER_LISTS[list_name], adder, reason, length
                )
            elif list_name in self.BROADCAST_ITEM_LISTS:
                self.listman.add_item_to_list(
                    item, self.BROADCAST_ITEM_LISTS[list_name], adder, reason, length
                )
            elif list_name == "CVP":
                self.listman.add_page_to_watchlist(item, "", adder, reason, length)
            # else: Gracefully ignore unknown message types

        elif action == "DEL":
            if list_name in self.BROADCAST_USER_LISTS:
                self.listman.del_user_from_list(
                    item, "", self.BROADCAST_USER_LISTS[list_name]
                )
            elif list_name in self.BROADCAST_ITEM_LISTS:
                self.listman.del_item_from_list(item, self.BROADCAST_ITEM_LISTS[list_name])
            elif list_name == "CVP":
                self.listman.del_page_from_watchlist(item, "")
            # else: Gracefully ignore unknown message types

        elif action == "FIND":
            if list_name == "BLEEP" and item in self.prjlist:
                self.send_message(
                    SendType.ACTION,
                    reason,
                    "has {0}, {1} :D".format(item, adder),
                    Priority.HIGH,
                )
        elif action == "COUNT":
            if list_name == "BLEEP":
                self.send_message(
                    SendType.ACTION,
                    reason,
                    "owns {0} wikis; version is {1}".format(len(self.prjlist), self.VERSION),
                    Priority.HIGH,
                )
        elif action == "CONFIG":
            if list_name == "BLEEP":
                self.bot_config_msg(reason)
        # Gracefully ignore unknown action types

    def _has_privileges(self, minimum, event):
        user = self.irc.get_channel_user(event.channel, event.nick)
        if minimum == "@":
            if user is None or not user.is_op:
                self.send_message(SendType.NOTICE, event.nick, self.msgs["00122"])
                return False
            return True
        if minimum == "+":
            if user is None or not (user.is_op or user.is_voice):
                self.send_message(SendType.NOTICE, event.nick, self.msgs["00120"])
                return False
            return True
        return False

    # Commands that map directly onto a list type for ListManager
    LIST_COMMANDS = {
        "wl": 0,
        "bl": 1,
        "al": 2,
        "bot": 5,
        "bots": 5,
        "gl": 6,
        "cvp": 10,
        "bnu": 11,
        "bna": 12,
        "bes": 20,
    }

    # The bot commands that only ops may use
    OP_COMMANDS = frozenset([
        "quit",
        "restart",
        "msgs",
        "reload",
        "load",
        "drop",
        "batchgetusers",
        "getadmins",
        "getbots",
        "purge",
        "batchreload",
    ])

    def _on_channel_message(self, client, event, recv_ts_ns=None):
        # Prevent empty messages from crashing the bot
        if not event.message:
            return

        match = self.bot_cmd.match(event.message)
        if not match:
            return

        # Have to be voiced to issue any commands
        if not self._has_privileges("+", event):
            return

        command = match.group("command")
        extra_params = (match.group("params") or "").strip()
        cmd_params = extra_params.split(" ")
        channel = event.channel

        if command in self.OP_COMMANDS and not self._has_privileges("@", event):
            return

        if command == "quit":
            logger.info("%s ordered a quit", event.nick)
            self.quit_irc(self.config.part_msg)
            self.exit()

        elif command == "restart":
            logger.info("%s ordered a restart", event.nick)
            self.quit_irc("Rebooting by order of {0} ...".format(event.nick))
            self.restart()

        elif command == "status":
            ago_handled = (datetime.datetime.now(tz=datetime.timezone.utc) - self.rcreader.last_handled).total_seconds()
            if self.rcreader.last_recv_ns:
                last_recv_dt = datetime.datetime.fromtimestamp(
                    self.rcreader.last_recv_ns / 1_000_000_000,
                    tz=datetime.timezone.utc
                )
                ago_recv = (datetime.datetime.now(tz=datetime.timezone.utc) - last_recv_dt).total_seconds()
                delay = round(ago_recv - ago_handled, 3)
                msg = ("Last message on RCReader handled {0} seconds ago, received {1} seconds ago. "
                       "The processing delay was {2} seconds.").format(ago_handled, ago_recv, delay)
            else:
                msg = "Last message on RCReader handled {0} seconds ago".format(ago_handled)

            self.send_message(
                SendType.MESSAGE,
                channel,
                msg,
                Priority.HIGH,
            )

        elif command == "help":
            self.send_message(SendType.MESSAGE, channel, self.msgs["20005"], Priority.HIGH)

        elif command in ("version", "settings", "config"):
            self.bot_config_msg(channel)
            if cmd_params[0] == "all":
                self.broadcast("BLEEP", "CONFIG", "BLEEP", 0, channel, event.nick)

        elif command == "msgs":
            # Reload messages file
            self.msgs.read(self.config.messages_file)
            self.send_message(
                SendType.MESSAGE, channel, "Re-read messages from %s" % self.config.messages_file, Priority.HIGH
            )

        elif command == "reload":
            # Re-download project details from the wiki
            if cmd_params[0] not in self.prjlist:
                self.send_message(
                    SendType.MESSAGE,
                    channel,
                    "Project {0} is not loaded".format(cmd_params[0]),
                    Priority.HIGH,
                )
                return
            try:
                self.prjlist[cmd_params[0]].retrieve_wiki_details()
                self.send_message(
                    SendType.MESSAGE,
                    channel,
                    "Reloaded project " + cmd_params[0],
                    Priority.HIGH,
                )
            except Exception as e:
                self.send_message(
                    SendType.MESSAGE, channel, "Unable to reload: " + str(e), Priority.HIGH
                )
                logger.exception("Reload project failed")

        elif command == "load":
            try:
                if len(cmd_params) == 2:
                    self.prjlist.add_new_project(cmd_params[0], cmd_params[1])
                else:
                    self.prjlist.add_new_project(cmd_params[0], "")

                self.send_message(
                    SendType.MESSAGE,
                    channel,
                    "Loaded new project " + cmd_params[0],
                    Priority.HIGH,
                )
                # Automatically get admins and bots
                self.send_message(
                    SendType.MESSAGE,
                    channel,
                    self.listman.config_get_admins(cmd_params[0]),
                    Priority.HIGH,
                )
                self.send_message(
                    SendType.MESSAGE,
                    channel,
                    self.listman.config_get_bots(cmd_params[0]),
                    Priority.HIGH,
                )
            except Exception as e:
                self.send_message(
                    SendType.MESSAGE,
                    channel,
                    "Unable to add project: " + str(e),
                    Priority.HIGH,
                )
                logger.exception("Add project failed")

        elif command == "bleep":
            if cmd_params[0]:
                try:
                    if cmd_params[0] in self.prjlist:
                        self.send_message(
                            SendType.ACTION,
                            channel,
                            "has {0}, {1} :D".format(cmd_params[0], event.nick),
                            Priority.HIGH,
                        )
                    else:
                        self.broadcast(
                            "BLEEP", "FIND", cmd_params[0], 0, channel, event.nick
                        )
                        self.send_message(
                            SendType.MESSAGE,
                            channel,
                            "Bleeped. Please wait for a reply.",
                            Priority.HIGH,
                        )
                except Exception as e:
                    self.send_message(
                        SendType.MESSAGE, channel, "Unable to bleep: " + str(e), Priority.HIGH
                    )

        elif command == "count":
            self.broadcast("BLEEP", "COUNT", "BLEEP", 0, channel, event.nick)
            self.send_message(
                SendType.ACTION,
                channel,
                "owns {0} wikis; version is {1}".format(len(self.prjlist), self.VERSION),
                Priority.HIGH,
            )

        elif command == "drop":
            try:
                self.prjlist.delete_project(cmd_params[0])
                self.send_message(
                    SendType.MESSAGE,
                    channel,
                    "Deleted project " + cmd_params[0],
                    Priority.HIGH,
                )
            except Exception as e:
                self.send_message(
                    SendType.MESSAGE,
                    channel,
                    "Unable to delete project: " + str(e),
                    Priority.HIGH,
                )
                logger.exception("Delete project failed")

        elif command == "list":
            result = "Currently monitoring: "
            for project_name in self.prjlist.keys():
                result += project_name + " "
            result += "(Total: {0} wikis)".format(len(self.prjlist))
            self.send_message_multi(SendType.MESSAGE, channel, result, Priority.HIGH)

        elif command == "batchgetusers":
            threading.Thread(
                target=self.listman.batch_get_all_admins_and_bots,
                args=(channel,),
                name="GetAllUsers",
                daemon=True,
            ).start()

        elif command in self.LIST_COMMANDS:
            self.send_message(
                SendType.MESSAGE,
                channel,
                self.listman.handle_list_command(
                    self.LIST_COMMANDS[command], event.nick, extra_params
                ),
                Priority.HIGH,
            )

        elif command == "getadmins":
            self.send_message(
                SendType.MESSAGE,
                channel,
                self.listman.config_get_admins(extra_params),
                Priority.HIGH,
            )

        elif command == "getbots":
            self.send_message(
                SendType.MESSAGE,
                channel,
                self.listman.config_get_bots(extra_params),
                Priority.HIGH,
            )

        elif command == "intel":
            self.send_message_multi(
                SendType.MESSAGE,
                channel,
                self.listman.global_intel(extra_params),
                Priority.HIGH,
            )

        elif command == "purge":
            self.send_message(
                SendType.MESSAGE,
                channel,
                self.listman.purge_wiki_data(extra_params),
                Priority.HIGH,
            )

        elif command == "batchreload":
            threading.Thread(
                target=self.prjlist.reload_all_wikis,
                args=(channel,),
                name="ReloadAll",
                daemon=True
            ).start()

    # -- Sending ----------------------------------------------------------

    def send_message(self, send_type, destination, message, priority=Priority.LOW):
        """Route all send_message call through this to use the send queue."""
        self.irc.send_message(send_type, destination, message, priority)

    def send_message_multi(self, send_type, destination, message, priority=Priority.LOW):
        """Split a message by line breaks and length if too long for send_message."""
        if message == "":
            return

        # Allow multiline
        for line in message.split("\n"):
            # Split messages that are too long
            for chunk in utils.string_split(line, self.CHUNK_SIZE):
                # Ignore lines that contain only "" or "
                if chunk.strip() not in ('""', '"'):
                    self.send_message(send_type, destination, chunk, priority)

    def broadcast(self, list_name, action, item, expiry, reason, adder):
        if self.config.broadcast_channel == "None":
            return

        message = (
            "*%BB/1.1%B*{0}*{1}*%C07%B{2}%B%C*%C13{3}%C*%C09%B{4}%B%C*%C11%B{5}%C%B*"
        ).format(list_name, action, item, expiry, reason, adder)
        self.send_message(
            SendType.NOTICE,
            self.config.broadcast_channel,
            message.replace("%C", "\x03").replace("%B", "\x02"),
            Priority.HIGH,
        )

    def broadcast_dd(self, kind, codename, message, ingredients):
        """Broadcast a message for Distributed Debugging."""
        if self.config.broadcast_channel == "None":
            return

        raw = "*%BDD/1.0%B*{0}*{1}*%C07%B{2}%B%C*%C13{3}%C*".format(
            kind, codename, message, ingredients
        )
        self.send_message(
            SendType.NOTICE,
            self.config.broadcast_channel,
            raw.replace("%C", "\x03").replace("%B", "\x02"),
            Priority.HIGH,
        )
        logger.info(
            "Broadcasted DD: %s,%s,%s,%s", kind, codename, message, ingredients
        )

    def bot_config_msg(self, dest_channel):
        """
        Report the bot version and its feed settings to a channel.

        The only operational information provided here is the bot version.
        The config values printed to IRC are limited to feed settings that users
        can observe.
        """
        settings = ", ".join(
            "{0}: {1}".format(name, value)
            for name, value in config_module.public_config(self.config).items()
        )
        message = "runs CVNBot {0} in {1}; settings: {2}".format(
            self.VERSION, self.config.feed_channel, settings
        )

        self.send_message_multi(SendType.ACTION, dest_channel, message, Priority.HIGH)

    # -- Reacting to the RC feed ------------------------------------------

    def add_to_greylist(self, user_offset, username, reason):
        """Shorthand greylisting function for use by react_to_rc_event."""

        # Only do greylisting if they are currently blacklisted, reguser, anon, or already greylisted.
        # In other words, never greylist trusted users (bot, admin, whitelist).
        if user_offset in (
            UserType.blacklisted,
            UserType.user,
            UserType.anon,
            UserType.greylisted,
        ):
            self.listman.add_user_to_list(
                username, "", UserType.greylisted, "CVNBot", reason, 1
            )
            # Greylist for 900 seconds = 15 mins
            # TODO: Why is the broadcasted expiry different from local expiry (line above)
            self.broadcast("GL", "ADD", username, 900, reason, "CVNBot")

    def _feed_filter_for_event(self, r):
        config = self.config
        if r.minor:
            return config.feed_filter_event_minor_edit
        if r.eventtype == EventType.edit and not r.newpage:
            return config.feed_filter_event_edit
        if r.eventtype == EventType.edit and r.newpage:
            return config.feed_filter_event_newpage
        if r.eventtype == EventType.move:
            return config.feed_filter_event_move
        if r.eventtype == EventType.delete:
            return config.feed_filter_event_delete
        if r.eventtype in (EventType.block, EventType.unblock):
            return config.feed_filter_event_block
        if r.eventtype in (EventType.newuser, EventType.newuser2, EventType.autocreate):
            return config.feed_filter_event_newuser
        if r.eventtype == EventType.upload:
            return config.feed_filter_event_upload
        if r.eventtype in (
            EventType.protect,
            EventType.unprotect,
            EventType.modifyprotect,
        ):
            return config.feed_filter_event_protect
        return 1

    def react_to_rc_event(self, r):
        """React to an RCEvent. Remember this runs in the RCReader thread!"""

        # Apply feed filters to this event type
        #
        # Check event type before classify_editor(), so that level 4 ("ignore")
        # is effective in saving a ListManager database query for user type.
        feed_filter_this_event = self._feed_filter_for_event(r)
        if feed_filter_this_event == 4:
            # Ignore
            return

        if self.config.is_cubbie and r.eventtype != EventType.upload:
            # Ignore all non-uploads
            return

        if r.botflag and self.config.feed_filter_users_bot == 4:
            return

        # FIXME: If the current event is by a bot user and it blocks (eg. bot admin) and
        # bot edits are ignored (default) then the user will not be blacklisted
        # TODO: Add new userOffset for botadmin?

        # Apply feed filters to this user type
        user_offset = self.listman.classify_editor(r.user, r.project)
        feed_filter_this_user = 1
        if user_offset == UserType.anon:
            feed_filter_this_user = self.config.feed_filter_users_anon
        elif user_offset == UserType.user:
            feed_filter_this_user = self.config.feed_filter_users_reg
        elif user_offset == UserType.bot:
            feed_filter_this_user = self.config.feed_filter_users_bot
        if feed_filter_this_user == 4:
            # Ignore
            return

        project = self.prjlist[r.project]

        attribs = {}
        handlers = {
            EventType.edit: self._react_edit,
            EventType.newuser: self._react_newuser,
            EventType.newuser2: self._react_newuser2,
            EventType.block: self._react_block,
            EventType.unblock: self._react_unblock,
            EventType.protect: self._react_protect,
            EventType.unprotect: self._react_protect,
            EventType.modifyprotect: self._react_protect,
            EventType.delete: self._react_delete,
            EventType.upload: self._react_upload,
            EventType.move: self._react_move,
        }
        handler = handlers.get(r.eventtype)
        if handler is None:
            message = ""
        else:
            message = handler(
                r, project, attribs, user_offset, feed_filter_this_event,
                feed_filter_this_user,
            )
            if message is None:
                # Ignore
                return

        if feed_filter_this_event == 3 or feed_filter_this_user == 3:
            # Autolistings have been done throughout react_to_rc_event().
            # If this event triggered hardhide, discard the message
            # Ignore
            return

        self.send_message_multi(
            SendType.MESSAGE, self.config.feed_channel, message, Priority.LOW
        )

    # This handles new page creations and page edits
    def _react_edit(
        self, r, project, attribs, user_offset, feed_filter_this_event,
        feed_filter_this_user,
    ):
        if r.szdiff >= 0:
            diffsize = "+" + str(r.szdiff)
        else:
            diffsize = str(r.szdiff)

        attribs["editor"] = project.interwiki_link + "User:" + r.user
        attribs["ceditor"] = r.user
        attribs["article"] = project.interwiki_link + r.title
        attribs["carticle"] = r.title
        attribs["size"] = diffsize
        attribs["url"] = r.url
        attribs["reason"] = r.comment

        if r.newpage:
            return self._react_newpage(
                r, project, attribs, user_offset, feed_filter_this_user
            )
        else:
            return self._react_pageedit(
                r, project, attribs, user_offset, feed_filter_this_user
            )

    def _react_newpage(self, r, project, attribs, user_offset, feed_filter_this_user):
        create_special = False

        if user_offset in (UserType.admin, UserType.whitelisted):
            # Ignore new pages created by an admin or whitelisted user
            return None

        # Initialise the "sizeattrib" and "sizereset" attributes, which are used
        # by all messages, including the later messages for listman-matches.
        # The message keys assigned here may be used as a fallback.
        if r.szdiff >= self.config.new_big:
            create_special = True
            attribs["sizeattrib"] = self.msgs.subst(100, attribs)
            attribs["sizereset"] = self.msgs.subst(102, attribs)
            message = self.msgs.subst(5010 + int(user_offset), attribs)
        elif r.szdiff <= self.config.new_small:
            create_special = True
            attribs["sizeattrib"] = self.msgs.subst(101, attribs)
            attribs["sizereset"] = self.msgs.subst(103, attribs)
            message = self.msgs.subst(5020 + int(user_offset), attribs)
        else:
            attribs["sizeattrib"] = ""
            attribs["sizereset"] = ""
            message = self.msgs.subst(5000 + int(user_offset), attribs)

        # The remaining checks go in descending order of priority.
        # The first match wins.
        # - Article is on watchlist
        # - Page title matches a BNA pattern
        # - Edit summary matches a BES pattern

        if self.listman.is_watched_article(r.title, r.project).success:
            # Page title is on watchlist (CVP)
            message = self.msgs.subst(5030 + int(user_offset), attribs)
            self.add_to_greylist(
                user_offset, r.user, self.msgs.format(16301, attribs["article"])
            )
            return message

        bna_match = self.listman.matches_list(r.title, 12)
        if bna_match.success:
            # Page title matches BNA pattern
            attribs["watchword"] = bna_match.matched_item
            message = self.msgs.subst(5040 + int(user_offset), attribs)
            self.add_to_greylist(
                user_offset,
                r.user,
                self.msgs.format(16300, attribs["article"], bna_match.matched_item),
            )
            return message

        # Does the edit summary match a BES pattern?
        bes_match = self.listman.matches_list(r.comment, 20)
        if bes_match.success:
            # Matches BES
            attribs["watchword"] = bes_match.matched_item
            message = self.msgs.subst(95040 + int(user_offset), attribs)
            self.add_to_greylist(
                user_offset,
                r.user,
                self.msgs.format(16300, attribs["article"], bes_match.matched_item),
            )
            return message

        # If we're still here that means
        # - the create didn't get ignored by adminlist or whitelist
        # - the create didn't match any watch patterns
        #
        # Now, if any of the following is true, we must report it.
        # - Create by blacklisted user
        # - Create by greylisted user
        # - Current usertype is configured to always report
        #   (By default this is for anonymous users, via feedFilterUsersAnon=1,
        #   but feedFilterUsersReg or feedFilterUsersBot could also be set to 1)
        if (
            user_offset in (UserType.blacklisted, UserType.greylisted)
            or feed_filter_this_user == 1
        ):
            return message

        if user_offset == UserType.user and not create_special:
            # Ignore page creation by unlisted reguser with non-special create size
            return None

        # Else: Create had special size, so let it be shown (default)
        return message

    def _react_pageedit(self, r, project, attribs, user_offset, feed_filter_this_user):
        edit_special = False

        if user_offset in (UserType.admin, UserType.whitelisted):
            # Ignore edit by admin or whitelisted user
            return None

        # Initialise the "sizeattrib" and "sizereset" attributes, which are used
        # by all messages, including the later messages for listman-matches.
        # The message keys assigned here may be used as a fallback.
        if r.szdiff >= self.config.edit_big:
            attribs["sizeattrib"] = self.msgs.subst(100, attribs)
            attribs["sizereset"] = self.msgs.subst(102, attribs)
            message = self.msgs.subst(5110 + int(user_offset), attribs)
            edit_special = True
        elif r.szdiff <= self.config.edit_blank:
            attribs["sizeattrib"] = self.msgs.subst(101, attribs)
            attribs["sizereset"] = self.msgs.subst(103, attribs)
            message = self.msgs.subst(5120 + int(user_offset), attribs)
            edit_special = True
        else:
            attribs["sizeattrib"] = ""
            attribs["sizereset"] = ""
            message = self.msgs.subst(5100 + int(user_offset), attribs)

        # The remaining checks go in descending order of priority.
        # The first match wins.
        # - Edit summary matches a BES pattern
        # - Edit blanked the page
        # - Edit replaced the page
        # - Article is on watchlist

        bes_match = self.listman.matches_list(r.comment, 20)
        if bes_match.success:
            # Edit summary matches BES pattern
            attribs["watchword"] = bes_match.matched_item
            message = self.msgs.subst(95130 + int(user_offset), attribs)
            self.add_to_greylist(
                user_offset,
                r.user,
                self.msgs.format(16310, r.comment, attribs["article"]),
            )
            return message

        if project.rautosumm_blank.search(r.comment):
            # User blanked the page
            message = self.msgs.subst(96010 + int(user_offset), attribs)
            self.add_to_greylist(
                user_offset, r.user, self.msgs.format(16311, attribs["article"])
            )
            return message

        replace_match = project.rautosumm_replace.search(r.comment)
        if replace_match:
            # The user replaced the page content.
            profanity = replace_match.groupdict().get("item1")
            if profanity is not None:
                attribs["profanity"] = profanity
                return self.msgs.subst(96020 + int(user_offset), attribs)
            else:
                # This wiki has a "Autosumm-replace" message without "$1"
                return self.msgs.subst(96030 + int(user_offset), attribs)

        if self.listman.is_watched_article(r.title, r.project).success:
            # Page title is on watchlist (CVP)
            return self.msgs.subst(5130 + int(user_offset), attribs)

        # If we're still here that means:
        # - the edit didn't get ignored by adminlist or whitelist
        # - the edit didn't match any watch patterns
        #
        # Now, if any of the following is true, we must still report it:
        # - Edit by blacklisted user
        # - Edit by greylisted user
        # - Current usertype is configured to always report
        #   (By default this is for anonymous users, via feedFilterUsersAnon=1,
        #   but feedFilterUsersReg or feedFilterUsersBot could also be set to 1)
        if (
            user_offset in (UserType.blacklisted, UserType.greylisted)
            or feed_filter_this_user == 1
        ):
            return message

        # If nothing special about the edit, return to ignore
        if not edit_special:
            return None

        return message

    def _react_move(self, r, project, attribs, user_offset, *_):
        # If moves are softhidden, then hide moves by admin, bot or whitelist
        if self.config.feed_filter_event_move == 2 and user_offset in (
            UserType.admin,
            UserType.bot,
            UserType.whitelisted,
        ):
            return None
        attribs["editor"] = project.interwiki_link + "User:" + r.user
        attribs["ceditor"] = r.user
        attribs["fromname"] = project.interwiki_link + r.title
        attribs["cfromname"] = r.title
        attribs["toname"] = project.interwiki_link + r.moved_to
        attribs["ctoname"] = r.moved_to
        # The block_length field stores the moveFrom URL
        attribs["url"] = r.block_length
        attribs["reason"] = r.comment
        return self.msgs.subst(5500 + int(user_offset), attribs)

    def _react_block(self, r, project, attribs, user_offset, *_):
        blocked_user = r.title.split(":", 1)[1]
        attribs["blockname"] = project.interwiki_link + r.title
        attribs["cblockname"] = blocked_user
        attribs["editor"] = project.interwiki_link + "User:" + r.user
        attribs["ceditor"] = r.user
        attribs["talkurl"] = project.rooturl + "wiki/User_talk:" + utils.wiki_encode(blocked_user)
        attribs["length"] = r.block_length
        attribs["reason"] = r.comment
        message = self.msgs.subst(5400, attribs)

        # If the blocked user isn't botlisted, add to blacklist
        if self.listman.classify_editor(blocked_user, r.project) != UserType.bot:
            # If this isn't an indefinite/infinite block, add to blacklist
            if r.block_length.lower() not in ("indefinite", "infinite"):
                # 2,678,400 seconds = 744 hours = 31 days
                list_len = int(
                    utils.parse_datetime_length(r.block_length, 2678400) * 2.5
                )
                bl_comment = "Autoblacklist: {0} on {1}".format(r.comment, r.project)
                message += "\n" + self.listman.add_user_to_list(
                    blocked_user, "",
                    UserType.blacklisted, r.user, bl_comment, list_len,
                )
                self.broadcast(
                    "BL", "ADD", blocked_user, list_len, bl_comment, r.user
                )
        return message

    def _react_unblock(self, r, project, attribs, user_offset, *_):
        attribs["blockname"] = project.interwiki_link + r.title
        attribs["cblockname"] = r.title.split(":", 1)[1]
        attribs["editor"] = project.interwiki_link + "User:" + r.user
        attribs["ceditor"] = r.user
        attribs["talkurl"] = project.rooturl + "wiki/User_talk:" + utils.wiki_encode(r.user)
        attribs["reason"] = r.comment
        return self.msgs.subst(5700, attribs)

    def _react_delete(self, r, project, attribs, user_offset, *_):
        attribs["editor"] = project.interwiki_link + "User:" + r.user
        attribs["ceditor"] = r.user
        attribs["article"] = project.interwiki_link + r.title
        attribs["carticle"] = r.title
        attribs["url"] = project.rooturl + "wiki/" + utils.wiki_encode(r.title)
        attribs["reason"] = r.comment
        return self.msgs.subst(5300, attribs)

    def _react_newuser(
        self, r, project, attribs, user_offset, feed_filter_this_event, *_
    ):
        attribs["editor"] = project.interwiki_link + "User:" + r.user
        attribs["ceditor"] = r.user
        attribs["blockurl"] = (
            project.rooturl + "wiki/Special:Block/" + utils.wiki_encode(r.user)
        )
        attribs["caurl"] = (
            "https://meta.wikimedia.org/wiki/Special:CentralAuth/"
            + utils.wiki_encode(r.user)
        )
        attribs["talkurl"] = (
            project.rooturl + "wiki/User_talk:" + utils.wiki_encode(r.user)
        )

        bnu_match = self.listman.matches_list(r.user, 11)
        if bnu_match.success and feed_filter_this_event == 1:
            # Matches BNU
            attribs["watchword"] = bnu_match.matched_item
            attribs["wwreason"] = bnu_match.matched_reason
            message = self.msgs.subst(5201, attribs)
            self.add_to_greylist(
                user_offset, r.user, self.msgs.format(16320, bnu_match.matched_item)
            )
            return message

        # Only show non-special creations if newuser event is 1 ('show')
        if feed_filter_this_event == 1:
            return self.msgs.subst(5200, attribs)

        return ""

    def _react_newuser2(
        self, r, project, attribs, user_offset, feed_filter_this_event, *_
    ):
        attribs["creator"] = project.interwiki_link + "User:" + r.user
        attribs["ccreator"] = r.user
        attribs["editor"] = project.interwiki_link + "User:" + r.title
        attribs["ceditor"] = r.title
        # It's toss up between creator and editor for which link is more useful.
        # Either of them are easy to find by hand. We don't need to take action
        # on good-faith creations. For bad-faith creations, the original account
        # might be of more immediate interest?
        attribs["blockurl"] = project.rooturl + "wiki/Special:Block/" + utils.wiki_encode(r.user)
        attribs["caurl"] = "https://meta.wikimedia.org/wiki/Special:CentralAuth/" + utils.wiki_encode(r.user)
        attribs["talkurl"] = project.rooturl + "wiki/User_talk:" + utils.wiki_encode(r.user)

        bnu_match_user = self.listman.matches_list(r.user, 11)
        if bnu_match_user.success:
            self.add_to_greylist(
                user_offset, r.user, self.msgs.format(16320, bnu_match_user.matched_item)
            )
        bnu_match_title = self.listman.matches_list(r.title, 11)
        if bnu_match_title.success:
            self.add_to_greylist(
                user_offset, r.title, self.msgs.format(16320, bnu_match_title.matched_item)
            )
        bnu_match = bnu_match_title if bnu_match_title.success else bnu_match_user
        if bnu_match.success:
            attribs["watchword"] = bnu_match.matched_item
            attribs["wwreason"] = bnu_match.matched_reason
            message = self.msgs.subst(5211, attribs)
            return message

        # Only show non-special creations if newuser event is 1 ('show')
        if feed_filter_this_event == 1:
            return self.msgs.subst(5210, attribs)

        return ""

    def _react_upload(self, r, project, attribs, user_offset, *_):
        umsg = 5600

        # Check if the edit summary matches BES
        bes_match = self.listman.matches_list(r.comment, 20)
        if not bes_match.success:
            # Now check if the title matches BES
            bes_match = self.listman.matches_list(r.title, 20)
        if bes_match.success:
            attribs["watchword"] = bes_match.matched_item
            attribs["lmreason"] = bes_match.matched_reason
            umsg = 95620

        # Check if upload is watched
        if self.listman.is_watched_article(r.title, r.project).success:
            umsg = 5610

        # If uninteresting, always ignore upload by admin, bot or whitelisted person
        if umsg == 5600 and user_offset in (
            UserType.admin,
            UserType.bot,
            UserType.whitelisted,
        ):
            return None

        # If uninteresting and uploads are softhidden, ignore upload by most users
        if (
            umsg == 5600
            and self.config.feed_filter_event_upload == 2
            and user_offset in (UserType.anon, UserType.user)
        ):
            return None

        # If we matched BES, we want to report the pattern so truncate the comment to compensate
        comment = r.comment
        if umsg == 95620 and len(comment) > 25:
            comment = comment[:23] + "..."

        attribs["editor"] = project.interwiki_link + "User:" + r.user
        attribs["ceditor"] = r.user
        attribs["uploaditem"] = project.interwiki_link + r.title
        attribs["cuploaditem"] = r.title
        attribs["reason"] = comment
        attribs["url"] = project.rooturl + "wiki/" + utils.wiki_encode(r.title)
        return self.msgs.subst(umsg + int(user_offset), attribs)

    def _react_protect(self, r, project, attribs, user_offset, *_):
        attribs["editor"] = project.interwiki_link + "User:" + r.user
        attribs["ceditor"] = r.user
        attribs["article"] = project.interwiki_link + r.title
        attribs["carticle"] = r.title
        attribs["comment"] = r.comment
        # TODO: Enable url after rce.title is fixed for 'protect' and 'modifyprotect' events
        # attribs["url"] = project.rooturl + "wiki/" + utils.wiki_encode(r.title)
        if r.eventtype == EventType.protect:
            return self.msgs.subst(5900, attribs)
        if r.eventtype == EventType.unprotect:
            # 'url' in unprotect is fine, it's just the pagetitle
            attribs["url"] = project.rooturl + "wiki/" + utils.wiki_encode(r.title)
            return self.msgs.subst(5901, attribs)
        if r.eventtype == EventType.modifyprotect:
            return self.msgs.subst(5902, attribs)
        return None

    # -- Shutting down ----------------------------------------------------

    def quit_irc(self, quit_message):
        """
        Call this with a descriptive reason, before Program.exit() or Program.restart().
        """
        self.rcreader.rcirc.rfc_quit(quit_message)
        self.irc.rfc_quit(quit_message)
        time.sleep(1)

    def exit(self):
        """
        Disconnect the bot and exit the process.

        You should call Program.quit_irc() before calling Program.exit().

        Unless:
        * if we encounter a problem before the program could fully initialize.
        * if we have already left the feed channel.
        """
        try:
            self.irc.disconnect()
            self.rcreader.rcirc.disconnect()
            self.listman.close_db_connection()
        except Exception:
            pass  # Ignore
        finally:
            sys.exit(0)

    def restart(self):
        """
        Disconnect the bot, spawn a new process, and exit the current one.

        You should call Program.quit_irc() before calling Program.restart().

        Unless:
        * if we encounter a problem before the program could fully initialize.
        * if we have already left the feed channel.
        """
        try:
            self.irc.disconnect()
            self.rcreader.rcirc.disconnect()
            self.listman.close_db_connection()
            time.sleep(1)
        except Exception:
            pass  # Ignore
        finally:
            args = self.relaunch_command()
            logger.info("Executing: %s", args)
            try:
                subprocess.Popen(args)
            except OSError:
                logger.exception("Restart failed")
            finally:
                sys.exit(0)

    def relaunch_command(self):
        """How this bot was started: "-m cvnbot", or the path of its script."""

        main_module = sys.modules.get("__main__")
        path = getattr(main_module, "__file__", None) or sys.argv[0]
        if os.path.basename(path) == "__main__.py":
            # Example: '/usr/bin/python3', '/srv/cvn/git/CVNBot/cvnbot/__main__.py'
            args = [sys.executable, path]
        else:
            # Installed by pip or pipx
            # Example: '/home/krinkle/.local/bin/cvnbot'
            # > $ head /Users/krinkle/.local/bin/cvnbot
            # > #!/Users/krinkle/.local/pipx/venvs/cvnbot/bin/python
            args = [os.path.abspath(path)]

        if self.config_filename is not None:
            args += ["--config", self.config_filename]

        return args
