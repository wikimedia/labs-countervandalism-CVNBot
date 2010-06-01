using System;
using System.Collections;
using Meebey.SmartIrc4net;
using System.Threading;
using System.Text.RegularExpressions;
using System.IO;
using System.Data;
using Mono.Data.SqliteClient;
using log4net;

//Logging:
[assembly: log4net.Config.XmlConfigurator(Watch = true)]

namespace SWMTBot
{
    class Program
    {
        const string version = "1.14.4";
        
        public static IrcClient irc = new IrcClient();
        public static RCReader rcirc = new RCReader();
        public static ProjectList prjlist = new ProjectList();
        public static ListManager listman = new ListManager();
        public static SortedList msgs = new SortedList();
        public static SortedList mainConfig = new SortedList();
        private static ILog logger = LogManager.GetLogger("SWMTBot.Program");

        static Regex broadcastMsg = new Regex(@"\*\x02B/1.0\x02\*(?<list>.+?)\*(?<action>.+?)\*\x03"
            +@"07\x02(?<item>.+?)\x02\x03\*\x03"
            +@"13(?<len>\d+?)\x03\*\x03"
            +@"09\x02(?<reason>.*?)\x02\x03\*\x03"
            +@"11\x02(?<adder>.*?)\x03\x02\*");
        static Regex botCmd;
        /* TODO: These options should be configurable */
        static int editblank = -1500;
        static int editbig = 1500;
        static int newbig = 2000;
        static int newsmall = 15;
        static bool ignoreBotEdits = true;
        static string ControlChannel;
        static string FeedChannel;
        static string BroadcastChannel;
        static string ircServerName;

        public static string botNick;

        static void Main(string[] args)
        {
            Thread.CurrentThread.Name = "Main";
            Thread.GetDomain().UnhandledException += new UnhandledExceptionEventHandler(Application_UnhandledException);

            string mainConfigFN = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)
                + Path.DirectorySeparatorChar + "SWMTBot.ini";

            logger.Info("Loading main configuration from "+mainConfigFN);
            using (StreamReader sr = new StreamReader(mainConfigFN))
            {
                String line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (!line.StartsWith("#") && (line != "")) //ignore comments
                    {
                        string[] parts = line.Split(new char[1] { '=' }, 2);
                        mainConfig.Add(parts[0], parts[1]);
                    }
                }
            }

            botNick = (string)mainConfig["botnick"];
            ControlChannel = (string)mainConfig["controlchannel"];
            FeedChannel = (string)mainConfig["feedchannel"];
            BroadcastChannel = (string)mainConfig["broadcastchannel"];
            ircServerName = (string)mainConfig["ircserver"];
            prjlist.fnProjectsXML = (string)mainConfig["projects"];
            botCmd = new Regex("^" + botNick + @" (?<command>\S*)(\s(?<params>.*))?$", RegexOptions.IgnoreCase);

            logger.Info("Loading messages");
            readMessages((string)mainConfig["messages"]);
            if ((!msgs.ContainsKey("00000")) || ((String)msgs["00000"] != "2.00"))
            {
                logger.Fatal("Message file version mismatch or read messages failed");
                Exit();
            }

            //Read projects (prjlist displays logger message)
            prjlist.loadFromFile();

            logger.Info("Loading lists");
            listman.initDBConnection((string)mainConfig["lists"]);

            logger.Info("Starting main IRC client");
            //Set up freenode IRC client
            irc.Encoding = System.Text.Encoding.UTF8;
            irc.SendDelay = 300;
            //irc.AutoReconnect = true;
            //irc.AutoRejoin = true;
            irc.ActiveChannelSyncing = true;
            irc.OnChannelMessage += new IrcEventHandler(irc_OnChannelMessage);
            irc.OnChannelNotice += new IrcEventHandler(irc_OnChannelNotice);
            irc.OnConnected += new EventHandler(irc_OnConnected);
            irc.OnError += new Meebey.SmartIrc4net.ErrorEventHandler(irc_OnError);

            try
            {
                irc.Connect(ircServerName, 6667);
            }
            catch (ConnectionException e)
            {
                logger.Fatal("Could not connect: " + e.Message);
                Exit();
            }

