# nothing

A CLI tool for doing nothing, productively.

Timers, Pomodoro sessions, and session history — all from your terminal.

```
  _ __   ___  _   _ | |_  _
 | '_ \ / _ \| | | || __|(_)
 | | | | (_) | |_| || |_  _
 |_| |_|\___/ \__,_| \__|(_)

  do nothing. productively.
```

## Install

```bash
pip install .
```

## Usage

### Wait timer

Do nothing for a set duration:

```bash
nothing wait 5m
nothing wait 30s
nothing wait 1h
nothing wait 25m --label "lunch break"
```

### Pomodoro

Run a full Pomodoro session:

```bash
nothing pomodoro
nothing pom --work 50 --break 10 --rounds 3
```

### Session log

View your nothing history:

```bash
nothing log
nothing log --limit 20
nothing log --clear
```

## Development

```bash
pip install -e ".[dev]"
pytest tests/
```

## License

MIT — © 2026 Kuyam Cliff
