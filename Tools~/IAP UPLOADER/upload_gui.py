r"""A small window for the upload tools.

Shows every product that will be uploaded (read from the folders in upload_config.json),
with buttons to upload to Apple, Google Play, PlayFab, or all three. It runs upload_all.py
in the background and shows its output live, so every rule of the command-line tools
applies: existing items are updated, the first failure stops the run, a log file is written.

The store credentials are read from Azure Key Vault. Use "Sign in to Azure" in the window
(it runs `az login`) before a real upload; the line under the title shows who is signed in
and whether the vault can be read. Needs the Azure CLI plus: pip install azure-identity
azure-keyvault-secrets

Usage:  python upload_gui.py
        python upload_gui.py --config D:\other\upload_config.json
"""
import argparse
import json
import os
import queue
import re
import shutil
import subprocess
import sys
import threading
import tkinter as tk
from pathlib import Path
from tkinter import filedialog, messagebox, ttk

from iap_secrets import APPLE_KEY_ID, SecretError, Vault
from upload_all import DEFAULTS, ORDER, load_config, resolve_path

HERE = Path(__file__).resolve().parent
LABEL = {"apple": "Apple App Store", "google": "Google Play", "playfab": "PlayFab"}
SUMMARY_NAMES = {"Apple": "apple", "Google Play": "google", "PlayFab": "playfab"}
SECTION_STATUS = {
    "Created new": "Created",
    "Already existed, updated": "Updated",
    "FAILED (run stopped here)": "FAILED",
    "Not processed because of the failure": "Not processed",
}
SECTION_RE = re.compile(r"^(Created new|Already existed, updated|FAILED \(run stopped here\)|"
                        r"Not processed because of the failure|Notes) \(\d+\):$")
PLATFORM_RE = re.compile(r"^===== (Apple|Google Play|PlayFab) summary =====$")
START_RE = re.compile(r"^######## (APPLE|GOOGLE|PLAYFAB) ########$")


CONFIG_FIELDS = [  # (key in upload_config.json, label, what can be picked)
    ("keyvault_name", "Azure Key Vault name", "text"),
    ("azure_tenant", "Azure tenant (optional, for guest accounts)", "text"),
    ("apple_root", "Apple folder (contains 0001, 0002, ...)", "dir"),
    ("google_input", "Google batch JSON (file or folder)", "either"),
    ("playfab_input", "PlayFab folder", "dir"),
    ("log_dir", "Log folder", "dir"),
]
NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0)
UNCHECKED, CHECKED = "\u2610", "\u2611"  # ballot box, ballot box with check


# ------------------------------------------------------------- signing in to Azure
def find_az():
    return shutil.which("az") or shutil.which("az.cmd")


def _run_az(args, timeout=60):
    return subprocess.run([find_az(), *args], capture_output=True, text=True, encoding="utf-8",
                          errors="replace", timeout=timeout, creationflags=NO_WINDOW)


def azure_account():
    """(signed-in user, None) or (None, reason)."""
    if not find_az():
        return None, "Azure CLI not installed (run: winget install Microsoft.AzureCLI)"
    try:
        r = _run_az(["account", "show", "--query", "user.name", "-o", "tsv"])
    except (OSError, subprocess.TimeoutExpired) as e:
        return None, f"could not run the Azure CLI ({e})"
    user = r.stdout.strip()
    return (user, None) if r.returncode == 0 and user else (None, "not signed in")


def azure_login(tenant=""):
    """Opens the browser sign-in. Returns (ok, message)."""
    if not find_az():
        return False, "Azure CLI not installed (run: winget install Microsoft.AzureCLI)"
    args = ["login", "--allow-no-subscriptions"] + (["--tenant", tenant] if tenant else [])
    try:
        r = _run_az(args, timeout=300)
    except subprocess.TimeoutExpired:
        return False, "Sign-in timed out after 5 minutes."
    except OSError as e:
        return False, f"Could not run the Azure CLI: {e}"
    return r.returncode == 0, (r.stderr.strip() or r.stdout.strip())[-600:]


def azure_logout():
    if not find_az():
        return
    try:
        _run_az(["logout"])
    except (OSError, subprocess.TimeoutExpired):
        pass