            try
            {
                irc.Login(botNick, (string)mainConfig["description"] + " " + version, 4, botNick, (string)mainConfig["botpass"]);
                irc.RfcJoin(ControlChannel);
                irc.RfcJoin(FeedChannel);
                irc.RfcJoin(BroadcastChannel);
                
                //Now connect the RCReader to channels
                new Thread(new ThreadStart(rcirc.initiateConnection)).Start();

                // here we tell the IRC API to go into a receive mode, all events
                // will be triggered by _this_ thread (main thread in this case)
                // Listen() blocks by default, you can also use ListenOnce() if you
                // need that does one IRC operation and then returns, so you need then 
                // an own loop 
                irc.Listen();

                // when Listen() returns our IRC session is over, to be sure we call
                // disconnect manually
                irc.Disconnect();
            }
            catch (ConnectionException)
            {
                // this exception is handled because Disconnect() can throw a not
                // connected exception
                Exit();
            }
            catch (Exception e)
            {
                // this should not happen by just in case we handle it nicely
                logger.Fatal("Error occurred! Message: " + e.Message);
                logger.Fatal("Exception: " + e.StackTrace);
                Exit();
            }
        }

        /// <summary>
        /// Catches all unhandled exceptions in the main thread
        /// </summary>
        public static void Application_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                logger.Error("Caught unhandled exception", (Exception)e.ExceptionObject);
            }
            catch
            {
                //Logging failed; considerably serious
                Console.WriteLine("Caught unhandled exception, and logging failed: " + ((Exception)e.ExceptionObject).ToString());

                try
                {
                    PartIRC("Caught unhandled exception and logging failed; restarting as a precaution");
                    Restart();
                }
                catch
                {
                    //Restart failed
                    Console.WriteLine("Restart failed; exiting with code 24.");
                    System.Environment.Exit(24);
                }
            }
        }

        static void irc_OnError(object sender, Meebey.SmartIrc4net.ErrorEventArgs e)
        {
            logger.Error("IRC: " + e.ErrorMessage);
            if (e.ErrorMessage.Contains("Excess Flood")) //Do not localize
            {
                //Oops, we were flooded off
                logger.Info("Initiating restart sequence");
                Restart();
            }
        }

        /// <summary>
        /// This event handler detects incoming broadcast messages
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        static void irc_OnChannelNotice(object sender, IrcEventArgs e)
        {
            if (e.Data.Channel != BroadcastChannel)
                return; //Just in case
            Match bm = broadcastMsg.Match(e.Data.Message);
            if (bm.Success)
            {
                try
                {
                    string action = bm.Groups["action"].Captures[0].Value;
                    string list = bm.Groups["list"].Captures[0].Value;
                    string item = bm.Groups["item"].Captures[0].Value;
                    int len = Convert.ToInt32(bm.Groups["len"].Captures[0].Value);
                    string reason = bm.Groups["reason"].Captures[0].Value;
                    string adder = bm.Groups["adder"].Captures[0].Value;

                    //Similar to ListManager.handleListCommand
                    switch (action)
                    {
                        case "ADD":
                            switch (list)
                            {
                                case "WL":
                                    listman.addUserToList(item, "", ListManager.UserType.whitelisted, adder, reason, len, ref listman.dbcon);
                                    break;
                                case "BL":
                                    listman.addUserToList(item, "", ListManager.UserType.blacklisted, adder, reason, len, ref listman.dbcon);
                                    break;
                                case "GL":
                                    listman.addUserToList(item, "", ListManager.UserType.greylisted, adder, reason, len, ref listman.dbcon);
                                    break;
                                case "BNU":
                                    listman.addItemToList(item, 11, adder, reason, len);
                                    break;
                                case "BNA":
                                    listman.addItemToList(item, 12, adder, reason, len);
                                    break;
                                case "BES":
                                    listman.addItemToList(item, 20, adder, reason, len);
                                    break;
                                case "CVP":
                                    listman.addPageToWatchlist(item, "", adder, reason, len);
                                    break;
                                //Gracefully ignore unknown message types
                            }
                            break;
                        case "DEL":
                            switch (list)
                            {
                                case "WL":
                                    listman.delUserFromList(item, "", ListManager.UserType.whitelisted);
                                    break;
                                case "BL":
                                    listman.delUserFromList(item, "", ListManager.UserType.blacklisted);
                                    break;
                                case "GL":
                                    listman.delUserFromList(item, "", ListManager.UserType.greylisted);
                                    break;
                                case "BNU":
                                    listman.delItemFromList(item, 11);
                                    break;
                                case "BNA":
                                    listman.delItemFromList(item, 12);
                                    break;
                                case "BES":
                                    listman.delItemFromList(item, 20);
                                    break;
                                case "CVP":
                                    listman.delPageFromWatchlist(item, "");
                                    break;
                                //Gracefully ignore unknown message types
                            }
                            break;
                        case "FIND":
                            if (list == "BLEEP")
                                if (prjlist.ContainsKey(item))
                                    irc.SendMessage(SendType.Action, reason, "has " + item + ", " + adder + " :D");
                            break;
                        case "COUNT":
                            if (list == "BLEEP")
                                irc.SendMessage(SendType.Action, reason, "owns " + prjlist.Count.ToString() + " wikis; version is " + version);
                            break;
                        //Gracefully ignore unknown action types
                    }
                }
                catch (Exception ex)
                {
                    BroadcastDD("ERROR", "BC_ERROR", ex.Message, e.Data.Message);
                }
            }
        }

        static void irc_OnConnected(object sender, EventArgs e)
        {
            logger.Info("Connected to " + ircServerName);
        }

        static bool hasPrivileges(char minimum, ref IrcEventArgs e)
        {
            switch (minimum)
            {
                case '@':
                    if (!irc.GetChannelUser(e.Data.Channel, e.Data.Nick).IsOp)
                    {
                        irc.SendMessage(SendType.Notice, e.Data.Nick, (String)msgs["00122"]);
                        return false;
                    }
                    else
                        return true;
                case '+':
                    if (!irc.GetChannelUser(e.Data.Channel, e.Data.Nick).IsOp && !irc.GetChannelUser(e.Data.Channel, e.Data.Nick).IsVoice)
                    {
                        irc.SendMessage(SendType.Notice, e.Data.Nick, (String)msgs["00120"]);
                        return false;
                    }
                    else
                        return true;
                default:
                    return false;
            }
        }

        static void irc_OnChannelMessage(object sender, IrcEventArgs e)
        {      
            Match cmdMatch = botCmd.Match(e.Data.Message);

            if (cmdMatch.Success)
            {
                // Have to be voiced to issue any commands
                if (!hasPrivileges('+', ref e))
                    return;

                string command = cmdMatch.Groups["command"].Captures[0].Value;

                string extraParams;
                try
                {
                    extraParams = cmdMatch.Groups["params"].Captures[0].Value;
                }
                catch (Exception)
                {
                    extraParams = "";
                }

                string[] cmdParams = extraParams.Split(new char[1] { ' ' });

                switch (command)
                {
                    case "quit":
                        if (!hasPrivileges('@', ref e))
                            return;
                        logger.Info(e.Data.Nick + " ordered a quit");
                        PartIRC((string)mainConfig["partmsg"]);
                        Exit();
                        break;
                    case "restart":
                        if (!hasPrivileges('@', ref e))
                            return;
                        logger.Info(e.Data.Nick + " ordered a restart");
                        PartIRC("Rebooting by order of " + e.Data.Nick + " ...");
                        Restart();
                        break;
                    case "status":
                        TimeSpan ago = DateTime.Now.Subtract(rcirc.lastMessage);
                        irc.SendMessage(SendType.Message, e.Data.Channel, "Last message was received on RCReader "
                            + ago.TotalSeconds + " seconds ago");
                        break;
                    case "msgs":
                        //Reloads msgs
                        if (!hasPrivileges('@', ref e))
                            return;
                        readMessages((string)mainConfig["messages"]);
                        irc.SendMessage(SendType.Message, e.Data.Channel, "Re-read messages");
                        break;
                    case "reload":
                        //Reloads wiki data for a project
                        if (!irc.GetChannelUser(e.Data.Channel, e.Data.Nick).IsOp)
                            return;
                        try
                        {
                            if (!prjlist.ContainsKey(cmdParams[0]))
                                throw new Exception("Project " + cmdParams[0] + " is not loaded");

                            ((Project)prjlist[cmdParams[0]]).retrieveWikiDetails();
                            irc.SendMessage(SendType.Message, e.Data.Channel, "Reloaded project " + cmdParams[0]);
                        }
                        catch (Exception ex)
                        {
                            irc.SendMessage(SendType.Message, e.Data.Channel, "Unable to reload: " + ex.Message);
                            logger.Error("Reload project failed: " + ex.Message);
                        }
                        break;
                    case "load":
                        if (!hasPrivileges('@', ref e))
                            return;
                        try
                        {
                            if (cmdParams.Length == 2)
                                prjlist.addNewProject(cmdParams[0], cmdParams[1]);
                            else
                                prjlist.addNewProject(cmdParams[0], "");

                            irc.SendMessage(SendType.Message, e.Data.Channel, "Loaded new project " + cmdParams[0]);
                            //Automatically get admins and bots:
                            Thread.Sleep(200);
                            irc.SendMessage(SendType.Message, e.Data.Channel, listman.configGetAdmins(cmdParams[0]));
                            Thread.Sleep(500);
                            irc.SendMessage(SendType.Message, e.Data.Channel, listman.configGetBots(cmdParams[0]));
                        }
                        catch (Exception ex)
                        {
                            irc.SendMessage(SendType.Message, e.Data.Channel, "Unable to add project: " + ex.Message);
                            logger.Error("Add project failed: " + ex.Message);
                        }
                        break;
                    case "bleep":
                        if (!hasPrivileges('+', ref e))
                            return;
                        try
                        {
                            if (cmdParams[0].Length > 0)
                            {
                                if (prjlist.ContainsKey(cmdParams[0]))
                                {
                                    irc.SendMessage(SendType.Action, e.Data.Channel, "has " + cmdParams[0] + ", " + e.Data.Nick + " :D");
                                }
                                else
                                {
                                    Broadcast("BLEEP", "FIND", cmdParams[0], 0, e.Data.Channel, e.Data.Nick);
                                    irc.SendMessage(SendType.Message, e.Data.Channel, "Bleeped. Please wait for a reply.");
                                }
                            }
                        } catch (Exception ex)
                        {
                            irc.SendMessage(SendType.Message, e.Data.Channel, "Unable to bleep: " + ex.Message);
                        }
                        break;
                    case "count":
                        if (!hasPrivileges('+', ref e))
                            return;
                        Broadcast("BLEEP", "COUNT", "BLEEP", 0, e.Data.Channel, e.Data.Nick);
                        irc.SendMessage(SendType.Action, e.Data.Channel, "owns " + prjlist.Count.ToString() + " wikis; version is " + version);
                        break;
                    case "drop":
                        if (!hasPrivileges('@', ref e))
                            return;
                        try
                        {
                            prjlist.deleteProject(cmdParams[0]);
                            irc.SendMessage(SendType.Message, e.Data.Channel, "Deleted project " + cmdParams[0]);
                        }
                        catch (Exception ex)
                        {
                            irc.SendMessage(SendType.Message, e.Data.Channel, "Unable to delete project: " + ex.Message);
                            logger.Error("Delete project failed: " + ex.Message);
                        }
                        break;
                    case "list":
                        string result = "Currently monitoring: ";
                        foreach (string p in prjlist.Keys)
                        {
                            result += p + " ";
                        }
                        result += "(Total: " + prjlist.Count.ToString() + " wikis)";
                        foreach (string chunk in SWMTUtils.stringSplit(result, 400))
                        {
                            irc.SendMessage(SendType.Message, e.Data.Channel, chunk);
                            Thread.Sleep(400);
                        }
                        break;
                    case "batchgetusers":
                        if (!hasPrivileges('@', ref e))
                            return;
                        listman.currentGetBatchChannel = e.Data.Channel;
                        new Thread(new ThreadStart(listman.BatchGetAllAdminsAndBots)).Start();
                        break;
                    case "bl":
                        irc.SendMessage(SendType.Message, e.Data.Channel,
                            listman.handleListCommand(1, e.Data.Nick, extraParams));
                        break;
                    case "wl":
                        irc.SendMessage(SendType.Message, e.Data.Channel,
                            listman.handleListCommand(0, e.Data.Nick, extraParams));
                        break;
                    case "gl":
                        irc.SendMessage(SendType.Message, e.Data.Channel,
                            listman.handleListCommand(6, e.Data.Nick, extraParams));
                        break;
                    case "al":
                        irc.SendMessage(SendType.Message, e.Data.Channel,
                            listman.handleListCommand(2, e.Data.Nick, extraParams));
                        break;
                    case "bots":
                        irc.SendMessage(SendType.Message, e.Data.Channel,
                            listman.handleListCommand(5, e.Data.Nick, extraParams));
                        break;
                    case "cvp":
                        irc.SendMessage(SendType.Message, e.Data.Channel,
                            listman.handleListCommand(10, e.Data.Nick, extraParams));
                        break;
                    case "bnu":
                        irc.SendMessage(SendType.Message, e.Data.Channel,
                            listman.handleListCommand(11, e.Data.Nick, extraParams));
                        break;
                    case "bna":
                        irc.SendMessage(SendType.Message, e.Data.Channel,
                            listman.handleListCommand(12, e.Data.Nick, extraParams));
                        break;
                    case "bes":
                        irc.SendMessage(SendType.Message, e.Data.Channel,
                            listman.handleListCommand(20, e.Data.Nick, extraParams));
                        break;
                    case "getadmins":
                        irc.SendMessage(SendType.Message, e.Data.Channel, listman.configGetAdmins(extraParams));
                        break;
                    case "getbots":
                        irc.SendMessage(SendType.Message, e.Data.Channel, listman.configGetBots(extraParams));
                        break;
                    case "intel":
                        string intelResult = listman.GlobalIntel(extraParams);
                        foreach (string chunk in intelResult.Split(new char[1] {'\n'}))
                        {
                            irc.SendMessage(SendType.Message, e.Data.Channel, chunk);
                            Thread.Sleep(400);
                        }
                        break;
                }
            }
        }

        /// <summary>
        /// Reads messages from filename (Console.msgs) into SortedList msgs
        /// </summary>
        /// <param name="filename">File to read messages from</param>
        static void readMessages(string filename)
        {
            msgs.Clear();
            try
            {
                using (StreamReader sr = new StreamReader(filename))
                {
                    String line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (line.StartsWith("#") || (line == ""))
                        {
                            //Ignore: comment or blank line
                        }
                        else
                        {
                            string[] parts = line.Split(new char[1] { '=' }, 2);
                            msgs.Add(parts[0], parts[1].Replace(@"%c", "\x03").Replace(@"%b", "\x02"));
                        }
                    }
                }
            }
            catch (Exception e)
            {
                logger.Error("Unable to read messages from file", e);
            }
        }

        /// <summary>
        /// Gets a message from the msgs store
        /// </summary>
        /// <param name="msgCode">The five-digit message code</param>
        /// <param name="attributes">The attributes to place in the message</param>
        /// <returns></returns>
        static string getMessage(int msgCode, ref Hashtable attributes)
        {
            try
            {
                string message = (string)msgs[msgCode.ToString().PadLeft(5,'0')];
                foreach (DictionaryEntry de in attributes)
                {
                    message = message.Replace("${" + (string)de.Key + "}", (string)de.Value);
                }
                return message;
            }
            catch (Exception e)
            {
                logger.Error("Cannot getMessage", e);
                return "[Error: cannot get message]";
            }
        }

        /// <summary>
        /// Get a message from the msgs store, and format it using the parameters specified.
        /// Messages should be those with a "1" prefix, incompatible with CVUBot.
        /// </summary>
        /// <param name="msgCode">The five-digit message code</param>
        /// <param name="fParams">The parameters to place in the string</param>
        /// <returns></returns>
        public static string getFormatMessage(int msgCode, params String[] fParams) {
            try
            {
                string message = (string)msgs[msgCode.ToString().PadLeft(5, '0')];
                return String.Format(message, fParams);
            }
            catch (Exception e)
            {
                logger.Error("Cannot getFormatMessage " + msgCode.ToString(), e);
                return "[Error: cannot get message]";
            }
        }

        public static void Broadcast(string list, string action, string item, int expiry, string reason, string adder)
        {
            string bMsg = "*%BB/1.0%B*" + list + "*" + action + "*%C07%B" + item + "%B%C*%C13" + expiry.ToString()
                + "%C*%C09%B" + reason + "%B%C*%C11%B" + adder + "%C%B*";
            irc.SendMessage(SendType.Notice, BroadcastChannel, bMsg.Replace(@"%C", "\x03").Replace(@"%B", "\x02"));
            Thread.Sleep(200);
        }

        public static void BroadcastDD(string type, string codename, string message, string ingredients)
        {
            string bMsg = "*%BDD/1.0%B*" + type + "*" + codename + "*%C07%B" + message + "%B%C*%C13" + ingredients + "%C*";
            irc.SendMessage(SendType.Notice, BroadcastChannel, bMsg.Replace(@"%C", "\x03").Replace(@"%B", "\x02"));
            logger.Info("Broadcasted DD: " + type + "," + codename + "," + message + "," + ingredients);
            Thread.Sleep(200);
        }

        /// <summary>
        /// Shorthand greylisting function for use by ReactToRCEvent
        /// </summary>
        /// <param name="userOffset"></param>
        /// <param name="username"></param>
        /// <param name="reason"></param>
        private static void AddToGreylist(int userOffset, string username, string reason)
        {
            //Only if blacklisted, anon, user, or already greylisted
            if ((userOffset == 1) || (userOffset == 4) || (userOffset == 3) || (userOffset == 6))
            {
                IDbConnection rcdbcon = (IDbConnection)new SqliteConnection(listman.connectionString);
                rcdbcon.Open();
                listman.addUserToList(username, "", ListManager.UserType.greylisted, "SWMTBot", reason, 1, ref rcdbcon);
                rcdbcon.Close();
                rcdbcon = null;
                Broadcast("GL", "ADD", username, 1, reason, "SWMTBot");
            }
        }

        /// <summary>
        /// Reacts to the RC Event, passed from RCReader. Remember: this runs in the RCReader thread!
        /// </summary>
        /// <param name="r"></param>
        public static void ReactToRCEvent(RCEvent r)
        {
            Hashtable attribs = new Hashtable();
            String message = "";
            int userOffset = (int)(listman.classifyEditor(r.user, r.project));

            /* If this is a bot action, and if bot edits are ignored, return */
            /* HACK: If this is a bot admin (not currently supported), and it blocks, then the user will not be blacklisted */
            if (ignoreBotEdits && (userOffset == 5))
                return;

            switch (r.eventtype)
            {
                case RCEvent.EventType.edit:
                    //This case handles: New pages, Edited pages
                    String diffsize;
                    if (r.szdiff >= 0)
                        diffsize = "+" + r.szdiff.ToString();
                    else
                        diffsize = r.szdiff.ToString();
                    attribs.Add("editor", ((Project)prjlist[r.project]).interwikiLink + "User:" + r.user);
                    attribs.Add("article", ((Project)prjlist[r.project]).interwikiLink + r.title);
                    attribs.Add("size", diffsize);
                    attribs.Add("url", r.url);
                    attribs.Add("reason", r.comment);

                    if (r.newpage)
                    {
                        bool createNothingSpecial = false;

                        // First, just check sizes, and assign default messages in case nothing else is at fault
                        if (r.szdiff >= newbig)
                        {
                            attribs.Add("sizeattrib", getMessage(100, ref attribs));
                            attribs.Add("sizereset", getMessage(102, ref attribs));
                            message = getMessage(5010 + userOffset, ref attribs);
                        }
                        else if (r.szdiff <= newsmall)
                        {
                            attribs.Add("sizeattrib", getMessage(101, ref attribs));
                            attribs.Add("sizereset", getMessage(103, ref attribs));
                            message = getMessage(5020 + userOffset, ref attribs);
                        }
                        else
                        {
                            attribs.Add("sizeattrib", "");
                            attribs.Add("sizereset", "");
                            message = getMessage(5000 + userOffset, ref attribs);
                            createNothingSpecial = true;
                        }

                        //If this is a blacklisted or anon or greylisted user, create is always special
                        if ((userOffset == 1) || (userOffset == 3) || (userOffset == 6))
                            createNothingSpecial = false;

                        // Now check if the edit summary matches BES
                        listMatch lm = listman.matchesList(r.comment, 20);
                        if (lm.Success)
                        {
                            //Matches BES
                            attribs.Add("watchword", lm.matchedItem);
                            //attribs.Add("reason", lm.matchedReason);
                            message = getMessage(95040 + userOffset, ref attribs);
                            createNothingSpecial = false;
                            AddToGreylist(userOffset, r.user, Program.getFormatMessage(16300, (String)attribs["article"], lm.matchedItem));
                        }

                        // Now check if this page title matches BNA
                        listMatch eslm = listman.matchesList(r.title, 12);
                        if (eslm.Success)
                        {
                            //Matches BNA
                            attribs.Add("watchword", eslm.matchedItem);
                            //attribs.Add("reason", eslm.matchedReason);
                            message = getMessage(5040 + userOffset, ref attribs);
                            createNothingSpecial = false;
                            AddToGreylist(userOffset, r.user, Program.getFormatMessage(16300, (String)attribs["article"], eslm.matchedItem));
                        }

                        // Now check if user has created a watched page
                        listMatch wlm = listman.isWatchedArticle(r.title, r.project);
                        if (wlm.Success)
                        {
                            //Is watched
                            //attribs.Add("reason", wlm.matchedReason);
                            message = getMessage(5030 + userOffset, ref attribs);
                            createNothingSpecial = false;
                            AddToGreylist(userOffset, r.user, Program.getFormatMessage(16301, (String)attribs["article"]));
                        }

                        // If created by an admin, bot or whitelisted person
                        if ((userOffset == 2) || (userOffset == 5) || (userOffset == 0))
                            return;

                        // If created by a user and nothing special
                        if ((userOffset == 4) && (createNothingSpecial))
                            return;

                        // If else: created by blacklist or IP or greylisted, show
                    }
                    else
                    { //Not new page; a simple edit
                        bool editNothingSpecial = false;

                        if (r.szdiff >= editbig)
                        {
                            attribs.Add("sizeattrib", getMessage(100, ref attribs));
                            attribs.Add("sizereset", getMessage(102, ref attribs));
                            message = getMessage(5110 + userOffset, ref attribs);
                        }
                        else if (r.szdiff <= editblank)
                        {
                            attribs.Add("sizeattrib", getMessage(101, ref attribs));
                            attribs.Add("sizereset", getMessage(103, ref attribs));
                            message = getMessage(5120 + userOffset, ref attribs);
                        }
                        else
                        {
                            attribs.Add("sizeattrib", "");
                            attribs.Add("sizereset", "");
                            message = getMessage(5100 + userOffset, ref attribs);
                            editNothingSpecial = true;
                        }

                        //If this is a blacklisted or anon or greylisted user, edit is always special
                        if ((userOffset == 1) || (userOffset == 3) || (userOffset == 6))
                            editNothingSpecial = false;

                        // Now check if user has edited a watched page
                        listMatch welm = listman.isWatchedArticle(r.title, r.project);
                        if (welm.Success)
                        {
                            //Is watched
                            //attribs.Add("reason", welm.matchedReason); //Current Console.msgs provides reason field for edsum only
                            message = getMessage(5130 + userOffset, ref attribs);
                            editNothingSpecial = false;
                        }

                        // Now check if user has actually blanked the page
                        if (((Project)prjlist[r.project]).rautosummBlank.IsMatch(r.comment))
                        {
                            message = getMessage(96010 + userOffset, ref attribs);
                            editNothingSpecial = false;
                            AddToGreylist(userOffset, r.user, Program.getFormatMessage(16311, (String)attribs["article"]));
                        }
                        else //i.e., it won't be both a blank and a replace, we want to save some resources
                        {
                            Match rplm = ((Project)prjlist[r.project]).rautosummReplace.Match(r.comment);
                            if (rplm.Success)
                            {
                                //It's a replace :(
                                try
                                {
                                    attribs.Add("profanity", rplm.Groups["item1"].Captures[0].Value);
                                    message = getMessage(96020 + userOffset, ref attribs);
                                }
                                catch (ArgumentOutOfRangeException)
                                {
                                    //This wiki probably doesn't have a profanity attribute
                                    message = getMessage(96030 + userOffset, ref attribs);
                                }
                                editNothingSpecial = false;
                            }
                        }

                        // Now check if the edit summary matches BES
                        listMatch elm = listman.matchesList(r.comment, 20);
                        if (elm.Success)
                        {
                            //Matches BES
                            attribs.Add("watchword", elm.matchedItem);
                            //attribs.Add("reason", elm.matchedReason);
                            message = getMessage(95130 + userOffset, ref attribs);
                            editNothingSpecial = false;
                            AddToGreylist(userOffset, r.user, Program.getFormatMessage(16310, r.comment, (String)attribs["article"]));
                        }

                        // If nothing special about the edit (i.e., it's normal-sized, it's not on a watched page), return
                        if (editNothingSpecial)
                            return;
                    }
                    break;
                case RCEvent.EventType.move:
                    attribs.Add("editor", ((Project)prjlist[r.project]).interwikiLink + "User:" + r.user);
                    attribs.Add("fromname", ((Project)prjlist[r.project]).interwikiLink + r.title);
                    attribs.Add("toname", ((Project)prjlist[r.project]).interwikiLink + r.movedTo);
                    attribs.Add("url", r.blockLength); //The blockLength field stores the moveFrom URL
                    attribs.Add("reason", r.comment);
                    message = getMessage(5500 + userOffset, ref attribs);
                    break;
                case RCEvent.EventType.block:
                    attribs.Add("ipcat", "");
                    attribs.Add("blockname", ((Project)prjlist[r.project]).interwikiLink + r.title);
                    attribs.Add("editor", ((Project)prjlist[r.project]).interwikiLink + "User:" + r.user);
                    attribs.Add("length", r.blockLength);
                    attribs.Add("reason", r.comment);
                    message = getMessage(5400, ref attribs);
                    //If this isn't an indefinite/infinite block, add to blacklist
                    //Since we're in the RCReader thread, and we'll be writing to the db, we better open a new connection
                    IDbConnection rcdbcon = (IDbConnection)new SqliteConnection(listman.connectionString);
                    rcdbcon.Open();
                    if ((r.blockLength.ToLower() != "indefinite") && (r.blockLength.ToLower() != "infinite"))
                    {
                        int listLen = Convert.ToInt32(SWMTUtils.ParseDateTimeLength(r.blockLength, 96) * 2.5);
                        message += "\n" + listman.addUserToList(r.title.Split(new char[1] { ':' }, 2)[1], "" //Global bl
                            , ListManager.UserType.blacklisted, r.user, "Autoblacklist: " + r.comment, listLen, ref rcdbcon);
                        Broadcast("BL", "ADD", r.title.Split(new char[1] { ':' }, 2)[1], listLen, "Autoblacklist: " + r.comment, r.user);
                    }
                    rcdbcon.Close();
                    rcdbcon = null;
                    break;
                case RCEvent.EventType.unblock:
                    attribs.Add("blockname", ((Project)prjlist[r.project]).interwikiLink + r.title);
                    attribs.Add("editor", ((Project)prjlist[r.project]).interwikiLink + "User:" + r.user);
                    attribs.Add("reason", r.comment);
                    message = getMessage(5700, ref attribs);
                    break;
                case RCEvent.EventType.newuser:
                    attribs.Add("editor", ((Project)prjlist[r.project]).interwikiLink + "User:" + r.user);
                    attribs.Add("blockurl", ((Project)prjlist[r.project]).rooturl + "wiki/Special:Blockip/" + SWMTUtils.wikiEncode(r.user));
                    listMatch bnuMatch = listman.matchesList(r.user, 11);
                    if (bnuMatch.Success)
                    {
                        // Matches BNU
                        attribs.Add("watchword", bnuMatch.matchedItem);
                        attribs.Add("wwreason", bnuMatch.matchedReason);
                        message = getMessage(5201, ref attribs);
                        AddToGreylist(userOffset, r.user, Program.getFormatMessage(16320, bnuMatch.matchedItem));
                    }
                    else                
                        message = getMessage(5200, ref attribs);
                    break;
                case RCEvent.EventType.newuser2:
                    attribs.Add("creator", ((Project)prjlist[r.project]).interwikiLink + "User:" + r.user);
                    attribs.Add("editor", ((Project)prjlist[r.project]).interwikiLink + "User:" + r.title);
                    attribs.Add("blockurl", ((Project)prjlist[r.project]).rooturl + "wiki/Special:Blockip/" + SWMTUtils.wikiEncode(r.user));
                    listMatch bnuMatch2 = listman.matchesList(r.user, 11);
                    if (bnuMatch2.Success)
                    {
                        // Matches BNU
                        attribs.Add("watchword", bnuMatch2.matchedItem);
                        attribs.Add("wwreason", bnuMatch2.matchedReason);
                        message = getMessage(5211, ref attribs);
                        AddToGreylist(userOffset, r.user, Program.getFormatMessage(16320, bnuMatch2.matchedItem));
                    }
                    else  
                        message = getMessage(5210, ref attribs);
                    break;
                case RCEvent.EventType.upload:
                    /* TODO: Check if watched item */
                    attribs.Add("editor", ((Project)prjlist[r.project]).interwikiLink + "User:" + r.user);
                    attribs.Add("uploaditem", ((Project)prjlist[r.project]).interwikiLink + r.title);
                    attribs.Add("reason", r.comment);
                    attribs.Add("url", ((Project)prjlist[r.project]).rooturl + "wiki/" + SWMTUtils.wikiEncode(r.title));
                    message = getMessage(userOffset + 5600, ref attribs);
                    break;
            }

            if (message != "")
            {
                //Allow multiline
                foreach (string line in message.Split(new char[1] { '\n' }))
                {
                    //Chunk messages that are too long
                    foreach (string chunk in SWMTUtils.stringSplit(line, 400))
                        irc.SendMessage(SendType.Message, FeedChannel, chunk);
                }
            }
        }

        public static void Exit()
        {
            try
            {
                listman.closeDBConnection();
                LogManager.Shutdown();
            }
            catch
            {
                //Ignore
            }
            finally
            {
                System.Environment.Exit(0);
            }
        }

        public static void Restart()
        {
            //If a custom restartcmd / restartarg has been set in the main config, use that
            if (mainConfig.ContainsKey("restartcmd"))
            {
                //Execute the custom command
                System.Diagnostics.Process.Start((string)mainConfig["restartcmd"], (string)mainConfig["restartarg"]);
            }
            else
            {
                //Note: argument is not actually used, but it's there to prevent a mono bug
                System.Diagnostics.Process.Start(System.Reflection.Assembly.GetExecutingAssembly().Location, "--restart");
            }
            Exit();
        }

        public static void PartIRC(string quitMessage)
        {
            rcirc.rcirc.AutoReconnect = false;
            rcirc.rcirc.RfcQuit(quitMessage);
            irc.RfcPart(ControlChannel, quitMessage);
            irc.RfcPart(FeedChannel, quitMessage);
            irc.RfcPart(BroadcastChannel, quitMessage);
            Thread.Sleep(1000);
        }
    }
}
