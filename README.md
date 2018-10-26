[![Build Status](https://travis-ci.org/countervandalism/CVNBot.svg?branch=master)](https://travis-ci.org/countervandalism/CVNBot)

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

For more information on SemVer, please visit http://semver.org/.


Build
----------
The software is written in C# and originally created as a Visual Studio Project.
We use `mono` to run the executable and `xbuild` to build the executable.

Standalone installers (you'll need both Mono and MonoDevelop. The latter provides `xbuild`):
* [mono-project.com](http://www.mono-project.com/download/)
* [monodevelop.com/Download](http://monodevelop.com/Download)

Or, if using `apt-get`, use one of these:
* [`mono-develop`](https://packages.debian.org/search?keywords=mono-devel) (`mono`)
* [`mono-complete`](https://packages.debian.org/search?keywords=mono-complete) (`mono`+`xbuild`)

Currently supported versions of Mono: **4.8**, **5.16**

Once mono is installed, build the project:

```bash
countervandalism/CVNBot/src/CVNBot:$ xbuild src/CVNBot.sln /p:Configuration=Release
```

Once built, you can run it (see [Installation](https://github.com/countervandalism/CVNBot/wiki/Documentation#installation) for more info on how to properly install it for actual usage, don't run it from the Debug directory in production):
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
* <irc://irc.freenode.net/#countervandalism>
* [cvn@lists.wikimedia.org](https://lists.wikimedia.org/mailman/listinfo/cvn) (Requires subscription before posting. [Subscribe here](https://lists.wikimedia.org/mailman/listinfo/cvn))


Copyright and license
---------------------

See [MIT-LICENSE](https://raw.github.com/countervandalism/CVNBot/master/MIT-LICENSE.txt) and [AUTHORS](https://github.com/countervandalism/CVNBot/blob/master/AUTHORS.txt).
