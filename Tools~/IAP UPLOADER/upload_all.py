#!/usr/bin/env python3
r"""Run the Apple, Google Play and PlayFab uploads one after another.

Paths and the Key Vault name come from upload_config.json (next to this script, or pass
--config). Relative paths in that file are relative to the config file's folder. The store
credentials are read from Azure Key Vault, so sign in first with `az login` (or use the
window). The first platform that fails stops the whole run, and the platforms after it are
not started.

Everything printed is also written to one log file for the run:
    <log_dir>\upload_all_<date>_<time>.log

Usage:  python upload_all.py --dry-run
        python upload_all.py --only apple --item 0001 0002     (just these items of one platform)
        python upload_all.py --only apple google --apple-item 0001 --google-item wrestler_x
        python upload_all.py
        python upload_all.py --only google playfab
        python upload_all.py --activate --publish
        python upload_all.py --only playfab --publish                          (PlayFab staging title, the default)
        python upload_all.py --only playfab --playfab-target production --publish

PlayFab goes to the staging title unless --playfab-target production is given; production only accepts
items whose exact file was uploaded to staging first (see upload_playfab_items.py). Apple and Google
have no staging stores, so --playfab-target doesn't affect them.

For the DLC Store Export window in Unity (no config file needed):
        python upload_all.py --export <dlc-store-export folder> --vault <name> --log-dir <dir> --progress ...
        python upload_all.py --export <folder> --list          print the items as one "@@items {json}" line
        python upload_all.py --vault <name> --check-vault      print "@@vault {json}": can the signed-in user read it?
--export uses <folder>/apple, <folder>/google and <folder>/playfab. --progress prints "@@platform" and "@@item"
status lines while the run goes (they are not written to the log).
"""
import argparse
import datetime
import glob
import json
import os
import subprocess
import sys
from pathlib import Path

from iap_common import emit, setup_logging
from iap_secrets import APPLE_KEY_ID, PLAYFAB_TARGETS, SecretError, Vault, needed

HERE = Path(__file__).resolve().parent
ORDER = ["apple", "google", "playfab"]
DEFAULTS = {
    "keyvault_name": "",
    "azure_tenant": "",
    "apple_root": "dlc-store-export*/apple",
    "google_input": "dlc-store-export*/google",
    "playfab_input": "dlc-store-export*/playfab",
    "log_dir": "logs",
}
TEXT_KEYS = ("keyvault_name", "azure_tenant")  # settings that are not paths


SCRIPTS = {"apple": "upload_apple_iap.py", "google": "upload_google_iap.py",
           "playfab": "upload_playfab_items.py"}


def resolve_path(value, base):
    """A path relative to `base` (unless absolute). `*` and `?` work: if several folders match,
    the most recently modified one is used. With no match the path is returned as written."""
    p = Path(value).expanduser()
    if not p.is_absolute():
        p = base / p
    if any(c in str(value) for c in "*?"):
        pattern = str(p) if Path(value).expanduser().is_absolute() else \
            glob.escape(str(base)) + os.sep + str(value)
        matches = sorted(glob.glob(pattern), key=os.path.getmtime)
        if matches:
            return Path(matches[-1])
    return p


def load_config(path):
    if not path.exists():
        sys.exit(f"Config file not found: {path}")
    cfg = dict(DEFAULTS)
    cfg.update({k: v for k, v in json.loads(path.read_text(encoding="utf-8-sig")).items() if v})
    base = path.resolve().parent
    out = {}
    for key, value in cfg.items():
        if key in TEXT_KEYS:
            out[key] = str(value).strip()
            continue
        out[key] = resolve_path(value, base)
    return out


