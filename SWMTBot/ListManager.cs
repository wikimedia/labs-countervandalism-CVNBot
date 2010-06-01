using System;
using System.Collections;
using System.Text;
using System.Text.RegularExpressions;
using System.Data;
using Mono.Data.SqliteClient;
using System.IO;
using System.Threading;
using log4net;

namespace SWMTBot
{
    struct listMatch
    {
        public bool Success;
        public string matchedItem;
        public string matchedReason;
    }

    class ListManager
    {
        public enum UserType { admin = 2, whitelisted = 0, blacklisted = 1, bot = 5, user = 4, anon = 3, greylisted = 6 }
        static Regex ipv4 = new Regex(@"\b(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\b");
        static Regex adminLine = new Regex("<li><a href=\"/wiki/.*?\" title=\".*?\">(.*?)</a>");
        static Regex rlistCmd = new Regex(@"^(?<cmd>add|del|show|test) (?<item>.+?)(?: p=(?<project>\S+?))?(?: x=(?<len>\d{1,4}))?(?: r=(?<reason>.+?))?$"
            , RegexOptions.IgnoreCase);

        private Object dbtoken = new Object();
        private static string currentGetThreadWiki = ""; //Used to pass data to the configGetAdmins/configGetBots threads
        private static string currentGetThreadMode = ""; //Same as above
        public string currentGetBatchChannel = ""; //Used for the batch function

        public IDbConnection dbcon;
        public string connectionString = "";

        private Timer garbageCollector;
 
        private static ILog logger = LogManager.GetLogger("SWMTBot.ListManager");

        public void initDBConnection(string filename)
        {
            FileInfo fi = new FileInfo(filename);
            bool alreadyExists = fi.Exists;
            connectionString = "URI=file:" + filename + ",version=3";
            dbcon = (IDbConnection)new SqliteConnection(connectionString);
            dbcon.Open();
            if (!alreadyExists)
            {
                // The file didn't exist before, so initialize tables
                IDbCommand cmd = dbcon.CreateCommand();
                cmd.CommandText = "CREATE TABLE users ( name varchar(64), project varchar(32), type integer(2), adder varchar(64), reason varchar(80), expiry integer(32) )";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "CREATE TABLE watchlist ( article varchar(64), project varchar(32), adder varchar(64), reason varchar(80), expiry integer(32) )";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "CREATE TABLE items ( item varchar(80), itemtype integer(2), adder varchar(64), reason varchar(80), expiry integer(32) )";
                cmd.ExecuteNonQuery();
            }

            // Start the expired item garbage collector
            TimerCallback gcDelegate = new TimerCallback(collectGarbage);
            garbageCollector = new Timer(gcDelegate, null, 10000, 7200000); //Start first collection in 10 secs; then, every two hours
        }

        void collectGarbage(object stateInfo)
        {
            //logger.Info("Tim is throwing out expired items");
            int total = 0;
            IDbConnection timdbcon = (IDbConnection)new SqliteConnection(connectionString);
            timdbcon.Open();
            IDbCommand timcmd = timdbcon.CreateCommand();
            lock (dbtoken)
            {
                timcmd.CommandText = "DELETE FROM users WHERE ((expiry < '" + DateTime.Now.Ticks.ToString() + "') AND (expiry != '0'))";
                total += timcmd.ExecuteNonQuery();
                timcmd.CommandText = "DELETE FROM watchlist WHERE ((expiry < '" + DateTime.Now.Ticks.ToString() + "') AND (expiry != '0'))";
                total += timcmd.ExecuteNonQuery();
                timcmd.CommandText = "DELETE FROM items WHERE ((expiry < '" + DateTime.Now.Ticks.ToString() + "') AND (expiry != '0'))";
                total += timcmd.ExecuteNonQuery();
                timdbcon.Close();
            }
            timdbcon = null;
            logger.Info("Tim threw away " + total.ToString() + " items");
        }

        public void closeDBConnection()
        {
            dbcon.Close();
            dbcon = null;
        }

        /// <summary>
        /// Gets an expiry date in ticks in relation to Now
        /// </summary>
        /// <param name="expiry">How many seconds in the future to set expiry to</param>
        /// <returns></returns>
        string getExpiryDate(int expiry)
        {
            if (expiry == 0)
                return "0";
            else
                return DateTime.Now.AddSeconds(expiry).Ticks.ToString();
        }

        /// <summary>
        /// Returns a human-readable form of the "ticks" representation
        /// </summary>
        /// <param name="expiry">When expiry is</param>
        /// <returns></returns>
        string parseExpiryDate(string expiry)
        {
            if (expiry == "0")
                return "the end of time";
            else
            {
                DateTime dt = new DateTime(Convert.ToInt64(expiry));
                return dt.ToUniversalTime().ToString("HH:mm, d MMMM yyyy");
            }
        }

