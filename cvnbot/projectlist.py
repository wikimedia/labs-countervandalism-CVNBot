import logging
import time
import xml.etree.ElementTree as ElementTree

from .ircclient import Priority, SendType
from .project import Project

logger = logging.getLogger("CVNBot.ProjectList")


class ProjectList:
    """The list of monitored wikis, stored as Projects.xml."""

    def __init__(self, program):
        self.bot = program
        self.projects = {}
        self.fn_projects_xml = ""

    # -- Map interface ----------------------------------------------------

    def __contains__(self, project_name):
        return project_name in self.projects

    def __getitem__(self, project_name):
        return self.projects[project_name]

    def __iter__(self):
        return iter(self.keys())

    def __len__(self):
        return len(self.projects)

    def keys(self):
        return sorted(self.projects)

    def values(self):
        return [self.projects[name] for name in self.keys()]

    def get(self, project_name, default=None):
        return self.projects.get(project_name, default)

    # -- Persistence ------------------------------------------------------

    def dump_to_file(self):
        """Save all project details to Projects.xml."""
        logger.info("Saving configuration to %s", self.fn_projects_xml)

        with open(self.fn_projects_xml, "w", encoding="utf-8") as handle:
            handle.write("<projects>\n")
            for project in self.values():
                # Get each Project's details and append it to the XML file
                handle.write(project.dump_project_details() + "\n")
            handle.write("</projects>\n")

    def load_from_file(self):
        try:
            root = ElementTree.parse(self.fn_projects_xml).getroot()
        except FileNotFoundError:
            logger.info("Creating new projects file at %s", self.fn_projects_xml)
            self.dump_to_file()
            return

        logger.info("Reading projects from %s", self.fn_projects_xml)
        for element in root:
            project = Project()
            project.read_project_details(element)
            self.projects[project.project_name] = project

    def add_new_project(self, project_name, interwiki):
        """
        Download new project details, start monitoring, and sync to Projects.xml.

        Args:
            string project_name: Name of the project e.g., "en.wikipedia"
            string interwiki: Interwiki link, e.g., "it:s:", can be empty string
                              in which case it is guessed from the project name,
                              e.g. "en.wikisource" -> "s:en:"
                              and "nl.wikipedia" -> "nl:"
        """
        if interwiki == "":
            # Try to guess interwiki
            if "." not in project_name:
                # Cannot guess; probably something like "mediawiki"
                raise Exception(self.bot.msgs["20004"])
            lang_portion, proj_portion = project_name.split(".", 1)
            # Interwiki prefixes per project family
            INTERWIKI_PREFIXES = {
                "wikipedia": "",
                "wiktionary": "wikt:",
                "wikibooks": "b:",
                "wikinews": "n:",
                "wikisource": "s:",
                "wikiquote": "q:",
                "wikiversity": "v:",
            }
            if proj_portion not in INTERWIKI_PREFIXES:
                raise Exception(self.bot.msgs["20004"])
            interwiki = INTERWIKI_PREFIXES[proj_portion] + lang_portion + ":"

        if project_name in self.projects:
            raise Exception(self.bot.msgs.format(16400, project_name))

        logger.info(
            "Registering new project %s with interwiki %s", project_name, interwiki
        )
        project = Project()
        project.project_name = project_name
        project.interwiki_link = interwiki

        # Wikis whose project name does not match their domain name
        SPECIAL_ROOT_URLS = {
            "mediawiki.wikipedia": "https://www.mediawiki.org/",
            "outreach.wikipedia": "https://outreach.wikimedia.org/",
            "testwikidata.wikipedia": "https://test.wikidata.org/",
            "wikidata.wikipedia": "https://www.wikidata.org/",
        }
        project.rooturl = SPECIAL_ROOT_URLS.get(project_name, "https://{0}.org/".format(project_name))
        project.retrieve_wiki_details()
        self.projects[project_name] = project

        # Join the new channel
        logger.info("Joining RCReader channel: #%s", project_name)
        self.bot.rcreader.rcirc.rfc_join("#" + project_name)

        self.dump_to_file()

    def delete_project(self, project_name):
        """Remove a project from the list, stop monitoring, and sync to Projects.xml."""
        if project_name not in self.projects:
            raise Exception(self.bot.msgs.format(16401, project_name))

        logger.info("Deleting existing project %s", project_name)

        # Leave monitoring channel
        logger.info("Leaving #%s", project_name)
        self.bot.rcreader.rcirc.rfc_part("#" + project_name, "No longer monitored")

        # Wait for existing RCEvents in separate thread to go through
        time.sleep(4)

        # Finally, remove from list
        del self.projects[project_name]

        self.dump_to_file()

    def reload_all_wikis(self, origin_channel):
        """
        Redownload the details of all projects and sync to Projects.xml.

        This will block for a long time and should run in its own thread.
        """
        self.bot.send_message(
            SendType.MESSAGE,
            origin_channel,
            "Request to reload all {0} wikis accepted.".format(len(self)),
            Priority.HIGH,
        )

        for project in self.values():
            project.retrieve_wiki_details()
            time.sleep(0.6)

        self.dump_to_file()

        self.bot.send_message(
            SendType.MESSAGE,
            origin_channel,
            "Reloaded all wikis. Phew, give the Wikimedia servers a break :(",
            Priority.HIGH,
        )

    # -- Namespaces -------------------------------------------------------

    def translate_namespace(self, project_name, original_title):
        """Translate a local title's namespace to canonical English."""
        project = self.projects.get(project_name)
        if project is None:
            return original_title
        return project.translate_namespace(original_title)
