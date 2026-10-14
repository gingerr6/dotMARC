# Deploy to DigitalOcean

`deploy.py` sets up a complete dotMARC instance on one Droplet: the published dotMARC image,
PostgreSQL 18, and Caddy with automatic HTTPS. It does this through the DigitalOcean API, and the
Droplet sets itself up on first boot. You don't need to SSH in.

## What it creates

| Resource | Details |
| --- | --- |
| Droplet `dotmarc` | Ubuntu 24.04, `s-1vcpu-2gb` in `lon1` (about $12/month plus $2.40 for weekly backups), tagged `dotmarc`, monitoring on, 2 GB swap |
| Firewall `dotmarc` | Inbound TCP 22, 80, 443 only; applies to the `dotmarc` tag |
| DNS A record | Only if the hostname's domain is hosted on DigitalOcean DNS; otherwise the script tells you what to add |

On the Droplet, everything lives in `/opt/dotmarc`. The script also sets up:

- a Postgres password generated on the Droplet itself
- a nightly `pg_dump` to `/var/backups/dotmarc`, kept for 14 days
- a `dotmarc-update <version>` helper

## Settings

The script reads these environment variables. Put secrets in the environment, never on the
command line.

| Variable | Required | Notes |
| --- | --- | --- |
| `DIGITALOCEAN_TOKEN` | yes | A DigitalOcean personal access token with read and write access |
| `DOTMARC_HOSTNAME` | yes | For example `dmarc.example.com` |
| `GRAPH_CLIENT_ID`, `GRAPH_TENANT_ID`, `GRAPH_CLIENT_SECRET`, `GRAPH_MAILBOX_ADDRESS` | yes | Mailbox app registration |
| `ENTRAID_CLIENT_ID`, `ENTRAID_CLIENT_SECRET` | yes | Dashboard sign-in app registration |
| `ENTRAID_TENANT_ID` | no | Defaults to `GRAPH_TENANT_ID` |
| `INITIAL_ADMIN_EMAILS` | yes | Comma-separated; these people become Admins on first start |
| `GRAPH_TLSRPT_MAILBOX_ADDRESS` | no | Separate TLS-RPT mailbox |
| `DOTMARC_VERSION` | no | Image tag, default `0.9.3` |
| `DO_REGION`, `DO_SIZE`, `DO_DROPLET_NAME` | no | Defaults `lon1`, `s-1vcpu-2gb`, `dotmarc` |

## Run

```sh
python3 deploy/digitalocean/deploy.py --dry-run   # checks the token and shows the plan
python3 deploy/digitalocean/deploy.py
```

Running it again is safe. It reuses an existing Droplet and firewall instead of creating
duplicates. Afterwards, add `https://<DOTMARC_HOSTNAME>/signin-oidc` as a Web redirect URI on the
dashboard app registration.

## Later

Open the Droplet's **Console** in the DigitalOcean control panel, then:

- **Update:** `dotmarc-update 0.9.4` takes a backup, then switches to the new image.
- **Logs:** `cd /opt/dotmarc && docker compose logs -f app`
- **Change a setting:** edit `/opt/dotmarc/.env`, then `cd /opt/dotmarc && docker compose up -d`.

The database also holds the keys that decrypt stored integration secrets. Keep the Droplet
backups switched on.
