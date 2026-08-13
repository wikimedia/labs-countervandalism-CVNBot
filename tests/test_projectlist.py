import os
import tempfile
import unittest
from unittest import mock

from cvnbot.projectlist import ProjectList

from .helpers import destroy_bot, make_bot, make_project


class ProjectListTest(unittest.TestCase):
    def setUp(self):
        self.bot = make_bot(with_project=False)
        self.prjlist = self.bot.prjlist
        self.prjlist.fn_projects_xml = "Unused.xml"

    def tearDown(self):
        destroy_bot(self.bot)

    def test_keys(self):
        prjlist = ProjectList(self.bot)
        for name in ("zh.wikipedia", "ar.wikipedia", "nl.wikipedia"):
            prjlist.projects[name] = make_project(name)

        self.assertEqual(
            prjlist.keys(),
            ["ar.wikipedia", "nl.wikipedia", "zh.wikipedia"],
            "present and sorted"
        )

    def test_contains(self):
        self.prjlist.projects["en.wikipedia"] = make_project("en.wikipedia")
        self.prjlist.projects["nl.wikipedia"] = make_project("nl.wikipedia")

        self.assertTrue("en.wikipedia" in self.prjlist)
        self.assertTrue("nl.wikipedia" in self.prjlist)
        self.assertFalse("xx.wikipedia" in self.prjlist)
        self.assertFalse("xx.example" in self.prjlist)

    def test_getitem(self):
        self.prjlist.projects["en.wikipedia"] = make_project("en.wikipedia")
        self.assertEqual(self.prjlist["en.wikipedia"].project_name, "en.wikipedia")

    def test_translate_namespace_unknown_project(self):
        self.assertEqual(
            self.prjlist.translate_namespace("xx.example", "File:A.png"),
            "File:A.png",
        )


class ProjectListFileTest(unittest.TestCase):
    def setUp(self):
        self.bot = make_bot(with_project=False)
        self.prjlist = self.bot.prjlist
        handle, self.path = tempfile.mkstemp(suffix=".xml")
        os.close(handle)
        # Delete so that we can test graceful loading, and autocreation in load and dump
        os.unlink(self.path)
        self.prjlist.fn_projects_xml = self.path
        self.patcher = mock.patch(
            "cvnbot.project.Project.retrieve_wiki_details", autospec=True
        )
        retrieve = self.patcher.start()
        retrieve.side_effect = self._fake_retrieve

    def tearDown(self):
        self.patcher.stop()
        destroy_bot(self.bot)
        if os.path.exists(self.path):
            os.unlink(self.path)

    @staticmethod
    def _fake_retrieve(project):
        template = make_project(project.project_name, project.interwiki_link)
        project.snamespaces = template.snamespaces
        project.namespaces = template.namespaces
        project.regex_dict = template.regex_dict

    def test_load_graceful_autocreate(self):
        self.assertFalse(os.path.exists(self.path))
        self.prjlist.load_from_file()
        self.assertTrue(os.path.exists(self.path))
        with open(self.path, 'r') as f:
            self.assertEqual('<projects>\n</projects>\n', f.read())

    def test_dump_and_load_roundtrip(self):
        self.prjlist.projects["en.wikipedia"] = make_project("en.wikipedia", "en:")
        self.prjlist.projects["de.wikipedia"] = make_project("de.wikipedia", "de:")
        self.prjlist.dump_to_file()

        restored = ProjectList(self.bot)
        restored.fn_projects_xml = self.path
        restored.load_from_file()

        self.assertEqual(restored.keys(), ["de.wikipedia", "en.wikipedia"])
        self.assertEqual(restored["de.wikipedia"].interwiki_link, "de:")
        self.assertEqual(
            restored["en.wikipedia"].regex_dict,
            self.prjlist["en.wikipedia"].regex_dict,
        )

    def add(self, name, interwiki=""):
        self.prjlist.add_new_project(name, interwiki)
        return self.prjlist[name]

    def test_interwiki_link_default_for_wikipedia(self):
        self.assertEqual(self.add("nl.wikipedia").interwiki_link, "nl:")

    def test_interwiki_link_default_for_sister_projects(self):
        self.assertEqual(self.add("en.wikisource").interwiki_link, "s:en:")
        self.assertEqual(self.add("de.wiktionary").interwiki_link, "wikt:de:")

    def test_interwiki_link_explicit(self):
        self.assertEqual(self.add("en.wikipedia", "w:en:").interwiki_link, "w:en:")

    def test_root_url(self):
        self.assertEqual(self.add("nl.wikipedia").rooturl, "https://nl.wikipedia.org/")
        self.assertEqual(
            self.add("wikidata.wikipedia").rooturl, "https://www.wikidata.org/"
        )

    def test_special_name_is_refused(self):
        with self.assertRaises(Exception):
            self.add("mediawiki")
        with self.assertRaises(Exception):
            self.add("en.wikithing")

    def test_duplicate_is_refused(self):
        self.add("nl.wikipedia")
        with self.assertRaises(Exception):
            self.add("nl.wikipedia")

    def test_add_new_project_joins_rcreader(self):
        self.add("nl.wikipedia")
        self.assertEqual(self.bot.rcreader.rcirc.joined, ["#nl.wikipedia"])
        self.assertTrue(os.path.getsize(self.path) > 0)

    def test_delete_removes_and_saves(self):
        self.add("nl.wikipedia")
        with mock.patch("cvnbot.projectlist.time.sleep"):
            self.prjlist.delete_project("nl.wikipedia")
        self.assertNotIn("nl.wikipedia", self.prjlist)
        self.assertEqual(self.bot.rcreader.rcirc.parted, ["#nl.wikipedia"])

    def test_delete_unknown_project_is_refused(self):
        with self.assertRaises(Exception):
            self.prjlist.delete_project("nl.wikipedia")


if __name__ == "__main__":
    unittest.main()
