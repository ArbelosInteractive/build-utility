"""Shared helpers for the upload scripts: a timestamped log file and a created/updated summary.

Keep this file in the same folder as the upload scripts.
"""
import datetime
import sys
from pathlib import Path

DEFAULT_LOG_DIR = Path(__file__).with_name("logs")


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
        self.created, self.updated, self.notes, self.failed, self.not_run = [], [], [], [], []

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
