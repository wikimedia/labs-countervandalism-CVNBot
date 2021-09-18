using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using log4net;

namespace CVNBot
{
    class Project
    {
        static ILog logger = LogManager.GetLogger("CVNBot.Project");

        public string projectName;
        public string interwikiLink;
        public string rooturl; // Format: https://en.wikipedia.org/

        public Regex rrestoreRegex;
        public Regex rdeleteRegex;
        public Regex rprotectRegex;
        public Regex runprotectRegex;
        public Regex rmodifyprotectRegex;
        public Regex ruploadRegex;
        public Regex rmoveRegex;
        public Regex rmoveredirRegex;
        public Regex rblockRegex;
        public Regex runblockRegex;
        public Regex rreblockRegex;
        public Regex rautosummBlank;
        public Regex rautosummReplace;
        public Regex rSpecialLogRegex;
        public Regex rCreate2Regex;

        public Hashtable namespaces;

        Dictionary<string, string> regexDict = new Dictionary<string, string>();

        static char[] rechars = {'\\', '.' ,'(', ')', '[' , ']' ,'^' ,'*' ,'+' ,'?' ,'{' ,'}' ,'|' };
        string snamespaces;

        /// <summary>
        /// Generates Regex objects from regex strings in class. Always generate the namespace list before calling this!
        /// </summary>
        void GenerateRegexen()
        {
            rrestoreRegex = new Regex(regexDict["restoreRegex"]);
            rdeleteRegex = new Regex(regexDict["deleteRegex"]);
            rprotectRegex = new Regex(regexDict["protectRegex"]);
            runprotectRegex = new Regex(regexDict["unprotectRegex"]);

            if (!regexDict.ContainsKey("modifyprotectRegex"))
            {
                // Added in CVNBot 1.20, fallback if missing in older XML files.
                regexDict["modifyprotectRegex"] = regexDict["protectRegex"];
                logger.Warn("generateRegexen: modifyprotectRegex is missing. Please reload this wiki.");
            }
            rmodifyprotectRegex = new Regex(regexDict["modifyprotectRegex"]);
            ruploadRegex = new Regex(regexDict["uploadRegex"]);
            rmoveRegex = new Regex(regexDict["moveRegex"]);
            rmoveredirRegex = new Regex(regexDict["moveredirRegex"]);
            rblockRegex = new Regex(regexDict["blockRegex"]);
            runblockRegex = new Regex(regexDict["unblockRegex"]);
            if (!regexDict.ContainsKey("reblockRegex")) {
                // Added in CVNBot 1.22, fallback if missing in older XML files.
                regexDict["reblockRegex"] = "^$";
                logger.Warn("generateRegexen: reblockRegex is missing. Please reload this wiki.");
            }
            rreblockRegex = new Regex(regexDict["reblockRegex"]);
            rautosummBlank = new Regex(regexDict["autosummBlank"]);
            rautosummReplace = new Regex(regexDict["autosummReplace"]);

            rSpecialLogRegex = new Regex(regexDict["specialLogRegex"]);

            rCreate2Regex = new Regex( namespaces["2"]+@":([^:]+)" );
        }

        public string DumpProjectDetails()
        {
            StringWriter output = new StringWriter();

            using (XmlTextWriter dump = new XmlTextWriter(output))
            {
                dump.WriteStartElement("project");

                dump.WriteElementString("projectName", projectName);
                dump.WriteElementString("interwikiLink", interwikiLink);
                dump.WriteElementString("rooturl", rooturl);
                dump.WriteElementString("speciallog", regexDict["specialLogRegex"]);
                dump.WriteElementString("namespaces", snamespaces.Replace("<?xml version=\"1.0\" encoding=\"utf-8\"?>", ""));

                dump.WriteElementString("restoreRegex", regexDict["restoreRegex"]);
                dump.WriteElementString("deleteRegex", regexDict["deleteRegex"]);
                dump.WriteElementString("protectRegex", regexDict["protectRegex"]);
                dump.WriteElementString("unprotectRegex", regexDict["unprotectRegex"]);
                dump.WriteElementString("modifyprotectRegex", regexDict["modifyprotectRegex"]);
                dump.WriteElementString("uploadRegex", regexDict["uploadRegex"]);
                dump.WriteElementString("moveRegex", regexDict["moveRegex"]);
                dump.WriteElementString("moveredirRegex", regexDict["moveredirRegex"]);
                dump.WriteElementString("blockRegex", regexDict["blockRegex"]);
                dump.WriteElementString("unblockRegex", regexDict["unblockRegex"]);
                dump.WriteElementString("reblockRegex", regexDict["reblockRegex"]);
                dump.WriteElementString("autosummBlank", regexDict["autosummBlank"]);
                dump.WriteElementString("autosummReplace", regexDict["autosummReplace"]);

                dump.WriteEndElement();
                dump.Flush();
            }

            return output.ToString();
        }