        string friendlyProject(string project)
        {
            if (project == "")
                return "global";
            else
                return project;
        }

        string friendlyList(int listType)
        {
            int msgCode = 17000 + listType;
            return (string)Program.msgs[msgCode.ToString()];
        }

        string friendlyList(UserType ut)
        {
            return friendlyList((int)ut);      
        }

        public string addUserToList(string name, string project, UserType type, string adder, string reason, int expiry, ref IDbConnection connector)
        {
            // Check if user is already on a list
            UserType originalType = classifyEditor(name, project);

            if (originalType == type)
            {
                // Original type was same as new type; update list with new details
                IDbCommand cmd = connector.CreateCommand();
                cmd.CommandText = "UPDATE users SET adder = '" + adder.Replace("'", "''") + "', reason = '"
                    + reason.Replace("'", "''") + "', expiry = '" + getExpiryDate(expiry)
                    + "' WHERE name = '" + name.Replace("'", "''") + "' AND project = '" + project
                    + "' AND type ='" + ((int)originalType).ToString() + "'";
                lock(dbtoken)
                    cmd.ExecuteNonQuery();
                return Program.getFormatMessage(16104, showUserOnList(name, project));
            }
                                                //If adding to greylist, we can accept a new entry, as they may overlap
            else if ((originalType == UserType.anon) || (originalType == UserType.user) || (type == UserType.greylisted)
                || ((originalType == UserType.greylisted) && (type == UserType.blacklisted))) //Also allow adding greylisted users to the blacklist
            {
                // User was originally unlisted or is on non-conflicting list
                IDbCommand cmd = connector.CreateCommand();
                cmd.CommandText = "INSERT INTO users (name, project, type, adder, reason, expiry) VALUES ('" + name.Replace("'", "''")
                    + "','" + project + "','" + ((int)type).ToString() + "','" + adder.Replace("'", "''")
                    + "','" + reason.Replace("'", "''") + "','" + getExpiryDate(expiry) + "')";
                lock (dbtoken)
                    cmd.ExecuteNonQuery();
                return Program.getFormatMessage(16103, showUserOnList(name, project));
            }
            else
            {
                // User was originally on some kind of list
                return Program.getFormatMessage(16102, name, friendlyList(originalType), friendlyList(type));
            }
        }

        public string delUserFromList(string name, string project, UserType uType)
        {
            // Check if user is already on a list
            UserType originalType = classifyEditor(name, project);

            if (originalType != uType)
            {
                return Program.getFormatMessage(16009, name, friendlyProject(project), friendlyList(uType));
            }

            IDbCommand cmd = dbcon.CreateCommand();
            cmd.CommandText = "DELETE FROM users WHERE name = '" + name.Replace("'", "''")
                + "' AND project = '" + project + "' AND type = '" + ((int)uType).ToString() + "'";
            lock (dbtoken)
                cmd.ExecuteNonQuery();

            return Program.getFormatMessage(16101, name, friendlyProject(project), friendlyList(originalType));
        }

        public string showUserOnList(string username, string project)
        {
            IDbCommand cmd = dbcon.CreateCommand();

            if (project != "")
            {
                // First, check if user is an admin or bot on this particular wiki
                cmd.CommandText = "SELECT type, adder, reason, expiry FROM users WHERE name = '" + username.Replace("'", "''")
                    + "' AND project = '" + project + "' AND ((expiry > '"
                    + DateTime.Now.Ticks.ToString() + "') OR (expiry = '0')) LIMIT 1";
                lock (dbtoken)
                {
                    IDataReader idr = cmd.ExecuteReader();
                    if (idr.Read())
                    {
                        if ((idr.GetInt32(0) == 2) || (idr.GetInt32(0) == 5)) //Is admin or bot on this project?
                        {
                            string res = Program.getFormatMessage(16004, username, project, friendlyList(idr.GetInt32(0))
                                , idr.GetString(1), parseExpiryDate(idr.GetString(3)), idr.GetString(2));
                            idr.Close();
                            return res;
                        }
                    }
                    idr.Close();
                }
            }

            // Is user globally greylisted? (This takes precedence)
            cmd.CommandText = "SELECT reason, expiry FROM users WHERE name = '" + username.Replace("'", "''")
                + "' AND project = '' AND type = '6' AND ((expiry > '" + DateTime.Now.Ticks.ToString() + "') OR (expiry = '0')) LIMIT 1";
            lock (dbtoken)
            {
                IDataReader idr3 = cmd.ExecuteReader();
                if (idr3.Read())
                {
                    string result2 = Program.getFormatMessage(16106, username
                        , parseExpiryDate(idr3.GetString(1)), idr3.GetString(0));
                    idr3.Close();
                    return result2;
                }
                idr3.Close();
            }

            // Next, if we're still here, check if user is globally whitelisted or blacklisted
            cmd.CommandText = "SELECT type, adder, reason, expiry FROM users WHERE name = '" + username.Replace("'", "''")
                + "' AND project = '' AND ((expiry > '" + DateTime.Now.Ticks.ToString() + "') OR (expiry = '0')) LIMIT 1";
            lock (dbtoken)
            {
                IDataReader idr2 = cmd.ExecuteReader();
                if (idr2.Read())
                {
                    if ((idr2.GetInt32(0) == 0) || (idr2.GetInt32(0) == 1)) //Is on blacklist or whitelist?
                    {
                        string result = Program.getFormatMessage(16004, username, friendlyProject(""), friendlyList(idr2.GetInt32(0))
                                , idr2.GetString(1), parseExpiryDate(idr2.GetString(3)), idr2.GetString(2));
                        idr2.Close();
                        return result;
                    }
                }
                idr2.Close();
            }

            // Finally, if we're still here, user is either user or anon
            if (ipv4.Match(username).Success)
                return Program.getFormatMessage(16005, username);
            else
                return Program.getFormatMessage(16006, username);
        }

