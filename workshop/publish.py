#!/usr/bin/env python3
"""Publish AlternativeHabitabilityModel to the Steam Workshop through steamcmd.

Full update (content + description + logo + change note):
    python publish.py

Metadata only (description + logo, content and change note untouched):
    python publish.py --meta

The Steam account defaults to "lazyranma"; override with --user or STEAM_USER.

Credentials may also come from the environment:
    STEAM_USER, STEAM_PASS, STEAM_GUARD_CODE

Other overrides:
    STEAMCMD        path to steamcmd.exe (default: C:\\soft\\steamcmd\\steamcmd.exe)
    STEAM_CONTENT   folder to upload (default: <repo>/game_dir/BepInEx/plugins/AlternativeHabitabilityModel)
"""
from __future__ import annotations

import argparse
import os
import re
import subprocess
import sys
from pathlib import Path

APP_ID = "1369700"
PUBLISHED_FILE_ID = "3834246135"

WORKSHOP_DIR = Path(__file__).resolve().parent
PLUGIN_DIR = WORKSHOP_DIR.parent
ROOT = PLUGIN_DIR.parents[1]  # .../solar-expanse

CHANGELOG = PLUGIN_DIR / "CHANGELOG.md"
DESCRIPTION = WORKSHOP_DIR / "description.txt"
# logo.jpg is 512×512, within Steam's 1 MB preview limit.
PREVIEW = WORKSHOP_DIR / "logo.jpg"

DEFAULT_STEAMCMD = r"C:\soft\steamcmd\steamcmd.exe"
DEFAULT_CONTENT = ROOT / "game_dir" / "BepInEx" / "plugins" / "AlternativeHabitabilityModel"


# --------------------------------------------------------------------------- #
# Markdown -> Steam BBCode (change note)
# --------------------------------------------------------------------------- #
def inline_md_to_bb(line: str) -> str:
    line = re.sub(r"\[([^\]]+)\]\(([^)]+)\)", r"[url=\2]\1[/url]", line)
    line = re.sub(r"\*\*(.+?)\*\*", r"[b]\1[/b]", line)
    line = re.sub(r"__(.+?)__", r"[b]\1[/b]", line)
    line = re.sub(r"~~(.+?)~~", r"[strike]\1[/strike]", line)
    line = re.sub(r"`([^`]+)`", r"[code]\1[/code]", line)
    line = re.sub(r"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)", r"[i]\1[/i]", line)
    line = re.sub(r"(?<!\w)_(?!_)(.+?)(?<!\w)_(?!\w)", r"[i]\1[/i]", line)
    return line


def first_changelog_section(path: Path) -> str:
    """Return the first `## ...` section, converted to BBCode.

    Only the initial `##` header is emitted (as [h2]); inner headers are
    dropped and bullet lines stay plain (no [list] markup).
    """
    lines = path.read_text(encoding="utf-8").splitlines()

    start = next((i for i, line in enumerate(lines) if line.startswith("## ")), None)
    if start is None:
        return ""

    end = next(
        (i for i in range(start + 1, len(lines)) if lines[i].startswith("## ")),
        len(lines),
    )

    out: list[str] = []
    header_done = False
    for line in lines[start:end]:
        stripped = line.strip()

        if stripped.startswith("## ") and not header_done:
            title = stripped[3:].strip()
            if title.startswith("[") and title.endswith("]"):
                title = title[1:-1]
            out.append(f"[h2]{inline_md_to_bb(title)}[/h2]")
            header_done = True
            continue

        if stripped.startswith("###"):
            continue  # no inner headers

        # Join markdown source wraps so each bullet stays on one line.
        if line[:2] == "  " and out and out[-1].strip() and not stripped.startswith(("- ", "* ")):
            out[-1] = out[-1].rstrip() + " " + inline_md_to_bb(stripped)
            continue

        if stripped.startswith("* "):
            line = "- " + line.lstrip()[2:]

        out.append(inline_md_to_bb(line))

    while out and not out[0].strip():
        out.pop(0)
    while out and not out[-1].strip():
        out.pop()

    return "\n".join(out)


