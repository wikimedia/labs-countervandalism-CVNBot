using System;
using System.Text.RegularExpressions;
using System.Threading;
using log4net;
using Meebey.SmartIrc4net;

namespace CVNBot
{
    struct RCEvent
    {
        public enum EventType
        {
            delete, restore, upload, block, unblock, edit, protect, unprotect,
            move, rollback, newuser, import, unknown, newuser2, autocreate,
            modifyprotect
        }

        public string project;
        public string title;
        public string url;
        public string user;
        public bool minor;
        public bool newpage;
        public bool botflag;
        public int szdiff;
        public string comment;
        public EventType eventtype;
        public string blockLength;
        public string movedTo;

        public override string ToString()
        {
            return "[" + project + "] " + user + " edited [[" + title + "]] (" + szdiff.ToString() + ") " + url + " " + comment;
        }
    }

    class RCReader
    {
        public IrcClient rcirc = new IrcClient();
        public DateTime lastMessage = DateTime.Now;

        // RC parsing regexen
        static readonly Regex stripColours = new Regex(@"\x04\d{0,2}\*?");
        static readonly Regex stripColours2 = new Regex(@"\x03\d{0,2}");
        static readonly Regex stripBold = new Regex(@"\x02");
        static readonly Regex rszDiff = new Regex(@"\(([\+\-])([0-9]+)\)");

        static readonly ILog logger = LogManager.GetLogger("CVNBot.RCReader");

        static readonly string serverName = "irc.wikimedia.org";

        public void InitiateConnection()
        {
            Thread.CurrentThread.Name = "RCReader";

            logger.Info("Thread started");

            // Set up RCReader
            rcirc.Encoding = System.Text.Encoding.UTF8;
            rcirc.AutoReconnect = true;
            rcirc.AutoRejoin = true;

            rcirc.OnChannelMessage += Rcirc_OnChannelMessage;
            rcirc.OnConnected += Rcirc_OnConnected;

            try
            {
                rcirc.Connect(serverName, 6667);
            }
            catch (ConnectionException e)
            {
                logger.Warn("Could not connect", e);
                return;
            }

            try
            {
                rcirc.Login(Program.config.botNick, "CVNBot", 4, "CVNBot");

                logger.InfoFormat("Joining {0} channels", Program.prjlist.Count);
                foreach (string prj in Program.prjlist.Keys)
                {
                    rcirc.RfcJoin("#" + prj);
                }

                // Enter loop
                rcirc.Listen();
                // When Listen() returns the IRC session is over
                rcirc.Disconnect();
            }
            catch (ConnectionException)
            {
                // Final disconnect may throw, ignore.
                return;
            }
        }

        void Rcirc_OnConnected(object sender, EventArgs e)
        {
            logger.InfoFormat("Connected to {0}", serverName);
        }

