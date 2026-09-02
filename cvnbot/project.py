import logging
import re
import xml.etree.ElementTree as ElementTree

from xml.sax.saxutils import escape

from . import utils

logger = logging.getLogger("CVNBot.Project")


class Project:
    """
    Namespaces and interface messages for one wiki.

    Used to parse and format a wiki's RC feed.

    Stored in Projects.xml, via projectlist.py.
    """

    # Characters that must be escaped when turning an interface message into a regex
    RE_CHARS = ["\\", ".", "(", ")", "[", "]", "^", "*", "+", "?", "{", "}", "|"]

    REGEX_DICT_KEYS = [
        "restoreRegex",
        "deleteRegex",
        "protectRegex",
        "unprotectRegex",
        "modifyprotectRegex",
        "uploadRegex",
        "moveRegex",
        "moveredirRegex",
        "blockRegex",
        "unblockRegex",
        "reblockRegex",
        "autosummBlank",
        "autosummReplace",
    ]

    def __init__(self):
        # Parsed in read_project_details(), stored in dump_project_details()
        self.project_name = ""
        self.interwiki_link = ""
        self.rooturl = ""  # Format: https://en.wikipedia.org/
        self.regex_dict = {}

        # Parsed via parse_namespaces()
        self.namespaces = {}
        # Parsed from regex_dict via generate_regexen()
        self.rrestore_regex = None
        self.rdelete_regex = None
        self.rprotect_regex = None
        self.runprotect_regex = None
        self.rmodifyprotect_regex = None
        self.rupload_regex = None
        self.rmove_regex = None
        self.rmoveredir_regex = None
        self.rblock_regex = None
        self.runblock_regex = None
        self.rreblock_regex = None
        self.rautosumm_blank = None
        self.rautosumm_replace = None
        self.rspecial_log_regex = None
        self.rcreate2_regex = None

    def generate_regexen(self):
        """
        Compile the regexen for this project.

        Always generate the namespace list before calling this!
        """
        self.rrestore_regex = utils.compile_dotnet(self.regex_dict["restoreRegex"])
        self.rdelete_regex = utils.compile_dotnet(self.regex_dict["deleteRegex"])
        self.rprotect_regex = utils.compile_dotnet(self.regex_dict["protectRegex"])
        self.runprotect_regex = utils.compile_dotnet(self.regex_dict["unprotectRegex"])

        if "modifyprotectRegex" not in self.regex_dict:
            # Added in CVNBot 1.20, fallback if missing in older XML files.
            self.regex_dict["modifyprotectRegex"] = self.regex_dict["protectRegex"]
            logger.warning(
                "generate_regexen: modifyprotectRegex is missing. Please reload this wiki."
            )
        self.rmodifyprotect_regex = utils.compile_dotnet(
            self.regex_dict["modifyprotectRegex"]
        )
        self.rupload_regex = utils.compile_dotnet(self.regex_dict["uploadRegex"])
        self.rmove_regex = utils.compile_dotnet(self.regex_dict["moveRegex"])
        self.rmoveredir_regex = utils.compile_dotnet(self.regex_dict["moveredirRegex"])
        self.rblock_regex = utils.compile_dotnet(self.regex_dict["blockRegex"])
        self.runblock_regex = utils.compile_dotnet(self.regex_dict["unblockRegex"])
        if "reblockRegex" not in self.regex_dict:
            # Added in CVNBot 1.22, fallback if missing in older XML files.
            self.regex_dict["reblockRegex"] = "^$"
            logger.warning(
                "generate_regexen: reblockRegex is missing. Please reload this wiki."
            )
        self.rreblock_regex = utils.compile_dotnet(self.regex_dict["reblockRegex"])
        self.rautosumm_blank = utils.compile_dotnet(self.regex_dict["autosummBlank"])
        self.rautosumm_replace = utils.compile_dotnet(self.regex_dict["autosummReplace"])

        self.regex_dict["specialLogRegex"] = self.namespaces["-1"] + r":.+?/(.+)"
        self.rspecial_log_regex = utils.compile_dotnet(self.regex_dict["specialLogRegex"])

        self.rcreate2_regex = re.compile(self.namespaces["2"] + r":([^:]+)")

    # -- Persistence ------------------------------------------------------

    def dump_project_details(self):
        """Serialize project details to a <project> element XML string."""
        parts = ["<project>"]

        def element(name, value):
            if value:
                parts.append("<{0}>{1}</{0}>".format(name, escape(value)))
            else:
                parts.append("<{0} />".format(name))

        element("projectName", self.project_name)
        element("interwikiLink", self.interwiki_link)
        element("rooturl", self.rooturl)
        element("speciallog", self.regex_dict["specialLogRegex"])

        namespaces_node = ElementTree.Element("namespaces")
        for key, value in self.namespaces.items():
            ns = ElementTree.SubElement(namespaces_node, "ns", id=key)
            ns.text = value
        element(
            "namespaces",
            ElementTree.tostring(namespaces_node, encoding="unicode"),
        )
        for name in self.REGEX_DICT_KEYS:
            element(name, self.regex_dict[name])

        parts.append("</project>")
        return "".join(parts)

    def read_project_details(self, element):
        """
        Load details from a <project> element from Projects.xml.

        Args:
            xml.etree.ElementTree.Element element
        """
        for child in element:
            value = child.text or ""
            if child.tag == "projectName":
                self.project_name = value
            if child.tag == "interwikiLink":
                self.interwiki_link = value
            if child.tag == "rooturl":
                self.rooturl = value
            elif child.tag == "speciallog":
                self.regex_dict["specialLogRegex"] = value
            elif child.tag == "namespaces":
                # Parse namespaces before generating regexen
                self.namespaces = Project.parse_namespaces(value)
            elif child.tag in self.REGEX_DICT_KEYS:
                self.regex_dict[child.tag] = value

        self.generate_regexen()

    @staticmethod
    def parse_namespaces(snamespaces):
        namespaces = {}
        namespaces_node = utils.parse_xml(snamespaces, "namespaces")
        if namespaces_node is None:
            raise Exception("No namespaces found")
        for child in namespaces_node:
            namespaces[child.get("id")] = child.text or ""
        return namespaces

    # -- Fetching from the wiki -------------------------------------------

    def get_namespaces(self):
        logger.info("Fetching namespaces from %s", self.rooturl)
        snamespaces = utils.get_raw_document(
            self.rooturl
            + "w/api.php?format=xml&action=query&meta=siteinfo&siprop=namespaces"
        )

        self.namespaces = Project.parse_namespaces(snamespaces)

    def retrieve_wiki_details(self):
        """
        Raises:
            Exception: if MediaWiki API request for namespaces fails.
        """

        # Find out what the localized Special: (ID -1) namespace is, and create a regex
        self.get_namespaces()

        logger.info("Fetching interface messages from %s", self.rooturl)

        self.get_interface_messages()

        self.generate_regexen()

    def get_interface_messages(self):
        # Interface message name -> (number of required parameters, regex_dict key, non-strict)
        INTERFACE_MESSAGES = {
            "Undeletedarticle": (1, "restoreRegex", False),
            "Deletedarticle": (1, "deleteRegex", False),
            "Protectedarticle": (1, "protectRegex", False),
            "Unprotectedarticle": (1, "unprotectRegex", False),
            "Modifiedarticleprotection": (1, "modifyprotectRegex", True),
            "Uploadedimage": (0, "uploadRegex", False),
            "1movedto2": (2, "moveRegex", False),
            "1movedto2_redir": (2, "moveredirRegex", False),

            # blockRegex is nonStrict because some wikis override the message without
            # including $2 (block length).
            # RCReader will fall back to "24 hours" if this is the case.
            # Some newer messages e.g. https://lmo.wikipedia.org/wiki/MediaWiki:Blocklogentry
            # have a third item $3 ("anononly,nocreate,autoblock"). This may conflict with
            # $2 detection.
            #
            # Trying (changed 2 -> 3) to see if length of time will be correctly detected
            # using just this method:
            "Blocklogentry": (3, "blockRegex", True),

            "Unblocklogentry": (0, "unblockRegex", False),
            "Reblock-logentry": (3, "reblockRegex", False),
            "Autosumm-blank": (0, "autosummBlank", False),

            # autosummReplace is nonStrict because some wikis use translation overrides
            # without a "$1" parameter for the content.
            "Autosumm-replace": (1, "autosummReplace", True),
        }
        raw = utils.get_raw_document(
            self.rooturl
            + "w/api.php?action=query&meta=allmessages&format=xml"
            + "&ammessages="
            + "|".join(INTERFACE_MESSAGES)
        )

        allmessages_node = utils.parse_xml(raw, "allmessages")
        if allmessages_node is None:
            raise Exception("No interface messages found for " + self.rooturl)
        for child in allmessages_node:
            name = child.get("name")
            required, dest_regex, non_strict = INTERFACE_MESSAGES[name]
            self.regex_dict[dest_regex] = self.generate_regex(
                name, child.text or "", required, non_strict
            )

    def generate_regex(self, message_title, message, req_count, non_strict):
        """Turn an interface message into a regex that matches itself."""

        # Now gently coax that into a regex
        for char in self.RE_CHARS:
            message = message.replace(char, "\\" + char)

        message = message.replace("$1", "(?<item1>.+?)", 1)
        message = message.replace("$2", "(?<item2>.+?)", 1)
        message = message.replace("$3", "(?<item3>.+?)", 1)
        message = message.replace("$1", "(?:.+?)")
        message = message.replace("$2", "(?:.+?)")
        message = message.replace("$3", "(?:.+?)")
        message = message.replace("$", r"\$")
        # Special:Log comments are preceded by a colon
        message = "^" + message + r"(?:: (?<comment>.*?))?$"

        # Dirty code: Block log exceptions!
        if message_title == "Blocklogentry":
            message = message.replace(
                "(?<item3>.+?)",
                "\\((?<item3>.+?)\\)"
            )
            message = message.replace(
                "(?<item2>.+?)(?:: (?<comment>.*?))?$",
                "(?<item2>.+?)$"
            )

        try:
            utils.compile_dotnet(message).search('')
        except re.error as e:
            raise Exception(
                "Failed to test-generate regex {0} for {1}; {2}".format(
                    message, message_title, e
                )
            )

        if req_count >= 1 and not non_strict:
            if "(?<item1>.+?)" not in message:
                raise Exception(
                    "Regex {0} requires one or more items but item1 not found in {1}".format(
                        message_title, message
                    )
                )
            if req_count >= 2 and "(?<item2>.+?)" not in message:
                raise Exception(
                    "Regex {0} requires two or more items but item2 not found in {1}".format(
                        message_title, message
                    )
                )

        return message

    # -- Namespaces -------------------------------------------------------

    def detect_namespace(self, page_title):
        """Get the namespace ID of a title such as "Special:Helloworld"."""
        if ":" in page_title:
            ns_local = page_title.split(":", 1)[0]
            for key, value in self.namespaces.items():
                if value == ns_local:
                    return int(key)
        # If no match for the prefix found, or if no colon,
        # assume main namespace
        return 0

    # DO NOT CHANGE OR REMOVE ANY VALUES. This is append-only.
    #
    # Incoming RC events from MediaWiki use titles formatted with the latest localised
    # namespace prefix.
    #
    # In ListManager, when adding/removing/showing titles in the "watchlist" or "items"
    # we noromalize the namespace prefixes to this canonical form so that titles reliably
    # match across wikis in different languages, and over time. For example, "Image"
    # changed to "File" many years ago, but this change must not be applied here.
    # If a change were needeed to fix a show-stopping bug, include release notes
    # and manually update existing databases to keep regexes and watchlists functional!
    #
    # In RCReader we also normalize titles to this form for the RCEvent message we
    # output to the IRC feed channels. This is mainly for multilingual channels like
    # #cvn-sw to help patrollers with unfamiliar namespace translations. The downside is
    # that monolingual channels like #cvn-wp-nl get titles presented in English among
    # an otherwise localised Console.msgs, and even #cvn-wp-en gets namespaces formatted
    # in an outdated form (File>Image).
    CANONICAL_NAMESPACES = {
        -2: "Media",
        -1: "Special",
        1: "Talk",
        2: "User",
        3: "User talk",
        4: "Project",
        5: "Project talk",
        6: "Image",
        7: "Image talk",
        8: "MediaWiki",
        9: "MediaWiki talk",
        10: "Template",
        11: "Template talk",
        12: "Help",
        13: "Help talk",
        14: "Category",
        15: "Category talk",
    }

    def translate_namespace(self, original_title):
        """Translate a local title's namespace to canonical English."""
        if ":" not in original_title:
            # Mainspace articles do not need translation
            return original_title

        ns_prefix = self.CANONICAL_NAMESPACES.get(self.detect_namespace(original_title))
        if ns_prefix is None:
            return original_title

        return ns_prefix + original_title[original_title.index(":"):]
