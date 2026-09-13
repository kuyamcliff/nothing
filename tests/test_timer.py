from nothing.timer import format_duration
from nothing.cli import parse_time


def test_format_duration_seconds():
    assert format_duration(45) == "45s"


def test_format_duration_minutes():
    assert format_duration(90) == "1m 30s"


def test_format_duration_hours():
    assert format_duration(3661) == "1h 1m 1s"


def test_parse_time_seconds():
    assert parse_time("30s") == 30


def test_parse_time_minutes():
    assert parse_time("5m") == 300


def test_parse_time_hours():
    assert parse_time("1h") == 3600


def test_parse_time_plain_int():
    assert parse_time("120") == 120