        public void ReadProjectDetails(string xml)
        {
            XmlDocument doc = new XmlDocument();
            doc.LoadXml(xml);
            XmlNode parentnode = doc.FirstChild;
            for (int i = 0; i < parentnode.ChildNodes.Count; i++)
            {
                string key = parentnode.ChildNodes[i].Name;
                string value = parentnode.ChildNodes[i].InnerText;
                switch (key)
                {
                    case "projectName": projectName = value; break;
                    case "interwikiLink": interwikiLink = value; break;
                    case "rooturl": rooturl = value; break;
                    case "speciallog": regexDict["specialLogRegex"] = value; break;
                    case "namespaces": snamespaces = value; break;
                    case "restoreRegex": regexDict["restoreRegex"] = value; break;
                    case "deleteRegex": regexDict["deleteRegex"] = value; break;
                    case "protectRegex": regexDict["protectRegex"] = value; break;
                    case "unprotectRegex": regexDict["unprotectRegex"] = value; break;
                    case "modifyprotectRegex": regexDict["modifyprotectRegex"] = value; break;
                    case "uploadRegex": regexDict["uploadRegex"] = value; break;
                    case "moveRegex": regexDict["moveRegex"] = value; break;
                    case "moveredirRegex": regexDict["moveredirRegex"] = value; break;
                    case "blockRegex": regexDict["blockRegex"] = value; break;
                    case "unblockRegex": regexDict["unblockRegex"] = value; break;
                    case "reblockRegex": regexDict["reblockRegex"] = value; break;
                    case "autosummBlank": regexDict["autosummBlank"] = value; break;
                    case "autosummReplace": regexDict["autosummReplace"] = value; break;
                }
            }
            // Always get namespaces before generating regexen
            GetNamespaces(true);
            // Regenerate regexen
            GenerateRegexen();
        }

        void GetNamespaces(bool snamespacesAlreadySet)
        {
            if (!snamespacesAlreadySet)
            {
                logger.InfoFormat("Fetching namespaces from {0}", rooturl);
                snamespaces = CVNBotUtils.GetRawDocument(rooturl + "w/api.php?format=xml&action=query&meta=siteinfo&siprop=namespaces");
                if (snamespaces == "")
                    throw new Exception("Can't load list of namespaces from " + rooturl);
            }

            namespaces = new Hashtable();

            XmlDocument doc = new XmlDocument();
            doc.LoadXml(snamespaces);
            string namespacesLogline = "";
            XmlNode namespacesNode = doc.GetElementsByTagName("namespaces")[0];
            for (int i = 0; i < namespacesNode.ChildNodes.Count; i++)
            {
                namespaces.Add(namespacesNode.ChildNodes[i].Attributes["id"].Value, namespacesNode.ChildNodes[i].InnerText);
                namespacesLogline += "id["+namespacesNode.ChildNodes[i].Attributes["id"].Value + "]="+namespacesNode.ChildNodes[i].InnerText + "; ";
            }
        }

        public struct MessagesOption
        {
            public int NumberOfArgs;
            public string RegexName;
            public bool NonStrictFlag;
            public MessagesOption (int ArgNumberOfArgs, string ArgRegexName, bool ArgNonStrictFlag)
            {
                NumberOfArgs = ArgNumberOfArgs;
                RegexName = ArgRegexName;
                NonStrictFlag = ArgNonStrictFlag;
            }
        }

