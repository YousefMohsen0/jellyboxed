#!/usr/bin/env python3
"""Package Jellyboxed for release.

Creates dist/jellyboxed_<version>.zip (plugin DLL + meta.json) and adds the version to manifest.json,
which is the plugin-repository file Jellyfin reads when a user adds this repo's manifest URL.

Used by .github/workflows/release.yml; also handy locally:

    dotnet publish Jellyfin.Plugin.Jellyboxed -c Release -o out -p:Version=1.0.0.0
    python3 scripts/package.py --dll out/Jellyfin.Plugin.Jellyboxed.dll --version 1.0.0.0
"""
import argparse
import datetime
import hashlib
import json
import re
import sys
import zipfile
from pathlib import Path

PLUGIN_GUID = "7b2f4a9e-3c61-4d58-9e0a-5f1c8d2b6a47"
NAME = "Jellyboxed"
DESCRIPTION = "Letterboxd inside Jellyfin: your ratings on posters, friends' activity, log films after watching, and a synced watchlist."
OVERVIEW = "Letterboxd integration for Jellyfin"
OWNER = "YousefMohsen0"
CATEGORY = "General"


def changelog_for(version: str, path: Path) -> str:
    """Returns the CHANGELOG.md section for the version (3-part form of the 4-part plugin version)."""
    short = ".".join(version.split(".")[:3])
    if not path.exists():
        return f"Release {short}"
    text = path.read_text(encoding="utf-8")
    match = re.search(rf"^## \[{re.escape(short)}\][^\n]*\n(.*?)(?=^## \[|\Z)", text, re.S | re.M)
    body = match.group(1).strip() if match else ""
    return body or f"Release {short}"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--dll", required=True, type=Path)
    ap.add_argument("--version", required=True, help="four-part version, e.g. 1.0.0.0")
    ap.add_argument("--abi", default="12.0.0.0")
    ap.add_argument("--repo", default="YousefMohsen0/jellyboxed")
    ap.add_argument("--out", default=Path("dist"), type=Path)
    ap.add_argument("--manifest", default=Path("manifest.json"), type=Path)
    ap.add_argument("--changelog", default=Path("CHANGELOG.md"), type=Path)
    args = ap.parse_args()

    if not re.fullmatch(r"\d+\.\d+\.\d+\.\d+", args.version):
        print("version must have four parts, e.g. 1.0.0.0", file=sys.stderr)
        return 2
    if not args.dll.exists():
        print(f"DLL not found: {args.dll}", file=sys.stderr)
        return 2

    now = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    changelog = changelog_for(args.version, args.changelog)

    meta = {
        "category": CATEGORY,
        "changelog": changelog,
        "description": DESCRIPTION,
        "guid": PLUGIN_GUID,
        "name": NAME,
        "overview": OVERVIEW,
        "owner": OWNER,
        "targetAbi": args.abi,
        "timestamp": now,
        "version": args.version,
        "status": "Active",
        "autoUpdate": True,
        "assemblies": [args.dll.name],
    }

    args.out.mkdir(parents=True, exist_ok=True)
    zip_path = args.out / f"jellyboxed_{args.version}.zip"
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        z.write(args.dll, args.dll.name)
        z.writestr("meta.json", json.dumps(meta, indent=2) + "\n")

    checksum = hashlib.md5(zip_path.read_bytes()).hexdigest().upper()  # Jellyfin verifies an MD5 of the zip
    tag = "v" + ".".join(args.version.split(".")[:3])
    entry = {
        "version": args.version,
        "changelog": changelog,
        "targetAbi": args.abi,
        "sourceUrl": f"https://github.com/{args.repo}/releases/download/{tag}/{zip_path.name}",
        "checksum": checksum,
        "timestamp": now,
    }

    if args.manifest.exists():
        manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
    else:
        manifest = []
    plugin = next((p for p in manifest if p.get("guid") == PLUGIN_GUID), None)
    if plugin is None:
        plugin = {
            "guid": PLUGIN_GUID,
            "name": NAME,
            "description": DESCRIPTION,
            "overview": OVERVIEW,
            "owner": OWNER,
            "category": CATEGORY,
            "versions": [],
        }
        manifest.append(plugin)
    plugin["versions"] = [v for v in plugin.get("versions", []) if v.get("version") != args.version]
    plugin["versions"].insert(0, entry)  # newest first
    args.manifest.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")

    print(f"Wrote {zip_path} (md5 {checksum}) and updated {args.manifest}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
