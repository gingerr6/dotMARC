#!/usr/bin/env python3
"""Stand up dotMARC on a DigitalOcean Droplet in one command.

Creates (or reuses, by name) a Droplet, a Cloud Firewall and, when the hostname's domain is hosted
on DigitalOcean DNS, its A record. The Droplet sets itself up on first boot through cloud-init:
Docker, the stack in this folder, a generated Postgres password, swap, nightly database dumps and
an update helper. No SSH from this machine is needed.

Everything is read from environment variables (see README.md next to this file). Secrets never go
on the command line. Standard library only, so it runs anywhere Python 3.9+ does.

    python3 deploy/digitalocean/deploy.py --dry-run   # validate and show the plan, change nothing
    python3 deploy/digitalocean/deploy.py
"""

import argparse
import json
import os
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

API = "https://api.digitalocean.com/v2"
HERE = Path(__file__).resolve().parent

REQUIRED = [
    "DIGITALOCEAN_TOKEN",
    "DOTMARC_HOSTNAME",
    "GRAPH_CLIENT_ID",
    "GRAPH_TENANT_ID",
    "GRAPH_CLIENT_SECRET",
    "GRAPH_MAILBOX_ADDRESS",
    "ENTRAID_CLIENT_ID",
    "ENTRAID_CLIENT_SECRET",
    "INITIAL_ADMIN_EMAILS",
]
SECRETS = {"DIGITALOCEAN_TOKEN", "GRAPH_CLIENT_SECRET", "ENTRAID_CLIENT_SECRET"}

# Written to the Droplet's .env. POSTGRES_PASSWORD is added on the Droplet itself, so it never
# leaves the box.
APP_ENV = [
    "DOTMARC_IMAGE",
    "DOTMARC_VERSION",
    "DOTMARC_HOSTNAME",
    "GRAPH_CLIENT_ID",
    "GRAPH_TENANT_ID",
    "GRAPH_CLIENT_SECRET",
    "GRAPH_MAILBOX_ADDRESS",
    "GRAPH_TLSRPT_MAILBOX_ADDRESS",
    "ENTRAID_TENANT_ID",
    "ENTRAID_CLIENT_ID",
    "ENTRAID_CLIENT_SECRET",
    "INITIAL_ADMIN_EMAILS",
]

BACKUP_SCRIPT = """#!/bin/sh
# Nightly pg_dump of the dotMARC database, kept for 14 days. The database also holds the keys
# that decrypt stored integration secrets, so these dumps are the whole of dotMARC's state.
set -eu
mkdir -p /var/backups/dotmarc
cd /opt/dotmarc
docker compose exec -T postgres pg_dump -U dotmarc dotmarc | gzip > /var/backups/dotmarc/dotmarc-$(date +%F).sql.gz
find /var/backups/dotmarc -name 'dotmarc-*.sql.gz' -mtime +14 -delete
"""

UPDATE_SCRIPT = """#!/bin/sh
# Usage: dotmarc-update <tag> [image]   e.g. dotmarc-update 0.9.4, or a commit's tag from a fork's image
set -eu
[ $# -ge 1 ] && [ $# -le 2 ] || { echo "usage: dotmarc-update <tag> [image]" >&2; exit 2; }
cd /opt/dotmarc
/usr/local/bin/dotmarc-backup
sed -i "s/^DOTMARC_VERSION=.*/DOTMARC_VERSION='$1'/" .env
if [ $# -eq 2 ]; then
    sed -i '/^DOTMARC_IMAGE=/d' .env
    echo "DOTMARC_IMAGE='$2'" >> .env
fi
docker compose pull app
docker compose up -d
"""


def fail(message):
    print(f"error: {message}", file=sys.stderr)
    sys.exit(1)


class DigitalOcean:
    def __init__(self, token):
        self.token = token

    def call(self, method, path, body=None):
        request = urllib.request.Request(
            API + path,
            method=method,
            data=json.dumps(body).encode() if body is not None else None,
            headers={"Authorization": f"Bearer {self.token}", "Content-Type": "application/json"},
        )
        try:
            with urllib.request.urlopen(request, timeout=60) as response:
                raw = response.read()
                return json.loads(raw) if raw else {}
        except urllib.error.HTTPError as e:
            fail(f"{method} {path} returned {e.code}: {e.read().decode(errors='replace')}")

    def list_all(self, path, key):
        items, page = [], 1
        while True:
            sep = "&" if "?" in path else "?"
            result = self.call("GET", f"{path}{sep}per_page=200&page={page}")
            items += result.get(key, [])
            if "next" not in result.get("links", {}).get("pages", {}):
                return items
            page += 1


