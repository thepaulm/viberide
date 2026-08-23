# Building a real installer, on a Mac

Everything shipped so far is built on Windows, and that has shaped it. This is
what becomes possible with a Mac and the Xcode command line tools in the loop —
a properly compressed disk image, a genuine installer package, and an app whose
signature is correct *before* it is packaged rather than repaired on the way in.

You need only the command line tools. Check with:

```bash
xcode-select -p
```

If that prints a path, you are ready. If not: `xcode-select --install`.

## The short version

```bash
cd ~/Downloads
curl -fL -O https://github.com/thepaulm/viberide/releases/latest/download/VibeRide-0.8.3-mac-universal.zip
git clone https://github.com/thepaulm/viberide.git    # if you have not already
bash viberide/packaging/mac/make_installer.sh --zip VibeRide-0.8.3-mac-universal.zip
```

That leaves `VibeRide-0.8.3.dmg` and `VibeRide-0.8.3-Installer.pkg` in the
current directory. Pass `--dmg` or `--pkg` for just one.

The `-Installer` in the pkg name is load-bearing: GitHub lists release assets in
alphabetical order, and it sorts the pkg above `-mac-universal.zip` so that a
visitor sees the thing to double-click first.

If you already have the repo checked out and an app somewhere:

```bash
bash packaging/mac/make_installer.sh --app /Applications/VibeRide.app --dmg
```

## What each one is, and why you might want it

| | What it does | Runs scripts | Overwrite |
| --- | --- | --- | --- |
| **zip** | unpack, double-click the installer app inside | yes, our own | installer deletes the old bundle |
| **.dmg** | mounts as a volume; drag the app to Applications | no | Finder asks to Replace |
| **.pkg** (shipped) | double-click, wizard, installs to /Applications | yes, if you add them | native, handled by Installer |

A `.dmg` is not an installer — it is a folder that mounts. It cannot run
anything, so it works here only because the app now sets itself up on first
launch. That was not true before v0.6.0.

A `.pkg` **is** an installer: a wizard, a progress bar, and installation to
`/Applications` with proper replace semantics. It is the most native of the
three. The script builds one with no scripts inside it at all, because signing
the app beforehand removes the only work there was to do.

## Why doing this on a Mac is worth something

Not presentation. The signature.

Unity ad-hoc signs the app when it builds it. The Windows build then adds the
Python bridge under `Contents/Resources` and writes the Bluetooth usage strings
into `Info.plist` — both after signing, both invalidating it. macOS identifies
apps to the privacy system by their signature, so a broken one means the
Bluetooth grant never sticks, and the bridge quietly sees zero BLE devices.

The current installer papers over this by running `codesign` on the way in.
That works, but it means every copy is repaired at the destination rather than
shipped correct. `make_installer.sh` signs the app first and packages the
result, so what you hand out is already right.

You can see the state of any bundle with:

```bash
codesign --verify --deep --strict --verbose=2 /Applications/VibeRide.app
```

## Gatekeeper: what this does and does not fix

**It fixes it for you.** Files you create locally are not quarantined, so a
`.dmg` or `.pkg` you build on your own Mac opens without a prompt. No "Apple
could not verify", no Privacy & Security detour.

**It does not fix it for anyone else.** The moment you upload one and someone
downloads it, their browser attaches `com.apple.quarantine` and macOS blocks it
exactly as before. Ad-hoc signing is not a Developer ID and cannot be notarised.

The script prints the Gatekeeper assessment so this is never a surprise:

```
==> Gatekeeper assessment (rejection here is expected)
    /path/to/VibeRide.app: rejected
    source=no usable signature
```

That rejection is the correct and expected result. Removing it needs the paid
Apple Developer Program ($99/yr) and `notarytool`, which is a different job.

Recipients still have the two routes that work today: **System Settings →
Privacy & Security → Open Anyway**, or downloading with `curl`, which does not
quarantine anything.

## Doing it by hand

The script is not doing anything exotic. In full:

