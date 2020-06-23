using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using log4net;
using Mono.Data.Sqlite;

namespace CVNBot
{
    struct ListMatch
    {
        public bool Success;
        public string matchedItem;
        public string matchedReason;
    }

    class ListManager
    {
        public enum UserType { admin = 2, whitelisted = 0, blacklisted = 1, bot = 5, user = 4, anon = 3, greylisted = 6 }

        public IDbConnection dbcon;
        public string connectionString = "";

        static Regex ipv4 = new Regex(@"\b(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\b");
        static Regex ipv6 = new Regex(@"\b(?:[0-9A-F]{1,4}:){7}[0-9A-F]{1,4}\b");
        static Regex rlistCmd = new Regex(@"^(?<cmd>add|del|show|test) (?<item>.+?)(?: p=(?<project>\S+?))?(?: x=(?<len>\d{1,4}))?(?: r=(?<reason>.+?))?$"
            , RegexOptions.IgnoreCase);

        readonly Object dbtoken = new Object();

        Timer garbageCollector;

        static readonly ILog logger = LogManager.GetLogger("CVNBot.ListManager");

        public void InitDBConnection(string filename)
        {
            FileInfo fi = new FileInfo(filename);
            bool alreadyExists = fi.Exists;
            connectionString = "URI=file:" + filename + ",version=3";
            dbcon = new SqliteConnection(connectionString);
            dbcon.Open();
            if (!alreadyExists)
            {
                // The file didn't exist before, so initialize tables
                using (IDbCommand cmd = dbcon.CreateCommand())
                {
                    cmd.CommandText = "CREATE TABLE users ( name varchar(64), project varchar(32), type integer(2), adder varchar(64), reason varchar(80), expiry integer(32) )";
                    cmd.ExecuteNonQuery();
                    cmd.CommandText = "CREATE TABLE watchlist ( article varchar(64), project varchar(32), adder varchar(64), reason varchar(80), expiry integer(32) )";
                    cmd.ExecuteNonQuery();
                    cmd.CommandText = "CREATE TABLE items ( item varchar(80), itemtype integer(2), adder varchar(64), reason varchar(80), expiry integer(32) )";
                    cmd.ExecuteNonQuery();
                }
            }

            // Start the expired item garbage collector
            TimerCallback gcDelegate = new TimerCallback(CollectGarbage);
            garbageCollector = new Timer(gcDelegate, null, 10000, 7200000); //Start first collection in 10 secs; then, every two hours
        }

        void CollectGarbage(object stateInfo)
        {
            int total = 0;
            using (IDbConnection timdbcon = new SqliteConnection(connectionString))
            {
                timdbcon.Open();
                using (IDbCommand timcmd = timdbcon.CreateCommand())
                {
                    lock (dbtoken)
                    {
                        timcmd.CommandText = "DELETE FROM users WHERE ((expiry < @expiry) AND (expiry != '0'))";
                        timcmd.Parameters.Add(new SqliteParameter("@expiry", DateTime.Now.Ticks.ToString()));
                        timcmd.Prepare();
                        total += timcmd.ExecuteNonQuery();

                        // Clean out parameters list for the next statement
                        timcmd.Parameters.Clear();
                        timcmd.CommandText = "DELETE FROM watchlist WHERE ((expiry < @expiry) AND (expiry != '0'))";
                        timcmd.Parameters.Add(new SqliteParameter("@expiry", DateTime.Now.Ticks.ToString()));
                        timcmd.Prepare();
                        total += timcmd.ExecuteNonQuery();

                        timcmd.Parameters.Clear();
                        timcmd.CommandText = "DELETE FROM items WHERE ((expiry < @expiry) AND (expiry != '0'))";
                        timcmd.Parameters.Add(new SqliteParameter("@expiry", DateTime.Now.Ticks.ToString()));
                        timcmd.Prepare();
                        total += timcmd.ExecuteNonQuery();
                    }
                }
            }
            logger.InfoFormat("Tim threw away {0} items", total);
        }

        public void CloseDBConnection()
        {
            dbcon.Close();
            dbcon = null;
        }

        /// <summary>
        /// Gets an expiry date in ticks in relation to Now
        /// </summary>
        /// <param name="expiry">How many seconds in the future to set expiry to</param>
        /// <returns></returns>
        static string GetExpiryDate(int expiry)
        {
            if (expiry == 0)
                return "0";
            return DateTime.Now.AddSeconds(expiry).Ticks.ToString();
        }

        /// <summary>
        /// Returns a human-readable form of the "ticks" representation
        /// </summary>
        /// <param name="expiry">When expiry is</param>
        /// <returns></returns>
        static string ParseExpiryDate(long expiry)
        {
            if (expiry == 0)
                return (string)Program.msgs["20006"];
            DateTime dt = new DateTime(expiry);
            return dt.ToUniversalTime().ToString("HH:mm, d MMMM yyyy");
        }

        static string FriendlyProject(string project)
        {
            if (project == "")
                return "global";

            return project;
        }

        static string FriendlyList(int listType)
        {
            int msgCode = 17000 + listType;
            return (string)Program.msgs[msgCode.ToString()];
        }

