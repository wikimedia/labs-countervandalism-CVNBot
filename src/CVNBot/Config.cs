namespace CVNBot
{
    class Config
    {
        /**
         * User
         */
        public string botNick = "CVNBot";
        public string botPass = ""; // Optional
        public string description = "CVNBot Version:"; // Optional

        /**
         * Server
         */

        // Host name
        public string ircServerName = "irc.libera.chat";
        // Channel name or "None"
        public string feedChannel = "#cvn-sandbox";
        // Channel name or "None"
        public string controlChannel = "None"; // Optional
        // Channel name or "None"
        public string broadcastChannel = "None"; // Optional

        /**
         * Files
         */
        public string messagesFile = "./Console.msgs";
        public string listsFile = "./Lists.sqlite";
        public string projectsFile = "./Projects.xml";

        /**
         * Process
         */

        // Restart command (e.g. "mono" or "nice")
        public string restartCmd = "mono";

        // Restart command arguments (e.g. "mono $1" if restartCmd is "nice")
        public string restartArgs = "$1";

        /**
         * Feed
         */
        public int editBlank = -500;
        public int editBig = 500;
        public int newBig = 500;
        public int newSmall = 10;

        // IsCubbie overrides feedfilters if true to only show uploads and ignore the rest
        public bool isCubbie;

        // Whether to entirely disable the database. This means requesting a usertype
        // will always return 3 (anon) or 4 (user) based on a static regex.
        // This speeds up the the flow incredibly (especially when using SQLite) and makes it possible
        // to load a many (or even, all) of the Wikimedia wikis without producing an ever-growing backlog
        // of change events faster than we can process them.
        // Disabling the database means the actual output in the feedchannel will not be useful (all edits go through,
        // no bot, user, or whitelist detection).
        // Recommended to be used in combination with high(est) feedFilter settings for the purposes
        // of detecting block events from all wikis to then automatically broadcast to other bots
        // for cross-wiki vandalism detection. Originally written for the CVNBlackRock bot.
        public bool disableClassifyEditor;

        /**
         * Feed filters
         *
         * These settings allow filtering of user types and event types
         * They are defined via the .ini file and loaded on top of the Main thread
         * Possible values:
         *  1 "show"     (show and allow autolist) - default
         *  2 "softhide" (hide non-specials, show exceptions and allow autolist)
         *     softhide users: only large actions or matching watchlist/BES/BNU etc.
         *     softhide events: hide bots, admins, whitelist performing the event
         *  3 "hardhide" (hide all but do autolist)
         *  4 "ignore"   (hide and ignore totally)
         * show/ignore is dealt with at beginning of ReactToRCEvent()
         * hardhide is dealt with at end of ReactToRCEvent() (after autolistings are done)
         * softhide is done inline
         */
        // any event by anon: show-all
        public int feedFilterUsersAnon = 1;
        // any event by reg: special-only
        public int feedFilterUsersReg = 2;
        // any event by bot: ignore
        public int feedFilterUsersBot = 4;
        // any minor edit: ignore
        public int feedFilterEventMinorEdit = 4;
        // any page edit: show-all (other filter may override)
        public int feedFilterEventEdit = 1;
        // any page create: show-all (other filter may override)
        public int feedFilterEventNewpage = 1;
        // any move event: show-all
        public int feedFilterEventMove = 1;
        // any move event: show-all (bots hidden?)
        public int feedFilterEventBlock = 1;
        public int feedFilterEventDelete = 1;
        public int feedFilterEventNewuser = 1;
        public int feedFilterEventUpload = 1;
        public int feedFilterEventProtect = 1;

        public override string ToString()
        {
            return "[CVNBot.config]";
        }
    }
}
