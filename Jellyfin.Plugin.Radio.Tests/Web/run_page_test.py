"""Runs page-test.html in headless Chrome and checks what the plugin page did.

Usage: python run_page_test.py            (exit code 0 = all checks passed)
Set CHROME to the browser binary if it is not found automatically.

The page runs the real Web/configPage.html against a mocked Jellyfin API and writes its
observations into <pre id="result">; this script reads them back from Chrome's --dump-dom.
"""
import html
import json
import os
import pathlib
import re
import shutil
import subprocess
import sys
import tempfile

HERE = pathlib.Path(__file__).resolve().parent
PAGE = HERE / "page-test.html"


def find_chrome():
    candidates = [
        os.environ.get("CHROME"),
        shutil.which("google-chrome"),
        shutil.which("google-chrome-stable"),
        shutil.which("chromium"),
        shutil.which("chromium-browser"),
        r"C:\Program Files\Google\Chrome\Application\chrome.exe",
        r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    ]
    for candidate in candidates:
        if candidate and pathlib.Path(candidate).exists():
            return candidate
    sys.exit("No Chrome found; set CHROME to its path.")


def run_page(chrome):
    with tempfile.TemporaryDirectory() as profile:
        args = [
            chrome, "--headless=new", "--disable-gpu", "--no-sandbox", "--lang=en-US",
            "--user-data-dir=" + profile,
            "--allow-file-access-from-files", "--virtual-time-budget=30000", "--dump-dom",
            PAGE.as_uri(),
        ]
        out = subprocess.run(args, capture_output=True, text=True, encoding="utf-8", timeout=120, check=False).stdout
    match = re.search(r'<pre id="result">(.*?)</pre>', out, re.S)
    if not match:
        sys.exit("The page did not report a result. Chrome output starts with:\n" + out[:2000])
    text = html.unescape(match.group(1))
    if text.startswith("RUNNER FAILED"):
        sys.exit(text)
    return json.loads(text)


def main():
    r = run_page(find_chrome())
    checks = [
        ("no script errors", r.get("errors") == [], r.get("errors")),
        ("a fresh install shows the built-in stations", r.get("initialRows") == 3, r.get("initialRows")),
        ("administrators are labelled", r.get("adminLabelled") is True, r.get("adminLabelled")),
        ("a name with quotes survives a re-render", r.get("quotedName") == 'Radio "Een"', r.get("quotedName")),
        ("a test result lands on the tested station after a move", "128 kbps" in (r.get("probeTarget") or ""), r.get("probeTarget")),
        ("a test result leaves other stations alone", "192 kbps" in (r.get("otherUntouched") or ""), r.get("otherUntouched")),
        ("Enter in Add tests the new station", r.get("enterProbed") == 1, r.get("enterProbed")),
        ("Enter in Add does not save", r.get("enterSaved") == 0, r.get("enterSaved")),
        ("the new station is added", r.get("rowsAfterAdd") == 4 and r.get("addedSlug") is True, (r.get("rowsAfterAdd"), r.get("addedSlug"))),
        ("Save stores the list", r.get("saves") == 1 and r.get("savedDefaultsLoaded") is True, (r.get("saves"), r.get("savedDefaultsLoaded"))),
        ("Save keeps the page order", r.get("savedOrder") == ["npo-radio-1", "npo-radio-2", "qmusic", "sky-radio"], r.get("savedOrder")),
        ("Save keeps the quoted name", r.get("savedQuotedName") == 'Radio "Een"', r.get("savedQuotedName")),
        ("a changed URL needs a new test", "press Test" in (r.get("formatAfterUrlChange") or ""), r.get("formatAfterUrlChange")),
        ("Save is refused for an untested URL", r.get("saveRefused") is True and "problems" in (r.get("refusalAlert") or ""), (r.get("saveRefused"), r.get("refusalAlert"))),
        ("disabling a saved station asks first", r.get("disableAsked") == 1 and r.get("disableKept") is True, (r.get("disableAsked"), r.get("disableKept"))),
        ("hiding from an administrator asks first", r.get("adminAsked") == 1 and r.get("adminKeptUnticked") is True, (r.get("adminAsked"), r.get("adminKeptUnticked"))),
    ]
    failed = 0
    for name, ok, seen in checks:
        print(("PASS " if ok else "FAIL ") + name + ("" if ok else f"  (got {seen!r})"))
        failed += 0 if ok else 1
    print(f"{len(checks) - failed}/{len(checks)} page checks passed")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
