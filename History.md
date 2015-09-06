1.21.0 / 2015-09-07
==================
* RCReader: Don't strip "/w/index.php" from urls.
* ListManager: Support IPv6 to detect anonymous users.
* Program: Add "caurl" message attribute. (CentralAuth link)
* Config: New "forceHttps" option to use HTTPS as protocol in the RCFeed.
* Project: Enforce HTTPS for rooturl.
* All: Resolved compiler warnings for unused variables.

1.20.0 / 2012-07-13
==================
* Trim whitespace around parameter when dealing with ListManager. Fixes #1.
* New feed filters added: feedFilterUsersAnon, feedFilterUsersReg
  feedFilterUsersBot, feedFilterEventMinorEdit, feedFilterEventEdit,
  feedFilterEventNewpage, feedFilterEventMove, feedFilterEventDelete,
  feedFilterEventNewuser, feedFilterEventUpload and feedFilterEventProtect.
  These can be set in SWMTBot.ini to 1, 2, 3 or 4.
  Check the bug ticket and code comments for more info. Fixes #2.
* New option to prevent dbconnection in ListManager.ClassifyEditor can now be
  done by adding setting disableClassifyEditor in SWMTBot.ini. Fixes #3.
* New command "config" for getting the customizable .ini settings optional
  parameter 'all' to broadcast it (like "count" does by default) causing all
  other bots in the same channel to also show their configs. Fixes #4.
* "the end of time" is now controlled by Console.msgs to allow translation.
* newuser/newuser2 event gets a 'talkurl' attribute.
* Delete event gets a 'url' attribute.
* Actions now have a clean-attribute without prefixes ('c' + attrname) besides
  the regular one, for more flexibility in the layout of the messages
  So [[${editor}]] renders "[[xx:User:Foo]]") and ${ceditor} renders "Foo".
* Add 'talkurl' attribute for block and unblock event. Fixes #5.
* Implement log type 'protect', 'unprotect' and 'modifyprotect'. Fixes #6.
* Added `SWMTBot.exe.config` to the repository. For some reason it was on the
  botserver but never made it here. This file is required to get the output of
  log4net (in the terminal or a log file).
* ignoreBotEdits is now deprecated in favor of feedFilterUsersBot.
* RCEvent now extracts botflag aswell. Making it possible to detect (and
  return) botedits in ReactToRCEvent without a database connection (ie.
  calling classifyEditor). This decreases delay/lag on busy wikis noticeably.
* Adding Console.msgs and sqlite3.dll as Content includes. No longer have to be
  moved manually before the app can be run.
* Default blacklist duration raised from 96 hours (4 days) to 744
  hours (31 days).
* `CVNBotUtils.wikiEncode()` now encodes exclamation mark aswell (some IRC
  clients don't include it in the link unless).
* The deprecated `{ipcat}` parameter in messsages has been removed.
* Added "patrol" (mark as patrolled) and "review" (FlaggedRevs/PendingChanges)
  as an empty switchcase in `RCReader->rcirc_OnChannelMessage` to clear out
  some unnecessary WARNs in the logs about "unhandled log types".
* Pagetitles containing a slash were not reported and cause a WARN in log.
  Fixes #7.
* nsEnglish[4] and nsEnglish[5] have been changed from "Wikipedia" to "Project"
  to fix the bug where one could watchlist project-ns page of a non-WP project,
  but when an edit occurs, the generated pagetitle (and url) gets the wrong
  namespace and thus resulted in a broken (404 error) link.
  "Project" works everywhere.

1.19.0 / 2011
==================
* URLs are now forces in blue in Console.msgs. Previously this was up
  to the IRC client or it URL-detection doesn't exist it would show in
  cyan (the last color set, from the label "URL: " and "Diff: ")
* "get" commands are now restricted the to ops.
* Moved to Visual C# 2010 format.
* Project.getNamespaces() now makes requests to /w/api.php instead of
  /w/query.php.

1.80.0
==================
* Bumped Console.msgs version to 2.02.
* Meaningless messages are now discarded.
* Limit flow rate - Properly implemented. ConnectionError no longer occurs, as
  we are now using a hacked version of SmartIrc4net (workaround). SWMTBot is now
  flood-protected in code.
* Main SWMTBot branch can now run Cubbie (set IsCubbie=yes in SWMTBot.ini).
* Bad Edit Summary list (BES) now applies to uploaded filenames/contents.
* Watchlist now applies to uploads as well.
* Now does not display uploads by admins, bots, and whitelisted users.

1.17.2
==================
* "al del" works now.
* Message chunks only containing parenthesis are discarded.

1.16.0 - 1.17.1
==================
* Auto-download lists now searches for a <ul> instead of an <ol> on
  [[Special:Listusers]] following the change in MediaWiki.
* If a local admin blocks a user, then the name of the wiki will be recorded
  in the blacklist reason.
* Unmatched log types now return more debug data via the Distributed Debugging
  feature.
* Changed various error messages to be more apparent as to their cause.
* Options for editblank, editbig, newbig, newsmall moved from static integers
  to the .ini configuration.
* "purge" command added.
* "batchreload" commands added.
* AutoReconnect.
* Support for deletions, now reported in rc stream.
* Limit flow rate - implemented. Translated code from CVUBot; SWMTBot is now
  aware of the flood protection that freenode uses and should not get flooded
  off easily. (However, response times may have gone down somewhat).
* Allow broadcasting to be turned off - implemented. If you specify None
  (case-sensitive) for the broadcastchannel in SWMTBot.ini, broadcasting will be
  turned off entirely.
* Empty messages are prevented from being send and from crashing the bot.
* Fixed a bug whereby usercreate2 (when a user creates an account for somebody
  else) was showing up as normal usercreate.
* Handled OnConnectionError, which will hopefully reduce or totally eliminate
  the strange object disposal exceptions.

1.15.1
==================
* Bug that caused move URLs to be linked to an often wrongly-encoded log page
  URL.

1.15.0
==================
* ListManager is measured in seconds instead of hours.
* Adding greylisted users to the blacklist is allowed.
* Bumped Console.msgs version to 2.01.
* Bumped BroadcastB version to 1.1.
* Default blacklist duration is 96 hours.
* "help" command added.
* Fixed: This time precision bug that caused users blocked for less than one
  hour to be blacklisted indefinitely.
