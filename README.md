[![Build Status](https://github.com/countervandalism/CVNBot/actions/workflows/CI.yaml/badge.svg)](https://github.com/countervandalism/CVNBot/actions/workflows/CI.yaml)

CVNBot
==================================================


Quick start
----------

Clone the repo, `git clone git://github.com/countervandalism/CVNBot.git`, or
[download the latest
release](https://github.com/countervandalism/CVNBot/zipball/master).


Versioning
----------

We use the Semantic Versioning guidelines as much as possible.

Releases will be numbered in the following format:

`<major>.<minor>.<patch>`

The `-alpha` suffix is used to indicate unreleased versions in development.

For more information on SemVer, please visit https://semver.org/.


Build
----------
The software is written in C# and originally created as a Visual Studio Project.
We use `mono` to run the executable and `msbuild` to build the executable.

Recommended installation methods:

* For Linux, install [`mono-complete`](https://packages.debian.org/search?keywords=mono-complete) from Debian, or [latest from mono-project.com](https://www.mono-project.com/download/stable/#download-lin),
* For Mac, install [Visual Studio for Mac](https://www.visualstudio.com/vs/visual-studio-mac/) (enable Mono and .NET during installation).
* For Windows, install [Visual Studio](https://visualstudio.microsoft.com/vs/) (enable Mono and .NET during installation).

For standalone command-line installations on Mac or Windows, see [monodevelop.com](https://www.monodevelop.com/download/).

Currently supported versions of Mono: **6.8**

Once mono is installed, build the project. The below uses Debug, for local development. (See [Installation](./docs/install.md) for how to install it in production):

```bash
countervandalism/CVNBot:$ msbuild src/CVNBot.sln /p:Configuration=Debug
```

Once built, you can run it:
```bash
countervandalism/CVNBot/src/CVNBot/bin/Debug:$ mono CVNBot.exe
```


Bug tracker
-----------

Found a bug? Please report it using our [issue
tracker](https://github.com/countervandalism/CVNBot/issues)!


Documentation, support and contact
-----------
* [Documentation (wiki)](https://github.com/countervandalism/CVNBot/wiki/Documentation)
* [`#countervandalism`](irc://irc.libera.chat/#countervandalism) on [Libera.Chat](https://libera.chat)
* [cvn@lists.wikimedia.org](https://lists.wikimedia.org/mailman/listinfo/cvn) (Requires subscription before posting. [Subscribe here](https://lists.wikimedia.org/mailman/listinfo/cvn))


Copyright and license
---------------------

See [LICENSE](https://raw.github.com/countervandalism/CVNBot/master/LICENSE.txt).
