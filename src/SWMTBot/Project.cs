using System;
using System.Collections;
using System.Text;
using System.IO;
using System.Xml;
using System.Text.RegularExpressions;
using log4net;

namespace SWMTBot
{
    class Project
    {
        private static ILog logger = LogManager.GetLogger("SWMTBot.Project");
        
        public string projectName;
        public string interwikiLink;
        public string rooturl; //Format: http://en.wikipedia.org/

        //See RCParser.py
        string restoreRegex;
        string deleteRegex;
        string protectRegex;
        string unprotectRegex;
        string modifyprotectRegex;
        string uploadRegex;
        string moveRegex;
        string moveredirRegex;
        string renameuserRegex;
        string rightsRegex;
        string blockRegex;
        string unblockRegex;
        string rollbackRegex;
        string undoRegex;
        //New user accounts are detected by their flags
        //New to SWMTBot:
        string autosummBlank;
        string autosummReplace;

        public Regex rrestoreRegex;
        public Regex rdeleteRegex;
        public Regex rprotectRegex;
        public Regex runprotectRegex;
        public Regex rmodifyprotectRegex;
        public Regex ruploadRegex;
        public Regex rmoveRegex;
        public Regex rmoveredirRegex;
        public Regex rrenameuserRegex;
        public Regex rrightsRegex;
        public Regex rblockRegex;
        public Regex runblockRegex;
        public Regex rrollbackRegex;
        public Regex rundoRegex;
        public Regex rautosummBlank;
        public Regex rautosummReplace;

        public String SpecialLogRegex;
        public Regex rSpecialLogRegex;

        public Regex rCreate2Regex;

        static char[] rechars = {'\\', '.' ,'(', ')', '[' , ']' ,'^' ,'*' ,'+' ,'?' ,'{' ,'}' ,'|' };

        public Hashtable namespaces;
        string snamespaces;

        /// <summary>
        /// Generates Regex objects from regex strings in class. Always generate the namespace list before calling this!
        /// </summary>
        void generateRegexen()
        {
            rrestoreRegex = new Regex(restoreRegex);
            rdeleteRegex = new Regex(deleteRegex);
            rprotectRegex = new Regex(protectRegex);
            runprotectRegex = new Regex(unprotectRegex);
            
            // modifyprotectRegex was recently added, may not exist in Projects.xml yet
            // fall back to protectregex (to avoid failure) and WARN that reload is needed
            if (modifyprotectRegex == null)
            {
                modifyprotectRegex = protectRegex;
                logger.Warn("generateRegexen: modifyprotectRegex is null. Please try a reload for this wiki to populate it.");
            }
            rmodifyprotectRegex = new Regex(modifyprotectRegex);
            ruploadRegex = new Regex(uploadRegex);
            rmoveRegex = new Regex(moveRegex);
            rmoveredirRegex = new Regex(moveredirRegex);
            rrenameuserRegex = new Regex(renameuserRegex);
            rrightsRegex = new Regex(rightsRegex);
            rblockRegex = new Regex(blockRegex);
            runblockRegex = new Regex(unblockRegex);
            rrollbackRegex = new Regex(rollbackRegex);
            rundoRegex = new Regex(undoRegex);
            rautosummBlank = new Regex(autosummBlank);
            rautosummReplace = new Regex(autosummReplace);

            rSpecialLogRegex = new Regex(SpecialLogRegex);

            rCreate2Regex = new Regex( namespaces["2"]+@":(.*): \[\[" );
        }
        
        public string dumpProjectDetails()
        {
            StringWriter output = new StringWriter();

            XmlTextWriter dump = new XmlTextWriter(output);
            dump.WriteStartElement("project");
            
            dump.WriteElementString("projectName", projectName);
            dump.WriteElementString("interwikiLink", interwikiLink);
            dump.WriteElementString("rooturl", rooturl);
            dump.WriteElementString("speciallog", SpecialLogRegex);
            dump.WriteElementString("namespaces", snamespaces.Replace("<?xml version=\"1.0\" encoding=\"utf-8\"?>", ""));

            dump.WriteElementString("restoreRegex", restoreRegex);
            dump.WriteElementString("deleteRegex", deleteRegex);
            dump.WriteElementString("protectRegex", protectRegex);
            dump.WriteElementString("unprotectRegex", unprotectRegex);
            dump.WriteElementString("modifyprotectRegex", modifyprotectRegex);
            dump.WriteElementString("uploadRegex", uploadRegex);
            dump.WriteElementString("moveRegex", moveRegex);
            dump.WriteElementString("moveredirRegex", moveredirRegex);
            dump.WriteElementString("renameuserRegex", renameuserRegex);
            dump.WriteElementString("rightsRegex", rightsRegex);
            dump.WriteElementString("blockRegex", blockRegex);
            dump.WriteElementString("unblockRegex", unblockRegex);
            dump.WriteElementString("rollbackRegex", rollbackRegex);
            dump.WriteElementString("undoRegex", undoRegex);
            dump.WriteElementString("autosummBlank", autosummBlank);
            dump.WriteElementString("autosummReplace", autosummReplace);

            dump.WriteEndElement();
            dump.Flush();

            return output.ToString();
        }

