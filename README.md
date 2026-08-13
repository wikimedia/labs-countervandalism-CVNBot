# CVNBot

## Support

* [Documentation: Bot commands](https://meta.wikimedia.org/wiki/CVNBot#Commands)
* [`#countervandalism`](irc://irc.libera.chat/#countervandalism) on [Libera.Chat](https://libera.chat)
* [Mailing list](https://lists.wikimedia.org/mailman/listinfo/cvn) (Subscribing before posting is required)

## Contribute

Found a bug? Please report it to our
[issue tracker](https://phabricator.wikimedia.org/tag/cvnbot/).

## Requirements

Python 3.8 or later. This project uses only the Python standard library and requires no third-party dependencies.

## Run

From anywhere:

```sh
python3 /path/to/CVNBot/cvnbot --config /srv/MyBot/CVNBot.ini
```

During development, from your clone of the Git repository:

```sh
python3 -m cvnbot --config /srv/MyBot/CVNBot.ini
```

Or, after `pip install .`:

```sh
cvnbot --config /srv/MyBot/CVNBot.ini
```

See [Installation](./docs/install.md) for a production setup.

## Test

Run the unit tests and the linter:

```sh
tox
```

Or, run only the unit tests:
```sh
python3 -m unittest
```

## Layout

* The `python3 -m cvnbot` or `python3 -m /path/to/cvnbot` commands are handled by `__main__.py`,
  which parses the CLI arguments and then delegates to `CVNBot.run` in `program.py`.
* The **Main** thread (`program.py`) is where `CVNBot.run`
  * .. starts the main IRC connection to Libera Chat.
  * .. starts the RCReader thread.
  * .. starts ListManager's database connection (open Lists.sqlite).
  * .. listens for and responds to any IRC commands.
* The **RCReader** thread (`rcreader.py`)
  * .. starts a second IRC connection for RecentChanges from wikimedia.org.
  * .. listens for messages and parse them into RecentChange events, for each one:
    * filter based on feed settings (CVNBot.ini).
    * filter based on database queries (ListManager, such as blacklist and watchlist).
    * format event using messages from Console.msgs.
    * send message to feed channel on Libera Chat, via the main IRC connection.
* The **Tim** thread (`listmanager.py`)
  * .. removes expired rows from the database every two hours.

Notable classes:

* `Messages`: This is formulates and formats messages spoken by the bot.
  These are read from the `Console.msgs` file, and can be reloaded at runtime
  via the `CVNBot msgs` command. The file is considered read-only, with changes
  deployed via version control outside the bot runtime.

  See also <https://gerrit.wikimedia.org/g/labs/countervandalism/cvn-infrastructure/+/HEAD/>.

* `ProjectList`: The set of monitored wikis as persisted in `Projects.xml`.

  For each wiki we store namespace names, and a copy of various MediaWiki interface
  messages to help parse log events and automatic edit summaries from the
  RecentChanges stream.

  This XML file is writable at runtime via the `CVNBot load`, `CVNBot drop`, `CVNBot reload`,
  and `CVNBot batchreload` commands.

* `ListManager`: The CVNBot database contains usernames, page titles, and various patterns
  that dictate which events from the RecentChanges stream to report to the CVN channel on IRC.

  It is backed by the `Lists.sqlite` file.

## Versioning

We use the Semantic Versioning guidelines as much as possible.
Releases will be numbered in the following format: `<major>.<minor>.<patch>`

For more information on SemVer, please visit https://semver.org/.

## License

See [LICENSE](./LICENSE.txt).