def azure_status(vault_name):
    """(ok, one-line text) describing the sign-in and whether the vault can be read."""
    user, why = azure_account()
    if not user:
        return False, f"Azure: {why}"
    if not vault_name:
        return False, f"Azure: signed in as {user}   |   no vault name set (Settings...)"
    try:
        Vault(vault_name).get(APPLE_KEY_ID)
    except SecretError as e:
        return False, f"Azure: signed in as {user}   |   vault {vault_name}: {str(e).splitlines()[0]}"
    return True, f"Azure: signed in as {user}   |   vault {vault_name}: OK"


# ------------------------------------------------------------- editing the config
def read_config_raw(path):
    path = Path(path)
    return json.loads(path.read_text(encoding="utf-8-sig")) if path.exists() else {}


def portable_path(value, base):
    """Store a path relative to the config file's folder when it's inside it."""
    p = Path(value)
    try:
        return p.resolve().relative_to(Path(base).resolve()).as_posix()
    except ValueError:
        return p.as_posix()


def save_config(path, values):
    """Write the chosen values, keeping any other keys already in the file."""
    data = read_config_raw(path)
    data.update({k: v for k, v in values.items()})
    Path(path).write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")


# ------------------------------------------------------------- reading the products
def _read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def _money(price):
    value = int(price.get("units", 0)) + int(price.get("nanos", 0)) / 1e9
    return f"{value:.2f} {price.get('currencyCode', '')}".strip()


def load_apple(root):
    items = []
    folders = sorted(p for p in Path(root).iterdir() if p.is_dir() and re.fullmatch(r"\d{4}", p.name)) \
        if Path(root).is_dir() else []
    for folder in folders:
        try:
            create = _read(next(iter(sorted(folder.glob("*-create.json")))))
            attrs = create["data"]["attributes"]
            details = [attrs.get("inAppPurchaseType", "")]
            price_file = next(iter(sorted(folder.glob("*-pricepoint.json"))), None)
            if price_file:
                spec = _read(price_file)
                details.append(f"{spec.get('customerPrice')} ({spec.get('territory', 'USA')})")
            problem = None
            reserve = next(iter(sorted(folder.glob("*-screenshot-reserve.json"))), None)
            if reserve:
                shot_name = _read(reserve)["data"]["attributes"]["fileName"]
                if not (folder / shot_name).exists():
                    problem = f"screenshot file missing: {shot_name}"
            items.append({"key": f"{folder.name} ({attrs['productId']})", "id": attrs["productId"],
                          "name": attrs.get("name", ""), "details": ", ".join(d for d in details if d),
                          "problem": problem, "arg": folder.name})
        except Exception as e:  # a broken folder is shown, not hidden
            items.append({"key": folder.name, "id": folder.name, "name": "", "details": "",
                          "problem": f"cannot read ({type(e).__name__}: {e})"})
    return items


def load_google(target):
    target = Path(target)
    files = [target] if target.is_file() else (sorted(target.glob("*.json")) if target.is_dir() else [])
    items = []
    for f in files:
        try:
            data = _read(f)
            requests_ = data.get("requests") if isinstance(data, dict) else None
            if not isinstance(requests_, list):
                continue
            for req in requests_:
                prod = req["oneTimeProduct"]
                title = (prod.get("listings") or [{}])[0].get("title", "")
                prices = [f"{c.get('regionCode')} {_money(c['price'])}"
                          for o in prod.get("purchaseOptions", [])
                          for c in o.get("regionalPricingAndAvailabilityConfigs", []) if "price" in c]
                items.append({"key": prod["productId"], "id": prod["productId"], "name": title,
                              "details": ", ".join(prices), "problem": None,
                              "arg": prod["productId"]})
        except Exception as e:
            items.append({"key": f.name, "id": f.name, "name": "", "details": "",
                          "problem": f"cannot read ({type(e).__name__}: {e})"})
    return items


def load_playfab(target):
    target = Path(target)
    files = [target] if target.is_file() else (sorted(target.rglob("*.json")) if target.is_dir() else [])
    items = []
    for f in files:
        if "credentials" in f.name.lower():
            continue
        try:
            item = _read(f)["Item"]
            fid = next(a["Value"] for a in item["AlternateIds"] if a.get("Type") == "FriendlyId")
            items.append({"key": fid, "id": fid, "name": item.get("Title", {}).get("NEUTRAL", ""),
                          "details": item.get("ContentType", ""), "problem": None, "arg": fid})
        except Exception as e:
            items.append({"key": f.name, "id": f.name, "name": "", "details": "",
                          "problem": f"cannot read ({type(e).__name__}: {e})"})
    return items