        bool isItemOnList(string item, int itemType)
        {
            IDbCommand cmd = dbcon.CreateCommand();
            cmd.CommandText = "SELECT item FROM items WHERE item='" + item.Replace("'", "''")
                + "' AND itemtype='" + itemType.ToString()
                + "' AND ((expiry > '" + DateTime.Now.Ticks.ToString() + "') OR (expiry = '0')) LIMIT 1";
            lock (dbtoken)
            {
                IDataReader idr = cmd.ExecuteReader();
                bool result = idr.Read();
                idr.Close();
                return result;
            }
        }

        /// <summary>
        /// Adds an item to BNU, BNA, or BES
        /// </summary>
        /// <param name="item">The item to add</param>
        /// <param name="itemType">The type of the item. BNU = 11, BNA = 12, BES = 20</param>
        /// <param name="adder">The adder</param>
        /// <param name="reason">The reason</param>
        /// <param name="expiry">The expiry time, in hours</param>
        /// <returns>The string to return to the client</returns>
        public string addItemToList(string item, int itemType, string adder, string reason, int expiry)
        {
            try
            {
                Regex testRegex = new Regex(item);
            }
            catch (Exception e)
            {
                return "Error: Regex does not compile: " + e.Message;
            }

            IDbCommand dbCmd = dbcon.CreateCommand();
            // First, check if item is already on the same list
            if (isItemOnList(item, itemType))
            {
                // Item is already on the same list, need to update
                dbCmd.CommandText = "UPDATE items SET adder='" + adder.Replace("'", "''") + "', reason='"
                    + reason.Replace("'", "''") + "', expiry='"+ getExpiryDate(expiry) +"' WHERE item='" + item.Replace("'", "''")
                    + "' AND itemtype='" + itemType.ToString() + "'";
                lock (dbtoken)
                    dbCmd.ExecuteNonQuery();
                return Program.getFormatMessage(16104, showItemOnList(item, itemType));
            }
            else
            {
                // Item is not on the list yet, can do simple insert
                dbCmd.CommandText = "INSERT INTO items (item, itemtype, adder, reason, expiry) VALUES('" + item.Replace("'", "''")
                    + "', '" + itemType.ToString() + "', '" + adder.Replace("'", "''") + "', '" + reason.Replace("'", "''")
                    + "', '" + getExpiryDate(expiry) + "')";
                lock (dbtoken)
                    dbCmd.ExecuteNonQuery();
                return Program.getFormatMessage(16103, showItemOnList(item, itemType));
            }    
        }

        public string showItemOnList(string item, int itemType)
        {
            IDbCommand cmd = dbcon.CreateCommand();
            cmd.CommandText = "SELECT adder, reason, expiry FROM items WHERE item='" + item.Replace("'", "''")
                + "' AND itemtype='" + itemType.ToString()
                + "' AND ((expiry > '" + DateTime.Now.Ticks.ToString() + "') OR (expiry = '0')) LIMIT 1";
            lock (dbtoken)
            {
                IDataReader idr = cmd.ExecuteReader();
                if (idr.Read())
                {
                    string result = Program.getFormatMessage(16007, item, friendlyList(itemType), idr.GetString(0),
                        parseExpiryDate(idr.GetString(2)), idr.GetString(1));
                    idr.Close();
                    return result;
                }
                else
                {
                    idr.Close();
                    return Program.getFormatMessage(16008, item, friendlyList(itemType));
                }
            }
        }

