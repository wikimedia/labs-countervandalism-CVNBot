# CVNBot

## Support

* [Documentation: Bot commands](https://meta.wikimedia.org/wiki/CVNBot#Commands)
* [`#countervandalism`](irc://irc.libera.chat/#countervandalism) on [Libera.Chat](https://libera.chat)
* [Mailing list](https://lists.wikimedia.org/mailman/listinfo/cvn) (Subscribing before posting is required)

## Contribute

Found a bug? Please report it to our
[issue tracker](https://phabricator.wikimedia.org/tag/cvnbot/).

## Build

The software is written in C# and originally created as a Visual Studio Project.
We use `mono` to run the executable and `msbuild` to build the executable.

Recommended installation methods:

* For Linux, install [`mono-complete`](https://packages.debian.org/search?keywords=mono-complete) from Debian, or [latest from mono-project.com](https://www.mono-project.com/download/stable/#download-lin),
* For Mac, install [Visual Studio for Mac](https://www.visualstudio.com/vs/visual-studio-mac/) (enable Mono and .NET during installation).
* For Windows, install [Visual Studio](https://visualstudio.microsoft.com/vs/) (enable Mono and .NET during installation).

For standalone command-line installations on Mac or Windows, see [monodevelop.com](https://www.monodevelop.com/download/).

Currently supported versions of Mono: **6.12**

Once mono is installed, build the project. The below uses Debug, for local development. (See [Installation](./docs/install.md) for how to install it in production):

```
countervandalism/CVNBot:$ msbuild src/CVNBot.sln /p:Configuration=Debug
```

Once built, you can run it:

```
countervandalism/CVNBot/src/CVNBot/bin/Debug:$ mono CVNBot.exe
```

## Versioning

We use the Semantic Versioning guidelines as much as possible.
Releases will be numbered in the following format: `<major>.<minor>.<patch>`

For more information on SemVer, please visit https://semver.org/.

## License

See [LICENSE](./LICENSE.txt).