        static string FriendlyList(UserType ut)
        {
            return FriendlyList((int)ut);
        }

        public string AddUserToList(string name, string project, UserType type, string adder, string reason, int expiry)
        {
            // Check if user is already on a list
            UserType originalType = ClassifyEditor(name, project);

            if (originalType == type)
            {
                // Original type was same as new type; update list with new details
                using (IDbCommand cmd = dbcon.CreateCommand())
                {
                    cmd.CommandText = "UPDATE users SET adder = @adder, reason = @reason, expiry = @expiry WHERE name = @name AND project = @project AND type = @type";
                    cmd.Parameters.Add(new SqliteParameter("@adder", adder));
                    cmd.Parameters.Add(new SqliteParameter("@reason", reason));
                    cmd.Parameters.Add(new SqliteParameter("@expiry", GetExpiryDate(expiry)));
                    cmd.Parameters.Add(new SqliteParameter("@name", name));
                    cmd.Parameters.Add(new SqliteParameter("@project", project));
                    cmd.Parameters.Add(new SqliteParameter("@type", ((int)originalType).ToString()));
                    cmd.Prepare();

                    lock (dbtoken)
                        cmd.ExecuteNonQuery();
                    return Program.GetFormatMessage(16104, ShowUserOnList(name, project));
                }
            }
            // Also allow adding greylisted users to the blacklist
            // If adding to greylist, we can accept a new entry, as they may overlap
            if ((originalType == UserType.anon)
                || (originalType == UserType.user)
                || (type == UserType.greylisted)
                || ((originalType == UserType.greylisted) && (type == UserType.blacklisted)))
            {
                // User was originally unlisted or is on non-conflicting list
                using (IDbCommand cmd = dbcon.CreateCommand())
                {
                    cmd.CommandText = "INSERT INTO users (name, project, type, adder, reason, expiry) VALUES (@name,@project,@type,@adder,@reason,@expiry)";
                    cmd.Parameters.Add(new SqliteParameter("@name", name));
                    cmd.Parameters.Add(new SqliteParameter("@project", project));
                    cmd.Parameters.Add(new SqliteParameter("@type", ((int)type).ToString()));
                    cmd.Parameters.Add(new SqliteParameter("@adder", adder));
                    cmd.Parameters.Add(new SqliteParameter("@reason", reason));
                    cmd.Parameters.Add(new SqliteParameter("@expiry", GetExpiryDate(expiry)));
                    cmd.Prepare();

                    lock (dbtoken)
                        cmd.ExecuteNonQuery();
                    return Program.GetFormatMessage(16103, ShowUserOnList(name, project));
                }
            }
            // User was originally on some kind of list
            return Program.GetFormatMessage(16102, name, FriendlyList(originalType), FriendlyList(type));
        }

        public string DelUserFromList(string name, string project, UserType uType)
        {
            // Check if user is already on a list
            UserType originalType = ClassifyEditor(name, project);

            if (originalType != uType)
            {
                return Program.GetFormatMessage(16009, name, FriendlyProject(project), FriendlyList(uType));
            }

            using (IDbCommand cmd = dbcon.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM users WHERE name = @name AND project = @project AND type = @type";
                cmd.Parameters.Add(new SqliteParameter("@name", name));
                cmd.Parameters.Add(new SqliteParameter("@project", project));
                cmd.Parameters.Add(new SqliteParameter("@type", ((int)uType).ToString()));
                cmd.Prepare();
                lock (dbtoken)
                    cmd.ExecuteNonQuery();
            }

            return Program.GetFormatMessage(16101, name, FriendlyProject(project), FriendlyList(originalType));
        }