        public string delItemFromList(string item, int itemType)
        {
            if (isItemOnList(item, itemType))
            {
                IDbCommand dbCmd = dbcon.CreateCommand();
                dbCmd.CommandText = "DELETE FROM items WHERE item='" + item.Replace("'", "''")
                    + "' AND itemtype='" + itemType.ToString() + "'";
                lock (dbtoken)
                    dbCmd.ExecuteNonQuery();
                return Program.getFormatMessage(16105, item, friendlyList(itemType));
            }
            else
            {
                return Program.getFormatMessage(16008, item, friendlyList(itemType));
            }
        }

        private string ucfirst(string input)
        {
            string temp = input.Substring(0, 1);
            return temp.ToUpper() + input.Remove(0, 1);
        }

        public string addPageToWatchlist(string item, string project, string adder, string reason, int expiry)
        {
            IDbCommand dbCmd = dbcon.CreateCommand();
            //First, if this is not a Wiktionary, uppercase the first letter
            if (!project.EndsWith("wiktionary"))
                item = ucfirst(item);

            //If this is a local watchlist, translate the namespace
            if (project != "")
                item = Project.translateNamespace(project, item);

            // First, check if item is already on watchlist
            if (isWatchedArticle(item, project).Success)
            {
                // Item is already on same watchlist, need to update
                dbCmd.CommandText = "UPDATE watchlist SET adder='" + adder.Replace("'", "''") + "', reason='"
                    + reason.Replace("'", "''") + "', expiry='" + getExpiryDate(expiry) + "' WHERE article='" + item.Replace("'", "''")
                    + "' AND project='" + project + "'";
                lock (dbtoken)
                    dbCmd.ExecuteNonQuery();
                return Program.getFormatMessage(16104, showPageOnWatchlist(item, project));
            }
            else
            {
                // Item is not on the watchlist yet, can do simple insert
                dbCmd.CommandText = "INSERT INTO watchlist (article, project, adder, reason, expiry) VALUES('" + item.Replace("'", "''")
                    + "', '" + project + "', '" + adder.Replace("'", "''") + "', '" + reason.Replace("'", "''")
                    + "', '" + getExpiryDate(expiry) + "')";
                lock (dbtoken)
                    dbCmd.ExecuteNonQuery();
                return Program.getFormatMessage(16103, showPageOnWatchlist(item, project));
            }
        }

        public string showPageOnWatchlist(string item, string project)
        {
            //First, if this is not a wiktionary, uppercase the first letter
            if (!project.EndsWith("wiktionary"))
                item = ucfirst(item);

            //If this is a local watchlist, translate the namespace
            if (project != "")
                item = Project.translateNamespace(project, item);

            IDbCommand cmd = dbcon.CreateCommand();
            cmd.CommandText = "SELECT adder, reason, expiry FROM watchlist WHERE article='" + item.Replace("'", "''")
                + "' AND project='" + project
                + "' AND ((expiry > '" + DateTime.Now.Ticks.ToString() + "') OR (expiry = '0')) LIMIT 1";
            lock (dbtoken)
            {
                IDataReader idr = cmd.ExecuteReader();
                if (idr.Read())
                {
                    string result = Program.getFormatMessage(16004, item, friendlyProject(project), friendlyList(10),
                        idr.GetString(0), parseExpiryDate(idr.GetString(2)), idr.GetString(1));
                    idr.Close();
                    return result;
                }
                else
                {
                    idr.Close();
                    return Program.getFormatMessage(16009, item, friendlyProject(project), friendlyList(10));
                }
            }
        }

        public string delPageFromWatchlist(string item, string project)
        {
            //First, if this is not a wiktionary, uppercase the first letter
            if (!project.EndsWith("wiktionary"))
                item = ucfirst(item);

            //If this is a local watchlist, translate the namespace
            if (project != "")
                item = Project.translateNamespace(project, item);

            if (isWatchedArticle(item, project).Success)
            {
                IDbCommand dbCmd = dbcon.CreateCommand();
                dbCmd.CommandText = "DELETE FROM watchlist WHERE article='" + item.Replace("'", "''")
                    + "' AND project='" + project + "'";
                lock (dbtoken)
                    dbCmd.ExecuteNonQuery();
                return Program.getFormatMessage(16101, item, friendlyProject(project), friendlyList(10));
            }
            else
            {
                return Program.getFormatMessage(16009, item, friendlyProject(project), friendlyList(10));
            }
        }

