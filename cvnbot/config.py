import importlib.resources
import os

from dataclasses import dataclass


@dataclass
class Config:
    # User
    bot_nick: str = "YourCVNBot"
    bot_pass: str = ""
    # Gets concatenated with a space and Program.version
    bot_real_name: str = "CVNBot"
    part_msg: str = "https://meta.wikimedia.org/wiki/CVNBot"

    # Server
    irc_server_name: str = "irc.libera.chat"
    irc_server_port: int = 6667
    # Channel name or "None"
    feed_channel: str = "#cvn-sandbox"
    control_channel: str = "None"
    broadcast_channel: str = "None"

    # Files
    messages_file: str = importlib.resources.files("cvnbot") / "Console.msgs"
    lists_file: str = "./Lists.sqlite"
    projects_file: str = "./Projects.xml"

    # Logging
    log_level: str = "INFO"
    log_syslog: bool = False

    # Feed
    edit_blank: int = -500
    edit_big: int = 500
    new_big: int = 500
    new_small: int = 10

    # If true, overrides feedfilters to only show uploads and ignore other RC events
    is_cubbie: bool = False

    # Whether to entirely disable the database. This means requesting a usertype
    # will always return 3 (anon) or 4 (user) based on a static regex.
    # This speeds up the flow incredibly (especially when using SQLite) and makes it possible
    # to load many (or even, all) of the Wikimedia wikis without producing an ever-growing backlog
    # of change events faster than we can process them.
    # Disabling the database means the actual output in the feedchannel will not be useful (all edits go through,
    # no bot, user, or whitelist detection).
    # Recommended to be used in combination with high(est) feedFilter settings for the purposes
    # of detecting block events from all wikis to then automatically broadcast to other bots
    # for cross-wiki vandalism detection. Originally written for the CVNBlackRock bot.
    disable_classify_editor: bool = False

    # Feed filters
    #
    # These settings allow filtering of user types and event types
    #
    # Possible values:
    #
    #  1 "show"     (show and allow autolist) - default
    #  2 "softhide" (hide non-specials, show exceptions and allow autolist)
    #     softhide users: only large actions or matching watchlist/BES/BNU etc.
    #     softhide events: hide bots, admins, whitelist performing the event
    #  3 "hardhide" (hide all but do autolist)
    #  4 "ignore"   (hide and ignore totally)
    #
    # show/ignore is dealt with at beginning of react_to_rc_event()
    # hardhide is dealt with at end of react_to_rc_event() (after autolistings are done)
    # softhide is done inline

    # any event by anon: show-all
    feed_filter_users_anon: int = 1
    # any event by reg: special-only
    feed_filter_users_reg: int = 2
    # any event by bot: ignore
    feed_filter_users_bot: int = 4
    # any minor edit: ignore
    feed_filter_event_minor_edit: int = 4
    # any page edit: show-all (other filter may override)
    feed_filter_event_edit: int = 1
    # any page create: show-all (other filter may override)
    feed_filter_event_newpage: int = 1
    # any move event: show-all
    feed_filter_event_move: int = 1
    # any block event: show-all (bots hidden?)
    feed_filter_event_block: int = 1
    feed_filter_event_delete: int = 1
    feed_filter_event_newuser: int = 1
    feed_filter_event_upload: int = 1
    feed_filter_event_protect: int = 1

    def __str__(self):
        # SECURITY: Reduce exposure to credentials
        return "[CVNBot.config]"


# The CLI interprets --config relative to the current working directory.
# After that, any relative file paths are resolved relative to the config
# file. This allows for do-what-I-mean operation like:
# $ python -m cvnbot --config /path/to/mybot/CVNBot.ini
# regardless of current working directory
def file_str(val, config_filename):
    val = str(val)
    bot_dir = os.path.dirname(config_filename)
    val = os.path.normpath(os.path.join(bot_dir, val))
    return val


# Maps INI file key to Config attribute, type, and bool is_public (False by default).
#
# The latter controls whether the value should be included in the public
# "CVNBot version" response on IRC. See program.bot_config_msg.
#
# SECURITY: Keep 'botpass' private.
INI_KEYS = {
    # User
    "botnick": ("bot_nick", str),
    "botpass": ("bot_pass", str),
    "botrealname": ("bot_real_name", str),
    "partmsg": ("part_msg", str),
    # Server
    "ircserver": ("irc_server_name", str),
    "ircport": ("irc_server_port", int),
    "feedchannel": ("feed_channel", str),
    "controlchannel": ("control_channel", str),
    "broadcastchannel": ("broadcast_channel", str),
    # Files
    "messages": ("messages_file", file_str),
    "lists": ("lists_file", file_str),
    "projects": ("projects_file", file_str),
    # Logging
    "loglevel": ("log_level", str),
    "logsyslog": ("log_syslog", bool),
    # Feed
    "editblank": ("edit_blank", int, True),
    "editbig": ("edit_big", int, True),
    "newbig": ("new_big", int, True),
    "newsmall": ("new_small", int, True),
    "IsCubbie": ("is_cubbie", bool, True),
    "disableClassifyEditor": ("disable_classify_editor", bool, True),
    "feedFilterUsersAnon": ("feed_filter_users_anon", int, True),
    "feedFilterUsersReg": ("feed_filter_users_reg", int, True),
    "feedFilterUsersBot": ("feed_filter_users_bot", int, True),
    "feedFilterEventMinorEdit": ("feed_filter_event_minor_edit", int, True),
    "feedFilterEventEdit": ("feed_filter_event_edit", int, True),
    "feedFilterEventNewpage": ("feed_filter_event_newpage", int, True),
    "feedFilterEventMove": ("feed_filter_event_move", int, True),
    "feedFilterEventBlock": ("feed_filter_event_block", int, True),
    "feedFilterEventDelete": ("feed_filter_event_delete", int, True),
    "feedFilterEventNewuser": ("feed_filter_event_newuser", int, True),
    "feedFilterEventUpload": ("feed_filter_event_upload", int, True),
    "feedFilterEventProtect": ("feed_filter_event_protect", int, True),
}


def read_raw_config(filename):
    """Read a flat INI file into a dict."""
    raw = {}
    with open(filename, "r", encoding="utf-8") as handle:
        for line in handle:
            line = line.rstrip("\r\n")
            # Ignore comments
            if line.startswith("#") or line == "":
                continue
            key, _, value = line.partition("=")
            raw[key] = value
    return raw


def apply_from_file(config, config_filename):
    """Read a flat INI file and apply to a Config."""
    raw = read_raw_config(config_filename)

    for key, (attr, kind, *rest) in INI_KEYS.items():
        if key in raw:
            if kind is bool:
                # Booleans are opt-in by mere presence of the key, as in the C# bot
                setattr(config, attr, True)
            elif kind is file_str:
                setattr(config, attr, file_str(raw[key], config_filename))
            else:
                setattr(config, attr, kind(raw[key]))
        else:
            # Key not set in INI file, resolve default relative to the INI file
            if key in ('lists', 'projects'):
                setattr(config, attr, file_str(getattr(config, attr), config_filename))
    return config


def public_config(config):
    """
    Return name/value dict with config data to report publicly to IRC.

    Use external INI keys rather than runtime Config fields.
    """
    public = {}
    for key, (attr, kind, *rest) in INI_KEYS.items():
        is_public = rest[0] if rest else False
        if is_public:
            public[key] = getattr(config, attr)

    return public
