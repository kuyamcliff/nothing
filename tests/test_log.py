import json
import pytest
from pathlib import Path
from unittest.mock import patch

import nothing.log as log_module


@pytest.fixture(autouse=True)
def tmp_log(tmp_path, monkeypatch):
    fake_path = tmp_path / ".nothing" / "log.json"
    monkeypatch.setattr(log_module, "LOG_PATH", fake_path)
    yield fake_path


def test_record_creates_entry():
    log_module.record("test", 60)
    data = json.loads(log_module.LOG_PATH.read_text())
    assert len(data) == 1
    assert data[0]["label"] == "test"
    assert data[0]["seconds"] == 60


def test_clear_history():
    log_module.record("test", 60)
    log_module.clear_history()
    data = json.loads(log_module.LOG_PATH.read_text())
    assert data == []


def test_show_history_empty(capsys):
    log_module.show_history()
    out = capsys.readouterr().out
    assert "No nothing sessions" in out
