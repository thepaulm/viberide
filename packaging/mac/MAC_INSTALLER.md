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

That leaves `VibeRide-0.8.3.dmg` and `VibeRide-0.8.3.pkg` in the current
directory. Pass `--dmg` or `--pkg` for just one.

If you already have the repo checked out and an app somewhere:

```bash
bash packaging/mac/make_installer.sh --app /Applications/VibeRide.app --dmg
```

## What each one is, and why you might want it

| | What it does | Runs scripts | Overwrite |
| --- | --- | --- | --- |
| **zip** (today) | unpack, double-click the installer app inside | yes, our own | installer deletes the old bundle |
| **.dmg** | mounts as a volume; drag the app to Applications | no | Finder asks to Replace |
| **.pkg** | double-click, wizard, installs to /Applications | yes, if you add them | native, handled by Installer |

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

## What I could not test

All of the above was written on Windows and has never been run. `hdiutil`,
`pkgbuild`, `codesign` and `spctl` do not exist here, and neither does any way
to mount a disk image or run an installer package. The commands are the standard
invocations and the script is defensive about paths, but you are the first
execution.

Things worth checking, in the order they would fail:

1. **The script finds the app.** It looks inside `Install VibeRide.app` first,
   then for a top-level `VibeRide.app`. It prints the path it chose.
2. **`codesign --verify` passes.** If it complains about nested code, the
   `--deep` flag is doing the wrong thing for this bundle and the nested
   binaries need signing individually, innermost first.
3. **The `.dmg` mounts and the app runs from it.**
4. **The `.pkg` installs to /Applications** and the installed app launches.
5. **Bluetooth still works** — the point of the signing. The status panel should
   reach `connected`, not sit at "no BLE devices visible at all".

One known wrinkle: an ad-hoc signature changes whenever the binary changes, so
each new build looks like a different app to the privacy system and macOS may
ask for Bluetooth permission again. That is expected, not a fault. To force the
prompt deliberately:

```bash
tccutil reset Bluetooth com.viberide.app
```

## If it works

Tell me which of the two you prefer and I will wire it into the release process
— though note that step has to run on your Mac, so `release.ps1` on the Windows
side would produce the zip as now and hand off to this script afterwards. The
zip route stays regardless, for anyone without a Mac to build on.
