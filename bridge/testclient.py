"""Connect to a running bridge, ride a synthetic hill, print what comes back."""

import asyncio
import json
import sys

from websockets.asyncio.client import connect


async def main():
    url = sys.argv[1] if len(sys.argv) > 1 else "ws://127.0.0.1:47812"
    async with connect(url) as ws:
        print(f"connected to {url}\n")
        print(f"{'t':>6} {'grade':>7} {'power':>7} {'cad':>6} {'speed':>8} {'dist':>9} {'climb':>7}")
        for i in range(60):
            # Ramp into a 7% climb to prove grade actually bites.
            grade = 0.0 if i < 15 else min(0.07, (i - 15) * 0.005)
            await ws.send(json.dumps({"type": "grade", "grade": grade}))
            msg = json.loads(await ws.recv())
            if i % 5 == 0:
                print(
                    f"{msg['t']:>6.1f} {msg['grade']*100:>6.1f}% {msg['power_w']:>6.0f}W "
                    f"{msg['cadence_rpm']:>5.0f} {msg['speed_kph']:>6.1f}kph "
                    f"{msg['distance_m']:>8.1f}m {msg['elevation_gain_m']:>6.1f}m"
                )
            await asyncio.sleep(0.1)
        print("\nlast full message:")
        print(json.dumps(msg, indent=2))


asyncio.run(main())