        /// <summary>
        /// Command to parse a list add/del/show request from the client, and to carry it out if necessary.
        /// Returns output to be returned to client.
        /// </summary>
        /// <param name="listtype">Type of list to operate on. Same numbers as UserTypeToInt(), and 10=Watchlist 11=BNU 12=BNA</param>
        /// <param name="user">Name of the user (nick) carrying out this operation</param>
        /// <param name="cmdParams">Command parameters</param>
        /// <returns></returns>
        public string handleListCommand(int listtype, string user, string cmdParams)
        {
            //cmdParams are given like so:
            //  add Tangotango[ x=96][ r=Terrible vandal]
            //  add Tangotango test account x=89
            //  del Tangotango r=No longer needed (r is not handled by SWMTBot, but accept anyway)

            Match lc = rlistCmd.Match(cmdParams);
            if (lc.Success)
            {
                try
                {
                    string cmd = lc.Groups["cmd"].Captures[0].Value.ToLower();
                    string item = lc.Groups["item"].Captures[0].Value.Trim();
                    int len;
                    //Set length defaults: for all but blacklist, this is 0 (indefinite). For blacklist, is 96 hours.
                    if (listtype == 1)
                        len = 345600; //= 96 hours for blacklist
                    else
                        len = 0;
                    if (lc.Groups["len"].Success)
                        len = Convert.ToInt32(lc.Groups["len"].Captures[0].Value) * 3600; //Convert input, in hours, to seconds
                    string reason = "No reason given";
                    if (lc.Groups["reason"].Success)
                        reason = lc.Groups["reason"].Captures[0].Value;
                    string project = "";
                    if (lc.Groups["project"].Success)
                    {
                        project = lc.Groups["project"].Captures[0].Value;
                        if (!Program.prjlist.ContainsKey(project))
                            return "Project " + project + " is unknown";
                    }

                    switch (cmd)
                    {
                        case "add":
                            switch (listtype)
                            {
                                case 0: //Whitelist
                                    Program.Broadcast("WL", "ADD", item, len, reason, user);
                                    return addUserToList(item, "", UserType.whitelisted, user, reason, len, ref dbcon);
                                case 1: //Blacklist
                                    Program.Broadcast("BL", "ADD", item, len, reason, user);
                                    return addUserToList(item, "", UserType.blacklisted, user, reason, len, ref dbcon);
                                case 6: //Greylist
                                    return "You cannot directly add users to the greylist";
                                case 2: //Adminlist
                                    if (project == "")
                                        return (string)Program.msgs["20001"];
                                    return addUserToList(item, project, UserType.admin, user, reason, len, ref dbcon);
                                case 5: //Botlist
                                    if (project == "")
                                        return (string)Program.msgs["20001"];
                                    return addUserToList(item, project, UserType.bot, user, reason, len, ref dbcon);
                                case 10: //Watchlist
                                    if (project == "")
                                        Program.Broadcast("CVP", "ADD", item, len, reason, user);
                                    return addPageToWatchlist(item, project, user, reason, len);
                                case 11: //BNU
                                    Program.Broadcast("BNU", "ADD", item, len, reason, user);
                                    return addItemToList(item, 11, user, reason, len);
                                case 12: //BNA
                                    Program.Broadcast("BNA", "ADD", item, len, reason, user);
                                    return addItemToList(item, 12, user, reason, len);
                                case 20: //BES
                                    Program.Broadcast("BES", "ADD", item, len, reason, user);
                                    return addItemToList(item, 20, user, reason, len);
                                default:
                                    return ""; //Should never be called, but compiler complains otherwise
                            }
                        case "del":
                            switch (listtype)
                            {
                                case 0: //Whitelist
                                    Program.Broadcast("WL", "DEL", item, 0, reason, user);
                                    return delUserFromList(item, "", UserType.whitelisted);
                                case 1: //Blacklist
                                    Program.Broadcast("BL", "DEL", item, 0, reason, user);
                                    return delUserFromList(item, "", UserType.blacklisted);
                                case 6: //Greylist
                                    Program.Broadcast("GL", "DEL", item, 0, reason, user);
                                    return delUserFromList(item, "", UserType.greylisted);
                                case 2: //Adminlist
                                case 5: //Botlist
                                    if (project == "")
                                        return (string)Program.msgs["20001"];
                                    return delUserFromList(item, project, UserType.bot);
                                case 10: //Watchlist
                                    if (project == "")
                                        Program.Broadcast("CVP", "DEL", item, len, reason, user);
                                    return delPageFromWatchlist(item, project);
                                case 11: //BNU
                                    Program.Broadcast("BNU", "DEL", item, 0, reason, user);
                                    return delItemFromList(item, 11);
                                case 12: //BNA
                                    Program.Broadcast("BNA", "DEL", item, 0, reason, user);
                                    return delItemFromList(item, 12);
                                case 20: //BES
                                    Program.Broadcast("BES", "DEL", item, 0, reason, user);
                                    return delItemFromList(item, 20);
                                default:
                                    return ""; //Should never be called, but compiler complains otherwise
                            }
                        case "show":
                            switch (listtype)
                            {
                                case 0: //Whitelist
                                case 1: //Blacklist
                                case 6: //Greylist
                                    return showUserOnList(item, "");
                                case 2: //Adminlist
                                case 5: //Botlist
                                    if (project == "")
                                        return (string)Program.msgs["20001"];
                                    return showUserOnList(item, project);
                                case 10: //Watchlist
                                    return showPageOnWatchlist(item, project);
                                case 11: //BNU
                                    return showItemOnList(item, 11);
                                case 12: //BNA
                                    return showItemOnList(item, 12);
                                case 20: //BES
                                    return showItemOnList(item, 20);
                                default:
                                    return ""; //Should never be called, but compiler complains otherwise
                            }
                        case "test":
                            switch (listtype)
                            {
                                case 11: //BNU
                                    return testItemOnList(item, 11);
                                case 12: //BNA
                                    return testItemOnList(item, 12);
                                case 20: //BES
                                    return testItemOnList(item, 20);
                                default:
                                    return (string)Program.msgs["20002"];
                            }
                        default:
                            return ""; //Should never be called, but compiler complains otherwise
                    }
                }
                catch (Exception e)
                {
                    logger.Error("Error in handleListCommand", e);
                    return "Sorry, an error occured while handling the list command: " + e.Message;
                }
            }
            else
            {
                return (string)Program.msgs["20000"];
            }
        }

