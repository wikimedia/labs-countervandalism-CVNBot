import unittest

from cvnbot.rcreader import EventType

from .helpers import destroy_bot, make_bot, make_project


def feed_line(title, flags, url, user, szdiff, comment):
    """Build a raw line in the format of the Wikimedia RC feed."""
    return (
        "\x0314[[\x0307{title}\x0314]]\x034 {flags}\x0310 \x0302{url}"
        "\x03 \x035* \x03 \x0303{user}\x03 \x035* \x03 ({szdiff}) "
        "\x0310{comment}\x03"
    ).format(
        title=title, flags=flags, url=url, user=user, szdiff=szdiff, comment=comment
    )


class RCReaderTest(unittest.TestCase):
    def setUp(self):
        self.bot = make_bot()
        self.reader = self.bot.rcreader

    def tearDown(self):
        destroy_bot(self.bot)

    def parse(self, *args, channel="#en.wikipedia"):
        return self.reader.parse_message(
            channel, feed_line(*args)
        )


class EditTest(RCReaderTest):
    def test_edit_plain(self):
        rce = self.parse(
            "Sandbox",
            "",
            "https://en.wikipedia.org/w/index.php?diff=1",
            "127.0.0.1",
            "+12",
            "typo fix",
        )
        self.assertEqual(rce.eventtype, EventType.edit)
        self.assertEqual(rce.project, "en.wikipedia")
        self.assertEqual(rce.title, "Sandbox")
        self.assertEqual(rce.user, "127.0.0.1")
        self.assertEqual(rce.url, "https://en.wikipedia.org/w/index.php?diff=1")
        self.assertEqual(rce.comment, "typo fix")
        self.assertEqual(rce.szdiff, 12)
        self.assertFalse(rce.minor)
        self.assertFalse(rce.newpage)
        self.assertFalse(rce.botflag)

    def test_edit_flags(self):
        rce = self.parse("Sandbox", "MB", "url", "Bot", "+1", "x")
        self.assertTrue(rce.minor)
        self.assertFalse(rce.newpage)
        self.assertTrue(rce.botflag)
        rce = self.parse("Sandbox", "NB", "url", "Bot", "+1", "x")
        self.assertFalse(rce.minor)
        self.assertTrue(rce.newpage)
        self.assertTrue(rce.botflag)

    def test_negative_diff(self):
        self.assertEqual(self.parse("A", "", "u", "U", "-345", "c").szdiff, -345)

    def test_missing_diff(self):
        self.assertEqual(self.parse("A", "", "u", "U", "", "c").szdiff, 0)

    def test_namespace_is_translated(self):
        self.assertEqual(
            self.parse("File:A.png", "", "u", "U", "+1", "c").title, "Image:A.png"
        )

    def test_ignore_truncated_line(self):
        self.assertIsNone(self.reader.parse_message("#en.wikipedia", "\x0314[[garbage"))

    def test_ignore_unmonitored_project(self):
        rce = self.parse("Sandbox", "MB", "url", "Bot", "+1", "x", channel="#de.wikipedia")
        self.assertIsNone(rce)