```bash
# 1. get the app out of the release zip
unzip -q VibeRide-0.8.3-mac-universal.zip -d unpacked
cp -R "unpacked/Install VibeRide.app/Contents/Resources/VibeRide.app" .

# 2. clear the download flag and sign it properly
xattr -dr com.apple.quarantine VibeRide.app
codesign --force --deep --sign - VibeRide.app
codesign --verify --deep --strict --verbose=2 VibeRide.app

# 3a. a disk image, with the usual drag-to-Applications target
mkdir dmgroot
cp -R VibeRide.app dmgroot/
ln -s /Applications dmgroot/Applications
hdiutil create -volname "VibeRide" -srcfolder dmgroot -fs HFS+ -format UDZO -ov VibeRide.dmg

# 3b. or an installer package
mkdir pkgroot
cp -R VibeRide.app pkgroot/
pkgbuild --root pkgroot --identifier com.viberide.app --version 0.8.3 \
         --install-location /Applications VibeRide.pkg
```

`-format UDZO` is the compressed image format and is not optional in practice:
without it the `.dmg` is the uncompressed size of the bundle, about 112 MB
against 46.

## What actually happened when it was first run

All of the above was written on Windows without ever being run — `hdiutil`,
`pkgbuild`, `codesign` and `spctl` do not exist there. It has since been run on
a Mac against the v0.8.3 release zip. Both artifacts built, the `.pkg` installs
and the app runs. What the first run turned up:

- **One real bug.** `VERSION.txt` is written by `release.ps1`, so its lines end
  CRLF, and the parsed version carried a trailing carriage return into every
  file name — `VibeRide-0.8.3\r.dmg`, a volume named `VibeRide 0.8.3\r`, and the
  same in the pkg's version field. It is an unusually good disguise: `ls` prints
  the name looking correct, while `stat`, `cp` and `hdiutil attach` all report
  "No such file or directory". Fixed by stripping `\r` where the version is
  parsed.
- **`codesign --deep` was fine.** No nested-code complaint; the four dylibs
  under `Contents/Frameworks` sign and validate, and the bundle satisfies its
  designated requirement.
- **`spctl` rejects it**, exactly as described above, and that is still correct.
- **The bundle survives packaging intact** — universal (`x86_64 arm64`), the
  Bluetooth usage strings still in `Info.plist`, and the Python bridge still
  under `Contents/Resources`, all of it inside a signature that now validates.

One wrinkle worth knowing: an ad-hoc signature changes whenever the binary
changes, so each new build looks like a different app to the privacy system and
macOS may ask for Bluetooth permission again. That is expected, not a fault. To
force the prompt deliberately:

```bash
tccutil reset Bluetooth com.viberide.app
```

Both wrinkles the first run turned up have since been fixed on the Windows side:

- `VERSION.txt` is now written with LF endings, so the `tr -d '
'` in
  `make_installer.sh` is belt-and-braces rather than load-bearing.
- The bundle carries the real version. `release.ps1` passes `-buildVersion` to
  Unity, so `CFBundleShortVersionString` matches the tag instead of always
  saying 1.0 — which is what Finder's Get Info shows, and the only thing
  `make_installer.sh --app` has to go on when naming its output.

## It works, and it is wired into releases

The `.pkg` won. [`../RELEASING.md`](../RELEASING.md) has the flow: `release.ps1`
publishes the zip from the Windows host, then `release_pkg.sh` on the Mac builds
the `.pkg` from that zip, uploads it to the same release, and rewrites the notes
to lead with it.

```bash
bash packaging/mac/release_pkg.sh              # newest release
bash packaging/mac/release_pkg.sh v0.8.4       # a specific tag
bash packaging/mac/release_pkg.sh v0.8.4 --dry-run
```

It refuses to publish a pkg whose version does not match the tag, or one that
does not install to `/Applications`.

`make_installer.sh` stays the way to build either artifact by hand, and the
`.dmg` half of it is still there for anyone who wants one — it is just not
published. The zip route stays regardless, for anyone without a Mac to build on.