def load_items(cfg):
    return {"apple": load_apple(cfg["apple_root"]),
            "google": load_google(cfg["google_input"]),
            "playfab": load_playfab(cfg["playfab_input"])}


# --------------------------------------------------- reading the live output
class StatusParser:
    """Turns the scripts' printed summaries into per-item status updates."""

    def __init__(self):
        self.platform = None
        self.section = None

    def feed(self, line):
        """Returns a list of events: ("start", platform), ("failed", platform) or
        ("item", platform, name, status)."""
        line = line.rstrip()
        m = START_RE.match(line)
        if m:
            self.platform, self.section = m.group(1).lower(), None
            return [("start", self.platform)]
        m = PLATFORM_RE.match(line)
        if m:
            self.platform, self.section = SUMMARY_NAMES[m.group(1)], None
            return []
        m = SECTION_RE.match(line)
        if m and self.platform:
            self.section = m.group(1)
            return [("failed", self.platform)] if SECTION_STATUS.get(self.section) == "FAILED" else []
        if line.startswith("Finished:"):
            self.section = None
            return []
        m = re.match(r"^  - (.*)$", line)
        if m and self.platform and self.section in SECTION_STATUS and m.group(1) != "(none)":
            return [("item", self.platform, m.group(1), SECTION_STATUS[self.section])]
        return []


def match_item(items, name):
    for item in items:
        key = item["key"]
        if name == key or name.startswith(key + ":") or name.startswith(key + " "):
            return item
    return None


def build_command(config_path, platforms, dry_run, activate, publish, items=None):
    """`items` is None (everything) or {platform: [item ids]} for a partial upload."""
    cmd = [sys.executable, "-u", str(HERE / "upload_all.py"), "--config", str(config_path)]
    if set(platforms) != set(ORDER):
        cmd += ["--only", *[p for p in ORDER if p in platforms]]
    for platform in ORDER:
        if items and items.get(platform):
            cmd += [f"--{platform}-item", *items[platform]]
    if dry_run:
        cmd.append("--dry-run")
    if activate and "google" in platforms:
        cmd.append("--activate")
    if publish and "playfab" in platforms:
        cmd.append("--publish")
    return cmd


