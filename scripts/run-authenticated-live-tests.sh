#!/usr/bin/env bash
# Runs all live tests, including the authenticated ones, in CI against the real service.
#
# Usage: scripts/run-authenticated-live-tests.sh [owner/repo]
#
# 1. Signs in with the ChatGPT device flow (you approve it in the browser) and keeps only the
#    short-lived access token and account ID. The refresh token is discarded, never stored.
# 2. Stores both as secrets of the `live-authenticated` environment, dispatches the Live workflow
#    with `authenticated=true` and waits for it.
# 3. Deletes the secrets and the local copies on exit, whether the run passed, failed or was interrupted.
#
# Needs: gh (authenticated, with admin rights on the repository) and the .NET 10 SDK.
set -euo pipefail

repo="${1:-tryAGI/Codex}"
environment="live-authenticated"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work="$(mktemp -d)"

cleanup() {
  rm -rf "$work"
  gh secret delete CODEX_ACCESS_TOKEN --env "$environment" --repo "$repo" > /dev/null 2>&1 || true
  gh secret delete CODEX_ACCOUNT_ID --env "$environment" --repo "$repo" > /dev/null 2>&1 || true
}
trap cleanup EXIT

gh api --method PUT "repos/$repo/environments/$environment" > /dev/null

dotnet run --project "$root/samples/tryAGI.Codex.Sample" -p:UseLocalProject=true -- --live-credentials "$work/credentials"
gh secret set CODEX_ACCESS_TOKEN --env "$environment" --repo "$repo" < "$work/credentials/access-token"
gh secret set CODEX_ACCOUNT_ID --env "$environment" --repo "$repo" < "$work/credentials/account-id"
rm -rf "$work/credentials"

started="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
gh workflow run live.yml --repo "$repo" -f authenticated=true
run=""
for _ in $(seq 1 30); do
  run="$(gh run list --repo "$repo" --workflow live.yml --event workflow_dispatch --limit 5 \
    --json databaseId,createdAt --jq "map(select(.createdAt >= \"$started\")) | last | .databaseId // empty")"
  [[ -n "$run" ]] && break
  sleep 2
done
[[ -n "$run" ]] || { echo "The dispatched Live run did not appear." >&2; exit 1; }

echo "Watching https://github.com/$repo/actions/runs/$run"
gh run watch "$run" --repo "$repo" --exit-status