        public string ShowUserOnList(string username, string project)
        {
            using (IDbCommand cmd = dbcon.CreateCommand())
            {

                // First, check user list for this particular wiki
                if (project != "")
                {
                    cmd.CommandText = "SELECT type, adder, reason, expiry FROM users WHERE name = @name AND project = @project AND ((expiry > @expiry) OR (expiry = '0')) LIMIT 1";
                    cmd.Parameters.Add(new SqliteParameter("@name", username));
                    cmd.Parameters.Add(new SqliteParameter("@project", project));
                    cmd.Parameters.Add(new SqliteParameter("@expiry", DateTime.Now.Ticks.ToString()));
                    cmd.Prepare();

                    lock (dbtoken)
                    {
                        using (IDataReader idr = cmd.ExecuteReader())
                            if (idr.Read())
                            {
                                // Is admin or bot on this project?
                                if ((idr.GetInt32(0) == 2) || (idr.GetInt32(0) == 5))
                                {
                                    string res = Program.GetFormatMessage(16004, username, project, FriendlyList(idr.GetInt32(0))
                                        , idr.GetString(1), ParseExpiryDate(idr.GetInt64(3)), idr.GetString(2));
                                    return res;
                                }
                            }
                    }
                }

                // Is user globally greylisted? (This takes precedence)
                cmd.Parameters.Clear();
                cmd.CommandText = "SELECT reason, expiry FROM users WHERE name = @name AND project = '' AND type = '6' AND ((expiry > @expiry) OR (expiry = '0')) LIMIT 1";
                cmd.Parameters.Add(new SqliteParameter("@name", username));
                cmd.Parameters.Add(new SqliteParameter("@expiry", DateTime.Now.Ticks.ToString()));
                cmd.Prepare();
                lock (dbtoken)
                {
                    using (IDataReader idr = cmd.ExecuteReader())
                        if (idr.Read())
                        {
                            string result2 = Program.GetFormatMessage(16106, username
                                , ParseExpiryDate(idr.GetInt64(1)), idr.GetString(0));
                            return result2;
                        }
                }

                // Next, if we're still here, check if user is globally whitelisted or blacklisted
                cmd.Parameters.Clear();
                cmd.CommandText = "SELECT type, adder, reason, expiry FROM users WHERE name = @name AND project = @project AND ((expiry > @expiry) OR (expiry = '0')) LIMIT 1";
                cmd.Parameters.Add(new SqliteParameter("@name", username));
                cmd.Parameters.Add(new SqliteParameter("@project", string.Empty));
                cmd.Parameters.Add(new SqliteParameter("@expiry", DateTime.Now.Ticks.ToString()));
                cmd.Prepare();
                lock (dbtoken)
                {
                    using (IDataReader idr = cmd.ExecuteReader())
                        if (idr.Read())
                        {
                            // Is on blacklist or whitelist?
                            if ((idr.GetInt32(0) == 0) || (idr.GetInt32(0) == 1))
                            {
                                string result = Program.GetFormatMessage(16004, username, FriendlyProject(""), FriendlyList(idr.GetInt32(0))
                                        , idr.GetString(1), ParseExpiryDate(idr.GetInt64(3)), idr.GetString(2));
                                return result;
                            }
                        }

                }
            }

            // Finally, if we're still here, user is either user or anon
            if ((ipv4.Match(username).Success) || (ipv6.Match(username).Success))
                // Anon
                return Program.GetFormatMessage(16005, username);

            // User
            return Program.GetFormatMessage(16006, username);
        }

