import unittest
import xml.etree.ElementTree as ElementTree

from cvnbot.project import Project
from cvnbot.utils import compile_dotnet

from .helpers import make_project


class GenerateRegexTest(unittest.TestCase):
    def test_generate_and_match_non_strict(self):
        project = Project()
        tmp = project.generate_regex(
            "didnot",
            "I did not $1 the yellow $2",
            2,
            True
        )
        match = compile_dotnet(tmp).search("I did not touch the yellow pencil")
        self.assertEqual(match.group("item1"), "touch")
        self.assertEqual(match.group("item2"), "pencil")
        self.assertEqual(match.group("comment"), None)
        match = compile_dotnet(tmp).search("I did not touch or steal the yellow pencil box: What if")
        self.assertEqual(match.group("item1"), "touch or steal")
        self.assertEqual(match.group("item2"), "pencil box")
        self.assertEqual(match.group("comment"), "What if")

    def test_generate_repeat_params(self):
        project = Project()
        tmp = project.generate_regex(
            "pulp-fiction",
            "Say what $2! I $1 you, I $1 dare you.",
            2,
            True
        )
        match = compile_dotnet(tmp).search("Say what again! I dare you, I double dare you.")
        self.assertEqual(match.group("item1"), "dare")
        self.assertEqual(match.group("item2"), "again")
        self.assertEqual(match.group("comment"), None)

    def test_missing_parameter_non_strict(self):
        project = Project()
        project.generate_regex("Blocklogentry", "blocked someone", 3, True)

    def test_generate_and_match_strict(self):
        project = Project()
        tmp = project.generate_regex("Deletedarticle", "deleted (a.b) [[$1]]", 1, False)
        self.assertEqual(
            r"^deleted \(a\.b\) \[\[(?<item1>.+?)\]\](?:: (?<comment>.*?))?$",
            tmp
        )
        match = compile_dotnet(tmp).search("deleted (a.b) [[Foo bar]]")
        self.assertEqual(match.group("item1"), "Foo bar")
        self.assertEqual(match.group("comment"), None)

    def test_missing_required_parameter_strict(self):
        project = Project()
        with self.assertRaises(Exception):
            project.generate_regex("Deletedarticle", "deleted a page", 1, False)

    def test_helpers_example(self):
        project = make_project()
        match = project.rdelete_regex.search('deleted "[[Spam]]"')
        self.assertEqual(match.group("item1"), "Spam")
        self.assertIsNone(match.group("comment"))
        match = project.rdelete_regex.search('deleted "[[Spam]]": Vandalism')
        self.assertEqual(match.group("item1"), "Spam")
        self.assertEqual(match.group("comment"), "Vandalism")
        match = project.rmove_regex.search("moved [[A]] to [[B]]: typo")
        self.assertEqual(match.group("item1"), "A")
        self.assertEqual(match.group("item2"), "B")
        match = project.rmoveredir_regex.search(
            "moved [[A]] to [[B]] over redirect"
        )
        self.assertEqual(match.group("item2"), "B")
        match = project.rblock_regex.search(
            "blocked [[User:Vandal]] with an expiration time of 31 hours (account creation blocked)"
        )
        self.assertEqual(match.group("item1"), "User:Vandal")
        self.assertEqual(match.group("item2"), "31 hours")
        match = project.rupload_regex.search('uploaded "[[File:A.png]]"')
        self.assertEqual(match.group("item1"), "File:A.png")
        self.assertEqual(
            project.rspecial_log_regex.search("Special:Log/newusers").group(1),
            "newusers",
        )
        self.assertEqual(
            project.rcreate2_regex.search(
                "created new account User:Newbie"
            ).group(1),
            "Newbie",
        )
        match = project.rautosumm_replace.search("Replaced content with 'poop'")
        self.assertEqual(match.group("item1"), "poop")


class NamespaceTest(unittest.TestCase):
    def setUp(self):
        self.project = make_project()

    def test_namespaces(self):
        self.assertEqual(self.project.namespaces["-1"], "Special")
        self.assertEqual(self.project.namespaces["2"], "User")
        self.assertEqual(self.project.namespaces["0"], "")

    def test_detect_namespace(self):
        self.assertEqual(self.project.detect_namespace("User:Foo"), 2)
        self.assertEqual(self.project.detect_namespace("Special:Log/block"), -1)
        self.assertEqual(self.project.detect_namespace("Sandbox"), 0)
        self.assertEqual(self.project.detect_namespace("Foo:Bar"), 0)

    def test_translate_namespace(self):
        self.assertEqual(self.project.translate_namespace("File:A.png"), "Image:A.png")
        self.assertEqual(
            self.project.translate_namespace("Wikipedia:Sandbox"), "Project:Sandbox"
        )
        self.assertEqual(self.project.translate_namespace("Sandbox"), "Sandbox")

    def test_translate_namespace_unknown_prefix(self):
        self.assertEqual(self.project.translate_namespace("Foo:Bar"), "Foo:Bar")


class PersistenceTest(unittest.TestCase):
    def test_dump_and_read_roundtrip(self):
        original = make_project()
        xml = "<projects>" + original.dump_project_details() + "</projects>"

        root = ElementTree.fromstring(xml)
        restored = Project()
        restored.read_project_details(root[0])

        self.assertEqual(restored.project_name, original.project_name)
        self.assertEqual(restored.interwiki_link, original.interwiki_link)
        self.assertEqual(restored.rooturl, original.rooturl)
        self.assertEqual(restored.regex_dict, original.regex_dict)
        self.assertEqual(restored.namespaces, original.namespaces)
        self.assertEqual(
            restored.rdelete_regex.pattern,
            original.rdelete_regex.pattern
        )

    def test_dump_escapes_xml(self):
        project = make_project()
        project.regex_dict["deleteRegex"] = "a<b&c"
        self.assertIn("a&lt;b&amp;c", project.dump_project_details())

    def test_read_upgrade_old_files(self):
        project = make_project()
        xml = ElementTree.fromstring(
            "<projects>" + project.dump_project_details() + "</projects>"
        )
        element = xml[0]
        for tag in ("modifyprotectRegex", "reblockRegex"):
            element.remove(element.find(tag))

        restored = Project()
        restored.read_project_details(element)
        self.assertEqual(
            restored.regex_dict["modifyprotectRegex"],
            restored.regex_dict["protectRegex"]
        )
        self.assertEqual(restored.regex_dict["reblockRegex"], "^$")


if __name__ == "__main__":
    unittest.main()
