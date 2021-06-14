# Install CVNBot

## Installation

1. Compile the code by running the following command:

   `msbuild src/CVNBot.sln /p:Configuration=Release`

   This command creates `CVNBot.exe` and other files in the output directory at `src/CVNBot/bin/Release`.
1. Create a directory for your bot, and move the contents of `src/CVNBot/bin/Release` to it.
1. Edit `CVNBot.ini`: Set at least `botnick`.
1. Set permissions and ownership correctly. This step is after the copying of files because group ownership is usually not preserved when copying files.
   * For personal use, `chmod 644 *`, `chmod 600 CVNBot.ini`, and `chmod 755 CVNBot.exe`.
   * For Countervandalism Network:`chmod 664 *`, `chmod 660 CVNBot.ini`,  `chmod 755 CVNBot.exe`, and `chgrp cvn.cvnservice *`.
1. You can now start the start the bot by running `mono CVNBot.exe` from your bot directory.<br/>The bot will join the specified `feedchannel` (by default: `#cvn-sandbox`).

## Upgrade

1. Compile the code by running the following command:

   `msbuild src/CVNBot.sln /p:Configuration=Release`

   This command creates `CVNBot.exe` and other files in the output directory at `src/CVNBot/bin/Release`.
1. Enter `src/CVNBot/bin/Release`.
1. Remove `Projects.xml` and `CVNBot.ini` (to avoid accidentally overwriting your existing ones, later)
1. Make sure the bot is not currently running (e.g. `Botname quit` on IRC, and check output of `ps aux`).
1. Copy all remaining files in `src/CVNBot/bin/Release` to your existing bot directory. For example: `src/CVNBot/bin/Release$ cp * /srv/cvn/services/cvnbot/CVNBotXYZ/`
1. Set permissions and ownership correctly. This step is after the copying of files because group ownership is usually not preserved when copying files.
   * For personal use, `chmod 644 *`, `chmod 600 CVNBot.ini`, and `chmod 755 CVNBot.exe`.
   * For Countervandalism Network:`chmod 664 *`, `chmod 660 CVNBot.ini`,  `chmod 755 CVNBot.exe`, and `chgrp cvn.cvnservice *`.
1. Start the bot (or, let [stillalive](https://github.com/countervandalism/stillalive) start it).