        bool IsItemOnList(string item, int itemType)
        {
            using (IDbCommand cmd = dbcon.CreateCommand())
            {
                cmd.CommandText = "SELECT item FROM items WHERE item = @item AND itemtype = @itemtype AND ((expiry > @expiry) OR (expiry = '0')) LIMIT 1";
                cmd.Parameters.Add(new SqliteParameter("@item", item));
                cmd.Parameters.Add(new SqliteParameter("@itemtype", itemType.ToString()));
                cmd.Parameters.Add(new SqliteParameter("@expiry", DateTime.Now.Ticks.ToString()));
                cmd.Prepare();
                lock (dbtoken)
                {
                    using (IDataReader idr = cmd.ExecuteReader())
                    {
                        bool result = idr.Read();
                        return result;
                    }
                }
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
        public string AddItemToList(string item, int itemType, string adder, string reason, int expiry)
        {
            try
            {
                Regex.Match("", item);
            }
            catch (Exception e)
            {
                return "Error: Regex does not compile: " + e.Message;
            }

            using (IDbCommand dbCmd = dbcon.CreateCommand())
            {
                // First, check if item is already on the same list
                if (IsItemOnList(item, itemType))
                {
                    // Item is already on the same list, need to update
                    dbCmd.CommandText = "UPDATE items SET adder = @adder, reason = @reason, expiry = @expiry WHERE item = @item AND itemtype = @itemtype";
                    dbCmd.Parameters.Add(new SqliteParameter("@adder", adder));
                    dbCmd.Parameters.Add(new SqliteParameter("@reason", reason));
                    dbCmd.Parameters.Add(new SqliteParameter("@expiry", GetExpiryDate(expiry)));
                    dbCmd.Parameters.Add(new SqliteParameter("@item", item));
                    dbCmd.Parameters.Add(new SqliteParameter("@itemtype", itemType.ToString()));
                    dbCmd.Prepare();
                    lock (dbtoken)
                        dbCmd.ExecuteNonQuery();
                    return Program.GetFormatMessage(16104, ShowItemOnList(item, itemType));
                }

                // Item is not on the list yet, can do simple insert
                dbCmd.Parameters.Clear();

                dbCmd.CommandText = "INSERT INTO items (item, itemtype, adder, reason, expiry) VALUES(@item, @itemtype, @adder, @reason, @expiry)";
                dbCmd.Parameters.Add(new SqliteParameter("@item", item));
                dbCmd.Parameters.Add(new SqliteParameter("@itemtype", itemType.ToString()));
                dbCmd.Parameters.Add(new SqliteParameter("@adder", adder));
                dbCmd.Parameters.Add(new SqliteParameter("@reason", reason));
                dbCmd.Parameters.Add(new SqliteParameter("@expiry", GetExpiryDate(expiry)));
                dbCmd.Prepare();

                lock (dbtoken)
                    dbCmd.ExecuteNonQuery();
            }
            return Program.GetFormatMessage(16103, ShowItemOnList(item, itemType));
        }

        public string ShowItemOnList(string item, int itemType)
        {
            using (IDbCommand cmd = dbcon.CreateCommand())
            {
                cmd.CommandText = "SELECT adder, reason, expiry FROM items WHERE item = @item AND itemtype = @itemtype AND ((expiry > @expiry) OR (expiry = '0')) LIMIT 1";
                cmd.Parameters.Add(new SqliteParameter("@item", item));
                cmd.Parameters.Add(new SqliteParameter("@itemtype", itemType.ToString()));
                cmd.Parameters.Add(new SqliteParameter("@expiry", DateTime.Now.Ticks.ToString()));
                cmd.Prepare();
                lock (dbtoken)
                {
                    using (IDataReader idr = cmd.ExecuteReader())
                        if (idr.Read())
                        {
                            string result = Program.GetFormatMessage(16007, item, FriendlyList(itemType), idr.GetString(0),
                                ParseExpiryDate(idr.GetInt64(2)), idr.GetString(1));
                            return result;
                        }
                    return Program.GetFormatMessage(16008, item, FriendlyList(itemType));
                }
            }
        }

        public string DelItemFromList(string item, int itemType)
        {
            if (IsItemOnList(item, itemType))
            {
                using (IDbCommand cmd = dbcon.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM items WHERE item = @item AND itemtype = @itemtype";
                    cmd.Parameters.Add(new SqliteParameter("@item", item));
                    cmd.Parameters.Add(new SqliteParameter("@itemtype", itemType.ToString()));
                    cmd.Prepare();

                    lock (dbtoken)
                        cmd.ExecuteNonQuery();
                    return Program.GetFormatMessage(16105, item, FriendlyList(itemType));
                }
            }
            return Program.GetFormatMessage(16008, item, FriendlyList(itemType));
        }

        static string Ucfirst(string input)
        {
            string temp = input.Substring(0, 1);
            return temp.ToUpper() + input.Remove(0, 1);
        }

        public string AddPageToWatchlist(string item, string project, string adder, string reason, int expiry)
        {
            // First, if this is not a Wiktionary, uppercase the first letter
            if (!project.EndsWith("wiktionary"))
                item = Ucfirst(item);

            // If this is a local watchlist, translate the namespace
            if (project != "")
                item = Project.TranslateNamespace(project, item);

            using (IDbCommand cmd = dbcon.CreateCommand())
            {
                // First, check if item is already on watchlist
                if (IsWatchedArticle(item, project).Success)
                {
                    // Item is already on same watchlist, need to update
                    cmd.CommandText = "UPDATE watchlist SET adder = @adder, reason = @reason, expiry = @expiry WHERE article = @item AND project = @project";
                    cmd.Parameters.Add(new SqliteParameter("@adder", adder));
                    cmd.Parameters.Add(new SqliteParameter("@reason", reason));
                    cmd.Parameters.Add(new SqliteParameter("@expiry", GetExpiryDate(expiry)));
                    cmd.Parameters.Add(new SqliteParameter("@item", item));
                    cmd.Parameters.Add(new SqliteParameter("@project", project));
                    cmd.Prepare();
                    
                    lock (dbtoken)
                        cmd.ExecuteNonQuery();
                    return Program.GetFormatMessage(16104, ShowPageOnWatchlist(item, project));
                }

                // Item is not on the watchlist yet, can do simple insert
                cmd.CommandText = "INSERT INTO watchlist (article, project, adder, reason, expiry) VALUES(@article, @project, @adder, @reason, @expiry)";
                cmd.Parameters.Add(new SqliteParameter("@article", item));
                cmd.Parameters.Add(new SqliteParameter("@project", project));
                cmd.Parameters.Add(new SqliteParameter("@adder", adder));
                cmd.Parameters.Add(new SqliteParameter("@reason", reason));
                cmd.Parameters.Add(new SqliteParameter("@expiry", GetExpiryDate(expiry)));
                cmd.Prepare();

                lock (dbtoken)
                    cmd.ExecuteNonQuery();
                return Program.GetFormatMessage(16103, ShowPageOnWatchlist(item, project));
            }
        }

        public string ShowPageOnWatchlist(string item, string project)
        {
            // First, if this is not a wiktionary, uppercase the first letter
            if (!project.EndsWith("wiktionary"))
                item = Ucfirst(item);

            // If this is a local watchlist, translate the namespace
            if (project != "")
                item = Project.TranslateNamespace(project, item);

            using (IDbCommand cmd = dbcon.CreateCommand())
            {
                cmd.CommandText = "SELECT adder, reason, expiry FROM watchlist WHERE article = @article AND project = @project AND ((expiry > @expiry) OR (expiry = '0')) LIMIT 1";
                cmd.Parameters.Add(new SqliteParameter("@article", item));
                cmd.Parameters.Add(new SqliteParameter("@project", project));
                cmd.Parameters.Add(new SqliteParameter("@expiry", DateTime.Now.Ticks.ToString()));
                cmd.Prepare();

                lock (dbtoken)
                {
                    using (IDataReader idr = cmd.ExecuteReader())
                    if (idr.Read())
                    {
                        string result = Program.GetFormatMessage(16004, item, FriendlyProject(project), FriendlyList(10),
                            idr.GetString(0), ParseExpiryDate(idr.GetInt64(2)), idr.GetString(1));
                        return result;
                    }

                    return Program.GetFormatMessage(16009, item, FriendlyProject(project), FriendlyList(10));
                }
            }
        }

        public string DelPageFromWatchlist(string item, string project)
        {
            // First, if this is not a wiktionary, uppercase the first letter
            if (!project.EndsWith("wiktionary"))
                item = Ucfirst(item);

            // If this is a local watchlist, translate the namespace
            if (project != "")
                item = Project.TranslateNamespace(project, item);

            if (IsWatchedArticle(item, project).Success)
            {
                using (IDbCommand dbCmd = dbcon.CreateCommand())
                {
                    dbCmd.CommandText = "DELETE FROM watchlist WHERE article = @article AND project = @project";
                    dbCmd.Parameters.Add(new SqliteParameter("@article", item));
                    dbCmd.Parameters.Add(new SqliteParameter("@project", project));
                    dbCmd.Prepare();

                    lock (dbtoken)
                        dbCmd.ExecuteNonQuery();
                    return Program.GetFormatMessage(16101, item, FriendlyProject(project), FriendlyList(10));
                }
            }

            return Program.GetFormatMessage(16009, item, FriendlyProject(project), FriendlyList(10));
        }

        /// <summary>
        /// Command to parse a list add/del/show request from the client, and to carry it out if necessary.
        /// Returns output to be returned to client.
        /// </summary>
        /// <param name="listtype">Type of list to operate on. Same numbers as UserTypeToInt(), and 10=Watchlist 11=BNU 12=BNA</param>
        /// <param name="user">Name of the user (nick) carrying out this operation</param>
        /// <param name="cmdParams">Command parameters</param>
        /// <returns></returns>
        public string HandleListCommand(int listtype, string user, string cmdParams)
        {
            // cmdParams are given like so:
            // - add Tangotango[ x=96][ r=Terrible vandal]
            // - add Tangotango test account x=89
            // - del Tangotango r=No longer needed (r is not handled by CVNBot, but accept anyway)

            Match lc = rlistCmd.Match(cmdParams);
            if (lc.Success)
            {
                try
                {
                    GroupCollection groups = lc.Groups;
                    string cmd =  groups["cmd"].Captures[0].Value.ToLower();
                    string item = groups["item"].Captures[0].Value.Trim();
                    int len;
                    // Set length defaults: except for blacklist (listtype=1), the default is 0 (indefinite)
                    if (listtype == 1)
                        // Default expiry for blacklist: 90 days (in seconds)
                        len = 7776000;
                    else
                        len = 0;
                    if (groups["len"].Success)
						// Convert input, in hours, to seconds
                        len = Convert.ToInt32(groups["len"].Captures[0].Value) * 3600;
                    string reason = "No reason given";
                    if (groups["reason"].Success)
                        reason = groups["reason"].Captures[0].Value;
                    string project = "";
                    if (groups["project"].Success)
                    {
                        project = groups["project"].Captures[0].Value;
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
                                    return AddUserToList(item, "", UserType.whitelisted, user, reason, len);
                                case 1: //Blacklist
                                    Program.Broadcast("BL", "ADD", item, len, reason, user);
                                    return AddUserToList(item, "", UserType.blacklisted, user, reason, len);
                                case 6: //Greylist
                                    return "You cannot directly add users to the greylist";
                                case 2: //Adminlist
                                    if (project == "")
                                        return (string)Program.msgs["20001"];
                                    return AddUserToList(item, project, UserType.admin, user, reason, len);
                                case 5: //Botlist
                                    if (project == "")
                                        return (string)Program.msgs["20001"];
                                    return AddUserToList(item, project, UserType.bot, user, reason, len);
                                case 10: //Watchlist
                                    if (project == "")
                                        Program.Broadcast("CVP", "ADD", item, len, reason, user);
                                    return AddPageToWatchlist(item, project, user, reason, len);
                                case 11: //BNU
                                    Program.Broadcast("BNU", "ADD", item, len, reason, user);
                                    return AddItemToList(item, 11, user, reason, len);
                                case 12: //BNA
                                    Program.Broadcast("BNA", "ADD", item, len, reason, user);
                                    return AddItemToList(item, 12, user, reason, len);
                                case 20: //BES
                                    Program.Broadcast("BES", "ADD", item, len, reason, user);
                                    return AddItemToList(item, 20, user, reason, len);
                                default:
                                    return ""; //Should never be called, but compiler complains otherwise
                            }
                        case "del":
                            switch (listtype)
                            {
                                case 0: //Whitelist
                                    Program.Broadcast("WL", "DEL", item, 0, reason, user);
                                    return DelUserFromList(item, "", UserType.whitelisted);
                                case 1: //Blacklist
                                    Program.Broadcast("BL", "DEL", item, 0, reason, user);
                                    return DelUserFromList(item, "", UserType.blacklisted);
                                case 6: //Greylist
                                    Program.Broadcast("GL", "DEL", item, 0, reason, user);
                                    return DelUserFromList(item, "", UserType.greylisted);
                                case 2: //Adminlist
                                    if (project == "")
                                        return (string)Program.msgs["20001"];
                                    return DelUserFromList(item, project, UserType.admin);
                                case 5: //Botlist
                                    if (project == "")
                                        return (string)Program.msgs["20001"];
                                    return DelUserFromList(item, project, UserType.bot);
                                case 10: //Watchlist
                                    if (project == "")
                                        Program.Broadcast("CVP", "DEL", item, len, reason, user);
                                    return DelPageFromWatchlist(item, project);
                                case 11: //BNU
                                    Program.Broadcast("BNU", "DEL", item, 0, reason, user);
                                    return DelItemFromList(item, 11);
                                case 12: //BNA
                                    Program.Broadcast("BNA", "DEL", item, 0, reason, user);
                                    return DelItemFromList(item, 12);
                                case 20: //BES
                                    Program.Broadcast("BES", "DEL", item, 0, reason, user);
                                    return DelItemFromList(item, 20);
                                default:
                                    return ""; //Should never be called, but compiler complains otherwise
                            }
                        case "show":
                            switch (listtype)
                            {
                                case 0: //Whitelist
                                case 1: //Blacklist
                                case 6: //Greylist
                                    return ShowUserOnList(item, "");
                                case 2: //Adminlist
                                case 5: //Botlist
                                    if (project == "")
                                        return (string)Program.msgs["20001"];
                                    return ShowUserOnList(item, project);
                                case 10: //Watchlist
                                    return ShowPageOnWatchlist(item, project);
                                case 11: //BNU
                                    return ShowItemOnList(item, 11);
                                case 12: //BNA
                                    return ShowItemOnList(item, 12);
                                case 20: //BES
                                    return ShowItemOnList(item, 20);
                                default:
                                    return ""; //Should never be called, but compiler complains otherwise
                            }
                        case "test":
                            switch (listtype)
                            {
                                case 11: //BNU
                                    return TestItemOnList(item, 11);
                                case 12: //BNA
                                    return TestItemOnList(item, 12);
                                case 20: //BES
                                    return TestItemOnList(item, 20);
                                default:
                                    return (string)Program.msgs["20002"];
                            }
                        default:
                            return ""; //Should never be called, but compiler complains otherwise
                    }
                }
                catch (Exception e)
                {
                    logger.Error("Error while handling list command", e);
                    return "Sorry, an error occured while handling the list command: " + e.Message;
                }
            }

            return (string)Program.msgs["20000"];
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
                using (IDbCommand cmd = dbcon.CreateCommand())
                {
                    cmd.CommandText = "SELECT project, type, adder, reason, expiry FROM users WHERE name = @username AND ((expiry > @expiry) OR (expiry = '0'))";
                    cmd.Parameters.Add(new SqliteParameter("@username", username));
                    cmd.Parameters.Add(new SqliteParameter("@expiry", DateTime.Now.Ticks.ToString()));
                    cmd.Prepare();
                    lock (dbtoken)
                    {
                        using (IDataReader idr = cmd.ExecuteReader())
                            while (idr.Read())
                            {
                                results.Add(Program.GetFormatMessage(16002, FriendlyProject(idr.GetString(0)), FriendlyList(idr.GetInt32(1))
                                    , idr.GetString(2), ParseExpiryDate(idr.GetInt64(4)), idr.GetString(3)));
                            }
                    }
                }

                if (results.Count == 0)
                    return Program.GetFormatMessage(16001, username);

                return Program.GetFormatMessage(16000, username, String.Join(" and ", (string[])results.ToArray(typeof(string))));
            }
            catch (Exception e)
            {
                logger.Error("GlobalIntel failed", e);
                return Program.GetFormatMessage(16003, e.Message);
            }
        }

