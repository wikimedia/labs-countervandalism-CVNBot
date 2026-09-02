"""
A simple IRC client

Only features that CVNBot needs are implemented:
- connect
- log in
- join channels
- send queue with priority and rate-limiting
- channel user tracking (so that op and voice status can be checked)
- automatic reconnecting

See:
    CAP spec: https://ircv3.net/specs/extensions/capability-negotiation.html
    SASL spec: https://ircv3.net/specs/extensions/sasl-3.2

"""

import base64
import enum
import heapq
import itertools
import logging
import socket
import struct
import threading
import time

logger = logging.getLogger("CVNBot.IrcClient")


class SendType(enum.Enum):
    MESSAGE = "message"
    ACTION = "action"
    NOTICE = "notice"


class Priority(enum.IntEnum):
    LOW = 0
    MEDIUM = 1
    HIGH = 2
    CRITICAL = 3


class IrcConnectionError(Exception):
    """Raised when the connection could not be established or was lost."""


# Mode prefixes as advertised by most servers.
_PREFIX_MODES = {"~": "q", "&": "a", "@": "o", "%": "h", "+": "v"}


class IrcMessage:
    """A parsed IRC protocol line."""

    __slots__ = ("raw", "prefix", "nick", "command", "params", "message", "channel")

    def __init__(self, raw):
        self.raw = raw
        self.prefix = ""
        self.nick = ""
        self.message = ""
        self.channel = ""

        rest = raw
        if rest.startswith(":"):
            self.prefix, _, rest = rest[1:].partition(" ")
            self.nick = self.prefix.split("!", 1)[0]

        head, sep, trailing = rest.partition(" :")
        parts = head.split()
        self.command = parts[0].upper() if parts else ""
        self.params = parts[1:]
        if sep:
            self.params.append(trailing)
            self.message = trailing
        elif self.params:
            self.message = self.params[-1]

        if self.params and self.params[0].startswith(("#", "&")):
            self.channel = self.params[0]

    def __str__(self):
        return "IrcMessage<%s>" % ({attr: getattr(self, attr) for attr in self.__slots__})


class ChannelUser:
    """A user as seen in a channel, with their mode flags."""

    def __init__(self, nick):
        self.nick = nick
        self.modes = set()

    @property
    def is_op(self):
        return bool(self.modes & {"o", "a", "q"})

    @property
    def is_voice(self):
        return "v" in self.modes


