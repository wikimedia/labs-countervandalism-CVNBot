import logging

logger = logging.getLogger("CVNBot.Messages")


class Messages:
    """Messages from a Console.msgs file, keyed by their five-digit code."""

    # The version of the message file this bot expects, stored under key "00000".
    MESSAGES_VERSION = "2.03"

    ERROR_MESSAGE = "[Error: cannot get message]"

    def __init__(self):
        self._messages = {}

    def __contains__(self, key):
        return key in self._messages

    def __getitem__(self, key):
        return self._messages[key]

    def __len__(self):
        return len(self._messages)

    def get(self, key, default=None):
        return self._messages.get(key, default)

    def read(self, filename):
        """Read messages from filename, replacing any previously read ones."""
        logger.info("Loading messages from %s", filename)
        try:
            with open(filename, "r", encoding="utf-8") as handle:
                self._messages.clear()
                for line in handle:
                    line = line.rstrip("\r\n")
                    if line.startswith("#") or line == "":
                        # Ignore: comment or blank line
                        continue
                    key, _, value = line.partition("=")
                    self._messages[key] = value.replace("%c", "\x03").replace(
                        "%b", "\x02"
                    )
        except OSError:
            logger.exception("Unable to read messages from file")
            return False

        if self._messages.get("00000") != self.MESSAGES_VERSION:
            logger.critical("Message file version mismatch or read messages failed")
            return False

        return True

    def subst(self, code, attributes):
        """Get a message and substitute its ${name} attributes."""
        try:
            message = self._messages[str(code).zfill(5)]
            for name, value in attributes.items():
                message = message.replace("${" + name + "}", value)
            return message
        except Exception:
            logger.exception("Cannot get message %s %s", code, attributes)
            return self.ERROR_MESSAGE

    def format(self, code, *params):
        """
        Get a message and format its {0}-style placeholders.

        Message code should have a "1" prefix.
        """
        try:
            return self._messages[str(code).zfill(5)].format(*params)
        except Exception:
            logger.exception("Cannot format message %s", code)
            return self.ERROR_MESSAGE
