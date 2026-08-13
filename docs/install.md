# Install CVNBot

## Installation

1. Create a directory for your new bot.
2. Create a `CVNBot.ini` file in this directory.
   It is recommended to start with a copy of `CVNBot-sample.ini`
   and change at least `botnick`.

   For CVN production, always enable `logsyslog=1` because the bot will run as
   a background process managed by stillalive with standard output disabled.

3. Set permissions and ownership correctly.
   For personal use:
   ```sh
   chmod 600 CVNBot.ini
   ```
   Or, for CVN production:
   ```sh
   chmod 664 * && chmod 660 CVNBot.ini
   ```

When you [run the bot](../README.md#run), it will automatically create `Projects.xml`
and `Lists.sqlite` files at either your specified location, or the default locations
relative to your CVNBot.ini file.

## Upgrade

1. Make sure the bot is not currently running (e.g. `Botname quit` on IRC, or check `ps aux`).
2. Run `git pull` in `/srv/cvn/git/CVNBot` or wherever you have it cloned.
   No build or compile step is required.

   (If you chose to install the command globally via pip, you'll need to re-run `pip install --force .` from the CVNBot directory to update that install.)

3. Start the bot.
   Or, let [stillalive](https://gerrit.wikimedia.org/g/labs/countervandalism/stillalive/) start it.