        /// <summary>
        /// Returns user information by looking in all lists
        /// </summary>
        /// <param name="username">The username to lookup information for</param>
        /// <returns></returns>
        public string GlobalIntel(string username)
        {
            if (username == "")
                return (string)Program.msgs["20003"];

            ArrayList results = new ArrayList();
                
            try
            {
                IDbCommand cmd = dbcon.CreateCommand();
                cmd.CommandText = "SELECT project, type, adder, reason, expiry FROM users WHERE name = '" + username.Replace("'", "''")
                    + "' AND ((expiry > '" + DateTime.Now.Ticks.ToString() + "') OR (expiry = '0'))";
                lock (dbtoken)
                {
                    IDataReader idr = cmd.ExecuteReader();
                    while (idr.Read())
                    {
                        results.Add(Program.getFormatMessage(16002, friendlyProject(idr.GetString(0)), friendlyList(idr.GetInt32(1))
                            , idr.GetString(2), parseExpiryDate(idr.GetString(4)), idr.GetString(3)));
                    }
                    idr.Close();
                }

                if (results.Count == 0)
                    return Program.getFormatMessage(16001, username);
                else
                    return Program.getFormatMessage(16000, username, String.Join(" and ", (string[])results.ToArray(typeof(string))));
            }
            catch (Exception e)
            {
                logger.Error("GlobalIntel failed", e);
                return Program.getFormatMessage(16003, e.Message);
            }
        }

        /// <summary>
        /// Classifies an editor on a particular wiki, or globally if "project" is empty
        /// </summary>
        /// <param name="username">The username or IP address to classify</param>
        /// <param name="project">The project to look in; leave blank to check global lists</param>
        /// <returns></returns>
        public UserType classifyEditor(string username, string project)
        {
            IDbCommand cmd = dbcon.CreateCommand();

            if (project != "")
            {
                // First, check if user is an admin or bot on this particular wiki
                cmd.CommandText = "SELECT type FROM users WHERE name = '" + username.Replace("'", "''") + "' AND project = '" + project
                    + "' AND ((expiry > '" + DateTime.Now.Ticks.ToString() + "') OR (expiry = '0')) LIMIT 1";
                lock (dbtoken)
                {
                    IDataReader idr = cmd.ExecuteReader();
                    if (idr.Read())
                    {
                        switch (idr.GetString(0))
                        {
                            case "2":
                                idr.Close();
                                return UserType.admin;
                            case "5":
                                idr.Close();
                                return UserType.bot;
                        }
                    }
                    idr.Close();
                }
            }

            // Is user globally greylisted? (This takes precedence)
            cmd.CommandText = "SELECT reason, expiry FROM users WHERE name = '" + username.Replace("'", "''")
                + "' AND project = '' AND type = '6' AND ((expiry > '" + DateTime.Now.Ticks.ToString() + "') OR (expiry = '0')) LIMIT 1";
            lock (dbtoken)
            {
                IDataReader idr3 = cmd.ExecuteReader();
                if (idr3.Read())
                {
                    idr3.Close();
                    return UserType.greylisted;
                }
                idr3.Close();
            }

            // Next, if we're still here, check if user is globally whitelisted or blacklisted
            cmd.CommandText = "SELECT type FROM users WHERE name = '" + username.Replace("'", "''")
                + "' AND project = '' AND ((expiry > '" + DateTime.Now.Ticks.ToString() + "') OR (expiry = '0')) LIMIT 1";
            lock (dbtoken)
            {
                IDataReader idr2 = cmd.ExecuteReader();
                if (idr2.Read())
                {
                    switch (idr2.GetString(0))
                    {
                        case "0":
                            idr2.Close();
                            return UserType.whitelisted;
                        case "1":
                            idr2.Close();
                            return UserType.blacklisted;
                    }
                }
                idr2.Close();
            }

            // Finally, if we're still here, user is either user or anon
            if (ipv4.Match(username).Success)
                return UserType.anon;
            else
                return UserType.user;
        }