# --------------------------------------------------------------------------- #
# VDF
# --------------------------------------------------------------------------- #
PATH_FIELDS = {"contentfolder", "previewfile"}


def build_vdf(fields: dict[str, str]) -> str:
    lines = ['"workshopitem"', "{"]
    for key, value in fields.items():
        if key in PATH_FIELDS:
            # VDF paths use \\ for each literal backslash.
            value = value.replace("\\", "\\\\")
        elif '"' in value:
            # A straight quote terminates the VDF string; \" is not decoded.
            raise ValueError(f'{key} contains a straight quote; use typographic (“ ”) or single quotes')
        # Text fields keep literal backslashes and newlines: steamcmd does not
        # unescape \\ or \n inside description/changenote.
        value = value.replace("\r\n", "\n")
        lines.append(f'    "{key}" "{value}"')
    lines.append("}")
    return "\n".join(lines) + "\n"


# --------------------------------------------------------------------------- #
# Main
# --------------------------------------------------------------------------- #
def main() -> int:
    parser = argparse.ArgumentParser(
        description="Publish AlternativeHabitabilityModel to the Steam Workshop."
    )
    parser.add_argument("--meta", action="store_true", help="update description and logo only")
    parser.add_argument("--user", default=os.environ.get("STEAM_USER", "lazyranma"), help="Steam account name")
    parser.add_argument("--password", default=os.environ.get("STEAM_PASS"), help="Steam password")
    parser.add_argument("--guard", default=os.environ.get("STEAM_GUARD_CODE"), help="Steam Guard code")
    parser.add_argument("--steamcmd", default=os.environ.get("STEAMCMD", DEFAULT_STEAMCMD))
    parser.add_argument("--content", default=os.environ.get("STEAM_CONTENT", str(DEFAULT_CONTENT)))
    parser.add_argument("--dry-run", action="store_true", help="write the VDF and print the command only")
    args = parser.parse_args()

    steamcmd = Path(args.steamcmd)
    if not steamcmd.is_file():
        print(f"steamcmd not found: {steamcmd}", file=sys.stderr)
        return 1

    content = Path(args.content)
    if not args.meta and not (content / "AlternativeHabitabilityModel.dll").is_file():
        print(f"content folder has no AlternativeHabitabilityModel.dll: {content}", file=sys.stderr)
        return 1

    if not DESCRIPTION.is_file():
        print(f"missing description: {DESCRIPTION}", file=sys.stderr)
        return 1
    if not PREVIEW.is_file():
        print(f"missing preview image: {PREVIEW}", file=sys.stderr)
        return 1

    fields: dict[str, str] = {
        "appid": APP_ID,
        "publishedfileid": PUBLISHED_FILE_ID,
    }

    if not args.meta:
        fields["contentfolder"] = str(content)

    fields["previewfile"] = str(PREVIEW)
    fields["description"] = DESCRIPTION.read_text(encoding="utf-8")

    if not args.meta:
        note = first_changelog_section(CHANGELOG)
        if note:
            fields["changenote"] = note
            print("Change note:")
            print(note)
            print()

    vdf_path = WORKSHOP_DIR / ("_publish_meta.vdf" if args.meta else "_publish.vdf")
    vdf_path.write_text(build_vdf(fields), encoding="utf-8", newline="\n")
    print(f"Wrote {vdf_path}")

    if not args.user:
        print("No --user/STEAM_USER given; steamcmd will prompt for login.", file=sys.stderr)

    cmd = [str(steamcmd), "+login"]
    if args.user:
        cmd.append(args.user)
        if args.password:
            cmd.append(args.password)
        if args.guard:
            cmd += ["+set_steam_guard_code", args.guard]
    cmd += ["+workshop_build_item", str(vdf_path), "+quit"]

    print("Running:", " ".join(cmd))
    if args.dry_run:
        return 0

    result = subprocess.run(cmd)

    if result.returncode == 0:
        vdf_path.unlink(missing_ok=True)
    else:
        print(f"steamcmd exited with {result.returncode}; kept {vdf_path}", file=sys.stderr)

    return result.returncode


if __name__ == "__main__":
    raise SystemExit(main())
