using System;
using System.Collections;
using System.Collections.Specialized;
using Meebey.SmartIrc4net;
using System.Threading;
using System.Text.RegularExpressions;
using System.IO;
using log4net;
using System.Reflection;

// Logging:
[assembly: log4net.Config.XmlConfigurator(Watch = true)]

namespace CVNBot
{
    class Program
    {
        const string version = "4.0.4";

        public static IrcClient irc = new IrcClient();
        public static RCReader rcirc = new RCReader();
        public static ProjectList prjlist = new ProjectList();
        public static ListManager listman = new ListManager();
        public static SortedList msgs = new SortedList();
        public static StringDictionary rawConfig = new StringDictionary();
        public static Config config = new Config();

        static ILog logger = LogManager.GetLogger("CVNBot.Program");

        static Regex broadcastMsg = new Regex(@"\*\x02B/1.1\x02\*(?<list>.+?)\*(?<action>.+?)\*\x03"
            + @"07\x02(?<item>.+?)\x02\x03\*\x03"
            + @"13(?<len>\d+?)\x03\*\x03"
            + @"09\x02(?<reason>.*?)\x02\x03\*\x03"
            + @"11\x02(?<adder>.*?)\x03\x02\*");
        static Regex botCmd;

        static void Main()
        {
            Thread.CurrentThread.Name = "Main";
            Thread.GetDomain().UnhandledException += Application_UnhandledException;

            string rawConfigFN = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)
                                     + Path.DirectorySeparatorChar.ToString() + "CVNBot.ini";

            // Read config file
            using (StreamReader sr = new StreamReader(rawConfigFN))
            {
                String line;
                while ((line = sr.ReadLine()) != null)
                {
					// Ignore comments
                    if (!line.StartsWith("#") && (line != ""))
                    {
                        string[] parts = line.Split(new char[] { '=' }, 2);
                        rawConfig[parts[0]] = parts[1];
                    }
                }
            }

            // Apply config
            // User
            if (rawConfig.ContainsKey("botnick"))
                config.botNick = rawConfig["botnick"];
            if (rawConfig.ContainsKey("botpass"))
                config.botPass = rawConfig["botpass"];
            if (rawConfig.ContainsKey("botrealname"))
                config.botRealName = rawConfig["botrealname"];
            if (rawConfig.ContainsKey("partmsg"))
                config.partMsg = rawConfig["partmsg"];
            // Server
            if (rawConfig.ContainsKey("ircserver"))
                config.ircServerName = rawConfig["ircserver"];
            if (rawConfig.ContainsKey("feedchannel"))
                config.feedChannel = rawConfig["feedchannel"];
            if (rawConfig.ContainsKey("controlchannel"))
                config.controlChannel = rawConfig["controlchannel"];
            if (rawConfig.ContainsKey("broadcastchannel"))
                config.broadcastChannel = rawConfig["broadcastchannel"];
            // Files
            if (rawConfig.ContainsKey("messages"))
                config.messagesFile = rawConfig["messages"];
            if (rawConfig.ContainsKey("lists"))
                config.listsFile = rawConfig["lists"];
            if (rawConfig.ContainsKey("projects"))
                config.projectsFile = rawConfig["projects"];
            // Process
            if (rawConfig.ContainsKey("restartcmd"))
                config.restartCmd = rawConfig["restartcmd"];
            if (rawConfig.ContainsKey("restartarg"))
                config.restartArgs = rawConfig["restartarg"];
            // Feed
            if (rawConfig.ContainsKey("editblank"))
                config.editBlank = Int32.Parse(rawConfig["editblank"]);
            if (rawConfig.ContainsKey("editbig"))
                config.editBig = Int32.Parse(rawConfig["editbig"]);
            if (rawConfig.ContainsKey("newbig"))
                config.newBig = Int32.Parse(rawConfig["newbig"]);
            if (rawConfig.ContainsKey("newsmall"))
                config.newSmall = Int32.Parse(rawConfig["newsmall"]);
            config.isCubbie = rawConfig.ContainsKey("IsCubbie");
            config.disableClassifyEditor = rawConfig.ContainsKey("disableClassifyEditor");
            if (rawConfig.ContainsKey("feedFilterUsersAnon"))
                config.feedFilterUsersAnon = Int32.Parse(rawConfig["feedFilterUsersAnon"]);
            if (rawConfig.ContainsKey("feedFilterUsersReg"))
                config.feedFilterUsersReg = Int32.Parse(rawConfig["feedFilterUsersReg"]);
            if (rawConfig.ContainsKey("feedFilterUsersBot"))
                config.feedFilterUsersBot = Int32.Parse(rawConfig["feedFilterUsersBot"]);
            if (rawConfig.ContainsKey("feedFilterEventMinorEdit"))
                config.feedFilterEventMinorEdit = Int32.Parse(rawConfig["feedFilterEventMinorEdit"]);
            if (rawConfig.ContainsKey("feedFilterEventEdit"))
                config.feedFilterEventEdit = Int32.Parse(rawConfig["feedFilterEventEdit"]);
            if (rawConfig.ContainsKey("feedFilterEventNewpage"))
                config.feedFilterEventNewpage = Int32.Parse(rawConfig["feedFilterEventNewpage"]);
            if (rawConfig.ContainsKey("feedFilterEventMove"))
                config.feedFilterEventMove = Int32.Parse(rawConfig["feedFilterEventMove"]);
            if (rawConfig.ContainsKey("feedFilterEventBlock"))
                config.feedFilterEventBlock = Int32.Parse(rawConfig["feedFilterEventBlock"]);
            if (rawConfig.ContainsKey("feedFilterEventDelete"))
                config.feedFilterEventDelete = Int32.Parse(rawConfig["feedFilterEventDelete"]);
            if (rawConfig.ContainsKey("feedFilterEventNewuser"))
                config.feedFilterEventNewuser = Int32.Parse(rawConfig["feedFilterEventNewuser"]);
            if (rawConfig.ContainsKey("feedFilterEventUpload"))
                config.feedFilterEventUpload = Int32.Parse(rawConfig["feedFilterEventUpload"]);
            if (rawConfig.ContainsKey("feedFilterEventProtect"))
                config.feedFilterEventProtect = Int32.Parse(rawConfig["feedFilterEventProtect"]);

            // Include bot nick in all logs from any thread.
            // Especially useful when running mulitple CVNBot instances that
            // log to the same syslog.
            GlobalContext.Properties["Nick"] = config.botNick;
            logger.InfoFormat("Loaded main configuration from {0}", rawConfigFN);

