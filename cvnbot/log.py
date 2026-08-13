import logging
import logging.handlers
import sys


class NickFormatter(logging.Formatter):
    def __init__(self, fmt, nick):
        super().__init__(fmt)
        self.nick = nick

    def format(self, record):
        record.nick = self.nick
        return super().format(record)


CONSOLE_FORMAT = (
    "%(asctime)s cvnbot[%(process)d]: %(levelname)s [%(nick)s] [%(threadName)s] [%(name)s] %(message)s"
)
SYSLOG_FORMAT = "cvnbot[%(process)d]: %(levelname)s [%(nick)s] [%(threadName)s] [%(name)s] %(message)s"


def setup_logger(config):
    """Configure the root logger for console and syslog output."""
    root = logging.getLogger()
    root.setLevel(getattr(logging, str(config.log_level).upper(), logging.INFO))
    for handler in list(root.handlers):
        root.removeHandler(handler)

    console = logging.StreamHandler(sys.stdout)

    # Include bot nick in all log messages to help distinguish output from
    # CVNBot instances running on the same server, writing to the same syslog.
    console.setFormatter(NickFormatter(CONSOLE_FORMAT, config.bot_nick))

    root.addHandler(console)

    if config.log_syslog:
        try:
            handler = logging.handlers.SysLogHandler(address="/dev/log")
        except OSError:
            root.warning("Syslog is not available; logging to console only")
        else:
            handler.setFormatter(NickFormatter(SYSLOG_FORMAT, config.bot_nick))
            root.addHandler(handler)
