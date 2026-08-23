#!/usr/bin/env bash
# Build the .pkg installer from a published release's zip and attach it to that
# same release.
#
#   bash packaging/mac/release_pkg.sh              # newest release
#   bash packaging/mac/release_pkg.sh v0.8.4
#   bash packaging/mac/release_pkg.sh v0.8.4 --dry-run
#
# This is the second half of a release and it has to run on a Mac. release.ps1
# runs on the Windows build host, because that is the machine with Unity, and
# publishes the zip. pkgbuild and codesign do not exist there, so the installer
# people actually double-click can only be made here.
#
# Order matters: the zip is published first and its release notes describe the
# zip, which is true at that moment. Uploading the .pkg rewrites those notes to
# lead with it. Skip this step and the release is still correct, just zip-only.
#
# Needs the Xcode command line tools and `gh auth login`.

set -euo pipefail

TAG=""
DRYRUN=0

while [ $# -gt 0 ]; do
    case "$1" in
        --dry-run) DRYRUN=1 ;;
        -h|--help) sed -n '2,8p' "$0"; exit 0 ;;
        -*) echo "unknown option: $1" >&2; exit 2 ;;
        *) TAG="$1" ;;
    esac
    shift
done

say() { printf '\n==> %s\n' "$1"; }
die() { printf '\nerror: %s\n' "$1" >&2; exit 1; }

HERE="$(cd "$(dirname "$0")" && pwd)"
REPO_DIR="$(git -C "$HERE" rev-parse --show-toplevel)"
cd "$REPO_DIR"

command -v gh >/dev/null || die "gh not found. brew install gh, then gh auth login."
gh auth status >/dev/null 2>&1 || die "gh is not authenticated. Run: gh auth login"

REPO="$(gh repo view --json nameWithOwner -q .nameWithOwner)"

if [ -z "$TAG" ]; then
    TAG="$(gh release view --json tagName -q .tagName)"
    [ -n "$TAG" ] || die "No releases found. Publish the zip from Windows first."
fi
VERSION="${TAG#v}"
ZIPNAME="VibeRide-$VERSION-mac-universal.zip"
# See make_installer.sh: GitHub sorts release assets by name, and "-Installer"
# puts this above the zip on the release page.
PKGNAME="VibeRide-$VERSION-Installer.pkg"

say "Release $TAG in $REPO"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

say "Fetching $ZIPNAME"
gh release download "$TAG" --pattern "$ZIPNAME" --dir "$WORK" \
    || die "No $ZIPNAME on release $TAG. Has release.ps1 run for this version?"

# make_installer.sh writes into the current directory and takes the version from
# the zip's own VERSION.txt rather than from the tag. Letting it do that and then
# insisting on the file name we expect is the check: a zip stamped with a
# different version than the tag it is attached to fails here rather than being
# published under the wrong name.
say "Building the installer"
( cd "$WORK" && bash "$HERE/make_installer.sh" --zip "$WORK/$ZIPNAME" --pkg ) | sed 's/^/    /'

PKG="$WORK/$PKGNAME"
if [ ! -f "$PKG" ]; then
    die "Expected $PKGNAME, got: $(ls "$WORK"/*.pkg 2>/dev/null || echo 'nothing')
The zip's VERSION.txt disagrees with the tag $TAG."
fi

# Cheap sanity check on the metadata the Installer app actually reads. --expand
# unpacks the metadata only, not the 46 MB payload.
pkgutil --expand "$PKG" "$WORK/expanded"
INFO="$WORK/expanded/PackageInfo"
# Read the attributes off <pkg-info> by name. PackageInfo also carries the app
# bundle's own CFBundleShortVersionString, which Unity leaves at 1.0 and which
# is not what the Installer app versions the receipt by.
attr() { xmllint --xpath "string(/pkg-info/@$1)" "$INFO"; }
PKGVER="$(attr version)"
PKGID="$(attr identifier)"
PKGLOC="$(attr install-location)"
[ "$PKGVER" = "$VERSION" ] || die "pkg says version $PKGVER, tag says $VERSION"
[ "$PKGLOC" = "/Applications" ] || die "pkg installs to $PKGLOC, not /Applications"
echo "    $PKGID $PKGVER -> $PKGLOC"

SHA="$(unzip -p "$WORK/$ZIPNAME" VERSION.txt 2>/dev/null | awk '/^commit/{print $2}' | tr -d '\r')"

NOTES="$WORK/notes.md"
cat > "$NOTES" <<EOF
## [Download $PKGNAME](https://github.com/$REPO/releases/download/$TAG/$PKGNAME)

**Double-click it.** That is the whole install: VibeRide goes into
/Applications, replacing any previous copy. Saved courses live in
\`~/Library/Application Support/VibeRide\` and are never touched by an upgrade.

macOS universal build (Apple Silicon + Intel), built from ${SHA:-source}.

macOS blocks it the first time, because it is not signed with a paid Apple
Developer ID. Open **System Settings > Privacy & Security**, find the message
naming the installer, and click **Open Anyway**. Once only.

To skip that entirely, download from a terminal -- files fetched with curl are
not quarantined, so nothing gets blocked:

\`\`\`
cd ~/Downloads && curl -fL -O https://github.com/$REPO/releases/download/$TAG/$PKGNAME && open $PKGNAME
\`\`\`

The first launch builds the Python environment the trainer bridge needs -- about
a minute, with progress in the app's status panel. It uses a Python 3.9+ you
already have, and only asks you to install one if it cannot find any. After that
it starts in a couple of seconds.

---

<sub>\`$ZIPNAME\` is the same build packaged the older way: unzip and
double-click **Install VibeRide**. Either works; the .pkg is fewer steps, and is
what you want unless you have a reason. START_HERE.md inside the zip has the long
form.</sub>
EOF

if [ "$DRYRUN" = 1 ]; then
    say "--dry-run; not uploading"
    cp "$PKG" "$REPO_DIR/$PKGNAME"
    echo "    $REPO_DIR/$PKGNAME ($(du -h "$PKG" | cut -f1))"
    echo "    notes that would be written:"
    sed 's/^/    | /' "$NOTES"
    exit 0
fi

say "Uploading $PKGNAME"
gh release upload "$TAG" "$PKG" --clobber | sed 's/^/    /'

say "Rewriting the release notes to lead with the .pkg"
gh release edit "$TAG" --notes-file "$NOTES" | sed 's/^/    /'

say "Done"
gh release view "$TAG" --json tagName,url,assets \
    -q '.url, (.assets[] | "  \(.name)  \(.size/1048576 | floor) MB")' | sed 's/^/    /'
