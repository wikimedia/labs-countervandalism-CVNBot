using System;
using System.Collections;
using System.IO;
using System.Threading;
using System.Xml;
using log4net;

namespace CVNBot
{
    class ProjectList : SortedList
    {
        ILog logger = LogManager.GetLogger("CVNBot.ProjectList");

        public string fnProjectsXML;
        public string currentBatchReloadChannel = "";

        /// <summary>
        /// Dumps all Projects to an XML file (Projects.xml)
        /// </summary>
        void DumpToFile()
        {
            logger.Info("Saving configuration to " + fnProjectsXML);
            StreamWriter sw = new StreamWriter(fnProjectsXML);
            sw.WriteLine("<projects>");
            foreach (DictionaryEntry dicent in this)
            {
                Project prj = (Project)dicent.Value;
                // Get each Project's details and append it to the XML file
                sw.WriteLine(prj.DumpProjectDetails());
            }
            sw.WriteLine("</projects>");
            sw.Flush();
            sw.Close();
        }

        /// <summary>
        /// Loads and initializes Projects from an XML file (Projects.xml)
        /// </summary>
        public void LoadFromFile()
        {
            logger.Info("Reading projects from " + fnProjectsXML);
            XmlDocument doc = new XmlDocument();
            doc.Load(fnProjectsXML);
            XmlNode parentnode = doc.FirstChild;
            for (int i = 0; i < parentnode.ChildNodes.Count; i++)
            {
                string prjDefinition = "<project>" + parentnode.ChildNodes[i].InnerXml + "</project>";
                Project prj = new Project();
                prj.ReadProjectDetails(prjDefinition);
                this.Add(prj.projectName, prj);
            }
        }

        /// <summary>
        /// Adds a new Project to the ProjectList. Remember to dump the configuration afterwards by calling dumpToFile()
        /// </summary>
        /// <param name="projectName">Name of the project (e.g., en.wikipedia) to add</param>
        /// <param name="interwiki">Interwiki link (e.g., it:s: -- can be empty string)</param>
        /// <param name="lang">ISO 639 lang code (e.g. "it", "es" Optional) </param>
        public void AddNewProject(string projectName, string interwiki, string lang = "")
        {
            if (interwiki == "")
            {
                // Try to guess interwiki

                if (!projectName.Contains("."))
                {
                    // Cannot guess; probably something like "mediawiki"
                    throw new Exception((String)Program.msgs["20004"]);
                }

                string langPortion = projectName.Split(new char[] { '.' }, 2)[0];
                string projPortion = projectName.Split(new char[] { '.' }, 2)[1];
                switch (projPortion)
                {
                    case "wikipedia":
                        interwiki = langPortion + ":";
                        break;
                    case "wiktionary":
                        interwiki = "wikt:" + langPortion + ":";
                        break;
                    case "wikibooks":
                        interwiki = "b:" + langPortion + ":";
                        break;
                    case "wikinews":
                        interwiki = "n:" + langPortion + ":";
                        break;
                    case "wikisource":
                        interwiki = "s:" + langPortion + ":";
                        break;
                    case "wikiquote":
                        interwiki = "q:" + langPortion + ":";
                        break;
                    case "wikiversity":
                        interwiki = "v:" + langPortion + ":";
                        break;
                    default:
                        throw new Exception((String)Program.msgs["20004"]);
                }
            }

            if (this.ContainsKey(projectName))
                throw new Exception(Program.GetFormatMessage(16400, projectName));

            logger.InfoFormat("Registering new project {0} with interwiki {1}", projectName, interwiki);
            Project prj = new Project();
            prj.projectName = projectName;
            prj.interwikiLink = interwiki;
            switch(projectName){
                case "mediawiki.wikipedia":
                    prj.langCode = "en";
                    prj.rooturl = "https://www.mediawiki.org/";
                    break;
                case "outreach.wikipedia":
                    prj.langCode = "en";
                    prj.rooturl = "https://outreach.wikimedia.org/";
                    break;
                case "testwikidata.wikipedia":
                    prj.langCode = "en";
                    prj.rooturl = "https://test.wikidata.org/";
                    break;
                case "wikidata.wikipedia":
                    prj.langCode = "en";
                    prj.rooturl = "https://www.wikidata.org/";
                    break;
                default:
                    prj.langCode = lang;
                    prj.rooturl = "https://" + projectName + ".org/";
                    break;
            }
            prj.RetrieveWikiDetails();
            this.Add(projectName, prj);
            // Join the new channel
            logger.InfoFormat("Joining RCReader channel: #{0}", projectName);
            Program.rcirc.rcirc.RfcJoin("#" + projectName);

            // Dump new settings
            DumpToFile();
        }

        /// <summary>
        /// Removes a project from the ProjectList
        /// </summary>
        /// <param name="projectName">Name of the project to remove</param>
        public void DeleteProject(string projectName)
        {
            if (!this.ContainsKey(projectName))
            {
                throw new Exception(Program.GetFormatMessage(16401, projectName));
            }

            logger.Info("Deleting existing project " + projectName);

            // Leave monitoring channel:
            logger.Info("Leaving #" + projectName);
            Program.rcirc.rcirc.RfcPart("#" + projectName, "No longer monitored");

            // Wait for existing RCEvents in separate thread to go through:
            Thread.Sleep(4000);

            // Finally, remove from list:
            this.Remove(projectName);

            // Dump new settings:
            DumpToFile();
        }

        public void ReloadAllWikis()
        {
            Thread.CurrentThread.Name = "ReloadAll";

            Program.SendMessageF(Meebey.SmartIrc4net.SendType.Message, currentBatchReloadChannel,
                                 "Request to reload all " + this.Count.ToString() + " wikis accepted.",
                                 Meebey.SmartIrc4net.Priority.High);

            foreach (DictionaryEntry dicent in this)
            {
                Project prj = (Project)dicent.Value;
                prj.RetrieveWikiDetails();
                Thread.Sleep(600);
            }

            // Dump new settings:
            DumpToFile();

            Program.SendMessageF(Meebey.SmartIrc4net.SendType.Message, currentBatchReloadChannel,
                                 "Reloaded all wikis. Phew, give the Wikimedia servers a break :(",
                                 Meebey.SmartIrc4net.Priority.High);
        }
    }
}