        public listMatch isWatchedArticle(string title, string project)
        {
            listMatch lm = new listMatch();
            lm.matchedItem = ""; //Unused
            IDbCommand cmd = dbcon.CreateCommand();

            cmd.CommandText = "SELECT reason FROM watchlist WHERE article='" + title.Replace("'", "''")
                + "' AND (project='" + project + "' OR project='') AND ((expiry > '"
                + DateTime.Now.Ticks.ToString() + "') OR (expiry = '0'))";
            lock (dbtoken)
            {
                IDataReader idr = cmd.ExecuteReader();
                if (idr.Read())
                {
                    // Matched; is on watchlist
                    lm.Success = true;
                    lm.matchedReason = idr.GetString(0);
                }
                else
                {
                    // Did not match anything
                    lm.Success = false;
                    lm.matchedReason = "";
                }
                idr.Close();
            }
            return lm;
        }

        public listMatch matchesList(string title, int list)
        {
            listMatch lm = new listMatch();
            IDbCommand cmd = dbcon.CreateCommand();

            cmd.CommandText = "SELECT item, reason FROM items WHERE itemtype='" + list.ToString() + "' AND ((expiry > '"
                + DateTime.Now.Ticks.ToString() + "') OR (expiry = '0'))";
            lock (dbtoken)
            {
                IDataReader idr = cmd.ExecuteReader();
                while (idr.Read())
                {
                    if (Regex.IsMatch(title, idr.GetString(0), RegexOptions.IgnoreCase))
                    {
                        lm.Success = true;
                        lm.matchedItem = idr.GetString(0);
                        lm.matchedReason = idr.GetString(1);
                        idr.Close();
                        return lm;
                    }
                }
                idr.Close();
            }
            
            // Obviously, did not match anything
            lm.Success = false;
            lm.matchedItem = "";
            lm.matchedReason = "";
            return lm;
        }

        string testItemOnList(string title, int list)
        {
            listMatch lm = matchesList(title, list);
            if (lm.Success)
                return Program.getFormatMessage(16200, title, lm.matchedItem, friendlyList(list), lm.matchedReason);
            else
                return Program.getFormatMessage(16201, title, friendlyList(list));
        }

        /// <summary>
        /// Downloads a list of admins/bots from wiki and adds them to the database (Run this in a separate thread)
        /// </summary>
        void addGroupToList()
        {
            string projectName = currentGetThreadWiki;
            currentGetThreadWiki = "";
            string getGroup = currentGetThreadMode;
            currentGetThreadMode = "";

            Thread.CurrentThread.Name = "Get" + getGroup + "@" + projectName;

            UserType getGroupUT;
            if (getGroup == "sysop")
                getGroupUT = UserType.admin;
            else if (getGroup == "bot")
                getGroupUT = UserType.bot;
            else
                throw new Exception("Undefined group: " + getGroup);

            logger.Info("Downloading list of " + getGroup + "s from " + projectName);

            IDbConnection ourdbcon = null;

            try
            {
                lock (dbtoken)
                {
                    //Open a new DB connection for this thread
                    ourdbcon = (IDbConnection)new SqliteConnection(connectionString);
                    ourdbcon.Open();

                    string list = SWMTUtils.getRawDocument("http://" + projectName
                        + ".org/w/index.php?title=Special:Listusers&group=" + getGroup + "&limit=5000&offset=0");

                    //Now parse the list: 
                    /* _1568: FIX: MW error */
                    string sr = list.Substring(list.IndexOf("<ul>") + 4);
                    Match lusers = adminLine.Match(sr.Substring(0, sr.IndexOf("</ul>")));
                    while (lusers.Success)
                    {
                        addUserToList(lusers.Groups[1].Captures[0].Value, projectName, getGroupUT, "SWMTBot"
                            , "Auto-download from wiki", 0, ref ourdbcon); //Add the user to the list, using our own DB connection to write
                        lusers = lusers.NextMatch();
                    }
                    logger.Info("Added all " + getGroup + "s from " + projectName);
                }
            }
            catch (Exception e)
            {
                logger.Error("Unable to get list of " + getGroup + "s from " + projectName + ": " + e.Message);
            }
            finally
            {
                //Clean up our own DB connection
                ourdbcon.Close();
                ourdbcon = null;
            }
        }