# --------------------------------------------------------------------------- window
class App:
    def __init__(self, root, config_path):
        self.root, self.config_path = root, Path(config_path)
        self.items, self.rows, self.groups = {}, {}, {}
        self.checked, self.iid_item, self.group_platform = set(), {}, {}
        self.group_state = {}
        self.queue, self.proc, self.running, self.stopping = queue.Queue(), None, False, False
        self.parser, self.run_platforms, self.run_dry = StatusParser(), [], False
        self.run_only = None

        root.title("IAP Uploader")
        root.geometry("1050x720")
        root.minsize(800, 520)
        root.protocol("WM_DELETE_WINDOW", self.on_close)

        top = ttk.Frame(root, padding=(10, 8))
        top.pack(fill="x")
        ttk.Label(top, text=f"Config: {self.config_path}").pack(side="left")
        ttk.Button(top, text="Reload products", command=self.reload).pack(side="right")
        self.settings_button = ttk.Button(top, text="Settings...", command=self.open_settings)
        self.settings_button.pack(side="right", padx=6)

        azure = ttk.Frame(root, padding=(10, 0, 10, 6))
        azure.pack(fill="x")
        self.azure_var = tk.StringVar(value="Azure: checking...")
        self.azure_label = ttk.Label(azure, textvariable=self.azure_var)
        self.azure_label.pack(side="left")
        self.signout_button = ttk.Button(azure, text="Sign out", command=lambda: self.azure_action("logout"))
        self.signout_button.pack(side="right")
        self.recheck_button = ttk.Button(azure, text="Check again", command=lambda: self.azure_action("check"))
        self.recheck_button.pack(side="right", padx=6)
        self.signin_button = ttk.Button(azure, text="Sign in to Azure", command=lambda: self.azure_action("login"))
        self.signin_button.pack(side="right")
        self.azure_buttons = [self.signin_button, self.recheck_button, self.signout_button]
        self.azure_busy = False

        bar = ttk.Frame(root, padding=(10, 0, 10, 4))
        bar.pack(fill="x")
        self.master_var = tk.BooleanVar()
        self.master_check = ttk.Checkbutton(bar, text="Select all products", variable=self.master_var,
                                            command=self.on_master)
        self.master_check.pack(side="left")
        ttk.Label(bar, text="   Click the box in the Upload column (or press Space) to tick products.").pack(
            side="left")

        body = ttk.Frame(root, padding=(10, 0))
        body.pack(fill="both", expand=True)
        columns = ("sel", "id", "name", "details", "status")
        self.tree = ttk.Treeview(body, columns=columns, show="tree headings", height=12)
        self.tree.heading("#0", text="Store")
        for col, text, width in (("sel", "Upload", 60), ("id", "Product ID", 230), ("name", "Name", 200),
                                 ("details", "Details", 260), ("status", "Status", 130)):
            self.tree.heading(col, text=text)
            self.tree.column(col, width=width, anchor="center" if col == "sel" else "w")
        self.tree.column("#0", width=170)
        scroll = ttk.Scrollbar(body, orient="vertical", command=self.tree.yview)
        self.tree.configure(yscrollcommand=scroll.set)
        self.tree.bind("<Button-3>", self.on_right_click)
        self.tree.bind("<Button-1>", self.on_left_click)
        self.tree.bind("<space>", self.on_space)
        self.tree.pack(side="left", fill="both", expand=True)
        scroll.pack(side="left", fill="y")
        for tag, color in (("ok", "#1a7f37"), ("fail", "#cf222e"), ("run", "#0969da"),
                           ("muted", "#6e7781"), ("group", "#000000")):
            self.tree.tag_configure(tag, foreground=color)

        buttons = ttk.Frame(root, padding=(10, 8))
        buttons.pack(fill="x")
        self.buttons = []
        for platform in ORDER:
            b = ttk.Button(buttons, text=f"Upload to {LABEL[platform]}",
                           command=lambda p=platform: self.start([p]))
            b.pack(side="left", padx=(0, 6))
            self.buttons.append(b)
        b = ttk.Button(buttons, text="Upload ALL", command=lambda: self.start(list(ORDER)))
        b.pack(side="left", padx=(12, 6))
        self.buttons.append(b)
        self.selected_button = ttk.Button(buttons, text="Upload selected (0)", command=self.start_selected,
                                          state="disabled")
        self.selected_button.pack(side="left", padx=(6, 0))
        self.buttons.append(self.settings_button)
        self.buttons += self.azure_buttons
        self.stop_button = ttk.Button(buttons, text="Stop", command=self.stop, state="disabled")
        self.stop_button.pack(side="left", padx=(12, 0))
        self.clear_button = ttk.Button(buttons, text="Clear log", command=self.clear_log)
        self.clear_button.pack(side="right")

        options = ttk.Frame(root, padding=(10, 0, 10, 6))
        options.pack(fill="x")
        self.dry_var, self.activate_var, self.publish_var = tk.BooleanVar(), tk.BooleanVar(), tk.BooleanVar()
        ttk.Checkbutton(options, text="Dry run (check only, send nothing)",
                        variable=self.dry_var).pack(side="left")
        ttk.Checkbutton(options, text="Google: make purchase options active",
                        variable=self.activate_var).pack(side="left", padx=16)
        ttk.Checkbutton(options, text="PlayFab: publish items",
                        variable=self.publish_var).pack(side="left")

        logframe = ttk.Frame(root, padding=(10, 0, 10, 4))
        logframe.pack(fill="both", expand=True)
        self.log = tk.Text(logframe, height=12, wrap="none", state="disabled",
                           font=("Consolas", 9), background="#f6f8fa")
        logscroll = ttk.Scrollbar(logframe, orient="vertical", command=self.log.yview)
        self.log.configure(yscrollcommand=logscroll.set)
        self.log.pack(side="left", fill="both", expand=True)
        logscroll.pack(side="left", fill="y")
        self.log.tag_configure("bad", foreground="#cf222e")
        self.log.tag_configure("good", foreground="#1a7f37")

        self.status = tk.StringVar(value="Ready")
        ttk.Label(root, textvariable=self.status, padding=(10, 2, 10, 8)).pack(fill="x")

        self.reload()
        self.root.after(100, self.poll)
        self.azure_action("check")

    # ---- azure
    def config_value(self, key):
        try:
            return str(read_config_raw(self.config_path).get(key) or "").strip()
        except (OSError, ValueError):
            return ""

    def azure_action(self, action):
        if self.azure_busy or self.running:
            return
        self.azure_busy = True
        for b in self.azure_buttons:
            b.configure(state="disabled")
        vault, tenant = self.config_value("keyvault_name"), self.config_value("azure_tenant")
        self.azure_var.set("Azure: waiting for the browser sign-in..." if action == "login"
                           else "Azure: checking...")
        threading.Thread(target=self.azure_worker, args=(action, vault, tenant), daemon=True).start()

    def azure_worker(self, action, vault, tenant):
        message = None
        try:
            if action == "login":
                ok, detail = azure_login(tenant)
                message = "Signed in to Azure." if ok else f"Azure sign-in failed:\n{detail}"
            elif action == "logout":
                azure_logout()
            result = azure_status(vault)
        except Exception as e:  # never leave the buttons disabled
            result, message = (False, "Azure: check failed"), f"Azure check failed: {e}"
        self.queue.put(("azure", (result, message)))

    def on_azure(self, payload):
        (ok, text), message = payload
        self.azure_var.set(text)
        self.azure_label.configure(foreground="#1a7f37" if ok else "#cf222e")
        if message:
            self.write(message + "\n", None if message.startswith("Signed in") else "bad")
        self.azure_busy = False
        if not self.running:
            for b in self.azure_buttons:
                b.configure(state="normal")

    # ---- settings
    def open_settings(self):
        if self.running:
            return
        raw = read_config_raw(self.config_path)
        base = self.config_path.parent
        win = tk.Toplevel(self.root)
        win.title("Settings")
        win.transient(self.root)
        win.grab_set()
        frame = ttk.Frame(win, padding=12)
        frame.pack(fill="both", expand=True)
        ttk.Label(frame, text=f"Saved to {self.config_path}").grid(row=0, column=0, columnspan=4,
                                                                  sticky="w", pady=(0, 8))
        variables = {}

        def browse(key, kind):
            current = base / (variables[key].get().strip() or ".")
            start = str(current if current.is_dir() else current.parent if current.parent.exists() else base)
            if kind == "dir":
                chosen = filedialog.askdirectory(parent=win, initialdir=start)
            else:
                chosen = filedialog.askopenfilename(parent=win, initialdir=start,
                                                    filetypes=[("JSON files", "*.json"), ("All files", "*.*")])
            if chosen:
                variables[key].set(portable_path(chosen, base))

        for row, (key, label, kind) in enumerate(CONFIG_FIELDS, start=1):
            ttk.Label(frame, text=label).grid(row=row, column=0, sticky="w", pady=3)
            variables[key] = tk.StringVar(value=str(raw.get(key) or DEFAULTS[key]))
            ttk.Entry(frame, textvariable=variables[key], width=58).grid(row=row, column=1, padx=6)
            if kind == "text":
                continue
            if kind == "either":
                ttk.Button(frame, text="Folder...", command=lambda k=key: browse(k, "dir")).grid(row=row, column=2)
                ttk.Button(frame, text="File...", command=lambda k=key: browse(k, "file")).grid(row=row, column=3, padx=(4, 0))
            else:
                ttk.Button(frame, text="Browse...", command=lambda k=key, kd=kind: browse(k, kd)).grid(
                    row=row, column=2, columnspan=2, sticky="w")

        def save():
            if self.apply_settings({k: v.get() for k, v in variables.items()}):
                win.destroy()

        buttons = ttk.Frame(frame)
        buttons.grid(row=len(CONFIG_FIELDS) + 1, column=0, columnspan=4, sticky="e", pady=(12, 0))
        ttk.Button(buttons, text="Cancel", command=win.destroy).pack(side="right")
        ttk.Button(buttons, text="Save", command=save).pack(side="right", padx=6)

    def apply_settings(self, values):
        """Validate and save the settings, then reload the product list. True if saved."""
        values = {k: (v.strip() or DEFAULTS[k]) for k, v in values.items()}
        base = self.config_path.parent
        missing = [f"{k}: {values[k]}" for k, _, kind in CONFIG_FIELDS
                   if k != "log_dir" and kind != "text" and not resolve_path(values[k], base).exists()]
        if missing and not messagebox.askyesno(
                "Paths not found", "These paths don't exist (yet):\n\n" + "\n".join(missing)
                + "\n\nSave anyway?"):
            return False
        try:
            save_config(self.config_path, values)
        except OSError as e:
            messagebox.showerror("Could not save", str(e))
            return False
        self.reload()
        self.status.set("Settings saved")
        self.azure_action("check")
        return True

    # ---- product list
    def reload(self):
        if self.running:
            return
        try:
            self.items = load_items(load_config(self.config_path))
        except SystemExit as e:
            messagebox.showerror("Config problem", str(e))
            return
        self.tree.delete(*self.tree.get_children())
        self.rows, self.groups = {}, {}
        self.checked, self.iid_item, self.group_platform = set(), {}, {}
        for platform in ORDER:
            items = self.items[platform]
            group = self.tree.insert("", "end", text=LABEL[platform], open=True, tags=("group",),
                                     values=(UNCHECKED, "", f"{len(items)} product(s)", "", "Pending"))
            self.groups[platform], self.rows[platform] = group, []
            self.group_platform[group] = platform
            self.group_state[platform] = "idle"
            for item in items:
                iid = self.tree.insert(group, "end", values=("" if item["problem"] else UNCHECKED, item["id"],
                                                             item["name"], item["details"],
                                                             item["problem"] or "Pending"),
                                       tags=("fail",) if item["problem"] else ())
                self.rows[platform].append((iid, item))
                self.iid_item[iid] = (platform, item)
        self.refresh_selection_ui()
        self.status.set("Ready")

    def set_status(self, iid, text, tag=None):
        self.tree.set(iid, "status", text)
        if tag is not None:
            self.tree.item(iid, tags=(tag,) if tag else ())

    # ---- running
    def on_right_click(self, event):
        """Right-click a product (or several selected ones) to upload just those."""
        if self.running:
            return
        iid = self.tree.identify_row(event.y)
        if not iid:
            return
        if iid not in self.tree.selection():
            self.tree.selection_set(iid)
        menu = tk.Menu(self.root, tearoff=0)
        platform = next((p for p, g in self.groups.items() if g == iid), None)
        if platform:  # the store heading
            menu.add_command(label=f"Upload all to {LABEL[platform]}", command=lambda: self.start([platform]))
        else:
            picked = {}
            for sel in self.tree.selection():
                for p, rows in self.rows.items():
                    for row_iid, item in rows:
                        if row_iid == sel:
                            picked.setdefault(p, []).append(item)
            if len(picked) != 1:
                menu.add_command(label="Select products from one store only", state="disabled")
            else:
                (platform, chosen), = picked.items()
                bad = [i for i in chosen if i["problem"]]
                if bad:
                    menu.add_command(label="Can't upload: a selected product has a problem", state="disabled")
                else:
                    count = len(chosen)
                    label = (f"Upload '{chosen[0]['id']}' only to {LABEL[platform]}" if count == 1
                             else f"Upload these {count} products only to {LABEL[platform]}")
                    menu.add_command(label=label, command=lambda: self.start(
                        [platform], {platform: [i["arg"] for i in chosen]}))
        try:
            menu.tk_popup(event.x_root, event.y_root)
        finally:
            menu.grab_release()

    # ---- ticking products
    def on_left_click(self, event):
        if self.tree.identify_region(event.x, event.y) != "cell" or self.tree.identify_column(event.x) != "#1":
            return None
        iid = self.tree.identify_row(event.y)
        if iid and not self.running:
            self.toggle(iid)
        return "break"

    def on_space(self, event):
        if not self.running:
            for iid in self.tree.selection():
                self.toggle(iid)
        return "break"

    def set_checked(self, iid, value):
        found = self.iid_item.get(iid)
        if found and not found[1]["problem"]:
            (self.checked.add if value else self.checked.discard)(iid)
            self.tree.set(iid, "sel", CHECKED if value else UNCHECKED)

    def uploadable(self, platform):
        return [iid for iid, item in self.rows.get(platform, []) if not item["problem"]]

    def toggle(self, iid):
        platform = self.group_platform.get(iid)
        if platform:
            kids = self.uploadable(platform)
            new = not (kids and all(k in self.checked for k in kids))
            for k in kids:
                self.set_checked(k, new)
        else:
            self.set_checked(iid, iid not in self.checked)
        self.refresh_selection_ui()

    def on_master(self):
        value = self.master_var.get()
        for platform in ORDER:
            for iid in self.uploadable(platform):
                self.set_checked(iid, value)
        self.refresh_selection_ui()

    def refresh_selection_ui(self):
        """Update the group boxes, the Select all box and the Upload selected button."""
        every = []
        for platform in ORDER:
            kids = self.uploadable(platform)
            every += kids
            if platform in self.groups:
                full = bool(kids) and all(k in self.checked for k in kids)
                self.tree.set(self.groups[platform], "sel", CHECKED if full else UNCHECKED)
        self.master_var.set(bool(every) and all(k in self.checked for k in every))
        count = len(self.checked)
        self.selected_button.configure(text=f"Upload selected ({count})",
                                       state="normal" if count and not self.running else "disabled")

    def start_selected(self):
        chosen = {}
        for platform in ORDER:
            args = [item["arg"] for iid, item in self.rows.get(platform, []) if iid in self.checked]
            if args:
                chosen[platform] = args
        if chosen:
            self.start([p for p in ORDER if p in chosen], chosen)

    def start(self, platforms, only=None):
        """Upload the given stores. `only` is {store: [item ids]} for a partial upload."""
        if self.running:
            return
        empty = [LABEL[p] for p in platforms if not self.items.get(p)]
        if empty:
            messagebox.showwarning("Nothing to upload", f"No products were found for: {', '.join(empty)}.")
            return
        dry = self.dry_var.get()
        self.run_only = {p: set(v) for p, v in only.items()} if only else None
        if not dry:
            total = sum(len(v) for v in only.values()) if only else sum(len(self.items[p]) for p in platforms)
            names = ", ".join(LABEL[p] for p in platforms)
            if not messagebox.askyesno(
                    "Confirm upload",
                    f"Upload {total} item(s) to {names}?\n\nThis creates or updates the products for real.\n"
                    "Tick 'Dry run' first if you only want to check the files."):
                return
        for p in platforms:
            self.group_state[p] = "waiting"
            self.set_status(self.groups[p], "Waiting", "muted")
            for iid, item in self.rows[p]:
                if not item["problem"] and self.in_run(p, item):
                    self.set_status(iid, "Waiting", "muted")
        self.parser, self.run_platforms, self.run_dry = StatusParser(), list(platforms), dry
        cmd = build_command(self.config_path, platforms, dry, self.activate_var.get(), self.publish_var.get(),
                            only)
        what = f"{sum(len(v) for v in only.values())} selected item(s) to " if only else ""
        self.write(f">>> {'Dry run: ' if dry else ''}{what}{', '.join(LABEL[p] for p in platforms)}\n")
        self.set_running(True)
        self.stopping = False
        threading.Thread(target=self.worker, args=(cmd,), daemon=True).start()

    def in_run(self, platform, item):
        return self.run_only is None or item.get("arg") in self.run_only.get(platform, ())

    def worker(self, cmd):
        env = dict(os.environ, PYTHONUNBUFFERED="1", PYTHONIOENCODING="utf-8")
        flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
        try:
            self.proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                                         encoding="utf-8", errors="replace", env=env, cwd=str(HERE),
                                         creationflags=flags)
            for line in self.proc.stdout:
                self.queue.put(("line", line.rstrip("\n")))
            self.queue.put(("done", self.proc.wait()))
        except Exception as e:
            self.queue.put(("line", f"Could not start the upload: {e}"))
            self.queue.put(("done", 1))

    def stop(self):
        if not (self.running and self.proc):
            return
        if not messagebox.askyesno("Stop", "Stop the upload now?\n\nThe item being uploaded may be left "
                                           "half done. Running the upload again will finish it."):
            return
        self.stopping = True
        try:
            if os.name == "nt":  # also stop the script the wrapper started
                subprocess.run(["taskkill", "/PID", str(self.proc.pid), "/T", "/F"],
                               creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
            else:
                self.proc.terminate()
        except Exception as e:
            self.write(f"Could not stop: {e}\n")

    def poll(self):
        try:
            while True:
                kind, value = self.queue.get_nowait()
                if kind == "line":
                    self.on_line(value)
                elif kind == "azure":
                    self.on_azure(value)
                else:
                    self.on_done(value)
        except queue.Empty:
            pass
        self.root.after(100, self.poll)

    def on_line(self, line):
        tag = "bad" if re.search(r"FAILED|STOPPED|Cannot start|Traceback|Error", line) else (
            "good" if line.startswith(("All done", "Created new")) else None)
        self.write(line + "\n", tag)
        for event in self.parser.feed(line):
            if event[0] == "start":
                self.group_state[event[1]] = "running"
                self.set_status(self.groups[event[1]], "Running...", "run")
                for iid, item in self.rows[event[1]]:
                    if not item["problem"] and self.in_run(event[1], item):
                        self.set_status(iid, "Running...", "run")
            elif event[0] == "failed":
                self.group_state[event[1]] = "failed"
                self.set_status(self.groups[event[1]], "FAILED", "fail")
            else:
                _, platform, name, status = event
                item = match_item(self.items[platform], name)
                if item:
                    iid = next(i for i, it in self.rows[platform] if it is item)
                    self.set_status(iid, status, {"Created": "ok", "Updated": "ok", "FAILED": "fail"}.get(
                        status, "muted"))
        if line.startswith("Log: "):
            self.status.set(line)

    def on_done(self, code):
        for p in self.run_platforms:
            state = self.group_state.get(p)
            if code == 0:
                group_text, item_text, tag = (("Checked" if self.run_dry else "Done"),) * 2 + ("ok",)
            elif state in ("failed", "running"):  # it failed, or died before printing a summary
                group_text, item_text, tag = "FAILED", "Not processed", "fail"
            else:
                group_text, item_text, tag = "Not run", "Not run", "muted"
            self.set_status(self.groups[p], group_text, tag)
            for iid, item in self.rows[p]:
                if self.tree.set(iid, "status") in ("Waiting", "Running..."):
                    self.set_status(iid, item_text, "ok" if code == 0 else "muted")
        if self.stopping:
            self.write("Stopped by you.\n", "bad")
        self.write(f">>> finished (exit code {code})\n", "good" if code == 0 else "bad")
        self.status.set("Finished" if code == 0 else "Stopped: see the log above")
        self.set_running(False)

    def set_running(self, running):
        self.running = running
        for b in self.buttons:
            b.configure(state="disabled" if running or (self.azure_busy and b in self.azure_buttons)
                        else "normal")
        self.stop_button.configure(state="normal" if running else "disabled")
        self.master_check.configure(state="disabled" if running else "normal")
        self.refresh_selection_ui()
        if running:
            self.status.set("Running...")

    def clear_log(self):
        """Empty the output window (the log files on disk are not touched)."""
        self.log.configure(state="normal")
        self.log.delete("1.0", "end")
        self.log.configure(state="disabled")

    def write(self, text, tag=None):
        self.log.configure(state="normal")
        self.log.insert("end", text, (tag,) if tag else ())
        self.log.see("end")
        self.log.configure(state="disabled")

    def on_close(self):
        if self.running:
            if not messagebox.askyesno("Upload running", "An upload is still running. Stop it and close?"):
                return
            self.stopping = True
            try:
                if os.name == "nt":
                    subprocess.run(["taskkill", "/PID", str(self.proc.pid), "/T", "/F"],
                                   creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
                elif self.proc:
                    self.proc.terminate()
            except Exception:
                pass
        self.root.destroy()


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawTextHelpFormatter)
    ap.add_argument("--config", type=Path, default=HERE / "upload_config.json",
                    help="paths file (default: upload_config.json next to this script)")
    args = ap.parse_args()
    try:
        root = tk.Tk()
        App(root, args.config.resolve())
        root.mainloop()
    except Exception:  # started without a console (double-click), so show the error instead
        import traceback
        text = traceback.format_exc()
        try:
            (HERE / "gui_error.log").write_text(text, encoding="utf-8")
            messagebox.showerror("IAP Uploader could not start",
                                 text[-1500:] + "\n\n(also saved to gui_error.log)")
        except Exception:
            raise


if __name__ == "__main__":
    main()