def preflight(cfg, platforms, dry_run, playfab_target="staging"):
    """Check every path before anything runs, so a typo doesn't stop us half way through."""
    needs = {"apple": "apple_root", "google": "google_input", "playfab": "playfab_input"}
    problems = []
    for platform in platforms:
        key = needs[platform]
        if not cfg[key].exists():
            problems.append(f"{key}: {cfg[key]} does not exist")
    for platform in platforms:
        if not (HERE / SCRIPTS[platform]).exists():
            problems.append(f"{SCRIPTS[platform]} is missing from {HERE}")
    if not dry_run:  # a dry run sends nothing, so it needs no credentials
        try:
            vault = Vault(cfg["keyvault_name"])
            secrets = [s for p in platforms for s in needed(p, playfab_target)]
            for secret in secrets:
                vault.get(secret)
            print(f"Key Vault {vault.name}: all {len(secrets)} secret(s) readable")
        except SecretError as e:
            problems.append(str(e))
    return problems


def command(platform, cfg, args, log_path):
    cmd = [sys.executable, str(HERE / SCRIPTS[platform])]
    if platform == "apple":
        cmd += ["--root", str(cfg["apple_root"])]
        if args.items.get("apple"):
            cmd += ["--products", *args.items["apple"]]
    elif platform == "google":
        cmd += ["--input", str(cfg["google_input"])]
        if args.items.get("google"):
            cmd += ["--only", *args.items["google"]]
        if args.activate:
            cmd.append("--activate")
    else:
        cmd += ["--input", str(cfg["playfab_input"])]
        if args.items.get("playfab"):
            cmd += ["--only", *args.items["playfab"]]
        if args.publish:
            cmd.append("--publish")
        cmd += ["--target", args.playfab_target]
        if args.skip_staging_check:
            cmd.append("--skip-staging-check")
    if cfg["keyvault_name"]:
        cmd += ["--vault", cfg["keyvault_name"]]
    cmd += ["--log-file", str(log_path)]
    if args.dry_run:
        cmd.append("--dry-run")
    return cmd


def config_for_export(export, vault, log_dir):
    """The settings for one export folder, without a config file (the Unity window's way of running)."""
    export = Path(export).resolve()
    return {"keyvault_name": (vault or "").strip(), "azure_tenant": "",
            "apple_root": export / "apple", "google_input": export / "google", "playfab_input": export / "playfab",
            "log_dir": Path(log_dir).resolve() if log_dir else HERE / "logs"}


