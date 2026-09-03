5.0.0 / 2026-09-01
==================

This release includes a rewrite from .NET to Python 3 to reduce complexity of server upgrades, simplify local development, and CI testing without custom setup ([T327136](https://phabricator.wikimedia.org/T327136), [change 1143806](https://gerrit.wikimedia.org/r/c/labs/countervandalism/CVNBot/+/1143806)). CVNBot is now a standalone script that works out-of-the-box on any Python 3.9+ runtime, which is ubiquitous and simple to install on Linux or macOS. No build, compile, or installation step of any kind; and no other packages or dependent processes need to be installed or configured.

CVNBot 5 is backwards-compatible and can run any existing bot directory with unchanged `CVNBot.ini`, `Lists.sqlite`, `Projects.xml`, and `Console.msgs` files. It accepts the same IRC commands, and produces identical IRC output (apart from intentional bug fixes listed below). This includes compatibility with .NET-style regexes as BNU/BNA/BES patterns stored in Lists.sqlite.

A new feature is that CVNBot no longer requires its code to be in your bot directories. You can download the software in a directory separate from the bot data, and run it from anywhere by setting `--config` to where your `CVNBot.ini` file is. Previously, file references in CVNBot.ini were resolved relative to the current working directory, which meant you had to `cd` to the bot directory and run `mono CVNBot.exe` from there so that settings like `lists=./Lists.sqlite` work correctly. You can continue to do this if you want, but you can now also run it from anywhere else! This lets you run multiple bots from a single copy of the code, or a single global or shared binary ([T327131](https://phabricator.wikimedia.org/T327131)). The current working directory is no longer used for anything, except to resolve the path to CVNBot.ini if you set `--config` to a relative path.

Once you have upgraded to CVNBot 5, you can safely delete any files other than `CVNBot.ini`, `Lists.sqlite`, `Projects.xml`, and `Console.msgs` from your bot directories.

This release consumes about **50% less RAM** and **30% less CPU** ([T327136#12284001](https://phabricator.wikimedia.org/T327136#12284001)). It now runs fast enough to allow Wikidata to be monitored in real-time. Previously the Wikidata instance often lagged by several hours due to its event queue growing quicker than it could process.

### Changed

* Rewrite CVNBot in Python. ([T327136](https://phabricator.wikimedia.org/T327136))

  Instead of `cd /path/to/git/CVNBot/src/CVNBot/bin/Release; mono CVNBot.exe` or `cd /path/to/MyBot; mono CVNBot.exe` you should now run the program as follows:

  ```sh
  python3 /path/to/git/CVNBot/cvnbot --config /path/to/MyBot/CVNBot.ini
  ```

### Removed

* Config: The `disableClassifyEditor` setting was removed.

* Config: The `restartcmd` and `restartcmd` settings are no longer used.

  These settings haven't been used by CVN in production since 2014. We do launch bots via `nohup` but this is done by [stillalive](https://gerrit.wikimedia.org/g/labs/countervandalism/stillalive/). Alternatively one can use systemd.

* `CVNBot.exe.config`: This file is no longer used.

  This was specific to .NET and log4net. CVNBot 5 defaults to log level INFO and stdout. You can enable syslog or change the log level via the `logsyslog=1` and `loglevel` settings in CVNBot.ini.

### Added

* CLI: Add `--config` option. ([T327131](https://phabricator.wikimedia.org/T327131))
* Config: Resolve file references relative to CVNBot.ini. ([T327131](https://phabricator.wikimedia.org/T327131))
* Config: Add `ircport` setting. Defaults to 6667, same as before.
* Config: Add `loglevel` setting. Defaults to INFO.
* Config: Add `logsyslog` setting. Defaults to False.
* Config: The `messages` setting is now optional. It defaults to a Console.msgs file bundled with CVNBot.
* Config: The `lists` setting is now optional. It defaults to `./Lists.sqlite` relative to your CVNBot.ini. As before, it is automatically created on first launch if absent.
* Config: The `projects` setting is now optional. It defaults to `./Projects.xml` relative to your CVNBot.ini.
* Config: The Projects.xml file is now automatically created on first launch if absent. You no longer have to copy a skeleton Projects.xml file from the CVNBot repository to each new bot directory.
* IrcClient: Add IRCv3 SASL support for more reliable connections. This should reduce stalled or failed connection attempts e.g. `ERROR IRC: Nick/channel is temporarily unavailable`. SASL is automatically negotiated when available, such as with Libera Chat.

### Fixed

* Program: Fix "msgs" command to perform validation and logging when reloading Console.msgs. Previously the file was only validated on startup..
* Program: Fix BNU bypass of newusers/create2 events, because they matched only against the "creator" name (r.user) instead of also the created "editor" account (r.title).
* RCReader: Fix URLs to not escape namespace colons in page titles, so https://az.wikipedia.org/wiki/Kateqoriya:Tisl_FK_oyun%C3%A7ular%C4%B1 instead of https://az.wikipedia.org/wiki/Kateqoriya%3aTisl_FK_oyun%c3%a7ular%c4%b1.
* IrcClient: Increase send delay between messages from 0.3s to 0.4s to reduce chances of an [Excess Flood error on Libera Chat](https://libera.chat/guides/faq#are-bots-allowed).
* IrcClient: Faster RCReader startup by batching IRC joins.
* ListManager: Fix incorrect classification of `X~200` as a temp user, due to a missing start anchor in the `temp_account` regex.
* ListManager: Faster classify_editor by consolidating 3 SQL queries into 1, which allows CVNBot to keep up with Wikidata in `#cvn-wikidata`. For the past few years, it often lagged by several hours and eventually became silent and needed a manual reboot due to being too slow and backlog of several seconds every minute minute, amounting to several hours after a few days. ([change 1332795](https://gerrit.wikimedia.org/r/c/labs/countervandalism/CVNBot/+/1332795/))

4.0.4 / 2024-11-22
==================

### Changed
* ListManager: Detect temporary accounts and treat as IP/anons ([T378530](https://phabricator.wikimedia.org/T378530))

4.0.3 / 2023-07-06
==================

### Changed
* Config: Update sample file with file layouts deployed on CVN servers
* Git repository moved from GitHub to gerrit.wikimedia.org
* Bug tracking moved to https://phabricator.wikimedia.org/tag/cvnbot/

### Fixed
* Project: Support newusers/byemail to be the same as newuser2 ([T327126](https://phabricator.wikimedia.org/T327126))
* Project: Set missing `caurl` attribute for newuser2 events ([T327127](https://phabricator.wikimedia.org/T327127))

4.0.2 / 2021-06-20
==================

### Changed
* Config: The default value for `ircserver` is now `irc.libera.chat`.

### Fixed
* Program: Fix login to use the value of `botrealname` instead of reusing
  `partmsg` as the IRC realname of the bot. This bug was introduced in version 3.0.

4.0.1 / 2021-01-25
==================

### Changed
* Config: Remove the `forceHttps` setting. If a wiki is available both
  on HTTP and HTTPS and advertises HTTP urls as canonical in its RCFeed,
  then CVNBot will always show those as-is in the feed channel.

### Fixed
* Project: Fix bogus "modifyprotectRegex is missing" warning that was sent
  when the key was not actually missing.

4.0.0 / 2020-06-08
==================

### Removed
*  Remove support for Mono 5.4 and 5.16. CVNBot 4 supports Mono 6.8 or later. (Mono 5.18+ might work, but is not officially supported).

### Changed
* build: Upgrade from .NET Framework 4.5 to 4.7.2. ([issue #13](https://github.com/countervandalism/CVNBot/issues/13))
* build: Add support for Mono 6, MSBuild 16, and Visual Studio 2019.

### Fixed
* Project: Remove duplicate log message from `GetInterfaceMessages()`.

3.1.0 / 2020-05-09
==================

### Changed
* ListManager: Increase default blacklist expiry from 31 days to 90 days. ([pull #56](https://github.com/countervandalism/CVNBot/pull/56))

### Fixed
* Program: Fix `ReactorException: Duplicate 'watchword'` bug that could happen for
  upload events due to BES matching both `r.title` and `r.comment`. ([issue #59](https://github.com/countervandalism/CVNBot/issues/59))

3.0.0 / 2019-07-31
==================

_CVNBot 3.0 was originally tagged as _CVNBot 1.22._

### Removed
* Remove support for Mono 3 and 4. CVNBot 3 requires Mono 5 to run.

### Added
* RCReader: Add support for block/reblock log events.
  Previously, only "Block" and "Unblock" were reported.
  Now, "Block modification" events are reported as well.

### Changed
* Program: Remove the 200ms artificial delay from the "load" bot command,
  which loads the list of admins and bots from the wiki.
* Program: Remove our custom logic for message buffering. This existed
  as flood protection in case the monitored wiki(s) had a higher rate of
  events than the feed IRC channel allows. This custom logic is no longer
  needed because the SmartIrc4net library already contains flood protection
  and message buffering. ([issue #31](https://github.com/countervandalism/CVNBot/issues/31))
* Program: Update urls from Special:Blockip to Special:Block. ([issue #36](https://github.com/countervandalism/CVNBot/issues/36))
* RCReader: Remove "emergency restarter" feature for irc.wikimedia.org ops.

### Fixed
* Project: Remove unused regexes for "Undo" detection that could
  cause the "reload" command to crash on certain wikis.
* ListManager: Catch invalid BES regex patterns and log them. ([issue #28](https://github.com/countervandalism/CVNBot/issues/28))
* Program: Fix "Key duplication when adding: watchword" problem in ReactToRCEvent.
  Previously, if an edit or new page event triggered both BES and BNA, this
  bug caused the change to be ignored by the bot. ([issue #9](https://github.com/countervandalism/CVNBot/issues/9))
* RCReader: Fix parsing of newusers/create2 log events.
  Previously, events for users creating another account were ignored due to
  the log format being out of sync with the wiki software. ([issue #30](https://github.com/countervandalism/CVNBot/issues/30))

### Maintenance
* build: Enable continuous integration via Travis CI.
* build: Automatically copy CVNBot.exe.config to simplify installation.
* build: Automatically copy CVNBot-sample.ini to simplify installation.
* build: Upgrade log4net from 1.2.10 to 2.0.8.
* build: Upgrade Meebey.SmartIrc4net from 0.4 to 1.1.
* build: Upgrade .NET Framework from 3.5 to 4.5.
  The Sqlite library in .NET 4.5 no longer supports reading integer fields
  with GetString. This was common in CVNBot code and has now been mitigated.
* Logger: Change default log destination to Syslog, not text files.
* Logger: Add Nickname to log messages.
* ListManager: Re-use the same Sqlite connection between threads.
* RCReader: Remove log warnings about unknown log events.

1.21.0 / 2015-09-07
==================
* RCReader: Don't strip "/w/index.php" from urls.
* ListManager: Support IPv6 to detect anonymous users.
* Program: Add "caurl" message attribute. (CentralAuth link)
* Config: New "forceHttps" setting to use HTTPS as protocol in the RCFeed.
* Project: Enforce HTTPS for rooturl.
* All: Resolved compiler warnings for unused variables.

1.20.0 / 2012-07-13
==================
* Trim whitespace around parameter when dealing with ListManager. ([issue #1](https://github.com/wikimedia/CVNBot/issues/1))
* New feed filters added: feedFilterUsersAnon, feedFilterUsersReg
  feedFilterUsersBot, feedFilterEventMinorEdit, feedFilterEventEdit,
  feedFilterEventNewpage, feedFilterEventMove, feedFilterEventDelete,
  feedFilterEventNewuser, feedFilterEventUpload and feedFilterEventProtect.
  These can be set in SWMTBot.ini to 1, 2, 3 or 4.
  Check the bug ticket and code comments for more info. [issue #2](https://github.com/wikimedia/CVNBot/issues/2))
* New setting to prevent dbconnection in ListManager.ClassifyEditor can now be
  done by adding setting disableClassifyEditor in SWMTBot.ini. [issue #3](https://github.com/wikimedia/CVNBot/issues/3))
* New command "config" for getting the customizable .ini settings optional
  parameter 'all' to broadcast it (like "count" does by default) causing all
  other bots in the same channel to also show their configs. [issue #4](https://github.com/wikimedia/CVNBot/issues/4))
* "the end of time" is now controlled by Console.msgs to allow translation.
* newuser/newuser2 event gets a 'talkurl' attribute.
* Delete event gets a 'url' attribute.
* Actions now have a clean-attribute without prefixes ('c' + attrname) besides
  the regular one, for more flexibility in the layout of the messages
  So `[[${editor}]]` renders `[[xx:User:Foo]]` and `${ceditor}` renders `Foo`.
* Add 'talkurl' attribute for block and unblock event. [issue #5](https://github.com/wikimedia/CVNBot/issues/5))
* Implement log type 'protect', 'unprotect' and 'modifyprotect'. [issue #6](https://github.com/wikimedia/CVNBot/issues/6))
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
  ([issue #7](https://github.com/wikimedia/CVNBot/issues/7))
* `nsEnglish[4]` and `nsEnglish[5]` have been changed from "Wikipedia" to "Project"
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
* Auto-download lists now searches for a `<ul>` instead of an `<ol>` on
  `[[Special:Listusers]]` following the change in MediaWiki.
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
