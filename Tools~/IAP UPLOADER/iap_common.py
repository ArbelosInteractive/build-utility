"""Shared helpers for the upload scripts: a timestamped log file and a created/updated summary.

Keep this file in the same folder as the upload scripts.
"""
import datetime
import json
import os
import sys
from pathlib import Path

DEFAULT_LOG_DIR = Path(__file__).with_name("logs")

# Set by `upload_all.py --progress` (the Unity window): print "@@item {json}" status lines as items finish.
PROGRESS = os.environ.get("IAP_PROGRESS") == "1"
PLATFORM_KEYS = {"Apple": "apple", "Google Play": "google", "PlayFab": "playfab"}


def emit(kind, **data):
    """One machine-readable line for the Unity window. Never written to the log (it goes to the real stdout)."""
    if PROGRESS:
        out = getattr(sys.stdout, "_console", sys.stdout)
        out.write(f"@@{kind} " + json.dumps(data) + "\n")
        out.flush()


class _LiveList(list):
    """A list that reports each added item, so the window can show statuses while the run is still going."""

    def __init__(self, platform, status, values=()):
        super().__init__(values)
        self._platform, self._status = platform, status
        for value in values:
            emit("item", platform=self._platform, name=str(value), status=self._status)

    def append(self, value):
        super().append(value)
        emit("item", platform=self._platform, name=str(value), status=self._status)

    def extend(self, values):
        for value in values:
            self.append(value)

    def __iadd__(self, values):
        self.extend(values)
        return self


class _Tee:
    """Write to the console as normal, and to the log file with a time on every line."""

    def __init__(self, console, log):
        self._console, self._log, self._buf = console, log, ""

    def write(self, text):
        self._console.write(text)
        self._buf += text
        while "\n" in self._buf:
            line, self._buf = self._buf.split("\n", 1)
            self._log.write(f"[{datetime.datetime.now():%H:%M:%S}] {line}\n")
        self._log.flush()
        return len(text)

    def flush(self):
        self._console.flush()
        self._log.flush()

    def __getattr__(self, name):  # encoding, isatty, ...
        return getattr(self._console, name)


def setup_logging(name, log_file=None):
    """Start copying everything printed to a log file. Returns the log file's path.

    With no log_file, a new file logs/<name>_YYYY-MM-DD_HH-MM-SS.log is created next to the
    scripts. A given log_file is appended to (the wrapper uses this so one run = one file).
    """
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):  # Windows consoles: don't choke on non-ASCII text
            stream.reconfigure(encoding="utf-8", errors="replace")
    now = datetime.datetime.now()
    path = Path(log_file) if log_file else DEFAULT_LOG_DIR / f"{name}_{now:%Y-%m-%d_%H-%M-%S}.log"
    path.parent.mkdir(parents=True, exist_ok=True)
    handle = open(path, "a", encoding="utf-8")
    sys.stdout, sys.stderr = _Tee(sys.stdout, handle), _Tee(sys.stderr, handle)
    print(f"Log file: {path}")
    print(f"Started:  {now:%Y-%m-%d %H:%M:%S} ({name})")
    return path


class Report:
    """Collects what happened to each item so it can be printed (and logged) at the end."""

    def __init__(self, platform):
        self.platform = platform
        key = PLATFORM_KEYS.get(platform, platform.lower())
        self._key = key
        self.created, self.updated = _LiveList(key, "Created"), _LiveList(key, "Updated")
        self.failed = _LiveList(key, "FAILED")
        self.notes = []
        self._not_run = []

    @property
    def not_run(self):
        return self._not_run

    @not_run.setter
    def not_run(self, values):  # the scripts assign the whole list when a failure stops the run
        self._not_run = _LiveList(self._key, "Not processed", values)

    @staticmethod
    def _section(title, items):
        print(f"{title} ({len(items)}):")
        for item in items or ["(none)"]:
            print(f"  - {item}")

    def print(self):
        print(f"\n===== {self.platform} summary =====")
        self._section("Created new", self.created)
        self._section("Already existed, updated", self.updated)
        if self.notes:
            self._section("Notes", self.notes)
        if self.failed:
            self._section("FAILED (run stopped here)", self.failed)
        if self.not_run:
            self._section("Not processed because of the failure", self.not_run)
        print(f"Finished: {datetime.datetime.now():%Y-%m-%d %H:%M:%S}")
