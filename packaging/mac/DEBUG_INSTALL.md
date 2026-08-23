# "I installed it but I'm still running the old one"

Run these on the Mac, in order. Each one is a copy-paste block. Stop when one of
them explains it.

The symptom being chased: installing produced no new app in `/Applications`, and
the app that does run behaves like an older build. The same happened from the
`.pkg`.

Two things were fixed in **v0.9.1**, so start by getting that:

```bash
cd ~/Downloads
curl -fL -O https://github.com/thepaulm/viberide/releases/download/v0.9.1/VibeRide-0.9.1-mac-universal.zip
unzip -o VibeRide-0.9.1-mac-universal.zip -d VibeRide-0.9.1
```

`curl` does not quarantine, so nothing downloaded this way gets blocked.

---

## 1. How many copies are on this machine?

This is the leading suspicion. Nothing stops several VibeRide.app copies
existing at once, and the Dock, Spotlight and a double-click can each land on a
different one.

```bash
mdfind -name 'VibeRide.app' 2>/dev/null
echo '--- versions ---'
for p in /Applications/VibeRide.app ~/Applications/VibeRide.app; do
  [ -d "$p" ] && printf '%s\n  version %s\n  modified %s\n' \
    "$p" \
    "$(defaults read "$p/Contents/Info" CFBundleShortVersionString 2>/dev/null)" \
    "$(stat -f '%Sm' "$p")"
done
```

**`~/Applications` is the one to look for.** The installer falls back to it
without stopping when `/Applications` is not writable, so an install can quietly
land somewhere other than where you then go looking. Anything before v0.9.1
reports `1.0` as its version regardless of what it actually is — the bundle
version was never being stamped, which is fixed now.

Delete whatever is stale before testing again:

```bash
rm -rf ~/Applications/VibeRide.app          # if there is one
sudo rm -rf /Applications/VibeRide.app      # start clean
```

## 2. What does the Dock icon actually point at?

A Dock entry keeps the path it was pinned from. If it was pinned from
`~/Downloads` months ago, clicking it launches that copy no matter what is in
`/Applications`.

```bash
defaults read com.apple.dock persistent-apps \
  | grep -A 2 -i viberide | grep _CFURLString
```

If that shows anything other than `/Applications/VibeRide.app`, drag it off the
Dock and re-add it from `/Applications`.

## 3. Install v0.9.1 and read what the dialog says

```bash
open ~/Downloads/VibeRide-0.9.1/
```

Double-click **Install VibeRide**. Gatekeeper will block it the first time:
**System Settings > Privacy & Security**, find the message, **Open Anyway**.

The completion dialog now names both the version and the full destination path.
Note down what it says — particularly whether the path is `/Applications` or
`/Users/you/Applications`.

Before v0.9.1 the installer aborted immediately *before* opening the app, because
a comment in it had lost its `#` and bash tried to run the text as a command. The
copy had already happened by then, so an install looked like it had done nothing
while having actually worked. If that is what you saw, the app was installed all
along.

## 4. Ask the running app what it is

The menu now shows the version at the bottom, under Exit. And every launch logs
the version and the directory it is running from:

```bash
grep -i 'VibeRide. version' ~/Library/Logs/VibeRide/VibeRide/Player.log | tail -5
```

That line settles it. `running from /Applications/VibeRide.app/...` and
`version 0.9.1` together mean you are on the new build; anything else names the
copy you are actually launching.

## 5. If the .pkg is the old one

`release_pkg.sh` defaults to the **newest published release** at the time it
runs. If it was run before v0.9.0 existed, it built the pkg from v0.8.3 — and
that pkg is genuinely old, correctly.

```bash
gh release view --json tagName -q .tagName        # what is newest now
```

Rebuild against the current release:

```bash
cd ~/code/viberide && git pull
bash packaging/mac/release_pkg.sh                 # newest
bash packaging/mac/release_pkg.sh v0.9.1          # or name it
```

## What to send back

Whichever of these is quickest:

- the output of step 1
- what the installer dialog said, verbatim
- the version shown at the bottom of the app's menu
- the `grep` line from step 4

Any one of them narrows it to a single cause.

## Still to fix

The `~/Applications` fallback is a trap: it means an install can succeed
somewhere other than where it was asked to go, and say so only in a dialog that
is easy to dismiss. If step 1 shows a copy there, that is the bug rather than
your reading of it, and the fallback should be replaced by asking rather than
guessing.
