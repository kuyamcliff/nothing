import argparse
import sys

from nothing import __version__
from nothing.timer import countdown, pomodoro
from nothing.log import show_history, clear_history, record


BANNER = r"""
  _ __   ___  _   _ | |_  _
 | '_ \ / _ \| | | || __|(_)
 | | | | (_) | |_| || |_  _
 |_| |_|\___/ \__,_| \__|(_)

  do nothing. productively.
"""


def parse_time(value: str) -> int:
    """Accept '5m', '90s', '1h', or plain int seconds."""
    value = value.strip().lower()
    if value.endswith("h"):
        return int(value[:-1]) * 3600
    if value.endswith("m"):
        return int(value[:-1]) * 60
    if value.endswith("s"):
        return int(value[:-1])
    return int(value)


def main() -> None:
    parser = argparse.ArgumentParser(
        prog="nothing",
        description="Do nothing, productively.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument("--version", action="version", version=f"nothing {__version__}")

    sub = parser.add_subparsers(dest="cmd")

    # nothing wait <duration>
    p_wait = sub.add_parser("wait", help="Do nothing for a set duration")
    p_wait.add_argument("duration", help="Duration e.g. 5m, 30s, 1h")
    p_wait.add_argument("--label", default="Nothing", help="Session label")

    # nothing pomodoro
    p_pom = sub.add_parser("pomodoro", aliases=["pom"], help="Run a Pomodoro session")
    p_pom.add_argument("--work", type=int, default=25, metavar="MIN", help="Work minutes (default 25)")
    p_pom.add_argument("--break", dest="brk", type=int, default=5, metavar="MIN", help="Break minutes (default 5)")
    p_pom.add_argument("--rounds", type=int, default=4, help="Number of rounds (default 4)")

    # nothing log
    p_log = sub.add_parser("log", help="Show session history")
    p_log.add_argument("--limit", type=int, default=10, help="Number of entries to show")
    p_log.add_argument("--clear", action="store_true", help="Clear history")

    args = parser.parse_args()

    if args.cmd is None:
        print(BANNER)
        parser.print_help()
        sys.exit(0)

    if args.cmd == "wait":
        seconds = parse_time(args.duration)
        countdown(seconds, label=args.label)
        record(args.label, seconds)

    elif args.cmd in ("pomodoro", "pom"):
        pomodoro(work_minutes=args.work, break_minutes=args.brk, rounds=args.rounds)

    elif args.cmd == "log":
        if args.clear:
            clear_history()
        else:
            show_history(limit=args.limit)