            botCmd = new Regex("^" + config.botNick + @" (\s*(?<command>\S*))(\s(?<params>.*))?$", RegexOptions.IgnoreCase);

            logger.InfoFormat("Loading messages from {0}", config.messagesFile);
            ReadMessages(config.messagesFile);
            if ((!msgs.ContainsKey("00000")) || ((String)msgs["00000"] != "2.03"))
            {
                logger.Fatal("Message file version mismatch or read messages failed");
                Exit();
            }

            // Read projects (prjlist displays logger message)
            prjlist.fnProjectsXML = config.projectsFile;
            prjlist.LoadFromFile();

            listman.InitDBConnection(config.listsFile);

            // Set up IRC client
            irc.Encoding = System.Text.Encoding.UTF8;
            irc.SendDelay = 300;
            irc.AutoReconnect = true;
            irc.AutoRejoin = true;
            irc.ActiveChannelSyncing = true;
            irc.OnChannelMessage += Irc_OnChannelMessage;
            irc.OnChannelNotice += Irc_OnChannelNotice;
            irc.OnConnected += Irc_OnConnected;
            irc.OnError += Irc_OnError;
            irc.OnConnectionError += Irc_OnConnectionError;

            try
            {
                irc.Connect(config.ircServerName, 6667);
            }
            catch (ConnectionException e)
            {
                logger.Fatal("Could not connect", e);
                Exit();
            }

            try
            {
                irc.Login(config.botNick, config.botRealName + " " + version, 4, config.botNick, config.botPass);

                string feedChannel = config.feedChannel;
                string controlChannel = config.controlChannel;
                string broadcastChannel = config.broadcastChannel;
                if (feedChannel != "None")
                {
                    logger.InfoFormat("Joining feed channel: {0}", feedChannel);
                    irc.RfcJoin(feedChannel);
                }
                if (controlChannel != "None")
                {
                    logger.InfoFormat("Joining control channel: {0}", controlChannel);
                    irc.RfcJoin(controlChannel);
                }
                if (broadcastChannel != "None")
                {
                    logger.InfoFormat("Joining broadcast channel: {0}", broadcastChannel);
                    irc.RfcJoin(broadcastChannel);
                }

                // Now connect the RCReader to channels
                new Thread(new ThreadStart(rcirc.InitiateConnection)).Start();

                // Here we tell the IRC API to go into a receive mode, all events
                // will be triggered by _this_ thread (main thread in this case)
                // Listen() blocks by default, you can also use ListenOnce() if you
                // need that does one IRC operation and then returns, so you need then
                // an own loop
                irc.Listen();

                // When Listen() returns, our IRC session is over, let's disconnect
                irc.Disconnect();
            }
            catch (ConnectionException)
            {
                // This exception is handled because Disconnect() can throw
                Exit();
            }
            catch (Exception e)
            {
                // This should not happen, but just in case, we handle it nicely
                logger.Fatal("Error occurred in Main IRC try clause!", e);
                Exit();
            }
        }

        static void Irc_OnConnectionError(object sender, EventArgs e)
        {
            logger.Error("OnConnectionError in Program, restarting...");
            Restart();
        }

