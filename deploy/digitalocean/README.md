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
| `DOTMARC_IMAGE` | no | Image to run, default `ghcr.io/homotechsual/dotmarc`; the workflow defaults it to this fork's `ghcr.io/gingerr6/dotmarc` |
| `DOTMARC_VERSION` | no | Image tag, default `0.9.3`. For a fork's image, use a commit's full SHA or `edge` |
| `DO_REGION`, `DO_SIZE`, `DO_DROPLET_NAME` | no | Defaults `lon1`, `s-1vcpu-2gb`, `dotmarc` |
| `DO_PROJECT` | no | DigitalOcean project to put the Droplet in; the workflow defaults it to `dotMARC` |

## Run

```sh
python3 deploy/digitalocean/deploy.py --dry-run   # checks the token and shows the plan
python3 deploy/digitalocean/deploy.py
```

Running it again is safe. It reuses an existing Droplet and firewall instead of creating
duplicates. Afterwards, add `https://<DOTMARC_HOSTNAME>/signin-oidc` as a Web redirect URI on the
dashboard app registration.

## Updates

Every 15 minutes, `dotmarc-autoupdate` checks the registry for a newer image under the tag in `.env`
(`DOTMARC_VERSION`). When there is one, it backs up the database and restarts dotMARC on it; otherwise it
does nothing. With the workflow's default tag, `edge`, every change merged into `main` reaches the server
within about 15 minutes of its image being published. Pin `DOTMARC_VERSION` to a commit SHA or a release
to stop that. What it did is in `/var/log/dotmarc-autoupdate.log`.

## Later

Open the Droplet's **Console** in the DigitalOcean control panel, then:

- **Update:** `dotmarc-update 0.9.4` takes a backup, then switches to the new image. Add an image to
  switch whose image runs too: `dotmarc-update <commit sha> ghcr.io/gingerr6/dotmarc`.
- **Logs:** `cd /opt/dotmarc && docker compose logs -f app`
- **Change a setting:** edit `/opt/dotmarc/.env`, then `cd /opt/dotmarc && docker compose up -d`.

The database also holds the keys that decrypt stored integration secrets. Keep the Droplet
backups switched on.

## Run from GitHub Actions instead

The **Deploy to DigitalOcean** workflow runs the same script with the repository's Actions
settings, so the values only need entering once. In the repository, go to **Settings**, then
**Secrets and variables**, then **Actions**, and add each setting from the table above. Put the
token and the two client secrets under **Secrets**. Put everything else under either **Secrets**
or **Variables**: variables stay readable in the run log. Then open **Actions**, choose **Deploy
to DigitalOcean**, and click **Run workflow**, with "dry run" ticked the first time.