        public void readProjectDetails(string xml)
        {
            XmlDocument doc = new XmlDocument();
            doc.LoadXml(xml);
            XmlNode parentnode = doc.FirstChild;
            for (int i = 0; i < parentnode.ChildNodes.Count; i++)
            {
                switch (parentnode.ChildNodes[i].Name)
                {
                    case "projectName": projectName = parentnode.ChildNodes[i].InnerText; break;
                    case "interwikiLink": interwikiLink = parentnode.ChildNodes[i].InnerText; break;
                    case "rooturl": rooturl = parentnode.ChildNodes[i].InnerText; break;
                    case "speciallog": SpecialLogRegex = parentnode.ChildNodes[i].InnerText; break;
                    case "namespaces": snamespaces = parentnode.ChildNodes[i].InnerText; break;

                    case "restoreRegex": restoreRegex = parentnode.ChildNodes[i].InnerText; break;
                    case "deleteRegex": deleteRegex = parentnode.ChildNodes[i].InnerText; break;
                    case "protectRegex": protectRegex = parentnode.ChildNodes[i].InnerText; break;
                    case "unprotectRegex": unprotectRegex = parentnode.ChildNodes[i].InnerText; break;
                    case "modifyprotectRegex": modifyprotectRegex = parentnode.ChildNodes[i].InnerText; break;
                    case "uploadRegex": uploadRegex = parentnode.ChildNodes[i].InnerText; break;
                    case "moveRegex": moveRegex = parentnode.ChildNodes[i].InnerText; break;
                    case "moveredirRegex": moveredirRegex = parentnode.ChildNodes[i].InnerText; break;
                    case "renameuserRegex": renameuserRegex = parentnode.ChildNodes[i].InnerText; break;
                    case "rightsRegex": rightsRegex = parentnode.ChildNodes[i].InnerText; break;
                    case "blockRegex": blockRegex = parentnode.ChildNodes[i].InnerText; break;
                    case "unblockRegex": unblockRegex = parentnode.ChildNodes[i].InnerText; break;
                    case "rollbackRegex": rollbackRegex = parentnode.ChildNodes[i].InnerText; break;
                    case "undoRegex": undoRegex = parentnode.ChildNodes[i].InnerText; break;
                    case "autosummBlank": autosummBlank = parentnode.ChildNodes[i].InnerText; break;
                    case "autosummReplace": autosummReplace = parentnode.ChildNodes[i].InnerText; break;
                }
            } 
            //Always get namespaces before generating regexen
            getNamespaces(true);
            //Regenerate regexen
            generateRegexen();
        }