class IrcClient:
    """A blocking IRC client, driven by listen()."""

    SO_TIMESTAMPNS = 35

    def __init__(self, name="irc",
                 auto_reconnect=False,
                 auto_rejoin=False
                 ):
        self.name = name
        self.encoding = "utf-8"
        # Seconds to wait between two outgoing messages
        # https://libera.chat/guides/faq#are-bots-allowed
        self.send_delay = 0.4
        self.auto_reconnect = auto_reconnect
        self.auto_rejoin = auto_rejoin
        self.max_reconnect_attempts = 5
        self.reconnect_delay = 10

        # Event callbacks, assigned by the owner
        self.on_channel_message = None
        self.on_channel_notice = None
        self.on_connected = None
        self.on_error = None
        self.on_connection_error = None

        self._socket = None
        self._buffer = b""
        self._channels = {}
        self._channels_lock = threading.RLock()
        self._joined = []
        self._send_queue = _PriorityQueue()
        self._sender_thread = None
        # Clear this flag once the server has welcomed us; nothing may be sent before that
        self._registered = threading.Event()
        self._welcomed_once = False
        self._quitting = False
        self._login_args = None
        self._sasl_token = None
        self._server = None
        self._port = None
        self.nickname = ""

    # -- Connecting -------------------------------------------------------

    def connect(self, server, port):
        """
        Raises:
            IrcConnectionError
        """
        self._server = server
        self._port = port
        self._quitting = False
        self._open_socket()
        if self._sender_thread is None:
            self._sender_thread = threading.Thread(
                target=self._sender_loop,
                name=f"{self.name}-IrcSender",
                daemon=True,
            )
            self._sender_thread.start()

    def _open_socket(self):
        self._registered.clear()

        try:
            self._socket = socket.create_connection((self._server, self._port), timeout=30)
        except OSError as e:
            raise IrcConnectionError(
                f"Could not connect to {self._server}:{self._port}: {e}"
            )

        try:
            self._socket.setsockopt(socket.SOL_SOCKET, self.SO_TIMESTAMPNS, 1)
        except OSError as e:
            logger.info("Could not enable SO_TIMESTAMPNS: %s", e)

        # Wikimedia ircstream disconnects after 180s with a PING every 45s
        # https://github.com/paravoid/ircstream/blob/v1.0.1/ircstream/ircserver.py#L259
        #
        # Libera disconnects after 240s with a PING every 120s
        # https://github.com/solanum-ircd/solanum/blob/30f74b2cfa70aff2bad65c8afc62956d95699639/include/defaults.h#L63
        # https://github.com/solanum-ircd/solanum/blob/30f74b2cfa70aff2bad65c8afc62956d95699639/ircd/client.c#L389
        self._socket.settimeout(200)
        self._buffer = b""

    def login(self, nick, realname, usermode=0, username=None, password=""):
        self.nickname = nick
        self._login_args = {
            "nick": nick,
            "realname": realname,
            "usermode": usermode,
            "username": username or nick
        }
        if password != "":
            sasl_user, sasl_pw = password.split(":", maxsplit=1)
            sasl_str = f"\0{sasl_user}\0{sasl_pw}"
            self._sasl_token = base64.b64encode(sasl_str.encode("utf-8")).decode("utf-8")

        self._send_login()

    def _send_login(self):
        self._write_now("CAP LS 302")
        self._write_now("NICK {0}".format(self._login_args["nick"]))
        self._write_now("USER {0} {1} * :{2}".format(
            self._login_args["username"], self._login_args["usermode"], self._login_args["realname"]
        ))

        # Server:
        #   :libera.chat CAP * LS :… sasl=…,PLAIN,… …
        # Client:
        #   CAP REQ :sasl
        # Server:
        #   libera.chat CAP YourName ACK :sasl

    def disconnect(self):
        self._quitting = True
        self._registered.clear()
        sock, self._socket = self._socket, None
        if sock is not None:
            try:
                sock.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
            sock.close()

    # -- Sending ----------------------------------------------------------

    def send_message(self, send_type, destination, message, priority=Priority.LOW):
        """Queue a message for delivery, honouring the send delay."""
        if send_type is SendType.ACTION:
            command = "PRIVMSG {0} :\x01ACTION {1}\x01"
        elif send_type is SendType.NOTICE:
            command = "NOTICE {0} :{1}"
        else:
            command = "PRIVMSG {0} :{1}"
        for chunk in self._split_for_wire(message, len(command.format(destination, ""))):
            self._send_queue.put(priority, command.format(destination, chunk))

    def rfc_join(self, *channels):
        if not channels:
            return
        MSG_MAXLEN = 500
        COMMAND = "JOIN {0}"
        batches = [[]]
        for channel in channels:
            if len(COMMAND.format(",".join(batches[-1] + [channel]))) > MSG_MAXLEN:
                batches.append([channel])
            else:
                batches[-1].append(channel)
        for batch in batches:
            self._send_queue.put(Priority.HIGH, COMMAND.format(",".join(batch)))

        with self._channels_lock:
            for channel in channels:
                if channel not in self._joined:
                    self._joined.append(channel)

    def rfc_part(self, channel, reason=""):
        self._send_queue.put(Priority.HIGH, "PART {0} :{1}".format(channel, reason))
        with self._channels_lock:
            if channel in self._joined:
                self._joined.remove(channel)
            self._channels.pop(channel, None)

    def rfc_quit(self, reason=""):
        self._quitting = True
        # Bypass the queue: a quit must not wait behind pending feed messages
        self._write_now("QUIT :{0}".format(reason))

    def _split_for_wire(self, message, overhead):
        """Split a message so that each line fits in a 512 byte IRC frame."""
        limit = 500 - overhead
        chunks = []
        current = ""
        current_len = 0
        for char in message:
            char_len = len(char.encode(self.encoding, errors="replace"))
            if current_len + char_len > limit:
                chunks.append(current)
                current, current_len = "", 0
            current += char
            current_len += char_len
        chunks.append(current)
        return chunks

    def _sender_loop(self):
        while True:
            line = self._send_queue.get()
            self._registered.wait()
            self._write_now(line)
            time.sleep(self.send_delay)

    def _write_now(self, line):
        sock = self._socket
        if sock is None:
            return
        logger.debug("IRC write %s: %s", self.name, line)
        data = line.encode(self.encoding, errors="replace")[:510] + b"\r\n"
        try:
            sock.sendall(data)
        except OSError as e:
            logger.warning("Failed to send %r: %s", line, e)

    # -- Receiving --------------------------------------------------------

    def listen(self):
        """Read and dispatch messages until the connection is over."""
        while True:
            try:
                for line, recv_ts_ns in self._read_lines():
                    self._handle_line(line, recv_ts_ns)
            except OSError as e:
                if self._quitting:
                    return
                logger.warning("Connection lost: %s", e)
            if self._quitting:
                return
            self._registered.clear()
            if not self._reconnect():
                self._fire(self.on_connection_error, self)
                return

    def _read_lines(self):
        while True:
            sock = self._socket
            if sock is None:
                return
            data, ancdata, flags, addr = sock.recvmsg(4096, 1024)
            if not data:
                raise OSError("Connection closed by peer")
            recv_ts_ns = None
            for cmsg_level, cmsg_type, cmsg_data in ancdata:
                if cmsg_level == socket.SOL_SOCKET and cmsg_type == self.SO_TIMESTAMPNS:
                    sec, nsec = struct.unpack("qq", cmsg_data[:16])
                    recv_ts_ns = sec * 1_000_000_000 + nsec
                    break

            self._buffer += data
            while b"\n" in self._buffer:
                raw, self._buffer = self._buffer.split(b"\n", 1)
                line = raw.rstrip(b"\r").decode(self.encoding, errors="replace")
                if line:
                    yield line, recv_ts_ns

    def _reconnect(self):
        if not self.auto_reconnect:
            return False
        for attempt in range(1, self.max_reconnect_attempts + 1):
            logger.info(
                "Reconnecting to %s (attempt %d/%d)",
                self._server, attempt, self.max_reconnect_attempts,
            )
            time.sleep(self.reconnect_delay)
            try:
                self._open_socket()
            except IrcConnectionError as e:
                logger.warning("Reconnect failed: %s", e)
                continue
            with self._channels_lock:
                self._channels.clear()
            self._send_login()
            return True
        return False

    def _handle_line(self, line, recv_ts_ns=None):
        message = IrcMessage(line)
        command = message.command
        logger.debug("IRC received: %s", line)

        if command == "PING":
            self._write_now("PONG :{0}".format(message.message))
            return

        if command == "PRIVMSG":
            if message.channel:
                self._fire(self.on_channel_message, self, message, recv_ts_ns)
            return

        if command == "NOTICE":
            if message.channel:
                self._fire(self.on_channel_notice, self, message)
            return

        if command == "ERROR":
            if not self._quitting:
                self._fire(self.on_error, self, message.message or line)
            return

        if command == "CAP":
            if message.params[1] == "LS":
                if self._sasl_token and "sasl" in message.params[2]:
                    self._write_now("CAP REQ :sasl")
                else:
                    self._write_now("CAP END")
            elif message.params[1] == "ACK":
                if self._sasl_token and message.params[2] == "sasl":
                    self._write_now("AUTHENTICATE PLAIN")
            return

        if command == "AUTHENTICATE":
            if message.params[0] == "+" and self._sasl_token:
                self._write_now(f"AUTHENTICATE {self._sasl_token}")
            return

        if command == "001":
            # Channels joined before this point are waiting in the send queue,
            # gated behind the "_registered" flag.
            # Only if this was a reconnect do we resend the joins.
            rejoin = self._welcomed_once and self.auto_rejoin
            self._welcomed_once = True
            self._registered.set()
            self._fire(self.on_connected, self)
            if rejoin:
                with self._channels_lock:
                    channels = list(self._joined)
                self.rfc_join(*channels)
            return

        if command == "433":
            # ERR_NICKNAMEINUSE: Fallback to alternate
            self.nickname += "_"
            self._write_now("NICK {0}".format(self.nickname))
            return

        if command == "900":
            # RPL_LOGGEDIN
            logger.info(message.message)
            return

        if command == "903":
            # RPL_SASLSUCCESS
            self._write_now("CAP END")
            logger.info(message.message)
            return

        if command == "904":
            # ERR_SASLFAIL
            self._write_now("CAP END")
            logger.warning(message.message)
            return

        if command in ("353", "366", "JOIN", "PART", "KICK", "QUIT", "NICK", "MODE"):
            self._track_channels(message)
            return

        if command.isdigit() and int(command) >= 400:
            self._fire(self.on_error, self, message.message or line)

    def _track_channels(self, message):
        command = message.command
        with self._channels_lock:
            if command == "353":
                # RPL_NAMREPLY: <me> <symbol> <channel> :<nicks>
                channel = message.params[2]
                users = self._channels.setdefault(channel, {})
                for entry in message.message.split():
                    modes = set()
                    while entry and entry[0] in _PREFIX_MODES:
                        modes.add(_PREFIX_MODES[entry[0]])
                        entry = entry[1:]
                    if entry:
                        user = users.setdefault(entry, ChannelUser(entry))
                        user.modes |= modes
            elif command == "JOIN":
                channel = message.channel or message.message
                users = self._channels.setdefault(channel, {})
                users.setdefault(message.nick, ChannelUser(message.nick))
            elif command in ("PART", "KICK"):
                channel = message.channel
                nick = message.params[1] if command == "KICK" else message.nick
                self._channels.get(channel, {}).pop(nick, None)
            elif command == "QUIT":
                for users in self._channels.values():
                    users.pop(message.nick, None)
            elif command == "NICK":
                new_nick = message.message
                for users in self._channels.values():
                    user = users.pop(message.nick, None)
                    if user is not None:
                        user.nick = new_nick
                        users[new_nick] = user
            elif command == "MODE" and message.channel:
                self._apply_modes(message)

    def _apply_modes(self, message):
        users = self._channels.setdefault(message.channel, {})
        if len(message.params) < 2:
            return
        adding = True
        targets = list(message.params[2:])
        for char in message.params[1]:
            if char == "+":
                adding = True
            elif char == "-":
                adding = False
            elif char in _PREFIX_MODES.values():
                if not targets:
                    continue
                nick = targets.pop(0)
                user = users.setdefault(nick, ChannelUser(nick))
                if adding:
                    user.modes.add(char)
                else:
                    user.modes.discard(char)
            elif char in "beIkflL":
                # Modes that take a parameter but do not concern a user
                if targets:
                    targets.pop(0)

    def get_channel_user(self, channel, nick):
        """Return the ChannelUser for nick in channel, or None."""
        with self._channels_lock:
            return self._channels.get(channel, {}).get(nick)

    @staticmethod
    def _fire(callback, *args):
        if callback is None:
            return
        try:
            callback(*args)
        except Exception:
            logger.exception("Unhandled exception in IRC event handler")


class _PriorityQueue:
    """A FIFO-within-priority blocking queue."""

    def __init__(self):
        self._heap = []
        self._counter = itertools.count()
        self._condition = threading.Condition()

    def put(self, priority, item):
        with self._condition:
            heapq.heappush(self._heap, (-int(priority), next(self._counter), item))
            self._condition.notify()

    def get(self):
        with self._condition:
            while not self._heap:
                self._condition.wait()
            return heapq.heappop(self._heap)[2]


__all__ = [
    "ChannelUser",
    "IrcConnectionError",
    "IrcClient",
    "IrcMessage",
    "Priority",
    "SendType",
]