        public void RetrieveWikiDetails()
        {
            //Find out what the localized Special: (ID -1) namespace is, and create a regex
            GetNamespaces(false);

            regexDict["specialLogRegex"] = namespaces["-1"] + @":.+?/(.+)";

            logger.InfoFormat("Fetching interface messages from {0}", rooturl);

            Dictionary<string, MessagesOption> Messages = new Dictionary<string, MessagesOption>();

            // Location of message, number of required parameters, reference to regex, allow lazy
            // Retrieve messages for all the required events and generate regexen for them

            Messages.Add("Undeletedarticle", new MessagesOption(1, "restoreRegex", false));
            Messages.Add("Deletedarticle", new MessagesOption(1, "deleteRegex", false));
            Messages.Add("Protectedarticle", new MessagesOption(1, "protectRegex", false));
            Messages.Add("Unprotectedarticle", new MessagesOption(1, "unprotectRegex", false));
            Messages.Add("Modifiedarticleprotection", new MessagesOption(1, "modifyprotectRegex", true));
            Messages.Add("Uploadedimage", new MessagesOption(0, "uploadRegex", false));
            Messages.Add("1movedto2",new MessagesOption(2, "moveRegex", false));
            Messages.Add("1movedto2_redir", new MessagesOption(2, "moveredirRegex", false));

            // blockRegex is nonStrict because some wikis override the message without including $2 (block length).
            // RCReader will fall back to "24 hours" if this is the case.
            // Some newer messages (e.g. https://lmo.wikipedia.org/wiki/MediaWiki:Blocklogentry) have a third item,
            // $3 ("anononly,nocreate,autoblock"). This may conflict with $2 detection.
            // Trying (changed 2 -> 3) to see if length of time will be correctly detected using just this method:
            Messages.Add("Blocklogentry", new MessagesOption(3, "blockRegex", true));

            Messages.Add("Unblocklogentry", new MessagesOption(0, "unblockRegex", false));
            Messages.Add("Reblock-logentry", new MessagesOption(3, "reblockRegex", false));
            Messages.Add("Autosumm-blank", new MessagesOption(0, "autosummBlank", false));

            // autosummReplace is nonStrict because some wikis use translations overrides without
            // a "$1" parameter for the content.
            Messages.Add("Autosumm-replace", new MessagesOption(1, "autosummReplace", true));

            GetInterfaceMessages(Messages);

            GenerateRegexen();
        }

        void GetInterfaceMessages(Dictionary<string, MessagesOption> Messages)
        {
            string CombinedMessages = string.Join("|", Messages.Keys);

            string sMwMessages = CVNBotUtils.GetRawDocument(
                rooturl +
                "w/api.php?action=query&meta=allmessages&format=xml" +
                "&ammessages=" + CombinedMessages
            );
            if (sMwMessages == "")
                throw new Exception("Can't load list of InterfaceMessages from " + rooturl);

            XmlDocument doc = new XmlDocument();
            doc.LoadXml(sMwMessages);
            string mwMessagesLogline = "";
            XmlNode allmessagesNode = doc.GetElementsByTagName("allmessages")[0];
            for (int i = 0; i < allmessagesNode.ChildNodes.Count; i++)
            {
                string elmName = allmessagesNode.ChildNodes[i].Attributes["name"].Value;
                GenerateRegex(
                    elmName,
                    allmessagesNode.ChildNodes[i].InnerText,
                    Messages[elmName].NumberOfArgs,
                    Messages[elmName].RegexName,
                    Messages[elmName].NonStrictFlag
                );
                mwMessagesLogline += "name[" + elmName + "]="+allmessagesNode.ChildNodes[i].InnerText + "; ";
            }
        }

