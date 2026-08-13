"""Command line entry point"""

import argparse
import os
import sys
# This makes `python3 /path/to/cvnbot` work from anywhere with no installation.
# (Instead of from current directory as `python3 -m cvnbot`)
sys.path.append(os.path.dirname(os.path.dirname(__file__)))

from cvnbot import CVNBot  # noqa: E402


def main(argv=None):
    parser = argparse.ArgumentParser(
        prog="cvnbot",
        description="Countervandalism Network IRC bot"
    )
    parser.add_argument(
        "-c",
        "--config",
        default="CVNBot.ini",
        help="path to configuration file (default: ./CVNBot.ini)",
    )
    parser.add_argument("--version", action="version", version="CVNBot " + CVNBot.VERSION)
    args = parser.parse_args(argv)

    CVNBot(args.config).run()


if __name__ == "__main__":
    main()
