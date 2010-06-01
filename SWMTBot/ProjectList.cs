using System;
using System.Collections;
using System.Text;
using System.Xml;
using System.IO;
using System.Threading;
using log4net;

namespace SWMTBot
{
    class ProjectList : SortedList
    {
        private ILog logger = LogManager.GetLogger("SWMTBot.ProjectList");

        public string fnProjectsXML;

        /// <summary>
        /// Dumps all Projects to an XML file (Projects.xml)
        /// </summary>
        void dumpToFile()
        {
            logger.Info("Saving configuration to " + fnProjectsXML);
            StreamWriter sw = new StreamWriter(fnProjectsXML);
            sw.WriteLine("<projects>");
            foreach (DictionaryEntry dicent in this)
            {
                Project prj = (Project)dicent.Value;
                //Get each Project's details and append it to the XML file
                sw.WriteLine(prj.dumpProjectDetails());
            }
            sw.WriteLine("</projects>");
            sw.Flush();
            sw.Close();
        }

        /// <summary>
        /// Loads and initializes Projects from an XML file (Projects.xml)
        /// </summary>
        public void loadFromFile()
        {
            logger.Info("Reading projects from " + fnProjectsXML);
            XmlDocument doc = new XmlDocument();
            doc.Load(fnProjectsXML);
            XmlNode parentnode = doc.FirstChild;
            for (int i = 0; i < parentnode.ChildNodes.Count; i++)
            {
                string prjDefinition = "<project>" + parentnode.ChildNodes[i].InnerXml + "</project>";
                Project prj = new Project();
                prj.readProjectDetails(prjDefinition);
                logger.Info("Registering " + prj.projectName);
                this.Add(prj.projectName, prj);
            }
        }

        /// <summary>
        /// Adds a new Project to the ProjectList. Remember to dump the configuration afterwards by calling dumpToFile()
        /// </summary>
        /// <param name="projectName">Name of the project (e.g., en.wikipedia) to add</param>
        /// <param name="interwiki">Interwiki link (e.g., it:s: -- can be empty string)</param>
        public void addNewProject(string projectName, string interwiki)
        {
            if (interwiki == "")
            {
                //Try to guess interwiki

                if (!projectName.Contains("."))
                {
                    //Cannot guess; probably something like "mediawiki"
                    throw new Exception((String)Program.msgs["20004"]);
                }

                string langPortion = projectName.Split(new char[1] { '.' }, 2)[0];
                string projPortion = projectName.Split(new char[1] { '.' }, 2)[1];
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
                throw new Exception(Program.getFormatMessage(16400, projectName));

            logger.Info("Registering new project " + projectName + " with interwiki " + interwiki);
            Project prj = new Project();
            prj.projectName = projectName;
            prj.interwikiLink = interwiki;
            prj.rooturl = "http://" + projectName + ".org/";
            prj.retrieveWikiDetails();
            this.Add(projectName, prj);
            //Join the new channel:
            logger.Info("Joining #" + projectName);
            Program.rcirc.rcirc.RfcJoin("#" + projectName);

            //Dump new settings:
            dumpToFile();
        }

        /// <summary>
        /// Removes a project from the ProjectList
        /// </summary>
        /// <param name="projectName">Name of the project to remove</param>
        public void deleteProject(string projectName)
        {
            if (!this.ContainsKey(projectName))
            {
                throw new Exception(Program.getFormatMessage(16401, projectName));
            }

            logger.Info("Deleting existing project " + projectName);

            //Leave monitoring channel:
            logger.Info("Leaving #" + projectName);
            Program.rcirc.rcirc.RfcPart("#" + projectName, "No longer monitored");

            //Wait for existing RCEvents in separate thread to go through:
            Thread.Sleep(4000);

            //Finally, remove from list:
            this.Remove(projectName);

            //Dump new settings:
            dumpToFile();
        }
    }
}