        void Rcirc_OnChannelMessage(object sender, IrcEventArgs e)
        {
            lastMessage = DateTime.Now;

            // Based on RCParser.py->parseRCmsg()
            // Example message from 2017-10-13 from #en.wikipedia
            // 01> #00314 [[
            // 02> #00307 Special:Log/newusers
            // 03> #00314 ]]
            // 04> #0034   create2
            // 05> #00310
            // 06> #00302
            // 07> #003
            // 08> #0035  *
            // 09> #003
            // 10> #00303 Ujju.19788
            // 11> #003
            // 12> #0035  *
            // 13> #003
            // 14> #00310 created new account User:Upendhare
            // 15> #003
            string strippedmsg = stripBold.Replace(stripColours.Replace(CVNBotUtils.ReplaceStrMax(e.Data.Message, '\x03', '\x04', 14), "\x03"), "");
            string[] fields = strippedmsg.Split(new char[] { '\x03' }, 15);
            if (fields.Length == 15)
            {
                if (fields[14].EndsWith("\x03"))
                    fields[14] = fields[14].Substring(0, fields[14].Length - 1);
            }
            else
            {
                // Probably really long article title or something that got cut off; we can't handle these
                return;
            }

            try
            {
                RCEvent rce;
                rce.eventtype = RCEvent.EventType.unknown;
                rce.blockLength = "";
                rce.movedTo = "";
                rce.project = e.Data.Channel.Substring(1);
                rce.title = Project.TranslateNamespace(rce.project, fields[2]);
                rce.url = fields[6];
                rce.user = fields[10];
                Project project = ((Project)Program.prjlist[rce.project]);
                // At the moment, fields[14] contains IRC colour codes. For plain edits, remove just the \x03's. For logs, remove using the regex.
                Match titlemo = project.rSpecialLogRegex.Match(fields[2]);
                if (!titlemo.Success)
                {
                    // This is a regular edit
                    rce.minor = fields[4].Contains("M");
                    rce.newpage = fields[4].Contains("N");
                    rce.botflag = fields[4].Contains("B");
                    rce.eventtype = RCEvent.EventType.edit;
                    rce.comment = fields[14].Replace("\x03", "");
                }
                else
                {
                    // This is a log edit; check for type
                    string logType = titlemo.Groups[1].Captures[0].Value;
                    // Fix comments
                    rce.comment = stripColours2.Replace(fields[14], "");
                    switch (logType)
                    {
                        case "newusers":
                            // Could be a user creating their own account, or a user creating a sockpuppet

                            // Example message as of 2016-11-02 on #nl.wikipedia (with log comment after colon)
                            // > [[Speciaal:Log/newusers]] create2  * BRPots *  created new account Gebruiker:BRPwiki: eerder fout gemaakt
                            // Example message as of 2016-11-02 on #nl.wikipedia (without log comment)
                            // > [[Speciaal:Log/newusers]] create2  * Sherani koster *  created new account Gebruiker:Rani farah koster

                            // Example message as of 2017-10-13 on #en.wikipedia:
                            // > [[Special:Log/newusers]] create2  * Ujju.19788 *  created new account User:Upendhare
                            // Example message as of 2022-01-24 on #en.wikipedia:
                            // > [[Special:Log/newusers]] byemail  * Mdaniels5757 *  created new account User:Hannahco12: Requested account
                            //
                            // Treat newusers/byemail the same as newusers/create2.
                            // MediaWiki internally re-uses the "create2" message for "byemail" as well.
                            // Ref mediawiki-core.git:/LogFormatter.php#getIRCActionText
                            // Ref https://phabricator.wikimedia.org/T327126
                            //
                            if (fields[4].Contains("create2") || fields[4].Contains("byemail"))
                            {
                                Match mc2 = project.rCreate2Regex.Match(rce.comment);
                                if (mc2.Success)
                                {
                                    rce.title = mc2.Groups[1].Captures[0].Value;
                                    rce.eventtype = RCEvent.EventType.newuser2;
                                }
                                else
                                {
                                    logger.Warn("Unmatched create2 event in " + rce.project + ": " + e.Data.Message);
                                }
                            }
                            else
                            {
                                if (fields[4].Contains("autocreate"))
                                {
                                    rce.eventtype = RCEvent.EventType.autocreate;
                                }
                                else
                                {
                                    rce.eventtype = RCEvent.EventType.newuser;
                                }
                            }
                            break;
                        case "block":
                            // Example message from October 2017 on #en.wikipedia:
                            // > [[Special:Log/block]] reblock  * Yamla *  changed block settings for [[User:Jeb BushDid911]] (account creation blocked, email disabled, cannot edit own talk page) with an expiry time of indefinite: {{uw-ublock}}
                            //
                            // Example message from October 2017 on #en.wikipedia
                            // > [[Special:Log/block]] reblock  * DeltaQuad *  changed block settings for [[User:208.111.64.0/19]] (anon. only, account creation blocked) with an expiry time of 06:21, February 2, 2019: {{colocationwebhost}}
                            if (fields[4].Contains("unblock"))
                            {
                                Match ubm = project.runblockRegex.Match(rce.comment);
                                if (ubm.Success)
                                {
                                    rce.eventtype = RCEvent.EventType.unblock;
                                    rce.title = ubm.Groups["item1"].Captures[0].Value;
                                    try
                                    {
                                        rce.comment = ubm.Groups["comment"].Captures[0].Value;
                                    }
                                    catch (ArgumentOutOfRangeException) { }
                                }
                                else
                                {
                                    logger.Warn("Unmatched block/unblock type in " + rce.project + ": " + e.Data.Message);
                                    return;
                                }
                            }
                            else if (fields[4].Contains("reblock"))
                            {
                                Match rbm = project.rreblockRegex.Match(rce.comment);
                                if (rbm.Success)
                                {
                                    // Treat reblock the same as a new block for simplicity
                                    rce.eventtype = RCEvent.EventType.block;
                                    rce.title = rbm.Groups["item1"].Captures[0].Value;
                                }
                                else
                                {
                                    logger.Warn("Unmatched block/reblock type in " + rce.project + ": " + e.Data.Message);
                                    return;
                                }
                            }
                            else
                            {
                                Match bm = project.rblockRegex.Match(rce.comment);
                                if (bm.Success)
                                {
                                    rce.eventtype = RCEvent.EventType.block;
                                    rce.title = bm.Groups["item1"].Captures[0].Value;
                                    // Assume default value of 24 hours in case the on-wiki message override
                                    // is missing expiry ($2) from its interface messag
                                    rce.blockLength = "24 hours";
                                    try
                                    {
                                        rce.blockLength = bm.Groups["item2"].Captures[0].Value;
                                    }
                                    catch (ArgumentOutOfRangeException) { }
                                    try
                                    {
                                        rce.comment = bm.Groups["comment"].Captures[0].Value;
                                    }
                                    catch (ArgumentOutOfRangeException) { }
                                }
                                else
                                {
                                    logger.Warn("Unmatched block type in " + rce.project + ": " + e.Data.Message);
                                    return;
                                }
                            }
                            break;
                        case "protect":
                            // Could be a protect, modifyprotect or unprotect; need to parse regex
                            Match pm = project.rprotectRegex.Match(rce.comment);
                            Match modpm = project.rmodifyprotectRegex.Match(rce.comment);
                            Match upm = project.runprotectRegex.Match(rce.comment);
                            if (pm.Success)
                            {
                                rce.eventtype = RCEvent.EventType.protect;
                                rce.title = Project.TranslateNamespace(rce.project, pm.Groups["item1"].Captures[0].Value);
                                try
                                {
                                    rce.comment = pm.Groups["comment"].Captures[0].Value;
                                }
                                catch (ArgumentOutOfRangeException) { }
                            }
                            else if (modpm.Success)
                            {
                                rce.eventtype = RCEvent.EventType.modifyprotect;
                                rce.title = Project.TranslateNamespace(rce.project, modpm.Groups["item1"].Captures[0].Value);
                                try
                                {
                                    rce.comment = modpm.Groups["comment"].Captures[0].Value;
                                }
                                catch (ArgumentOutOfRangeException) { }
                            }
                            else
                            {
                                if (upm.Success)
                                {
                                    rce.eventtype = RCEvent.EventType.unprotect;
                                    rce.title = Project.TranslateNamespace(rce.project, upm.Groups["item1"].Captures[0].Value);
                                    try
                                    {
                                        rce.comment = upm.Groups["comment"].Captures[0].Value;
                                    }
                                    catch (ArgumentOutOfRangeException) { }
                                }
                                else
                                {
                                    logger.Warn("Unmatched protect type in " + rce.project + ": " + e.Data.Message);
                                    return;
                                }
                            }
                            break;
                        case "rights":
                            // Ignore event
                            return;
                        //break;
                        case "delete":
                            // Could be a delete or restore; need to parse regex
                            Match dm = project.rdeleteRegex.Match(rce.comment);
                            if (dm.Success)
                            {
                                rce.eventtype = RCEvent.EventType.delete;
                                rce.title = Project.TranslateNamespace(rce.project, dm.Groups["item1"].Captures[0].Value);
                                try
                                {
                                    rce.comment = dm.Groups["comment"].Captures[0].Value;
                                }
                                catch (ArgumentOutOfRangeException) { }
                            }
                            else
                            {
                                Match udm = project.rrestoreRegex.Match(rce.comment);
                                if (udm.Success)
                                {
                                    rce.eventtype = RCEvent.EventType.restore;
                                    rce.title = Project.TranslateNamespace(rce.project, udm.Groups["item1"].Captures[0].Value);
                                    try
                                    {
                                        rce.comment = udm.Groups["comment"].Captures[0].Value;
                                    }
                                    catch (ArgumentOutOfRangeException) { }
                                }
                                else
                                {
                                    // Could be 'revision' (change visibility of revision) or something else
                                    // Ignore event
                                    return;
                                }
                            }
                            break;
                        case "upload":
                            Match um = project.ruploadRegex.Match(rce.comment);
                            if (um.Success)
                            {
                                rce.eventtype = RCEvent.EventType.upload;
                                rce.title = Project.TranslateNamespace(rce.project, um.Groups["item1"].Captures[0].Value);
                                try
                                {
                                    rce.comment = um.Groups["comment"].Captures[0].Value;
                                }
                                catch (ArgumentOutOfRangeException) { }
                            }
                            else
                            {
                                // Could be 'overwrite' (upload new version) or something else
                                // Ignore event
                                return;
                            }
                            break;
                        case "move":
                            //Is a move
                            rce.eventtype = RCEvent.EventType.move;
                            //Check "move over redirect" first: it's longer, and plain "move" may match both (e.g., en-default)
                            Match mrm = project.rmoveredirRegex.Match(rce.comment);
                            if (mrm.Success)
                            {
                                rce.title = Project.TranslateNamespace(rce.project, mrm.Groups["item1"].Captures[0].Value);
                                rce.movedTo = Project.TranslateNamespace(rce.project, mrm.Groups["item2"].Captures[0].Value);
                                //We use the unused blockLength field to store our "moved from" URL
                                rce.blockLength = project.rooturl + "wiki/" + CVNBotUtils.WikiEncode(mrm.Groups["item1"].Captures[0].Value);
                                try
                                {
                                    rce.comment = mrm.Groups["comment"].Captures[0].Value;
                                }
                                catch (ArgumentOutOfRangeException) { }
                            }
                            else
                            {
                                Match mm = project.rmoveRegex.Match(rce.comment);
                                if (mm.Success)
                                {
                                    rce.title = Project.TranslateNamespace(rce.project, mm.Groups["item1"].Captures[0].Value);
                                    rce.movedTo = Project.TranslateNamespace(rce.project, mm.Groups["item2"].Captures[0].Value);
                                    //We use the unused blockLength field to store our "moved from" URL
                                    rce.blockLength = project.rooturl + "wiki/" + CVNBotUtils.WikiEncode(mm.Groups["item1"].Captures[0].Value);
                                    try
                                    {
                                        rce.comment = mm.Groups["comment"].Captures[0].Value;
                                    }
                                    catch (ArgumentOutOfRangeException) { }
                                }
                                else
                                {
                                    logger.Warn("Unmatched move type in " + rce.project + ": " + e.Data.Message);
                                    return;
                                }
                            }
                            break;
                        case "import":
                            //rce.eventtype = RCEvent.EventType.import;
                            // Ignore event
                            return;
                        //break;
                        default:
                            // Ignore event
                            return;
                    }
                    // These flags don't apply to log events, but must be initialized
                    rce.minor = false;
                    rce.newpage = false;
                    rce.botflag = false;
                }

                // Deal with the diff size
                Match n = rszDiff.Match(fields[13]);
                if (n.Success)
                {
                    if (n.Groups[1].Captures[0].Value == "+")
                        rce.szdiff = Convert.ToInt32(n.Groups[2].Captures[0].Value);
                    else
                        rce.szdiff = 0 - Convert.ToInt32(n.Groups[2].Captures[0].Value);
                }
                else
                    rce.szdiff = 0;

                try
                {
                    Program.ReactToRCEvent(rce);
                }
                catch (Exception exce)
                {
                    logger.Error("Failed to handle RCEvent", exce);
                    Program.BroadcastDD("ERROR", "ReactorException", exce.Message, e.Data.Channel + " " + e.Data.Message);
                }
            }
            catch (ArgumentOutOfRangeException eor)
            {
                // Broadcast this for Distributed Debugging
                logger.Error("Failed to process incoming message", eor);
                Program.BroadcastDD("ERROR", "RCR_AOORE", eor.Message, e.Data.Channel + "/" + e.Data.Message
                    + "Fields: " + fields);
            }
        }

    }
}