        /// <summary>
        /// Classifies an editor on a particular wiki, or globally if "project" is empty
        /// </summary>
        /// <param name="username">The username or IP address to classify</param>
        /// <param name="project">The project to look in; leave blank to check global lists</param>
        /// <returns></returns>
        public UserType ClassifyEditor(string username, string project)
        {
            if (!Program.config.disableClassifyEditor)
            {

                using (IDbCommand cmd = dbcon.CreateCommand())
                {
                    if (project != "")
                    {
                        // First, check if user is an admin or bot on this particular wiki
                        cmd.CommandText = "SELECT type FROM users WHERE name = @username AND project = @project AND ((expiry > @expiry) OR (expiry = '0')) LIMIT 1";
                        cmd.Parameters.Add(new SqliteParameter("@username", username));
                        cmd.Parameters.Add(new SqliteParameter("@project", project));
                        cmd.Parameters.Add(new SqliteParameter("@expiry", DateTime.Now.Ticks.ToString()));
                        cmd.Prepare();
                        lock (dbtoken)
                        {
                            using (IDataReader idr = cmd.ExecuteReader())
                                if (idr.Read())
                                {
                                    switch (idr.GetInt32(0))
                                    {
                                        case 2:
                                            return UserType.admin;
                                        case 5:
                                            return UserType.bot;
                                    }
                                }
                        }
                    }

                    // Is user globally greylisted? (This takes precedence)
                    cmd.CommandText = "SELECT reason, expiry FROM users WHERE name = @username AND project = @project AND type = '6' AND ((expiry > @expiry) OR (expiry = '0')) LIMIT 1";
                    cmd.Parameters.Add(new SqliteParameter("@username", username));
                    cmd.Parameters.Add(new SqliteParameter("@project", string.Empty));
                    cmd.Parameters.Add(new SqliteParameter("@expiry", DateTime.Now.Ticks.ToString()));
                    cmd.Prepare();
                    lock (dbtoken)
                    {
                        using (IDataReader idr3 = cmd.ExecuteReader())
                            if (idr3.Read())
                            {
                                return UserType.greylisted;
                            }
                    }

                    // Next, if we're still here, check if user is globally whitelisted or blacklisted
                    cmd.CommandText = "SELECT type FROM users WHERE name = @username AND project = @project AND ((expiry > @expiry) OR (expiry = '0')) LIMIT 1";
                    cmd.Parameters.Add(new SqliteParameter("@username", username));
                    cmd.Parameters.Add(new SqliteParameter("@project", string.Empty));
                    cmd.Parameters.Add(new SqliteParameter("@expiry", DateTime.Now.Ticks.ToString()));
                    cmd.Prepare();
                    lock (dbtoken)
                    {
                        using (IDataReader idr2 = cmd.ExecuteReader())
                            if (idr2.Read())
                            {
                                switch (idr2.GetInt32(0))
                                {
                                    case 0:
                                        return UserType.whitelisted;
                                    case 1:
                                        return UserType.blacklisted;
                                }
                            }
                    }
                }
            }

            // Finally, if we're still here, user is either user or anon
            if ((ipv4.Match(username).Success) || (ipv6.Match(username).Success))
                return UserType.anon;

            return UserType.user;
        }