        /// <summary>
        /// Catches all unhandled exceptions in the main thread
        /// </summary>
        public static void Application_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                logger.Error("Caught unhandled exception in global catcher", (Exception)e.ExceptionObject);
            }
            catch
            {
                // Logging failed
                Console.WriteLine("Caught unhandled exception, and logging failed: " + e.ExceptionObject);
            }

            try
            {
                PartIRC("Caught unhandled exception");
                Restart();
            }
            catch
            {
                // Restart failed
                Console.WriteLine("Restart failed; exiting with code 24.");
                System.Environment.Exit(24);
            }
        }

        static void Irc_OnError(object sender, Meebey.SmartIrc4net.ErrorEventArgs e)
        {
            logger.Error("IRC: " + e.ErrorMessage);
            if (e.ErrorMessage.Contains("Excess Flood")) // Do not localize
            {
                // Oops, we were flooded off
                logger.Warn("Initiating restart sequence after Excess Flood");
                Restart();
            }
        }

        /// <summary>
        /// This event handler detects incoming broadcast messages
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        static void Irc_OnChannelNotice(object sender, IrcEventArgs e)
        {
            if (e.Data.Channel != config.broadcastChannel)
                return; // Just in case
            if (string.IsNullOrEmpty(e.Data.Message))
                return; // Prevent empty messages from crashing the bot
            Match bm = broadcastMsg.Match(e.Data.Message);
            if (bm.Success)
            {
                try
                {
                    GroupCollection groups = bm.Groups;
                    string action = groups["action"].Captures[0].Value;
                    string list = groups["list"].Captures[0].Value;
                    string item = groups["item"].Captures[0].Value;
                    int len = Convert.ToInt32(groups["len"].Captures[0].Value);
                    string reason = groups["reason"].Captures[0].Value;
                    string adder = groups["adder"].Captures[0].Value;

                    // Similar to ListManager.HandleListCommand
                    switch (action)
                    {
                        case "ADD":
                            switch (list)
                            {
                                case "WL":
                                    listman.AddUserToList(item, "", UserType.whitelisted, adder, reason, len);
                                    break;
                                case "BL":
                                    listman.AddUserToList(item, "", UserType.blacklisted, adder, reason, len);
                                    break;
                                case "GL":
                                    listman.AddUserToList(item, "", UserType.greylisted, adder, reason, len);
                                    break;
                                case "BNU":
                                    listman.AddItemToList(item, 11, adder, reason, len);
                                    break;
                                case "BNA":
                                    listman.AddItemToList(item, 12, adder, reason, len);
                                    break;
                                case "BES":
                                    listman.AddItemToList(item, 20, adder, reason, len);
                                    break;
                                case "CVP":
                                    listman.AddPageToWatchlist(item, "", adder, reason, len);
                                    break;
                                    //Gracefully ignore unknown message types
                            }
                            break;
                        case "DEL":
                            switch (list)
                            {
                                case "WL":
                                    listman.DelUserFromList(item, "", UserType.whitelisted);
                                    break;
                                case "BL":
                                    listman.DelUserFromList(item, "", UserType.blacklisted);
                                    break;
                                case "GL":
                                    listman.DelUserFromList(item, "", UserType.greylisted);
                                    break;
                                case "BNU":
                                    listman.DelItemFromList(item, 11);
                                    break;
                                case "BNA":
                                    listman.DelItemFromList(item, 12);
                                    break;
                                case "BES":
                                    listman.DelItemFromList(item, 20);
                                    break;
                                case "CVP":
                                    listman.DelPageFromWatchlist(item, "");
                                    break;
                                    //Gracefully ignore unknown message types
                            }
                            break;
                        case "FIND":
                            if (list == "BLEEP")
                                if (prjlist.ContainsKey(item))
                                SendMessageF(SendType.Action, reason, "has " + item + ", " + adder + " :D", Priority.High);
                            break;
                        case "COUNT":
                            if (list == "BLEEP")
                                SendMessageF(SendType.Action, reason, "owns " + prjlist.Count.ToString() + " wikis; version is " + version,
                                             Priority.High);
                            break;
                        case "CONFIG":
                            if (list == "BLEEP")
                                BotConfigMsg(reason);
                            break;

                            //Gracefully ignore unknown action types
                    }
                }
                catch (Exception ex)
                {
                    logger.Error("Failed to handle broadcast command", ex);
                    BroadcastDD("ERROR", "BC_ERROR", ex.Message, e.Data.Message);
                }
            }
        }

        static void Irc_OnConnected(object sender, EventArgs e)
        {
            logger.InfoFormat("Connected to {0}", config.ircServerName);
        }


        #region Flood protection code

        /// <summary>
        /// Route all irc.SendMessage() calls through this to use the queue
        /// </summary>
        public static void SendMessageF(SendType type, string destination, string message, Priority priority = Priority.Low)
        {
            irc.SendMessage(type, destination, message, priority);
        }

        /// <summary>
        /// Splitting messages by line breaks and in chucks if they're too long and forward to SendMessageF
        /// </summary>
        public static void SendMessageFMulti(SendType type, string destination, string message, Priority priority = Priority.Low)
        {

            if (message != "")
            {
                //Allow multiline
                foreach (string line in message.Split(new char[] { '\n' }))
                {
                    //Chunk messages that are too long
                    foreach (string chunk in CVNBotUtils.StringSplit(line, 400))
                    {
                        // Ignore "" and "
                        if ((chunk.Trim() != "\"\"") && (chunk.Trim() != "\""))
                        {
                            SendMessageF(type, destination, chunk, priority);
                        }
                    }
                }
            }
        }

        #endregion

        static bool HasPrivileges(char minimum, ref IrcEventArgs e)
        {
            switch (minimum)
            {
                case '@':
                    if (!irc.GetChannelUser(e.Data.Channel, e.Data.Nick).IsOp)
                    {
                        SendMessageF(SendType.Notice, e.Data.Nick, (String)msgs["00122"]);
                        return false;
                    }
                    return true;
                case '+':
                    if (!irc.GetChannelUser(e.Data.Channel, e.Data.Nick).IsOp && !irc.GetChannelUser(e.Data.Channel, e.Data.Nick).IsVoice)
                    {
                        SendMessageF(SendType.Notice, e.Data.Nick, (String)msgs["00120"]);
                        return false;
                    }
                    return true;
                default:
                    return false;
            }
        }

        static void Irc_OnChannelMessage(object sender, IrcEventArgs e)
        {
            // Prevent empty messages from crashing the bot
            if (string.IsNullOrEmpty(e.Data.Message))
                return;

            Match cmdMatch = botCmd.Match(e.Data.Message);

            if (cmdMatch.Success)
            {
                // Have to be voiced to issue any commands
                if (!HasPrivileges('+', ref e))
                    return;

                string command = cmdMatch.Groups["command"].Captures[0].Value;

                string extraParams;
                try
                {
                    extraParams = cmdMatch.Groups["params"].Captures[0].Value.Trim();
                }
                catch (Exception)
                {
                    extraParams = "";
                }

                string[] cmdParams = extraParams.Split(new char[] { ' ' });

                switch (command)
                {
                    case "quit":
                        if (!HasPrivileges('@', ref e))
                            return;
                        logger.Info(e.Data.Nick + " ordered a quit");
                        PartIRC(config.partMsg);
                        Exit();
                        break;
                    case "restart":
                        if (!HasPrivileges('@', ref e))
                            return;
                        logger.Info(e.Data.Nick + " ordered a restart");
                        PartIRC("Rebooting by order of " + e.Data.Nick + " ...");
                        Restart();
                        break;
                    case "status":
                        TimeSpan ago = DateTime.Now.Subtract(rcirc.lastMessage);
                        SendMessageF(SendType.Message, e.Data.Channel, "Last message was received on RCReader "
                                     + ago.TotalSeconds.ToString() + " seconds ago", Priority.High);
                        break;
                    case "help":
                        SendMessageF(SendType.Message, e.Data.Channel, (String)msgs["20005"], Priority.High);
                        break;
                    case "version":
                    case "settings":
                    case "config":
                        BotConfigMsg(e.Data.Channel);
                        if (cmdParams[0] == "all")
                        {
                            Broadcast("BLEEP", "CONFIG", "BLEEP", 0, e.Data.Channel, e.Data.Nick);
                        }
                        break;
                    case "msgs":
                        // Reloads messages
                        if (!HasPrivileges('@', ref e))
                            return;
                        ReadMessages(rawConfig["messages"]);
                        SendMessageF(SendType.Message, e.Data.Channel, "Re-read messages", Priority.High);
                        break;
                    case "reload":
                        // Reloads wiki data for a project
                        if (!HasPrivileges('@', ref e))
                            return;

                        if (!prjlist.ContainsKey(cmdParams[0]))
                        {
                            SendMessageF(SendType.Message, e.Data.Channel, "Project " + cmdParams[0] + " is not loaded", Priority.High);
                            return;
                        }

                        try
                        {

                            ((Project)prjlist[cmdParams[0]]).RetrieveWikiDetails();
                            SendMessageF(SendType.Message, e.Data.Channel, "Reloaded project " + cmdParams[0], Priority.High);
                        }
                        catch (Exception ex)
                        {
							SendMessageF(SendType.Message, e.Data.Channel, "Unable to reload: " + ex.Message, Priority.High);
                            logger.Error("Reload project failed", ex);
                        }
                        break;
                    case "load":
                        if (!HasPrivileges('@', ref e))
                            return;
                        try
                        {
                            if (cmdParams.Length == 2)
                                prjlist.AddNewProject(cmdParams[0], cmdParams[1]);
                            else
                                prjlist.AddNewProject(cmdParams[0], "");

                            SendMessageF(SendType.Message, e.Data.Channel, "Loaded new project " + cmdParams[0], Priority.High);
                            // Automatically get admins and bots
                            SendMessageF(SendType.Message, e.Data.Channel, listman.ConfigGetAdmins(cmdParams[0]), Priority.High);
                            SendMessageF(SendType.Message, e.Data.Channel, listman.ConfigGetBots(cmdParams[0]), Priority.High);
                        }
                        catch (Exception ex)
                        {
                            SendMessageF(SendType.Message, e.Data.Channel, "Unable to add project: " + ex.Message, Priority.High);
                            logger.Error("Add project failed", ex);
                        }
                        break;
                    case "bleep":
                        if (!HasPrivileges('+', ref e))
                            return;
                        try
                        {
                            if (cmdParams[0].Length > 0)
                            {
                                if (prjlist.ContainsKey(cmdParams[0]))
                                {
                                    SendMessageF(SendType.Action, e.Data.Channel, "has " + cmdParams[0] + ", " + e.Data.Nick + " :D", Priority.High);
                                }
                                else
                                {
                                    Broadcast("BLEEP", "FIND", cmdParams[0], 0, e.Data.Channel, e.Data.Nick);
                                    SendMessageF(SendType.Message, e.Data.Channel, "Bleeped. Please wait for a reply.", Priority.High);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            SendMessageF(SendType.Message, e.Data.Channel, "Unable to bleep: " + ex.Message, Priority.High);
                        }
                        break;
                    case "count":
                        if (!HasPrivileges('+', ref e))
                            return;
                        Broadcast("BLEEP", "COUNT", "BLEEP", 0, e.Data.Channel, e.Data.Nick);
                        SendMessageF(SendType.Action, e.Data.Channel, "owns " + prjlist.Count.ToString() + " wikis; version is " + version,
                                     Priority.High);
                        break;
                    case "drop":
                        if (!HasPrivileges('@', ref e))
                            return;
                        try
                        {
                            prjlist.DeleteProject(cmdParams[0]);
                            SendMessageF(SendType.Message, e.Data.Channel, "Deleted project " + cmdParams[0], Priority.High);
                        }
                        catch (Exception ex)
                        {
                            SendMessageF(SendType.Message, e.Data.Channel, "Unable to delete project: " + ex.Message, Priority.High);
                            logger.Error("Delete project failed", ex);
                        }
                        break;
                    case "list":
                        string result = "Currently monitoring: ";
                        foreach (string p in prjlist.Keys)
                        {
                            result += p + " ";
                        }
                        result += "(Total: " + prjlist.Count.ToString() + " wikis)";
                        SendMessageFMulti(SendType.Message, e.Data.Channel, result, Priority.High);
                        break;
                    case "batchgetusers":
                        if (!HasPrivileges('@', ref e))
                            return;
                        new Thread(listman.BatchGetAllAdminsAndBots).Start(e.Data.Channel);
                        break;
                    case "bl":
                        SendMessageF(SendType.Message, e.Data.Channel,
                                     listman.HandleListCommand(1, e.Data.Nick, extraParams), Priority.High);
                        break;
                    case "wl":
                        SendMessageF(SendType.Message, e.Data.Channel,
                                     listman.HandleListCommand(0, e.Data.Nick, extraParams), Priority.High);
                        break;
                    case "gl":
                        SendMessageF(SendType.Message, e.Data.Channel,
                                     listman.HandleListCommand(6, e.Data.Nick, extraParams), Priority.High);
                        break;
                    case "al":
                        SendMessageF(SendType.Message, e.Data.Channel,
                                     listman.HandleListCommand(2, e.Data.Nick, extraParams), Priority.High);
                        break;
                    case "bots":
                    case "bot":
                        SendMessageF(SendType.Message, e.Data.Channel,
                                     listman.HandleListCommand(5, e.Data.Nick, extraParams), Priority.High);
                        break;
                    case "cvp":
                        SendMessageF(SendType.Message, e.Data.Channel,
                                     listman.HandleListCommand(10, e.Data.Nick, extraParams), Priority.High);
                        break;
                    case "bnu":
                        SendMessageF(SendType.Message, e.Data.Channel,
                                     listman.HandleListCommand(11, e.Data.Nick, extraParams), Priority.High);
                        break;
                    case "bna":
                        SendMessageF(SendType.Message, e.Data.Channel,
                                     listman.HandleListCommand(12, e.Data.Nick, extraParams), Priority.High);
                        break;
                    case "bes":
                        SendMessageF(SendType.Message, e.Data.Channel,
                                     listman.HandleListCommand(20, e.Data.Nick, extraParams), Priority.High);
                        break;

                    //_1568: Restrict the "get" command to ops
                    case "getadmins":
                        if (!HasPrivileges('@', ref e))
                            return;
                        SendMessageF(SendType.Message, e.Data.Channel, listman.ConfigGetAdmins(extraParams), Priority.High);
                        break;
                    case "getbots":
                        if (!HasPrivileges('@', ref e))
                            return;
                        SendMessageF(SendType.Message, e.Data.Channel, listman.ConfigGetBots(extraParams), Priority.High);
                        break;

                    case "intel":
                        string intelResult = listman.GlobalIntel(extraParams);
                        SendMessageFMulti(SendType.Message, e.Data.Channel, intelResult, Priority.High);
                        break;
                    case "purge":
                        if (!HasPrivileges('@', ref e))
                            return;
                        SendMessageF(SendType.Message, e.Data.Channel, listman.PurgeWikiData(extraParams), Priority.High);
                        break;
                    case "batchreload":
                        if (!HasPrivileges('@', ref e))
                            return;
                        prjlist.currentBatchReloadChannel = e.Data.Channel;
                        new Thread(new ThreadStart(prjlist.ReloadAllWikis)).Start();
                        break;
                }
            }
        }

        /// <summary>
        /// Reads messages from filename (Console.msgs) into SortedList msgs
        /// </summary>
        /// <param name="filename">File to read messages from</param>
        static void ReadMessages(string filename)
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
                            string[] parts = line.Split(new char[] { '=' }, 2);
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
        static string GetMessage(int msgCode, ref Hashtable attributes)
        {
            try
            {
                string message = (string)msgs[msgCode.ToString().PadLeft(5, '0')];
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
        public static string GetFormatMessage(int msgCode, params String[] fParams)
        {
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
            if (config.broadcastChannel == "None")
                return;
            string bMsg = "*%BB/1.1%B*" + list + "*" + action + "*%C07%B" + item + "%B%C*%C13" + expiry.ToString()
                + "%C*%C09%B" + reason + "%B%C*%C11%B" + adder + "%C%B*";
            SendMessageF(SendType.Notice, config.broadcastChannel, bMsg.Replace(@"%C", "\x03").Replace(@"%B", "\x02"), Priority.High);
        }

        public static void BroadcastDD(string type, string codename, string message, string ingredients)
        {
            if (config.broadcastChannel == "None")
                return;
            string bMsg = "*%BDD/1.0%B*" + type + "*" + codename + "*%C07%B" + message + "%B%C*%C13" + ingredients + "%C*";
            SendMessageF(SendType.Notice, config.broadcastChannel, bMsg.Replace(@"%C", "\x03").Replace(@"%B", "\x02"), Priority.High);
            logger.Info("Broadcasted DD: " + type + "," + codename + "," + message + "," + ingredients);
        }

        /// <summary>
        /// Shorthand greylisting function for use by ReactToRCEvent
        /// </summary>
        /// <param name="userOffset"></param>
        /// <param name="username"></param>
        /// <param name="reason"></param>
        static void AddToGreylist(UserType userOffset, string username, string reason)
        {
            // Only do greylisting if they are currently blacklisted, reguser, anon, or already greylisted.
            // In other words, never greylist trusted users (bot, admin, whitelist).
            if (userOffset == UserType.blacklisted || userOffset == UserType.user || userOffset == UserType.anon || userOffset == UserType.greylisted)
            {
                listman.AddUserToList(username, "", UserType.greylisted, "CVNBot", reason, 1);
                // Greylist for 900 seconds = 15 mins
                // TODO: Why is the broadcasted expiry different from local expiry (line above)
                Broadcast("GL", "ADD", username, 900, reason, "CVNBot");
            }
        }

        /// <summary>
        /// Reacts to the RC Event, passed from RCReader. Remember: this runs in the RCReader thread!
        /// </summary>
        /// <param name="r"></param>
        public static void ReactToRCEvent(RCEvent r)
        {
            int feedFilterThisEvent = 1;
            int feedFilterThisUser = 1;

            // Feed filters -> Event
            // Perform these checks before even classifying the user
            // EventType is available right away, thus saving a db connection when setting is on 4 ('ignore')
            if (r.minor)
                feedFilterThisEvent = config.feedFilterEventMinorEdit;

            if (r.eventtype == RCEvent.EventType.edit && !r.newpage)
                feedFilterThisEvent = config.feedFilterEventEdit;

            if (r.eventtype == RCEvent.EventType.edit && r.newpage)
                feedFilterThisEvent = config.feedFilterEventNewpage;

            if (r.eventtype == RCEvent.EventType.move)
                feedFilterThisEvent = config.feedFilterEventMove;

            if (r.eventtype == RCEvent.EventType.delete)
                feedFilterThisEvent = config.feedFilterEventDelete;

            if (r.eventtype == RCEvent.EventType.block || r.eventtype == RCEvent.EventType.unblock)
                feedFilterThisEvent = config.feedFilterEventBlock;

            if (r.eventtype == RCEvent.EventType.newuser || r.eventtype == RCEvent.EventType.newuser2 || r.eventtype == RCEvent.EventType.autocreate)
                feedFilterThisEvent = config.feedFilterEventNewuser;

            if (r.eventtype == RCEvent.EventType.upload)
                feedFilterThisEvent = config.feedFilterEventUpload;

            if (r.eventtype == RCEvent.EventType.protect || r.eventtype == RCEvent.EventType.unprotect || r.eventtype == RCEvent.EventType.modifyprotect)
                feedFilterThisEvent = config.feedFilterEventProtect;

            if (feedFilterThisEvent == 4)
                // 4 means "ignore"
                return;

            if (config.isCubbie && (r.eventtype != RCEvent.EventType.upload))
                //If this IsCubbie, then ignore all non-uploads
                return;

            if (r.botflag && (config.feedFilterUsersBot == 4))
                return;

            Hashtable attribs = new Hashtable();
            String message = "";
            UserType userOffset = listman.ClassifyEditor(r.user, r.project);

            // FIXME: If the current event is by a bot user and it blocks (eg. bot admin) and
            // bot edits are ignored (default) then the user will not be blacklisted
            // TODO: Add new userOffset for botadmin?

            // Feed filters -> Users
            if (userOffset == UserType.anon)
                feedFilterThisUser = config.feedFilterUsersAnon;

            if (userOffset == UserType.user)
                feedFilterThisUser = config.feedFilterUsersReg;

            if (userOffset == UserType.bot)
                feedFilterThisUser = config.feedFilterUsersBot;

            if (feedFilterThisUser == 4)// 4 is "ignore"
                return;

            Project project = ((Project)prjlist[r.project]);

            switch (r.eventtype)
            {
                // This case handles:
                // - New page creations
                // - Page edits
                case RCEvent.EventType.edit:
                    String diffsize;
                    if (r.szdiff >= 0)
                        diffsize = "+" + r.szdiff.ToString();
                    else
                        diffsize = r.szdiff.ToString();

                    attribs.Add("editor", project.interwikiLink + "User:" + r.user);
                    attribs.Add("ceditor", r.user);
                    attribs.Add("article", project.interwikiLink + r.title);
                    attribs.Add("carticle", r.title);
                    attribs.Add("size", diffsize);
                    attribs.Add("url", r.url);
                    attribs.Add("reason", r.comment);

                    // This block handles: New page creations
                    if (r.newpage)
                    {
                        bool createSpecial = false;

                        if (userOffset == UserType.admin || userOffset == UserType.whitelisted)
                            // Ignore new pages created by an admin or whitelisted user
                            return;

                        // Initialise the "sizeattrib" and "sizereset" attributes, which are used
                        // by all messages, including the later messages for listman-matches.
                        // The message keys assigned here may be used as a fallback.
                        if (r.szdiff >= config.newBig)
                        {
                            createSpecial = true;
                            attribs.Add("sizeattrib", GetMessage(100, ref attribs));
                            attribs.Add("sizereset", GetMessage(102, ref attribs));
                            message = GetMessage(5010 + (int)userOffset, ref attribs);
                        }
                        else if (r.szdiff <= config.newSmall)
                        {
                            createSpecial = true;
                            attribs.Add("sizeattrib", GetMessage(101, ref attribs));
                            attribs.Add("sizereset", GetMessage(103, ref attribs));
                            message = GetMessage(5020 + (int)userOffset, ref attribs);
                        }
                        else
                        {
                            attribs.Add("sizeattrib", "");
                            attribs.Add("sizereset", "");
                            message = GetMessage(5000 + (int)userOffset, ref attribs);
                        }

                        // The remaining checks go descending order of priority.
                        // The first match wins.
                        // - Article is on watchlist
                        // - Page title matches a BNA pattern
                        // - Edit summary matches a BES pattern

                        // Is the article on the watchlist?
                        ListMatch wlm = listman.IsWatchedArticle(r.title, r.project);
                        if (wlm.Success)
                        {
                            // Matches watchlist (CVP)
                            message = GetMessage(5030 + (int)userOffset, ref attribs);
                            AddToGreylist(userOffset, r.user, Program.GetFormatMessage(16301, (String)attribs["article"]));
                            break;
                        }

                        // Does the page title match a BNA pattern?
                        ListMatch eslm = listman.MatchesList(r.title, 12);
                        if (eslm.Success)
                        {
                            // Matches BNA
                            attribs.Add("watchword", eslm.matchedItem);
                            message = GetMessage(5040 + (int)userOffset, ref attribs);
                            AddToGreylist(userOffset, r.user, Program.GetFormatMessage(16300, (String)attribs["article"], eslm.matchedItem));
                            break;
                        }

                        // Does the edit summary match a BES pattern?
                        ListMatch lm = listman.MatchesList(r.comment, 20);
                        if (lm.Success)
                        {
                            // Matches BES
                            attribs.Add("watchword", lm.matchedItem);
                            message = GetMessage(95040 + (int)userOffset, ref attribs);
                            AddToGreylist(userOffset, r.user, Program.GetFormatMessage(16300, (String)attribs["article"], lm.matchedItem));
                            break;
                        }

                        // If we're still here that means
                        // - the create didn't get ignored by adminlist or whitelist
                        // - the create didn't match any watch patterns
                        // Now, if any of the following is true, we'll must report it.
                        // - Create by blacklisted user
                        // - Create by greylisted user
                        // - Current usertype is configured to always report
                        //   (By default this is for anonymous users, via feedFilterUsersAnon=1,
                        //   but feedFilterUsersReg or feedFilterUsersBot could also be set to 1)
                        if (userOffset == UserType.blacklisted || userOffset == UserType.greylisted || feedFilterThisUser == 1)
                            break;

                        if (userOffset == UserType.user && !createSpecial)
                            // Ignore page creation by unlisted reguser with non-special create size
                            return;

                        // Else: Create had special size, so let it shown (default), don't return!
                    }
                    // This block handles: Page edits
                    else
                    {
                        bool editSpecial = false;

                        if (userOffset == UserType.admin || userOffset == UserType.whitelisted)
                            // Ignore edit by admin or whitelisted user
                            return;

                        // Initialise the "sizeattrib" and "sizereset" attributes, which are used
                        // by all messages, including the later messages for listman-matches.
                        // The message keys assigned here may be used as a fallback.
                        if (r.szdiff >= config.editBig)
                        {
                            attribs.Add("sizeattrib", GetMessage(100, ref attribs));
                            attribs.Add("sizereset", GetMessage(102, ref attribs));
                            message = GetMessage(5110 + (int)userOffset, ref attribs);
                            editSpecial = true;
                        }
                        else if (r.szdiff <= config.editBlank)
                        {
                            attribs.Add("sizeattrib", GetMessage(101, ref attribs));
                            attribs.Add("sizereset", GetMessage(103, ref attribs));
                            message = GetMessage(5120 + (int)userOffset, ref attribs);
                            editSpecial = true;
                        }
                        else
                        {
                            attribs.Add("sizeattrib", "");
                            attribs.Add("sizereset", "");
                            message = GetMessage(5100 + (int)userOffset, ref attribs);
                        }

                        // The remaining checks go descending order of priority.
                        // The first match wins.
                        // - Edit summary matches a BES pattern
                        // - Edit blanked the page
                        // - Edit replaced the page
                        // - Article is on watchlist

                        // Does the edit summary match a BES pattern?
                        ListMatch elm = listman.MatchesList(r.comment, 20);
                        if (elm.Success)
                        {
                            // Matches BES
                            attribs.Add("watchword", elm.matchedItem);
                            message = GetMessage(95130 + (int)userOffset, ref attribs);
                            AddToGreylist(userOffset, r.user, Program.GetFormatMessage(16310, r.comment, (String)attribs["article"]));
                            break;
                        }

                        // Did the user user blank the page?
                        if (project.rautosummBlank.IsMatch(r.comment))
                        {
                            message = GetMessage(96010 + (int)userOffset, ref attribs);
                            AddToGreylist(userOffset, r.user, Program.GetFormatMessage(16311, (String)attribs["article"]));
                            break;
                        }

                        Match rplm = project.rautosummReplace.Match(r.comment);
                        if (rplm.Success)
                        {
                            // The user replaced the page.
                            try
                            {
                                attribs.Add("profanity", rplm.Groups["item1"].Captures[0].Value);
                                message = GetMessage(96020 + (int)userOffset, ref attribs);
                            }
                            catch (ArgumentOutOfRangeException)
                            {
                                // This wiki probably doesn't have a profanity attribute
                                message = GetMessage(96030 + (int)userOffset, ref attribs);
                            }
                            break;
                        }

                        // Is the article on the watchlist?
                        ListMatch welm = listman.IsWatchedArticle(r.title, r.project);
                        if (welm.Success)
                        {
                            // Matches watchlist (CVP)
                            message = GetMessage(5130 + (int)userOffset, ref attribs);
                            break;
                        }

                        // If we're still here that means:
                        // - the edit didn't get ignored by adminlist or whitelist
                        // - the edit didn't match any watch patterns
                        //
                        // Now, if any of the following is true, we must still report it:
                        // - Edit by blacklisted user
                        // - Edit by greylisted user
                        // - Current usertype is configured to always report
                        //   (By default this is for anonymous users, via feedFilterUsersAnon=1,
                        //   but feedFilterUsersReg or feedFilterUsersBot could also be set to 1)
                        if (userOffset == UserType.blacklisted || userOffset == UserType.greylisted || feedFilterThisUser == 1)
                            break;

                        // If nothing special about the edit, return to ignore
                        if (!editSpecial)
                            return;
                    }
                    break;
                case RCEvent.EventType.move:
                    // if moves are softhidden, then hide moves by admin, bot or whitelist
                    if (config.feedFilterEventMove == 2 && (userOffset == UserType.admin || userOffset == UserType.bot || userOffset == UserType.whitelisted))
                    {
                        return;
                    }
                    attribs.Add("editor", project.interwikiLink + "User:" + r.user);
                    attribs.Add("ceditor", r.user);
                    attribs.Add("fromname", project.interwikiLink + r.title);
                    attribs.Add("cfromname", r.title);
                    attribs.Add("toname", project.interwikiLink + r.movedTo);
                    attribs.Add("ctoname", r.movedTo);
                    // The blockLength field stores the moveFrom URL
                    attribs.Add("url", r.blockLength);
                    attribs.Add("reason", r.comment);
                    message = GetMessage(5500 + (int)userOffset, ref attribs);
                    break;
                case RCEvent.EventType.block:
                    attribs.Add("blockname", project.interwikiLink + r.title);
                    attribs.Add("cblockname", r.title.Split(new char[] { ':' }, 2)[1]);
                    attribs.Add("editor", project.interwikiLink + "User:" + r.user);
                    attribs.Add("ceditor", r.user);
                    attribs.Add("talkurl", project.rooturl + "wiki/User_talk:" + CVNBotUtils.WikiEncode(r.title.Split(new char[] { ':' }, 2)[1]));
                    attribs.Add("length", r.blockLength);
                    attribs.Add("reason", r.comment);
                    message = GetMessage(5400, ref attribs);
                    // If the blocked user (r.title) isn't botlisted, add to blacklist
                    if (listman.ClassifyEditor(r.title.Split(new char[] { ':' }, 2)[1], r.project) != UserType.bot)
                    {
                        // If this isn't an indefinite/infinite block, add to blacklist
                        if ((r.blockLength.ToLower() != "indefinite") && (r.blockLength.ToLower() != "infinite"))
                        {                                                               // 2,678,400 seconds = 744 hours = 31 days
                            int listLen = Convert.ToInt32(CVNBotUtils.ParseDateTimeLength(r.blockLength, 2678400) * 2.5);
                            string blComment = "Autoblacklist: " + r.comment + " on " + r.project;
                            message += "\n" + listman.AddUserToList(r.title.Split(new char[] { ':' }, 2)[1], "" //Global bl
                                , UserType.blacklisted, r.user, blComment, listLen);
                            Broadcast("BL", "ADD", r.title.Split(new char[] { ':' }, 2)[1], listLen, blComment, r.user);
                        }
                    }
                    break;
                case RCEvent.EventType.unblock:
                    attribs.Add("blockname", project.interwikiLink + r.title);
                    attribs.Add("cblockname", r.title.Split(new char[] { ':' }, 2)[1]);
                    attribs.Add("editor", project.interwikiLink + "User:" + r.user);
                    attribs.Add("ceditor", r.user);
                    attribs.Add("talkurl", project.rooturl + "wiki/User_talk:" + CVNBotUtils.WikiEncode(r.user));
                    attribs.Add("reason", r.comment);
                    message = GetMessage(5700, ref attribs);
                    break;
                case RCEvent.EventType.delete:
                    attribs.Add("editor", project.interwikiLink + "User:" + r.user);
                    attribs.Add("ceditor", r.user);
                    attribs.Add("article", project.interwikiLink + r.title);
                    attribs.Add("carticle", r.title);
                    attribs.Add("url", project.rooturl + "wiki/" + CVNBotUtils.WikiEncode(r.title));
                    attribs.Add("reason", r.comment);
                    message = GetMessage(05300, ref attribs);
                    break;
                case RCEvent.EventType.newuser:
                    attribs.Add("editor", project.interwikiLink + "User:" + r.user);
                    attribs.Add("ceditor", r.user);
                    attribs.Add("blockurl", project.rooturl + "wiki/Special:Block/" + CVNBotUtils.WikiEncode(r.user));
                    attribs.Add("caurl", "https://meta.wikimedia.org/wiki/Special:CentralAuth/" + CVNBotUtils.WikiEncode(r.user));
                    attribs.Add("talkurl", project.rooturl + "wiki/User_talk:" + CVNBotUtils.WikiEncode(r.user));
                    ListMatch bnuMatch = listman.MatchesList(r.user, 11);
                    if (bnuMatch.Success && feedFilterThisEvent == 1)
                    {
                        // Matches BNU
                        attribs.Add("watchword", bnuMatch.matchedItem);
                        attribs.Add("wwreason", bnuMatch.matchedReason);
                        message = GetMessage(5201, ref attribs);
                        AddToGreylist(userOffset, r.user, Program.GetFormatMessage(16320, bnuMatch.matchedItem));
                    }
                    // Only show non-special creations if newuser event is 1 ('show')
                    else if (feedFilterThisEvent == 1)
                    {
                        message = GetMessage(5200, ref attribs);
                    }
                    break;
                case RCEvent.EventType.newuser2:
                    attribs.Add("creator", project.interwikiLink + "User:" + r.user);
                    attribs.Add("ccreator", r.user);
                    attribs.Add("editor", project.interwikiLink + "User:" + r.title);
                    attribs.Add("ceditor", r.title);
                    attribs.Add("blockurl", project.rooturl + "wiki/Special:Block/" + CVNBotUtils.WikiEncode(r.user));
                    attribs.Add("caurl", "https://meta.wikimedia.org/wiki/Special:CentralAuth/" + CVNBotUtils.WikiEncode(r.user));
                    attribs.Add("talkurl", project.rooturl + "wiki/User_talk:" + CVNBotUtils.WikiEncode(r.user));
                    ListMatch bnuMatch2 = listman.MatchesList(r.user, 11);
                    if (bnuMatch2.Success)
                    {
                        // Matches BNU
                        attribs.Add("watchword", bnuMatch2.matchedItem);
                        attribs.Add("wwreason", bnuMatch2.matchedReason);
                        message = GetMessage(5211, ref attribs);
                        AddToGreylist(userOffset, r.user, Program.GetFormatMessage(16320, bnuMatch2.matchedItem));
                    }
                    // Only show non-special creations if newuser event is 1 ('show')
                    else if (feedFilterThisEvent == 1)
                    {
                        message = GetMessage(5210, ref attribs);
                    }
                    break;
                case RCEvent.EventType.upload:
                    int uMsg = 5600;

                    // Check if the edit summary matches BES
                    ListMatch ubes2 = listman.MatchesList(r.comment, 20);
                    if (ubes2.Success)
                    {
                        attribs.Add("watchword", ubes2.matchedItem);
                        attribs.Add("lmreason", ubes2.matchedReason);
                        uMsg = 95620;
                    }
                    else
                    {
                        // Now check if the title matches BES
                        ListMatch ubes1 = listman.MatchesList(r.title, 20);
                        if (ubes1.Success)
                        {
                            attribs.Add("watchword", ubes1.matchedItem);
                            attribs.Add("lmreason", ubes1.matchedReason);
                            uMsg = 95620;
                        }
                    }

                    // Check if upload is watched
                    ListMatch uwa = listman.IsWatchedArticle(r.title, r.project);
                    if (uwa.Success)
                        uMsg = 5610;

                    // If normal and uploaded by an admin, bot or whitelisted person always hide
                    if (uMsg == 5600 && (userOffset == UserType.admin || userOffset == UserType.bot || userOffset == UserType.whitelisted))
                        return;

                    // if normal and uploads are softhidden hide normal user and anon
                    if (uMsg == 5600 && config.feedFilterEventUpload == 2 && (userOffset == UserType.anon || userOffset == UserType.user))
                        return;

                    // If our message is 95620, we might need to truncate r.comment
                    if (uMsg == 95620)
                    {
                        if (r.comment.Length > 25)
                            r.comment = r.comment.Substring(0, 23) + "...";
                    }

                    attribs.Add("editor", project.interwikiLink + "User:" + r.user);
                    attribs.Add("ceditor", r.user);
                    attribs.Add("uploaditem", project.interwikiLink + r.title);
                    attribs.Add("cuploaditem", r.title);
                    attribs.Add("reason", r.comment);
                    attribs.Add("url", project.rooturl + "wiki/" + CVNBotUtils.WikiEncode(r.title));
                    message = GetMessage(uMsg + (int)userOffset, ref attribs);
                    break;
                case RCEvent.EventType.protect:
                    attribs.Add("editor", project.interwikiLink + "User:" + r.user);
                    attribs.Add("ceditor", r.user);
                    attribs.Add("article", project.interwikiLink + r.title);
                    attribs.Add("carticle", r.title);
                    attribs.Add("comment", r.comment);
                    // 'url' in protect is broken, it also contains " [move=sysop] (indefinite)" etc.
                    //attribs.Add("url", CVNBotUtils.rootUrl(project.rooturl) + "wiki/" + CVNBotUtils.wikiEncode(r.title));
                    message = GetMessage(5900, ref attribs);
                    break;
                case RCEvent.EventType.unprotect:
                    attribs.Add("editor", project.interwikiLink + "User:" + r.user);
                    attribs.Add("ceditor", r.user);
                    attribs.Add("article", project.interwikiLink + r.title);
                    attribs.Add("carticle", r.title);
                    attribs.Add("comment", r.comment);
                    // 'url' in unprotect is fine, it's just the pagetitle
                    attribs.Add("url", project.rooturl + "wiki/" + CVNBotUtils.WikiEncode(r.title));
                    message = GetMessage(5901, ref attribs);
                    break;
                case RCEvent.EventType.modifyprotect:
                    attribs.Add("editor", project.interwikiLink + "User:" + r.user);
                    attribs.Add("ceditor", r.user);
                    attribs.Add("article", project.interwikiLink + r.title);
                    attribs.Add("carticle", r.title);
                    attribs.Add("comment", r.comment);
                    // 'url' in modifyprotect is broken, it also contains " [move=sysop] (indefinite)" etc.
                    //attribs.Add("url", CVNBotUtils.rootUrl(project.rooturl) + "wiki/" + CVNBotUtils.wikiEncode(r.title));
                    message = GetMessage(5902, ref attribs);
                    break;
            }

            if (feedFilterThisEvent == 3 || feedFilterThisUser == 3)
            {
                // Autolistings have been done throughout ReactToRCEvent()
                // If this message triggered hardhide, we're done now
                // Hide message:
                message = "";
            }

            SendMessageFMulti(SendType.Message, config.feedChannel, message, Priority.Low);

        }

        public static void BotConfigMsg(string destChannel)
        {
            // The only operational information provided here is the bot version.
            // The settings printed to IRC are limited to feed settings that users
            // can observe.
            // Operational configuration for servers, files, and restarts is not
            // interesting to users.

            string message = "runs CVNBot " + version + " in " + config.feedChannel + "; settings: ";

            FieldInfo[] fields = config.GetType().GetFields();
            foreach (FieldInfo field in fields)
            {
                string name = field.Name;
                if (name.StartsWith("bot") ||
                    name == "realName" ||
                    name == "partMsg" ||
                    name == "ircServerName" ||
                    name.EndsWith("Channel") ||
                    name.EndsWith("File") ||
                    name.StartsWith("restart")
                   ) {
                    // SECURITY: Keep 'botpass' private.
                    continue;
                }
                string val = Convert.ToString(field.GetValue(config));
                message += name + ": " + val + ", ";
            }
            message = message.TrimEnd(new char[] { ',', ' ' });

            SendMessageFMulti(SendType.Action, destChannel, message, Priority.High);

        }

        /// <summary>
        /// First call PartIRC() with a reason, before calling Exit().
        /// Except:
        /// - if there is a problem before the program was fully connected.
        /// - if we know we have already left the channel.
        /// - from inside Restart(), which is a wrapper for Exit().
        /// </remarks>
        public static void Exit()
        {
            try
            {
                irc.Disconnect();
                rcirc.rcirc.Disconnect();

                listman.CloseDBConnection();
                LogManager.Shutdown();
            }
            catch
            {
                // Ignore
            }
            finally
            {
                Environment.Exit(0);

            }
        }

        /// <summary>
        /// Always call PartIRC() with a reason before calling Restart().
        /// </summary>
        public static void Restart()
        {
            string cmd = config.restartCmd;
            string args = config.restartArgs.Replace("$1", Assembly.GetExecutingAssembly().Location);
            logger.InfoFormat("Executing: {0} {1}", cmd, args);
            System.Diagnostics.Process.Start(cmd, args);
            Exit();
        }

        public static void PartIRC(string quitMessage)
        {
            rcirc.rcirc.RfcQuit(quitMessage);
            irc.RfcQuit(quitMessage);
            Thread.Sleep(1000);
        }
    }
}
