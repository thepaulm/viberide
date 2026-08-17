"""Scan for BLE fitness devices and dump the GATT tree of the chosen one.

Usage:
    python scan.py                 # scan only, list what's nearby
    python scan.py --connect KICKR # scan, then connect to first match and dump services

The KICKR must be awake (spin the cranks) and NOT connected to another app
-- Zwift, the Wahoo phone app, a head unit. BLE trainers accept one host at a time.
"""

import argparse
import asyncio
import contextlib

from bleak import BleakClient, BleakScanner

# Assigned-number services we care about, so we can flag them in the scan list.
KNOWN_SERVICES = {
    "00001826-0000-1000-8000-00805f9b34fb": "FTMS (Fitness Machine)",
    "00001818-0000-1000-8000-00805f9b34fb": "Cycling Power",
    "00001816-0000-1000-8000-00805f9b34fb": "Cycling Speed & Cadence",
    "0000180d-0000-1000-8000-00805f9b34fb": "Heart Rate",
    "0000180a-0000-1000-8000-00805f9b34fb": "Device Information",
    "0000180f-0000-1000-8000-00805f9b34fb": "Battery",
}

KNOWN_CHARS = {
    "00002ad2-0000-1000-8000-00805f9b34fb": "Indoor Bike Data",
    "00002ad9-0000-1000-8000-00805f9b34fb": "Fitness Machine Control Point",
    "00002ada-0000-1000-8000-00805f9b34fb": "Fitness Machine Status",
    "00002acc-0000-1000-8000-00805f9b34fb": "Fitness Machine Feature",
    "00002ad6-0000-1000-8000-00805f9b34fb": "Supported Resistance Level Range",
    "00002ad8-0000-1000-8000-00805f9b34fb": "Supported Power Range",
    "00002a63-0000-1000-8000-00805f9b34fb": "Cycling Power Measurement",
    "00002a65-0000-1000-8000-00805f9b34fb": "Cycling Power Feature",
    "00002a66-0000-1000-8000-00805f9b34fb": "Cycling Power Control Point",
    "00002a5b-0000-1000-8000-00805f9b34fb": "CSC Measurement",
    "00002a29-0000-1000-8000-00805f9b34fb": "Manufacturer Name",
    "00002a24-0000-1000-8000-00805f9b34fb": "Model Number",
    "00002a26-0000-1000-8000-00805f9b34fb": "Firmware Revision",
    "00002a28-0000-1000-8000-00805f9b34fb": "Software Revision",
    "00002a27-0000-1000-8000-00805f9b34fb": "Hardware Revision",
    "00002a25-0000-1000-8000-00805f9b34fb": "Serial Number",
}

# Readable characteristics whose value is genuinely useful to see during discovery.
READ_ON_DISCOVER = {
    "00002acc-0000-1000-8000-00805f9b34fb",
    "00002ad6-0000-1000-8000-00805f9b34fb",
    "00002ad8-0000-1000-8000-00805f9b34fb",
    "00002a29-0000-1000-8000-00805f9b34fb",
    "00002a24-0000-1000-8000-00805f9b34fb",
    "00002a26-0000-1000-8000-00805f9b34fb",
    "00002a27-0000-1000-8000-00805f9b34fb",
    "00002a28-0000-1000-8000-00805f9b34fb",
}


async def scan(timeout: float):
    """Return {address: (device, advertisement_data)} using the detection-callback
    form, which has been stable across bleak versions."""
    found: dict = {}

    def on_detect(device, adv):
        found[device.address] = (device, adv)

    scanner = BleakScanner(detection_callback=on_detect)
    await scanner.start()
    try:
        await asyncio.sleep(timeout)
    finally:
        await scanner.stop()
    return found


def describe(found: dict) -> list:
    """Sort devices so likely trainers float to the top; return the sorted rows."""
    rows = []
    for device, adv in found.values():
        uuids = [u.lower() for u in (adv.service_uuids or [])]
        tags = [KNOWN_SERVICES[u] for u in uuids if u in KNOWN_SERVICES]
        name = adv.local_name or device.name or "(unnamed)"
        interesting = bool(tags) or "kickr" in name.lower() or "wahoo" in name.lower()
        rows.append(
            {
                "address": device.address,
                "name": name,
                "rssi": adv.rssi,
                "tags": tags,
                "uuids": uuids,
                "interesting": interesting,
                "device": device,
            }
        )
    rows.sort(key=lambda r: (not r["interesting"], -(r["rssi"] or -999)))
    return rows


async def dump_services(device):
    print(f"\nConnecting to {device.name or device.address} ...")
    async with BleakClient(device) as client:
        print(f"Connected. Enumerating GATT tree.\n")
        for service in client.services:
            label = KNOWN_SERVICES.get(service.uuid.lower(), "")
            suffix = f"  <-- {label}" if label else ""
            print(f"[service] {service.uuid}{suffix}")
            for char in service.characteristics:
                props = ",".join(char.properties)
                clabel = KNOWN_CHARS.get(char.uuid.lower(), "")
                csuffix = f"  ({clabel})" if clabel else ""
                print(f"    [char] {char.uuid}{csuffix}")
                print(f"           props: {props}")
                if char.uuid.lower() in READ_ON_DISCOVER and "read" in char.properties:
                    try:
                        raw = await client.read_gatt_char(char)
                        text = raw.decode("utf-8", "replace").strip("\x00").strip()
                        printable = text if text.isprintable() and text else raw.hex()
                        print(f"           value: {printable}   (hex {raw.hex()})")
                    except Exception as exc:  # noqa: BLE001 - discovery is best-effort
                        print(f"           value: <read failed: {exc}>")
        print()


async def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--timeout", type=float, default=10.0, help="scan seconds")
    parser.add_argument(
        "--connect",
        metavar="MATCH",
        help="connect to the first device whose name or address contains MATCH",
    )
    args = parser.parse_args()

    print(f"Scanning for {args.timeout:.0f}s ... (spin the cranks to wake the trainer)")
    found = await scan(args.timeout)
    if not found:
        print("No BLE devices seen at all. Is Bluetooth on?")
        return

    rows = describe(found)
    print(f"\nSaw {len(rows)} device(s):\n")
    for row in rows:
        marker = "*" if row["interesting"] else " "
        tags = ("  [" + ", ".join(row["tags"]) + "]") if row["tags"] else ""
        print(f" {marker} {row['address']}  {row['rssi']:>4} dBm  {row['name']}{tags}")
    print("\n(* = advertises a fitness service or looks like a Wahoo device)")

    if not args.connect:
        return

    needle = args.connect.lower()
    match = next(
        (
            r
            for r in rows
            if needle in r["name"].lower() or needle in r["address"].lower()
        ),
        None,
    )
    if not match:
        print(f"\nNothing matching {args.connect!r} to connect to.")
        return
    await dump_services(match["device"])


if __name__ == "__main__":
    with contextlib.suppress(KeyboardInterrupt):
        asyncio.run(main())