        public ListMatch IsWatchedArticle(string title, string project)
        {
            ListMatch lm = new ListMatch();
            lm.matchedItem = ""; // Unused
            using (IDbCommand cmd = dbcon.CreateCommand())
            {
                cmd.CommandText = "SELECT reason FROM watchlist WHERE article=@article AND (project = @project OR project='') AND ((expiry > @expiry) OR (expiry = '0'))";
                cmd.Parameters.Add(new SqliteParameter("@article", title));
                cmd.Parameters.Add(new SqliteParameter("@project", project));
                cmd.Parameters.Add(new SqliteParameter("@expiry", DateTime.Now.Ticks.ToString()));
                cmd.Prepare();

                lock (dbtoken)
                {
                    using (IDataReader idr = cmd.ExecuteReader())
                    {
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
                    }
                }
            }
            return lm;
        }

        static bool MatchesPattern(string input, string pattern)
        {
            try
            {
                if (Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase))
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                logger.Warn("Found invalid pattern: " + pattern, ex);
                Program.BroadcastDD("ERROR", "LMGNR_REGEX", ex.Message, input);
            }

            return false;
        }

        public ListMatch MatchesList(string title, int list)
        {
            ListMatch lm = new ListMatch();
            using (IDbCommand cmd = dbcon.CreateCommand())
            {
                cmd.CommandText = "SELECT item, reason FROM items WHERE itemtype = @itemtype AND ((expiry > @expiry) OR (expiry = '0'))";
                cmd.Parameters.Add(new SqliteParameter("@itemtype", list.ToString()));
                cmd.Parameters.Add(new SqliteParameter("@expiry", DateTime.Now.Ticks.ToString()));
                cmd.Prepare();

                lock (dbtoken)
                {
                    using (IDataReader idr = cmd.ExecuteReader())
                        while (idr.Read())
                        {
                            if (MatchesPattern(title, idr.GetString(0)))
                            {
                                lm.Success = true;
                                lm.matchedItem = idr.GetString(0);
                                lm.matchedReason = idr.GetString(1);
                                return lm;
                            }
                        }
                }
            }

            // Obviously, did not match anything
            lm.Success = false;
            lm.matchedItem = "";
            lm.matchedReason = "";
            return lm;
        }

