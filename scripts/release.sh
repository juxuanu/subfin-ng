#!/usr/bin/env bash
# Cut a release: bump version, commit, tag, push → GitHub Actions handles the rest.
# Usage: [REMOTE=origin] ./scripts/release.sh <version>
# Example: ./scripts/release.sh 12.1.7.0

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

if [ $# -lt 1 ]; then
  echo "Usage: $0 <version>"
  echo "Example: $0 12.1.7.0"
  exit 1
fi

VERSION="$1"
REMOTE="${REMOTE:-origin}"
# owner/repo of the remote, for the URLs printed at the end
REPO=$(git -C "$REPO_ROOT" remote get-url "$REMOTE" | sed -E 's#^(git@github\.com:|https://github\.com/)##; s#\.git$##')

# Verify we're on main
CURRENT_BRANCH=$(git -C "$REPO_ROOT" rev-parse --abbrev-ref HEAD)
if [ "$CURRENT_BRANCH" != "main" ]; then
  echo "ERROR: Must be on 'main' branch to release (currently on '$CURRENT_BRANCH')"
  echo "Merge your changes to main first, then re-run."
  exit 1
fi

echo "[release] Bumping version to $VERSION..."
"$REPO_ROOT/scripts/bump-version.sh" "$VERSION"

echo "[release] Committing version bump..."
git -C "$REPO_ROOT" add meta.json Jellyfin.Plugin.Subsonic/Jellyfin.Plugin.Subsonic.csproj
git -C "$REPO_ROOT" commit -m "release: bump to v${VERSION}"

echo "[release] Pushing main to $REMOTE..."
git -C "$REPO_ROOT" push "$REMOTE" main

echo "[release] Tagging v${VERSION}..."
git -C "$REPO_ROOT" tag -a "v${VERSION}" -m "Release $VERSION"
git -C "$REPO_ROOT" push "$REMOTE" "v${VERSION}"

echo ""
echo "[release] Done. GitHub Actions will build, package, and update the manifest."
echo "  Watch: https://github.com/${REPO}/actions"
echo ""
echo "  After the workflow completes, the plugin repository URL for Jellyfin is:"
echo "  https://raw.githubusercontent.com/${REPO}/main/jellyfin-plugin-subfin-manifest.json"