def read_config():
    config = {name: os.environ.get(name, "").strip() for name in REQUIRED + APP_ENV}
    missing = [name for name in REQUIRED if not config[name]]
    if missing:
        fail("missing environment variables: " + ", ".join(missing))
    config["ENTRAID_TENANT_ID"] = config["ENTRAID_TENANT_ID"] or config["GRAPH_TENANT_ID"]
    config["DOTMARC_IMAGE"] = config["DOTMARC_IMAGE"] or "ghcr.io/homotechsual/dotmarc"
    config["DOTMARC_VERSION"] = config["DOTMARC_VERSION"] or "0.9.3"
    config["DOTMARC_HOSTNAME"] = config["DOTMARC_HOSTNAME"].lower().rstrip(".")
    for name in APP_ENV:
        if "'" in config[name] or "\n" in config[name]:
            fail(f"{name} contains a quote or newline, which the .env file cannot hold")
    # GitHub Actions passes an unset variable as an empty string, so treat empty as unset.
    config["region"] = os.environ.get("DO_REGION", "").strip() or "lon1"
    config["size"] = os.environ.get("DO_SIZE", "").strip() or "s-1vcpu-2gb"
    config["name"] = os.environ.get("DO_DROPLET_NAME", "").strip() or "dotmarc"
    config["project"] = os.environ.get("DO_PROJECT", "").strip()
    return config


def user_data(config):
    # Single-quoted so docker compose takes the values literally ($ in a secret stays a $).
    env_file = "".join(f"{name}='{config[name]}'\n" for name in APP_ENV)
    cloud_config = {
        "package_update": True,
        "write_files": [
            {"path": "/opt/dotmarc/docker-compose.yml", "content": (HERE / "docker-compose.yml").read_text()},
            {"path": "/opt/dotmarc/Caddyfile", "content": (HERE / "Caddyfile").read_text()},
            {"path": "/opt/dotmarc/.env", "content": env_file, "permissions": "0600"},
            {"path": "/usr/local/bin/dotmarc-backup", "content": BACKUP_SCRIPT, "permissions": "0755"},
            {"path": "/usr/local/bin/dotmarc-update", "content": UPDATE_SCRIPT, "permissions": "0755"},
            {"path": "/etc/cron.d/dotmarc-backup", "content": "30 3 * * * root /usr/local/bin/dotmarc-backup\n"},
            {"path": "/usr/local/bin/dotmarc-autoupdate", "content": (HERE / "dotmarc-autoupdate").read_text(), "permissions": "0755"},
            {"path": "/usr/local/bin/dotmarc-install-updater", "content": (HERE / "install-updater.sh").read_text(), "permissions": "0755"},
            {"path": "/etc/cron.d/dotmarc-autoupdate",
             "content": "*/15 * * * * root /usr/local/bin/dotmarc-autoupdate >> /var/log/dotmarc-autoupdate.log 2>&1\n"
                        "* * * * * root /usr/local/bin/dotmarc-autoupdate --if-requested >> /var/log/dotmarc-autoupdate.log 2>&1\n"},
        ] + (
            # Lets the updater refresh itself from this repository after each update (see install-updater.sh).
            [{"path": "/etc/dotmarc-updater-repo", "content": os.environ["GITHUB_REPOSITORY"] + "\n"}]
            if os.environ.get("GITHUB_REPOSITORY") else []
        ),
        "runcmd": [
            "fallocate -l 2G /swapfile && chmod 600 /swapfile && mkswap /swapfile && swapon /swapfile",
            "echo '/swapfile none swap sw 0 0' >> /etc/fstab",
            "curl -fsSL https://get.docker.com | sh",
            "echo \"POSTGRES_PASSWORD='$(openssl rand -hex 24)'\" >> /opt/dotmarc/.env",
            "cd /opt/dotmarc && docker compose up -d",
        ],
    }
    return "#cloud-config\n" + json.dumps(cloud_config, indent=2)


def find_or_create_droplet(do, config, dry_run):
    existing = [d for d in do.list_all("/droplets", "droplets") if d["name"] == config["name"]]
    if existing:
        print(f"Droplet '{config['name']}' already exists (id {existing[0]['id']}); leaving it as it is.")
        return existing[0]
    ssh_keys = [k["id"] for k in do.list_all("/account/keys", "ssh_keys")]
    print(f"Creating Droplet '{config['name']}' ({config['size']} in {config['region']}, weekly backups on"
          f"{', ' + str(len(ssh_keys)) + ' SSH key(s)' if ssh_keys else ', root password emailed to you'}).")
    if dry_run:
        return None
    body = {
        "name": config["name"],
        "region": config["region"],
        "size": config["size"],
        "image": "ubuntu-24-04-x64",
        "backups": True,
        "monitoring": True,
        "ipv6": True,
        "tags": ["dotmarc"],
        "ssh_keys": ssh_keys,
        "user_data": user_data(config),
    }
    droplet = do.call("POST", "/droplets", body)["droplet"]
    print("Waiting for the Droplet to start", end="", flush=True)
    for _ in range(60):
        droplet = do.call("GET", f"/droplets/{droplet['id']}")["droplet"]
        if droplet["status"] == "active" and public_ip(droplet):
            print(" done.")
            return droplet
        print(".", end="", flush=True)
        time.sleep(5)
    fail("the Droplet did not become active within 5 minutes; check the DigitalOcean control panel")


