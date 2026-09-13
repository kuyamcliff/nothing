import time
import sys
from datetime import datetime, timedelta


def format_duration(seconds: int) -> str:
    m, s = divmod(seconds, 60)
    h, m = divmod(m, 60)
    if h:
        return f"{h}h {m}m {s}s"
    if m:
        return f"{m}m {s}s"
    return f"{s}s"


def countdown(seconds: int, label: str = "Nothing") -> None:
    start = datetime.now()
    end = start + timedelta(seconds=seconds)

    print(f"\n  {label} starts now. Back at {end.strftime('%H:%M:%S')}.\n")

    try:
        while True:
            remaining = int((end - datetime.now()).total_seconds())
            if remaining <= 0:
                break
            bar_len = 30
            filled = bar_len - int((remaining / seconds) * bar_len)
            bar = "█" * filled + "░" * (bar_len - filled)
            sys.stdout.write(f"\r  [{bar}] {format_duration(remaining)} left  ")
            sys.stdout.flush()
            time.sleep(0.5)
    except KeyboardInterrupt:
        elapsed = int((datetime.now() - start).total_seconds())
        print(f"\n\n  Stopped after {format_duration(elapsed)}.\n")
        return

    print(f"\r  [{'█' * bar_len}] Done!                    ")
    print(f"\n  You did nothing for {format_duration(seconds)}. Nice.\n")


def pomodoro(work_minutes: int = 25, break_minutes: int = 5, rounds: int = 4) -> None:
    print(f"\n  Pomodoro: {rounds} rounds × {work_minutes}m work / {break_minutes}m break\n")
    for i in range(1, rounds + 1):
        print(f"  Round {i}/{rounds} — Work")
        countdown(work_minutes * 60, label=f"Work ({i}/{rounds})")
        if i < rounds:
            print(f"  Round {i}/{rounds} — Break")
            countdown(break_minutes * 60, label="Break")
    print("  All rounds complete. Go touch grass.\n")
