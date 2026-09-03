import datetime
import re
import urllib.error
import urllib.parse
import urllib.request
import warnings
import xml.etree.ElementTree as ElementTree

USER_AGENT = (
    "Mozilla/5.0 (en-US) CVNBot/1.0 (like SWMTBot) "
    "More info: https://meta.wikimedia.org/wiki/CVNBot"
)

_r_stripper = re.compile(r"(,|and)")
_r_spaces = re.compile(r"\s{2,}")
# TODO: Something is still wrong here, some expiries show up as 3 instead of 3 day(s)
_r_find_values = re.compile(
    r"(\d+) (year|month|fortnight|week|day|hour|minute|min|second|sec)s?"
)


def parse_datetime_length(text, default_len):
    """
    Like PHP's strtotime() function, attempts to parse a GNU date/time into number of seconds.

    Returns 0 for indefinite blocks, and default_len when nothing was parsed.
    """
    parse_str = text.lower()
    parse_str = _r_stripper.sub("", parse_str)
    parse_str = _r_spaces.sub(" ", parse_str)

    # Handle specials here
    if parse_str in ("indefinite", "infinite"):
        return 0
    if parse_str == "tomorrow":
        return 24 * 3600

    units = {
        "year": 8760 * 3600,  # 365 days
        "month": 732 * 3600,  # 30.5 days
        "fortnight": 336 * 3600,  # 14 days
        "week": 168 * 3600,  # 7 days
        "day": 24 * 3600,
        "hour": 3600,
        "minute": 60,
        "min": 60,
        "second": 1,
        "sec": 1,
    }

    # Now for some real parsing
    seconds = 0
    for value, unit in _r_find_values.findall(parse_str):
        # Round the float
        seconds += int(value) * units[unit]

    if seconds == 0:
        return default_len

    return seconds


def get_raw_document(url, timeout=30):
    """Fetch a URL and return its body as text."""
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return response.read().decode("utf-8")
    except (urllib.error.URLError, OSError) as e:
        raise Exception(
            "Unable to retrieve {0} from server. Error was: {1}".format(url, e)
        )


def replace_str_max(text, old_char, new_char, max_chars):
    """Replace up to max_chars occurrences of old_char with new_char."""
    for _ in range(max_chars):
        place = text.find(old_char)
        if place == -1:
            break
        text = text[:place] + new_char + text[place + 1:]
    return text


def wiki_encode(text):
    """
    Encode a string for use in wiki URLs.

    It is important that we at least:
    * Replace space with underscore and preserve "/" and ":" because
      they are common, and reduces needless encoding in a way that differs
      from MediaWiki.
    * Encode `!*()~'"` because links may otherwise be terminated mid-way or
      parsed incorrectly by recipieints in their applications and graphical
      IRC clients, based on whatever link trail logic they might have.
    """
    encoded = urllib.parse.quote(text.replace(" ", "_"), safe='/:')
    encoded = encoded.replace("~", "%7e")
    return encoded


_r_dotnet_group = re.compile(r"\(\?<(?![=!])")
_r_dotnet_global_i = re.compile(r"\(\?i\)")


def compile_dotnet(pattern, flags=0):
    """
    Compile a regex written in the .NET dialect.

    Known differences:
    * .NET writes named groups `(?<name>...)`, Python writes them `(?P<name>...)`.

      This is used in Projects.xml and supported to ease migration from CVNBot 4.

    * .NET allows `(?i)` in the middle of a pattern, taking effect from that point
      on. Python supports global flags only, which must be at the start.
      Strip this because we use case-insensitive matching for many years already.
      Without this, pattern `\b(?i)(alpaca|alpacas)\b` would fail as
      "re.error: global flags not at the start of the expression at position 2"

      ListManager patterns are submitted by end-users via IRC commands and stored
      in the CVNBot database. These were historically executed in C# dotnet as
      `Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase)`.

    Returns:
        re.Pattern
    """
    pattern = _r_dotnet_group.sub("(?P<", pattern)
    pattern = _r_dotnet_global_i.sub("", pattern)

    # Silence "FutureWarning: Possible nested set at position"
    with warnings.catch_warnings():
        warnings.simplefilter("ignore", FutureWarning)
        return re.compile(pattern, flags)


def parse_xml(text, tag):
    """Parse XML document and return the first matching element by tag name."""
    document = ElementTree.fromstring(text)
    if document.tag == tag:
        return document
    return document.find(".//" + tag)


_DOTNET_EPOCH = datetime.datetime(1, 1, 1, 0, 0, 0, tzinfo=datetime.timezone.utc)


def ticks_now():
    """
    The current time in ticks, like .NET DateTime.Now.Ticks.

    This is used for the expiry columns in Lists.sqlite.
    """
    return ticks_from_datetime(datetime.datetime.now(tz=datetime.timezone.utc))


def ticks_from_datetime(when):
    """
    Convert a Python datetime to a .NET DateTime.Ticks.

    https://learn.microsoft.com/en-us/dotnet/api/system.datetime.now?view=net-10.0
    https://learn.microsoft.com/en-us/dotnet/api/system.datetime.ticks?view=net-10.0

    Returns:
        int: Number of 100-nanosecond "ticks" since 0001-01-01.
    """
    # Delta from _DOTNET_EPOCH to the given timestamp
    delta = when - _DOTNET_EPOCH
    # 1 second is 10 million (1e7) ticks
    # 1 microsecond is 1000 nanoseconds, or 10 ticks
    return (delta.days * 86400 + delta.seconds) * 10**7 + delta.microseconds * 10


def datetime_from_ticks(ticks):
    """Inverse of ticks_from_datetime(); returns a naive datetime."""
    return _DOTNET_EPOCH + datetime.timedelta(microseconds=ticks // 10)


_MONTHS = [
    "January", "February", "March", "April", "May", "June",
    "July", "August", "September", "October", "November", "December",
]


def format_expiry(ticks):
    """Format an expiry timestamp in UTC, like "13:37, 5 April 2024"."""
    dt = datetime_from_ticks(ticks)
    return "{:%H:%M}, {} {} {}".format(dt, dt.day, _MONTHS[dt.month - 1], dt.year)