        void getNamespaces(bool snamespacesAlreadySet)
        {
            if (!snamespacesAlreadySet)
            {
                snamespaces = SWMTUtils.getRawDocument(rooturl + "w/api.php?action=query&meta=siteinfo&siprop=namespaces&format=xml");
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
            logger.Info("getNamespaces: "+namespacesLogline);
        }

        public void retrieveWikiDetails()
        {
            //Find out what the localized Special: (ID -1) namespace is, and create a regex
            getNamespaces(false);
            
            SpecialLogRegex = namespaces["-1"] + @":.+?/(.+)";

            // Location of message, number of required parameters, reference to regex, allow lazy

            //Retrieve messages for all the required events and generate regexen for them
            generateRegex("MediaWiki:Undeletedarticle", 1, ref restoreRegex, false);
            //ms.wt is missing the all important $1: what was undeleted. Can't support this, so no lazifying:
            //Link: http://ms.wiktionary.org/w/index.php?title=MediaWiki:Undeletedarticle&action=edit :
            generateRegex("MediaWiki:Deletedarticle", 1, ref deleteRegex, false);
            generateRegex("MediaWiki:Protectedarticle", 1, ref protectRegex, false);
            generateRegex("MediaWiki:Unprotectedarticle", 1, ref unprotectRegex, false);
            generateRegex("MediaWiki:Modifiedarticleprotection", 1, ref modifyprotectRegex, true);
            generateRegex("MediaWiki:Uploadedimage", 0, ref uploadRegex, false);
            generateRegex("MediaWiki:1movedto2", 2, ref moveRegex, false);
            generateRegex("MediaWiki:1movedto2_redir", 2, ref moveredirRegex, false);
            //mg.wp is missing details. However, not using it yet, so ignore:
            generateRegex("MediaWiki:Renameuserlog", 2, ref renameuserRegex, true);
            generateRegex("MediaWiki:Bureaucratlogentry", 0, ref rightsRegex, false);
            //Sometimes $2, block length, is not included. Our functions fall back to "96 hours" if this is the case:
            //Some newer messages (e.g. http://lmo.wikipedia.org/wiki/MediaWiki:Blocklogentry have a third item, $3: "anononly,nocreate,autoblock", for example.
            //This may conflict with $2 detection.
            //Trying (changed 2 -> 3) to see if length of time will be correctly detected using just this method:
            generateRegex("MediaWiki:Blocklogentry", 3, ref blockRegex, true);
            generateRegex("MediaWiki:Unblocklogentry", 0, ref unblockRegex, false);
            //Sometimes the details of the rollback are not included. We're not using this message, so ignore:
            generateRegex("MediaWiki:Revertpage", 2, ref rollbackRegex, true);
            generateRegex("MediaWiki:Undo-summary", 2, ref undoRegex, false);
            generateRegex("MediaWiki:Autosumm-blank", 0, ref autosummBlank, false);
            //Some large wikis are not including the "profanity" in their messages (privacy measure?)
            generateRegex("MediaWiki:Autosumm-replace", 1, ref autosummReplace, true);

            generateRegexen();
        }        

        /*
         * Equivalent to function getre in RCParser.py
         */
        void generateRegex(string mwMessageTitle, int reqCount, ref string destRegex, bool nonStrict)
        {
            //Get raw wikitext
            string mwMessage = SWMTUtils.getRawDocument(rooturl + "w/index.php?title=" + mwMessageTitle + "&action=raw&usemsgcache=yes");

            //Now gently coax that into a regex
            foreach (char c in rechars)
                mwMessage = mwMessage.Replace(c.ToString(), @"\" + c);
            mwMessage = mwMessage.Replace("$1", "(?<item1>.+?)");
            mwMessage = mwMessage.Replace("$2", "(?<item2>.+?)");
            mwMessage = mwMessage.Replace("$3", "(?<item3>.+?)");
            mwMessage = mwMessage.Replace("$1", "(?:.+?)");
            mwMessage = mwMessage.Replace("$2", "(?:.+?)");
            mwMessage = mwMessage.Replace("$3", "(?:.+?)");
            mwMessage = mwMessage.Replace("$", @"\$");
            mwMessage = "^" + mwMessage + @"(?:: (?<comment>.*?))?$"; //Special:Log comments are preceded by a colon

            //Dirty code: Block log exceptions!
            if (mwMessageTitle == "MediaWiki:Blocklogentry")
            {
                mwMessage = mwMessage.Replace("(?<item3>.+?)", "\\((?<item3>.+?)\\)");
                mwMessage = mwMessage.Replace(@"(?<item2>.+?)(?:: (?<comment>.*?))?$", "(?<item2>.+?)$");
            }

            try
            {
                Regex r = new Regex(mwMessage);
            }
            catch (Exception e)
            {
                throw new Exception("Failed to test-generate regex " + mwMessage + " for " + mwMessageTitle + "; " + e.Message);
            }

            if (reqCount > 0)
            {
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
            }
            
            destRegex = mwMessage;
        }

        /// <summary>
        /// Gets the namespace code
        /// </summary>
        /// <param name="pageTitle">A page title, such as "Special:Helloworld" and "Helloworld"</param>
        /// <returns></returns>
        public int detectNamespace(string pageTitle)
        {
            if (pageTitle.Contains(":"))
            {
                string nsLocal = pageTitle.Substring(0, pageTitle.IndexOf(':'));
                //Try to locate value (As fast as ContainsValue())
                foreach (DictionaryEntry de in this.namespaces)
                {
                    if ((string)de.Value == nsLocal)
                        return Convert.ToInt32(de.Key);
                }
                //If we're here (couldn't find, then probably not a namespace)
                return 0;
            }
            else
            {
                return 0; //Main namespace
            }
        }

        /// <summary>
        /// Returns a copy of the article title with the namespace translated into English
        /// </summary>
        /// <param name="originalTitle">Title in original (localized) language</param>
        /// <returns></returns>
        public static string translateNamespace(string project, string originalTitle)
        {
            if (originalTitle.Contains(":"))
            {
                string nsEnglish;
                
		        // *Don't change these* unless it's a stopping bug. These names are made part of the title
		        // in the watchlist and items database. (ie. don't change Image to File unless Image is broken)
		        // When they do need to be changed, make sure to make note in the RELEASE-NOTES that databases
		        // should be updated manually to keep all regexes and watchlists functional!
                switch (((Project)Program.prjlist[project]).detectNamespace(originalTitle))
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

                //If we're still here, then nsEnglish has been set
                return nsEnglish + originalTitle.Substring(originalTitle.IndexOf(':'));
            }
            else
                return originalTitle; //Mainspace articles do not need translation
        }
    }
}