        void GenerateRegex(string mwMessageTitle, string mwMessage, int reqCount, string destRegex, bool nonStrict)
        {
            // Now gently coax that into a regex
            foreach (char c in rechars)
                mwMessage = mwMessage.Replace(c.ToString(), @"\" + c.ToString());

            mwMessage = mwMessage.Replace("$1", "(?<item1>.+?)");
            mwMessage = mwMessage.Replace("$2", "(?<item2>.+?)");
            mwMessage = mwMessage.Replace("$3", "(?<item3>.+?)");
            mwMessage = mwMessage.Replace("$1", "(?:.+?)");
            mwMessage = mwMessage.Replace("$2", "(?:.+?)");
            mwMessage = mwMessage.Replace("$3", "(?:.+?)");
            mwMessage = mwMessage.Replace("$", @"\$");
            mwMessage = "^" + mwMessage + @"(?:: (?<comment>.*?))?$"; // Special:Log comments are preceded by a colon

            // Dirty code: Block log exceptions!
            if (mwMessageTitle == "Blocklogentry")
            {
                mwMessage = mwMessage.Replace("(?<item3>.+?)", "\\((?<item3>.+?)\\)");
                mwMessage = mwMessage.Replace(@"(?<item2>.+?)(?:: (?<comment>.*?))?$", "(?<item2>.+?)$");
            }

            try
            {
                Regex.Match("", mwMessage);
            }
            catch (Exception e)
            {
                throw new Exception("Failed to test-generate regex " + mwMessage + " for " + mwMessageTitle + "; " + e.Message);
            }

            if (reqCount >= 1)
            {
                if (!mwMessage.Contains(@"(?<item1>.+?)") && !nonStrict)
                    throw new Exception("Regex " + mwMessageTitle + " requires one or more items but item1 not found in "+mwMessage);
                if (reqCount >= 2)
                {
                    if (!mwMessage.Contains(@"(?<item2>.+?)") && !nonStrict)
                        throw new Exception("Regex " + mwMessageTitle + " requires two or more items but item2 not found in "+mwMessage);
                }
            }

            regexDict[destRegex] = mwMessage;
        }

        /// <summary>
        /// Gets the namespace code
        /// </summary>
        /// <param name="pageTitle">A page title, such as "Special:Helloworld" and "Helloworld"</param>
        /// <returns></returns>
        public int DetectNamespace(string pageTitle)
        {
            if (pageTitle.Contains(":"))
            {
                string nsLocal = pageTitle.Substring(0, pageTitle.IndexOf(':'));
                // Try to locate value (As fast as ContainsValue())
                foreach (DictionaryEntry de in this.namespaces)
                {
                    if ((string)de.Value == nsLocal)
                        return Convert.ToInt32(de.Key);
                }
            }
            // If no match for the prefix found, or if no colon,
            // assume main namespace
            return 0;
        }

        /// <summary>
        /// Returns a copy of the article title with the namespace translated into English
        /// </summary>
        /// <param name="originalTitle">Title in original (localized) language</param>
        /// <returns></returns>
        public static string TranslateNamespace(string project, string originalTitle)
        {
            if (originalTitle.Contains(":"))
            {
                string nsEnglish;

		        // *Don't change these* unless it's a stopping bug. These names are made part of the title
		        // in the watchlist and items database. (ie. don't change Image to File unless Image is broken)
		        // When they do need to be changed, make sure to make note in the RELEASE-NOTES that databases
		        // should be updated manually to keep all regexes and watchlists functional!
                switch (((Project)Program.prjlist[project]).DetectNamespace(originalTitle))
                {
                    case -2:
                        nsEnglish = "Media";
                        break;
                    case -1:
                        nsEnglish = "Special";
                        break;
                    case 1:
                        nsEnglish = "Talk";
                        break;
                    case 2:
                        nsEnglish = "User";
                        break;
                    case 3:
                        nsEnglish = "User talk";
                        break;
                    case 4:
                        nsEnglish = "Project";
                        break;
                    case 5:
                        nsEnglish = "Project talk";
                        break;
                    case 6:
                        nsEnglish = "Image";
                        break;
                    case 7:
                        nsEnglish = "Image talk";
                        break;
                    case 8:
                        nsEnglish = "MediaWiki";
                        break;
                    case 9:
                        nsEnglish = "MediaWiki talk";
                        break;
                    case 10:
                        nsEnglish = "Template";
                        break;
                    case 11:
                        nsEnglish = "Template talk";
                        break;
                    case 12:
                        nsEnglish = "Help";
                        break;
                    case 13:
                        nsEnglish = "Help talk";
                        break;
                    case 14:
                        nsEnglish = "Category";
                        break;
                    case 15:
                        nsEnglish = "Category talk";
                        break;
                    default:
                        return originalTitle;
                }

                // If we're still here, then nsEnglish has been set
                return nsEnglish + originalTitle.Substring(originalTitle.IndexOf(':'));
            }

			// Mainspace articles do not need translation
            return originalTitle;
        }
    }
}