def public_ip(droplet):
    return next((n["ip_address"] for n in droplet["networks"]["v4"] if n["type"] == "public"), None)


def find_project(do, name):
    if not name:
        return None
    for project in do.list_all("/projects", "projects"):
        if project["name"].lower() == name.lower():
            return project
    fail(f"no DigitalOcean project named '{name}'; check DO_PROJECT")


def assign_to_project(do, project, droplet, dry_run):
    if project is None:
        return
    print(f"Putting the Droplet in project '{project['name']}'.")
    if dry_run or droplet is None:
        return
    do.call("POST", f"/projects/{project['id']}/resources", {"resources": [f"do:droplet:{droplet['id']}"]})


def ensure_firewall(do, dry_run):
    if any(f["name"] == "dotmarc" for f in do.list_all("/firewalls", "firewalls")):
        print("Firewall 'dotmarc' already exists.")
        return
    print("Creating firewall 'dotmarc' (inbound 22, 80, 443 only).")
    if dry_run:
        return
    anywhere = {"addresses": ["0.0.0.0/0", "::/0"]}
    do.call("POST", "/firewalls", {
        "name": "dotmarc",
        "tags": ["dotmarc"],
        "inbound_rules": [{"protocol": "tcp", "ports": p, "sources": anywhere} for p in ("22", "80", "443")],
        "outbound_rules": [{"protocol": proto, "ports": "all", "destinations": anywhere} for proto in ("tcp", "udp")]
        + [{"protocol": "icmp", "destinations": anywhere}],
    })


def ensure_dns(do, hostname, ip, dry_run):
    """Returns True when the A record is managed here, False when the user must add it."""
    zones = [d["name"] for d in do.list_all("/domains", "domains")]
    zone = max((z for z in zones if hostname == z or hostname.endswith("." + z)), key=len, default=None)
    if zone is None:
        return False
    name = "@" if hostname == zone else hostname[: -len(zone) - 1]
    records = [r for r in do.list_all(f"/domains/{zone}/records?type=A", "domain_records") if r["name"] == name]
    print(f"Pointing {hostname} at {ip or '<new Droplet IP>'} in DigitalOcean DNS (zone {zone}).")
    if dry_run:
        return True
    if records:
        do.call("PATCH", f"/domains/{zone}/records/{records[0]['id']}", {"data": ip, "ttl": 300})
    else:
        do.call("POST", f"/domains/{zone}/records", {"type": "A", "name": name, "data": ip, "ttl": 300})
    return True


def wait_for_https(hostname):
    url = f"https://{hostname}/"
    print(f"Waiting for {url} to answer (first boot and the certificate take a few minutes)", end="", flush=True)
    for _ in range(90):
        try:
            urllib.request.urlopen(urllib.request.Request(url, method="HEAD"), timeout=10)
            print(" up.")
            return True
        except urllib.error.HTTPError:
            print(" up.")
            return True
        except Exception:
            print(".", end="", flush=True)
            time.sleep(10)
    print(" not yet.")
    return False


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--dry-run", action="store_true", help="validate and print the plan without changing anything")
    parser.add_argument("--show-user-data", action="store_true", help="print the cloud-init file with secrets masked")
    args = parser.parse_args()

    config = read_config()
    if args.show_user_data:
        masked = dict(config, **{name: "********" for name in SECRETS})
        print(user_data(masked))
        return

    do = DigitalOcean(config["DIGITALOCEAN_TOKEN"])
    account = do.call("GET", "/account")["account"]
    print(f"DigitalOcean account: {account['email']} ({account['status']})")
    if args.dry_run:
        print("Dry run: nothing below will be changed.")

    project = find_project(do, config["project"])
    droplet = find_or_create_droplet(do, config, args.dry_run)
    ip = public_ip(droplet) if droplet else None
    assign_to_project(do, project, droplet, args.dry_run)
    ensure_firewall(do, args.dry_run)
    dns_managed = ensure_dns(do, config["DOTMARC_HOSTNAME"], ip, args.dry_run)

    hostname = config["DOTMARC_HOSTNAME"]
    print()
    if not dns_managed:
        print(f"ACTION NEEDED: {hostname}'s domain isn't on DigitalOcean DNS. At your DNS provider, add:")
        print(f"    A  {hostname}  ->  {ip or '<the Droplet IP, shown when you run this without --dry-run>'}")
    print(f"Add this redirect URI to the dashboard app registration: https://{hostname}/signin-oidc")
    if args.dry_run:
        return
    if dns_managed:
        wait_for_https(hostname)
    print(f"\ndotMARC: https://{hostname}   Droplet IP: {ip}")


if __name__ == "__main__":
    main()