        string TestItemOnList(string title, int list)
        {
            ListMatch lm = MatchesList(title, list);
            if (lm.Success)
                return Program.GetFormatMessage(16200, title, lm.matchedItem, FriendlyList(list), lm.matchedReason);

            return Program.GetFormatMessage(16201, title, FriendlyList(list));
        }

        /// <summary>
        /// Downloads a list of admins/bots from wiki and adds them to the database (Run this in a separate thread)
        /// </summary>
        void AddGroupToList(object data)
        {
            Dictionary<string, string> args = (Dictionary<string, string>)data;
            string projectName = args["project"];
            string getGroup = args["group"];

            Thread.CurrentThread.Name = "Get" + getGroup + "@" + projectName;

            UserType getGroupUT;
            if (getGroup == "sysop")
                getGroupUT = UserType.admin;
            else if (getGroup == "bot")
                getGroupUT = UserType.bot;
            else
                throw new Exception("Undefined group: " + getGroup);

            logger.InfoFormat("Fetching list of {0} users from {1}", getGroup, projectName);


            if (!Program.prjlist.ContainsKey(projectName))
            {
                throw new Exception("Undefined project: " + projectName);
            }
            Project project = ((Project)Program.prjlist[projectName]);

            string resp = null;
            try
            {

                resp = CVNBotUtils.GetRawDocument(project.rooturl
                                                         + "w/api.php?format=xml&action=query&list=allusers&augroup="
                                                         + getGroup + "&aulimit=max");
                XmlDocument doc = new XmlDocument();
                doc.LoadXml(resp);
                XmlNode list = doc.GetElementsByTagName("allusers")[0];
                XmlNodeList nodes = list.ChildNodes;
                int total = nodes.Count;
                for (int i = 0; i < total; i++)
                {
                    string name = nodes[i].Attributes["name"].Value;
                    AddUserToList(name, projectName, getGroupUT, "CVNBot", "Auto-download from wiki", 0);
                }

                logger.InfoFormat("Added {0} {1} users from {2}", total, getGroup, projectName);
            }
            catch (Exception e)
            {
                if (resp != null)
                {
                    logger.InfoFormat("Preview of failed user list fetch: {0}", resp.Substring(0, 100));
                }
                logger.Error("Unable to get list", e);
            }
        }