def check_vault(name):
    """@@vault line: whether the signed-in Azure CLI user can read the vault. Never prints a secret."""
    if not (name or "").strip():
        print("@@vault " + json.dumps({"ok": False, "message": "no Key Vault name set"}), flush=True)
        return 1
    try:
        vault = Vault(name)
        vault.get(APPLE_KEY_ID)
        result = {"ok": True, "message": f"vault {name}: OK"}
        # Which PlayFab titles the vault can upload to (names only; secrets are never printed).
        for target, (title_secret, _) in PLAYFAB_TARGETS.items():
            try:
                result[f"{target}TitleId"] = vault.get_text(title_secret)
            except SecretError:
                result[f"{target}TitleId"] = ""
    except SecretError as e:
        result = {"ok": False, "message": f"vault {name or '(none)'}: {str(e).splitlines()[0]}"}
    print("@@vault " + json.dumps(result), flush=True)
    return 0 if result["ok"] else 1


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawTextHelpFormatter)
    ap.add_argument("--config", type=Path, default=HERE / "upload_config.json",
                    help="paths file (default: upload_config.json next to this script)")
    ap.add_argument("--export", type=Path, help="a dlc-store-export folder; replaces the config file's paths")
    ap.add_argument("--vault", help="Key Vault name (with --export, or to override the config)")
    ap.add_argument("--log-dir", type=Path, help="log folder (with --export, or to override the config)")
    ap.add_argument("--list", action="store_true", help='print the items as one "@@items {json}" line and exit')
    ap.add_argument("--check-vault", action="store_true", help='print "@@vault {json}" and exit')
    ap.add_argument("--progress", action="store_true", help="print @@platform / @@item status lines")
    ap.add_argument("--only", nargs="+", choices=ORDER, metavar="PLATFORM",
                    help="run just these: apple google playfab (they still run in that order)")
    ap.add_argument("--item", nargs="+", metavar="ID",
                    help="upload only these items; needs exactly one platform in --only.\n"
                         "Apple: folder names (0001), Google: product IDs, PlayFab: FriendlyIds")
    for name in ORDER:
        ap.add_argument(f"--{name}-item", nargs="+", metavar="ID",
                        help=f"like --item, but just for {name}; can be combined across platforms")
    ap.add_argument("--activate", action="store_true", help="Google: make purchase options active")
    ap.add_argument("--publish", action="store_true", help="PlayFab: publish items instead of leaving drafts")
    ap.add_argument("--playfab-target", choices=sorted(PLAYFAB_TARGETS), default="staging",
                    help="PlayFab title to upload to (default: staging)")
    ap.add_argument("--skip-staging-check", action="store_true",
                    help="PlayFab production: don't require a matching staging upload first")
    ap.add_argument("--dry-run", action="store_true", help="validate and print only, nothing is sent")
    args = ap.parse_args()

    if args.item and not (args.only and len(args.only) == 1):
        ap.error("--item needs exactly one platform, e.g. --only apple --item 0001")
    args.items = {p: getattr(args, f"{p}_item") for p in ORDER if getattr(args, f"{p}_item")}
    if args.item:
        args.items[args.only[0]] = args.item

    if args.check_vault:
        vault = args.vault if args.vault is not None else (load_config(args.config)["keyvault_name"] if not args.export else "")
        sys.exit(check_vault(vault))
    if args.export:
        cfg = config_for_export(args.export, args.vault, args.log_dir)
    else:
        cfg = load_config(args.config)
        if args.vault is not None:
            cfg["keyvault_name"] = args.vault.strip()
        if args.log_dir:
            cfg["log_dir"] = args.log_dir.resolve()
    if args.list:
        from iap_items import load_items
        print("@@items " + json.dumps(load_items(cfg["apple_root"], cfg["google_input"], cfg["playfab_input"])), flush=True)
        return
    if args.progress:
        os.environ["IAP_PROGRESS"] = "1"  # inherited by the platform scripts, read by iap_common
        import iap_common
        iap_common.PROGRESS = True

    log_path = cfg["log_dir"] / f"upload_all_{datetime.datetime.now():%Y-%m-%d_%H-%M-%S}.log"
    setup_logging("upload_all", log_path)

    platforms = [p for p in ORDER if not args.only or p in args.only]
    print(f"Export:    {args.export}" if args.export else f"Config:    {args.config}")
    print(f"Platforms: {', '.join(platforms)}" + ("   (dry run)" if args.dry_run else ""))
    if "playfab" in platforms:
        print(f"PlayFab:   {args.playfab_target.upper()} title")
    for platform, key in (("apple", "apple_root"), ("google", "google_input"), ("playfab", "playfab_input")):
        if platform in platforms:
            print(f"  {platform:<8} {cfg[key]}")

    problems = preflight(cfg, platforms, args.dry_run, args.playfab_target)
    if problems:
        print("\nCannot start:")
        for p in problems:
            print(f"  - {p}")
        sys.exit(1)

    completed = []
    for platform in platforms:
        print(f"\n######## {platform.upper()} ########", flush=True)
        emit("platform", platform=platform, state="running")
        result = subprocess.run(command(platform, cfg, args, log_path))
        if result.returncode != 0:
            emit("platform", platform=platform, state="failed")
            for later in platforms[platforms.index(platform) + 1:]:
                emit("platform", platform=later, state="not run")
            print(f"\nSTOPPED: {platform} failed (exit code {result.returncode}).")
            if completed:
                print(f"Already finished before the failure: {', '.join(completed)}")
            not_run = platforms[platforms.index(platform) + 1:]
            if not_run:
                print(f"Not run: {', '.join(not_run)}")
            print(f"Log: {log_path}")
            sys.exit(1)
        emit("platform", platform=platform, state="dry run OK" if args.dry_run else "done")
        completed.append(platform)

    print(f"\nAll done: {', '.join(completed)}" + ("  (dry run, nothing sent)" if args.dry_run else ""))
    print(f"Log: {log_path}")


if __name__ == "__main__":
    main()
