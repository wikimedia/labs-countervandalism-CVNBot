# Install CVNBot

## Installation

1. Compile the code by running the following command:

   `msbuild src/CVNBot.sln /p:Configuration=Release`

   This command creates `CVNBot.exe` and other files in the output directory at `src/CVNBot/bin/Release`.
1. Create a directory for your bot, and move the contents of `src/CVNBot/bin/Release` to it.
1. Edit `CVNBot.ini`: Set at least `botnick`.
1. For personal use, chmod the files 644 (except the .exe file 755, and the ini file 600). For organisational use (e.g. Countervandalism Network), chmod the files 664 (except the .exe 775, and the ini file 660). For organisational use, also make sure the files are all owned by the correct group (e.g. `cvn.cvnservice`)
1. You can now start the start the bot by running `mono CVNBot.exe` from your bot directory.<br/>The bot will join the specified `feedchannel` on `chat.freenode.net` (by default: `#cvn-sandbox`).

## Upgrade

1. Compile the code by running the following command:

   `msbuild src/CVNBot.sln /p:Configuration=Release`

   This command creates `CVNBot.exe` and other files in the output directory at `src/CVNBot/bin/Release`.
1. Enter `src/CVNBot/bin/Release`.
1. Remove `Projects.xml` and `CVNBot.ini` (to avoid accidentally overwriting your existing ones, later)
1. For personal use, chmod the remaining files 644 (except the .exe file, use 755).
   For organisational use (e.g. Countervandalism Network), chmod the remaining files 664 (except the .exe, use 775), and make sure all files are owned by the correct group (e.g. `chgrp cvn.cvnservice *`)
1. Make sure the bot is not currently running (e.g. `Botname quit` on IRC, and check output of `ps aux`).
1. Copy all remaining files in `src/CVNBot/bin/Release` to your existing bot directory.
1. Restart the bot (or let it start automatically if using [stillalive](https://github.com/countervandalism/stillalive)).