class LogEventTest(RCReaderTest):
    def test_newusers_create(self):
        rce = self.parse(
            "Special:Log/newusers", "create", "", "Newbie", "", "created new account"
        )
        self.assertEqual(rce.eventtype, EventType.newuser)
        self.assertEqual(rce.user, "Newbie")

    def test_newusers_create2(self):
        rce = self.parse(
            "Special:Log/newusers", "create2", "", "Creator", "", "created new account User:Newbie: welcome"
        )
        self.assertEqual(rce.eventtype, EventType.newuser2)
        self.assertEqual(rce.user, "Creator")
        self.assertEqual(rce.title, "Newbie")

    def test_newusers_create2_localised(self):
        self.bot.prjlist.projects["nl.wikipedia"] = make_project("nl.wikipedia", "nl:")
        self.bot.prjlist.projects["nl.wikipedia"].namespaces["-1"] = "Speciaal"
        self.bot.prjlist.projects["nl.wikipedia"].namespaces["2"] = "Gebruiker"
        self.bot.prjlist.projects["nl.wikipedia"].generate_regexen()
        rce = self.parse(
            "Speciaal:Log/newusers", "create2", "", "Creator", "", "created new account Gebruiker:Newbie: welkom",
            channel="#nl.wikipedia"
        )
        self.assertEqual(rce.eventtype, EventType.newuser2)
        self.assertEqual(rce.user, "Creator")
        self.assertEqual(rce.title, "Newbie")

    def test_newusers_byemail(self):
        rce = self.parse(
            "Special:Log/newusers", "byemail", "", "Creator", "", "created new account User:Newbie: Requested account"
        )
        self.assertEqual(rce.eventtype, EventType.newuser2)
        self.assertEqual(rce.user, "Creator")
        self.assertEqual(rce.title, "Newbie")

    def test_newusers_autocreate(self):
        rce = self.parse(
            "Special:Log/newusers", "autocreate", "", "Newbie", "", "created new account"
        )
        self.assertEqual(rce.eventtype, EventType.autocreate)
        self.assertEqual(rce.user, "Newbie")

    def test_block_no_comment(self):
        rce = self.parse(
            "Special:Log/block", "block", "", "Admin", "",
            "blocked [[User:Vandal]] with an expiration time of 31 hours (account creation blocked)",
        )
        self.assertEqual(rce.eventtype, EventType.block)
        self.assertEqual(rce.user, "Admin")
        self.assertEqual(rce.title, "User:Vandal")
        self.assertEqual(rce.block_length, "31 hours")
        self.assertEqual(rce.comment, "")

    def test_block_comment(self):
        rce = self.parse(
            "Special:Log/block", "block", "", "Admin", "",
            "blocked [[User:Vandal]] with an expiration time of 31 hours (account creation blocked): spam",
        )
        self.assertEqual(rce.eventtype, EventType.block)
        self.assertEqual(rce.user, "Admin")
        self.assertEqual(rce.title, "User:Vandal")
        self.assertEqual(rce.block_length, "31 hours")
        self.assertEqual(rce.comment, "spam")

    def test_block_comment_fixed_expiry_(self):
        rce = self.parse(
            "Special:Log/block", "block", "", "Admin", "",
            "blocked [[User:Vandal]] with an expiration time of 00:00, 25 August 2026 (account creation blocked): spam",
        )
        self.assertEqual(rce.eventtype, EventType.block)
        self.assertEqual(rce.user, "Admin")
        self.assertEqual(rce.title, "User:Vandal")
        self.assertEqual(rce.block_length, "00:00, 25 August 2026")
        self.assertEqual(rce.comment, "spam")

    def test_reblock(self):
        rce = self.parse(
            "Special:Log/block", "reblock", "", "Admin", "",
            "changed block settings for [[User:Vandal]] with an expiration time of 1 second (autoblock disabled)",
        )
        self.assertEqual(rce.eventtype, EventType.block)
        self.assertEqual(rce.user, "Admin")
        self.assertEqual(rce.title, "User:Vandal")
        self.assertEqual(rce.block_length, "")
        self.assertEqual(rce.comment, "changed block settings for [[User:Vandal]] with an expiration time of 1 second (autoblock disabled)")

    def test_unblock_no_comment(self):
        rce = self.parse(
            "Special:Log/block", "unblock", "", "Admin", "", "unblocked User:Vandal"
        )
        self.assertEqual(rce.eventtype, EventType.unblock)
        self.assertEqual(rce.title, "User:Vandal")
        self.assertEqual(rce.block_length, "")
        self.assertEqual(rce.comment, "")

    def test_unblock_comment(self):
        rce = self.parse(
            "Special:Log/block", "unblock", "", "Admin", "", "unblocked User:Vandal: mistake"
        )
        self.assertEqual(rce.eventtype, EventType.unblock)
        self.assertEqual(rce.title, "User:Vandal")
        self.assertEqual(rce.block_length, "")
        self.assertEqual(rce.comment, "mistake")

    def test_unmatched_block_is_ignored(self):
        self.assertIsNone(
            self.parse("Special:Log/block", "block", "", "Admin", "", "did something")
        )

    def test_delete(self):
        rce = self.parse(
            "Special:Log/delete", "delete", "", "Admin", "", 'deleted "[[File:A.png]]": copyvio',
        )
        self.assertEqual(rce.eventtype, EventType.delete)
        self.assertEqual(rce.title, "Image:A.png", "namespace is translated")
        self.assertEqual(rce.comment, "copyvio")

    def test_restore(self):
        rce = self.parse(
            "Special:Log/delete", "restore", "", "Admin", "", 'restored "[[Sandbox]]"'
        )
        self.assertEqual(rce.eventtype, EventType.restore)

    def test_revision_delete_is_ignored(self):
        self.assertIsNone(
            self.parse(
                "Special:Log/delete", "revision", "", "Admin", "", "changed visibility of a revision"
            )
        )

    def test_protect_simpified_no_comment(self):
        # Simplified, not how MediaWiki actually emits this, given the introduction of restrictions addendum
        rce = self.parse(
            "Special:Log/protect", "protect", "", "Admin", "", 'protected "[[Sandbox]]"',
        )
        self.assertEqual(rce.eventtype, EventType.protect)
        self.assertEqual(rce.title, "Sandbox")
        self.assertEqual(rce.comment, "")

        rce = self.parse(
            "Special:Log/protect", "protect", "", "Admin", "", 'protected "[[Module:Message box/tmbox.css \u200E[edit=sysop] (indefinite)\u200E[move=sysop] (indefinite)]]": Highly visible template',
        )
        self.assertEqual(rce.eventtype, EventType.protect)
        self.assertEqual(rce.title, "Module:Message box/tmbox.css \u200E[edit=sysop] (indefinite)\u200E[move=sysop] (indefinite)")
        self.assertEqual(rce.comment, "Highly visible template")

    def test_protect_real_ltr_comment(self):
        rce = self.parse(
            "Special:Log/protect", "protect", "", "Admin", "", 'protected "[[Sandbox]]": vandalism',
        )
        self.assertEqual(rce.eventtype, EventType.protect)
        # TODO: Enable after restriction addendum if supported
        # self.assertEqual(rce.title, "Sandbox")
        self.assertEqual(rce.comment, "vandalism")

    def test_protect_real_rtl_comment(self):
        self.bot.prjlist.projects["he.wikipedia"] = make_project("he.wikipedia", "he:")
        self.bot.prjlist.projects["he.wikipedia"].namespaces["-1"] = "מיוחד"
        self.bot.prjlist.projects["he.wikipedia"].generate_regexen()
        rce = self.parse(
            "מיוחד:Log/protect", "protect", "", "Admin", "", 'protected "[[ויקיפדיה:ארגז חול \u200F[edit=autoconfirmed] (פגה ב־05:43, 25 באוגוסט 2026 (UTC))\u200F[move=autoconfirmed] (פגה ב־05:43, 25 באוגוסט 2026 (UTC))]]": some reason',
            channel="#he.wikipedia"
        )
        self.assertEqual(rce.eventtype, EventType.protect)
        # TODO: Enable after restriction addendum if supported
        # self.assertEqual(rce.title, "Sandbox")
        self.assertEqual(rce.comment, "some reason")

    def test_protect_unprotect(self):
        rce = self.parse(
            "Special:Log/protect", "unprotect", "", "Admin", "", 'removed protection from "[[Sandbox]]"',
        )
        self.assertEqual(rce.eventtype, EventType.unprotect)
        self.assertEqual(rce.title, "Sandbox")

    def test_protect_modify(self):
        rce = self.parse(
            "Special:Log/protect", "modify", "", "Admin", "", 'changed protection settings for "[[Sandbox]]"',
        )
        self.assertEqual(rce.eventtype, EventType.modifyprotect)
        self.assertEqual(rce.title, "Sandbox")

    def test_upload_no_comment(self):
        rce = self.parse(
            "Special:Log/upload", "upload", "", "Uploader", "", 'uploaded "[[File:A.png]]"',
        )
        self.assertEqual(rce.eventtype, EventType.upload)
        self.assertEqual(rce.title, "Image:A.png", "namespace is translated")
        self.assertEqual(rce.comment, "")

    def test_upload_comment(self):
        rce = self.parse(
            "Special:Log/upload", "upload", "", "Uploader", "", 'uploaded "[[File:A.png]]": my photo',
        )
        self.assertEqual(rce.eventtype, EventType.upload)
        self.assertEqual(rce.user, "Uploader")
        self.assertEqual(rce.title, "Image:A.png", "namespace is translated")
        self.assertEqual(rce.comment, "my photo")

    def test_move_comment(self):
        rce = self.parse(
            "Special:Log/move", "move", "", "Tango", "", "moved [[A]] to [[B]]: better title",
        )
        self.assertEqual(rce.eventtype, EventType.move)
        self.assertEqual(rce.user, "Tango")
        self.assertEqual(rce.title, "A")
        self.assertEqual(rce.moved_to, "B")
        self.assertEqual(rce.comment, "better title")
        self.assertEqual(rce.block_length, "https://en.wikipedia.org/wiki/A")

    def test_move_no_comment(self):
        rce = self.parse(
            "Special:Log/move", "move", "", "Tango", "", "moved [[A]] to [[B]]",
        )
        self.assertEqual(rce.eventtype, EventType.move)
        self.assertEqual(rce.user, "Tango")
        self.assertEqual(rce.title, "A")
        self.assertEqual(rce.moved_to, "B")
        self.assertEqual(rce.block_length, "https://en.wikipedia.org/wiki/A")
        self.assertEqual(rce.comment, "")

    def test_move_over_redirect(self):
        rce = self.parse(
            "Special:Log/move", "move_redir", "", "Tango", "", "moved [[A]] to [[B]] over redirect",
        )
        self.assertEqual(rce.eventtype, EventType.move)
        self.assertEqual(rce.user, "Tango")
        self.assertEqual(rce.moved_to, "B")
        self.assertEqual(rce.block_length, "https://en.wikipedia.org/wiki/A")
        self.assertEqual(rce.comment, "")

    def test_rights_is_ignored(self):
        self.assertIsNone(
            self.parse("Special:Log/rights", "rights", "", "Admin", "", "changed rights")
        )

    def test_import_is_ignored(self):
        self.assertIsNone(
            self.parse("Special:Log/import", "import", "", "Admin", "", "imported")
        )


if __name__ == "__main__":
    unittest.main()
