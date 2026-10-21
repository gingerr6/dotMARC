#!/bin/sh
# Installs (or refreshes) dotMARC's updater on a Droplet set up by deploy.py: the dotmarc-autoupdate script and its cron
# lines, so new versions install themselves every 15 minutes, and "Install now" in dotMARC's control panel works within
# a minute. Run once as root:
#   curl -fsSL https://raw.githubusercontent.com/<owner>/<repo>/main/deploy/digitalocean/install-updater.sh | sh -s <owner>/<repo>
# It records the repository in /etc/dotmarc-updater-repo and copies itself to /usr/local/bin/dotmarc-install-updater,
# which dotmarc-autoupdate runs after each update to stay in step with the version it installed.
set -eu
quiet=false
[ "${1:-}" = "--quiet" ] && { quiet=true; shift; }
repo=${1:-$(cat /etc/dotmarc-updater-repo 2>/dev/null || true)}
[ -n "$repo" ] || { echo "usage: install-updater.sh <owner>/<repo>" >&2; exit 2; }
base="https://raw.githubusercontent.com/$repo/main/deploy/digitalocean"

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
curl -fsSL "$base/dotmarc-autoupdate" -o "$work/dotmarc-autoupdate"
curl -fsSL "$base/install-updater.sh" -o "$work/install-updater.sh"
sh -n "$work/dotmarc-autoupdate"
sh -n "$work/install-updater.sh"

install -m 0755 "$work/dotmarc-autoupdate" /usr/local/bin/dotmarc-autoupdate
install -m 0755 "$work/install-updater.sh" /usr/local/bin/dotmarc-install-updater
echo "$repo" > /etc/dotmarc-updater-repo
cat > /etc/cron.d/dotmarc-autoupdate <<'CRON'
*/15 * * * * root /usr/local/bin/dotmarc-autoupdate >> /var/log/dotmarc-autoupdate.log 2>&1
* * * * * root /usr/local/bin/dotmarc-autoupdate --if-requested >> /var/log/dotmarc-autoupdate.log 2>&1
CRON
$quiet || echo "dotMARC's updater is installed. The control panel's Updates tab will show it within a minute."