        public string configGetAdmins(string cmdParams)
        {
            if (currentGetThreadWiki != "")
                return "The userlist fetcher is currently off on another errand.";

            if (Program.prjlist.ContainsKey(cmdParams))
            {
                currentGetThreadWiki = cmdParams;
                currentGetThreadMode = "sysop";
                new Thread(new ThreadStart(addGroupToList)).Start();
                return "Started admin userlist fetcher in separate thread";
            }
            else
                return "Project is unknown: " + cmdParams;
        }

        public string configGetBots(string cmdParams)
        {
            if (currentGetThreadWiki != "")
                return "The userlist fetcher is currently off on another errand";

            if (Program.prjlist.ContainsKey(cmdParams))
            {
                currentGetThreadWiki = cmdParams;
                currentGetThreadMode = "bot";
                new Thread(new ThreadStart(addGroupToList)).Start();
                return "Started bot userlist fetcher in separate thread";
            }
            else
                return "Project is unknown: " + cmdParams;
        }

        public void BatchGetAllAdminsAndBots()
        {
            Thread.CurrentThread.Name = "GetAllUsers";

            try
            {
                if (currentGetBatchChannel == "")
                    return; //Shouldn't happen, but here anyway

                if (currentGetThreadMode != "")
                    Program.irc.SendMessage(Meebey.SmartIrc4net.SendType.Message, currentGetBatchChannel
                        , "The userlist fetcher is currently off on another errand");

                Program.irc.SendMessage(Meebey.SmartIrc4net.SendType.Message, currentGetBatchChannel
                        , "Request to get admins and bots for all " + Program.prjlist.Count.ToString() + " wikis accepted.");

                foreach (DictionaryEntry de in Program.prjlist)
                {
                    // Get admins
                    currentGetThreadWiki = (string)de.Key;                   
                    currentGetThreadMode = "sysop";
                    Thread myThread = new Thread(new ThreadStart(addGroupToList));
                    myThread.Start();
                    while (myThread.IsAlive)
                        Thread.Sleep(0);

                    Thread.Sleep(500);

                    //Get bots
                    currentGetThreadWiki = (string)de.Key;
                    currentGetThreadMode = "bot";
                    Thread myThread2 = new Thread(new ThreadStart(addGroupToList));
                    myThread2.Start();
                    while (myThread2.IsAlive)
                        Thread.Sleep(0);

                    Thread.Sleep(800);
                }

                Program.irc.SendMessage(Meebey.SmartIrc4net.SendType.Message, currentGetBatchChannel
                        , "Done fetching all admins and bots. Phew, I'm tired :P");
            }
            finally
            {
                //Now reset all persistent variables
                currentGetThreadWiki = "";
                currentGetThreadMode = "";
                currentGetBatchChannel = "";
            }
        }

        /// <summary>
        /// Purges the local data for a particular project
        /// </summary>
        /// <param name="cmdParams">The name of the project. Remember, it might not actually exist now.</param>
        /// <returns></returns>
        public string purgeWikiData(string cmdParams)
        {
            if (cmdParams.Contains("'"))
                return "Sorry, invalid wiki name.";

            int total = 0;
            IDbConnection timdbcon = (IDbConnection)new SqliteConnection(connectionString);
            timdbcon.Open();
            IDbCommand timcmd = timdbcon.CreateCommand();
            lock (dbtoken)
            {
                timcmd.CommandText = "DELETE FROM users WHERE project = '" + cmdParams + "'";
                total += timcmd.ExecuteNonQuery();
                timcmd.CommandText = "DELETE FROM watchlist WHERE project = '" + cmdParams + "'";
                total += timcmd.ExecuteNonQuery();
                timdbcon.Close();
            }
            timdbcon = null;
            string resultStr =  "Threw away " + total.ToString() + " items that were related to " + cmdParams;
            logger.Info(resultStr);
            return resultStr;
        }
    }
}