        public string ConfigGetAdmins(string cmdParams)
        {
            if (Program.prjlist.ContainsKey(cmdParams))
            {
                Dictionary<string, string> args = new Dictionary<string, string>
                {
                    { "project", cmdParams },
                    { "group", "sysop" }
                };
                new Thread(AddGroupToList).Start(args);
                return "Started admin userlist fetcher in the background";
            }

            return "Project is unknown: " + cmdParams;
        }

        public string ConfigGetBots(string cmdParams)
        {
            if (Program.prjlist.ContainsKey(cmdParams))
            {
                Dictionary<string, string> args = new Dictionary<string, string>
                {
                    { "project", cmdParams },
                    { "group", "bot" }
                };
                new Thread(AddGroupToList).Start(args);
                return "Started bot userlist fetcher in the background";
            }

            return "Project is unknown: " + cmdParams;
        }

        public void BatchGetAllAdminsAndBots(object data)
        {
            Thread.CurrentThread.Name = "GetAllUsers";

            string originChannel = (string)data;

            Program.SendMessageF(Meebey.SmartIrc4net.SendType.Message, originChannel,
                                 "Request to get admins and bots for all " + Program.prjlist.Count.ToString() + " wikis accepted.",
                                 Meebey.SmartIrc4net.Priority.High);

            foreach (DictionaryEntry de in Program.prjlist)
            {
                Thread myThread;

                // Get admins
                Dictionary<string, string> args = new Dictionary<string, string>();
                args.Add("project", (string)de.Key);
                args.Add("group", "sysop");
                myThread = new Thread(AddGroupToList);
                myThread.Start(args);
                while (myThread.IsAlive)
                    Thread.Sleep(1);

                Thread.Sleep(500);

                // Get bots
                args.Remove("group");
                args.Add("group", "bot");
                myThread = new Thread(AddGroupToList);
                myThread.Start(args);
                while (myThread.IsAlive)
                    Thread.Sleep(1);

                Thread.Sleep(500);
            }

            Program.SendMessageF(Meebey.SmartIrc4net.SendType.Message, originChannel,
                                 "Done fetching all admins and bots. Phew, I'm tired :P",
                                 Meebey.SmartIrc4net.Priority.High);
        }

        /// <summary>
        /// Purges the local data for a particular project
        /// </summary>
        /// <param name="cmdParams">The name of the project. Remember, it might not actually exist now.</param>
        /// <returns></returns>
        public string PurgeWikiData(string cmdParams)
        {
            if (cmdParams.Contains("'"))
                return "Sorry, invalid wiki name.";

            int total = 0;
            using (IDbConnection timdbcon = new SqliteConnection(connectionString))
            {
                timdbcon.Open();
                using (IDbCommand timcmd = timdbcon.CreateCommand())
                {
                    lock (dbtoken)
                    {
                        timcmd.CommandText = "DELETE FROM users WHERE project = @project";
                        timcmd.Parameters.Add(new SqliteParameter("@project", cmdParams));
                        timcmd.Prepare();
                        total += timcmd.ExecuteNonQuery();

                        timcmd.Parameters.Clear();

                        timcmd.CommandText = "DELETE FROM watchlist WHERE project = @project";
                        timcmd.Parameters.Add(new SqliteParameter("@project", cmdParams));
                        timcmd.Prepare();
                        total += timcmd.ExecuteNonQuery();
                    }
                }
            }
            string resultStr = "Threw away " + total.ToString() + " items that were related to " + cmdParams;
            logger.Info(resultStr);
            return resultStr;
        }
    }
}
