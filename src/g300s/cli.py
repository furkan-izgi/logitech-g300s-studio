"""Command Line Interface for Logitech G300s management."""

import argparse
import json
import sys
import time
from typing import List

from .device import G300sDevice
from .profiles import Profile, DeviceSnapshot
from .constants import LED_CYCLE, LED_PALETTE, RATE_TO_CODE
from .exceptions import G300sError, G300sConflictWarning


def format_led(led_tuple: tuple) -> str:
    r, g, b = led_tuple
    colors = "".join(c for c, v in zip("RGB", (r, g, b)) if v)
    return colors if colors else "off"


def describe_profile(p: Profile) -> str:
    led_str = format_led(p.led)
    dpi_str = "/".join(str(x) for x in p.dpis)
    btn_str = ", ".join(f"{i}:{action}" for i, action in enumerate(p.buttons))
    return (
        f"Profile {p.index}: Rate={p.rate}Hz  LED={led_str}  DPI={dpi_str} (Default={p.default_dpi})  Raw7={p.raw7:#04x}\n"
        f"           Buttons: {btn_str}"
    )


def cmd_info(dev: G300sDevice, args):
    state = dev.get_active_state()
    live_led = dev.get_live_led()
    live_rate = dev.get_live_rate()
    print(f"Device: Logitech G300s (046d:c246)")
    print(f"Active: Profile {state['profile']}, DPI Level {state['dpi_level']} (Busy: {state['is_busy']})")
    print(f"Live RAM State: LED={format_led(live_led)}, Rate={live_rate}Hz\n")

    for i in range(3):
        raw = dev.get_profile_raw(i)
        p = Profile.from_raw(raw, i)
        print(describe_profile(p))


def cmd_dump(dev: G300sDevice, args):
    state = dev.get_active_state()
    profiles = [Profile.from_raw(dev.get_profile_raw(i), i) for i in range(3)]
    snap = DeviceSnapshot(state["profile"], state["dpi_level"], profiles)
    print(json.dumps(snap.to_dict(), indent=2))


def cmd_save(dev: G300sDevice, args):
    state = dev.get_active_state()
    profiles = [Profile.from_raw(dev.get_profile_raw(i), i) for i in range(3)]
    snap = DeviceSnapshot(state["profile"], state["dpi_level"], profiles)
    snap.save_json(args.filepath)
    print(f"Backup saved to: {args.filepath}")


def cmd_restore(dev: G300sDevice, args):
    snap = DeviceSnapshot.load_json(args.filepath)
    print(f"Restoring {len(snap.profiles)} profiles from {args.filepath}...")
    for p in snap.profiles:
        dev.set_profile_raw(p.index, p.to_raw())
        print(f"  Restored Profile {p.index}")

    dev.set_active_profile(snap.active_profile)
    dev.set_active_dpi_level(snap.active_dpi_level)
    print("Device state fully restored.")


def cmd_profile(dev: G300sDevice, args):
    dev.set_active_profile(args.index)
    print(f"Active profile switched to {args.index}")


def cmd_dpi_level(dev: G300sDevice, args):
    dev.set_active_dpi_level(args.level)
    print(f"Active DPI level switched to {args.level}")


def cmd_led_live(dev: G300sDevice, args):
    dev.set_live_led(args.r, args.g, args.b)
    print(f"Live LED set to R={args.r} G={args.g} B={args.b} (RAM instant)")


def cmd_rate_live(dev: G300sDevice, args):
    dev.set_live_rate(args.hz)
    print(f"Live polling rate set to {args.hz}Hz (RAM instant)")


def cmd_profile_led(dev: G300sDevice, args):
    p = Profile.from_raw(dev.get_profile_raw(args.profile), args.profile)
    p.led = (args.r, args.g, args.b)
    dev.set_profile_raw(args.profile, p.to_raw())
    print(f"Profile {args.profile} persistent LED updated to R={args.r} G={args.g} B={args.b}")


def cmd_profile_rate(dev: G300sDevice, args):
    p = Profile.from_raw(dev.get_profile_raw(args.profile), args.profile)
    p.rate = args.hz
    dev.set_profile_raw(args.profile, p.to_raw())
    print(f"Profile {args.profile} persistent polling rate updated to {args.hz}Hz")


def cmd_profile_dpi(dev: G300sDevice, args):
    p = Profile.from_raw(dev.get_profile_raw(args.profile), args.profile)
    p.dpis[args.level] = args.dpi
    if args.default:
        p.default_dpi = args.level
    dev.set_profile_raw(args.profile, p.to_raw())
    print(f"Profile {args.profile} DPI[{args.level}] updated to {args.dpi}" + (" (Default)" if args.default else ""))


def cmd_profile_btn(dev: G300sDevice, args):
    p = Profile.from_raw(dev.get_profile_raw(args.profile), args.profile)
    p.buttons[args.button] = args.action
    dev.set_profile_raw(args.profile, p.to_raw())
    print(f"Profile {args.profile} Button {args.button} updated to: {args.action}")


def cmd_ledfx(dev: G300sDevice, args):
    name = args.effect.lower()
    seconds = args.duration
    fps = 20

    patterns = {
        "rainbow": lambda i: LED_CYCLE[(i // 4) % len(LED_CYCLE)],
        "blink": lambda i: LED_CYCLE[6] if (i // 8) % 2 else LED_CYCLE[7],
        "strobe": lambda i: LED_CYCLE[6] if (i // 2) % 2 else LED_CYCLE[7],
        "police": lambda i: LED_CYCLE[0] if (i // 6) % 2 else LED_CYCLE[4],
        "pulse": lambda i: LED_CYCLE[4] if (i // 20) % 2 else LED_CYCLE[7]
    }

    if name not in patterns:
        raise ValueError(f"Unknown effect '{name}'. Options: {list(patterns.keys())}")

    orig_led = dev.get_live_led()
    frame_func = patterns[name]
    end_time = time.time() + seconds
    i = 0
    print(f"Running '{name}' LED animation for {seconds}s (Ctrl+C to abort)...")
    try:
        while time.time() < end_time:
            dev.set_live_led(*frame_func(i))
            i += 1
            time.sleep(1.0 / fps)
    finally:
        dev.set_live_led(*orig_led)
    print("Animation completed.")


def cmd_monitor(dev: G300sDevice, args):
    print(f"Monitoring G300s hardware notifications for {args.duration}s (click DPI/Profile/Sniper buttons)...")
    def on_event(evt):
        state = "PRESSED" if evt["pressed"] else "RELEASED"
        print(f"[{time.strftime('%H:%M:%S')}] Hardware Button: {evt['action']} -> {state}")

    dev.listen_events(args.duration, on_event)
    print("Monitoring ended.")


def cmd_selftest(dev: G300sDevice, args):
    print("Running protocol round-trip validation and selftest...")
    state = dev.get_active_state()
    for i in range(3):
        raw = dev.get_profile_raw(i)
        p = Profile.from_raw(raw, i)
        serialized = p.to_raw()
        if serialized != raw:
            raise AssertionError(f"Roundtrip mismatch for profile {i}!\nOrig: {raw.hex()}\nPack: {serialized.hex()}")
        print(f"  [OK] Profile {i}: Verified 35 bytes serialize roundtrip")

    print(f"  [OK] Report 0xF0 parsing: Profile={state['profile']}, DPI={state['dpi_level']}, Busy={state['is_busy']}")
    live_led = dev.get_live_led()
    print(f"  [OK] Report 0xF1 live LED: {live_led}")
    live_rate = dev.get_live_rate()
    print(f"  [OK] Report 0xF2 live polling rate: {live_rate}Hz")
    print("Selftest PASSED. All hardware interfaces verified.")


def cmd_daemon(args):
    from .daemon.service import DaemonService
    service = DaemonService(config_path=args.config)
    print(f"Starting Logitech G300s Daemon (Config: {service.config_path})...")
    service.run_forever()


def main(argv: List[str] = None):
    if argv is None:
        argv = sys.argv[1:]

    parser = argparse.ArgumentParser(
        prog="g300s",
        description="Logitech G300s Onboard Memory & Realtime Hardware Control Tool"
    )
    subparsers = parser.add_subparsers(dest="command", required=True)

    # info
    subparsers.add_parser("info", help="Display active status and 3 profile configurations")

    # dump
    subparsers.add_parser("dump", help="Dump complete device configuration as JSON")

    # save
    p_save = subparsers.add_parser("save", help="Save backup to a JSON file")
    p_save.add_argument("filepath", help="Destination JSON path")

    # restore
    p_restore = subparsers.add_parser("restore", help="Restore backup from a JSON file")
    p_restore.add_argument("filepath", help="Source JSON path")

    # profile
    p_prof = subparsers.add_parser("profile", help="Switch active profile (0..2)")
    p_prof.add_argument("index", type=int, choices=[0, 1, 2], help="Profile index (0..2)")

    # dpi-level
    p_dpi_lvl = subparsers.add_parser("dpi-level", help="Switch active DPI level index (0..3)")
    p_dpi_lvl.add_argument("level", type=int, choices=[0, 1, 2, 3], help="DPI level (0..3)")

    # led-live
    p_led_live = subparsers.add_parser("led-live", help="Set instant live RAM LED color (zero flash wear)")
    p_led_live.add_argument("r", type=int, choices=[0, 1], help="Red channel (0 or 1)")
    p_led_live.add_argument("g", type=int, choices=[0, 1], help="Green channel (0 or 1)")
    p_led_live.add_argument("b", type=int, choices=[0, 1], help="Blue channel (0 or 1)")

    # rate-live
    p_rate_live = subparsers.add_parser("rate-live", help="Set instant live USB polling rate (zero flash wear)")
    p_rate_live.add_argument("hz", type=int, choices=[125, 250, 500, 1000], help="Rate in Hz")

    # led
    p_led = subparsers.add_parser("led", help="Set persistent profile LED color")
    p_led.add_argument("profile", type=int, choices=[0, 1, 2], help="Profile index")
    p_led.add_argument("r", type=int, choices=[0, 1], help="Red")
    p_led.add_argument("g", type=int, choices=[0, 1], help="Green")
    p_led.add_argument("b", type=int, choices=[0, 1], help="Blue")

    # rate
    p_rate = subparsers.add_parser("rate", help="Set persistent profile polling rate")
    p_rate.add_argument("profile", type=int, choices=[0, 1, 2], help="Profile index")
    p_rate.add_argument("hz", type=int, choices=[125, 250, 500, 1000], help="Rate in Hz")

    # dpi
    p_dpi = subparsers.add_parser("dpi", help="Configure profile DPI level")
    p_dpi.add_argument("profile", type=int, choices=[0, 1, 2], help="Profile index")
    p_dpi.add_argument("level", type=int, choices=[0, 1, 2, 3], help="DPI level index")
    p_dpi.add_argument("dpi", type=int, help="DPI value (250..2500 step 250)")
    p_dpi.add_argument("--default", action="store_true", help="Set as default DPI for profile")

    # btn
    p_btn = subparsers.add_parser("btn", help="Set profile button action")
    p_btn.add_argument("profile", type=int, choices=[0, 1, 2], help="Profile index")
    p_btn.add_argument("button", type=int, choices=list(range(9)), help="Button index (0..8)")
    p_btn.add_argument("action", help="Action spec (e.g. mouse:1, key:ctrl+c, dpi-up, dpi-alternate)")

    # ledfx
    p_fx = subparsers.add_parser("ledfx", help="Run animated live RGB effect")
    p_fx.add_argument("effect", choices=["rainbow", "blink", "strobe", "police", "pulse"], help="Effect name")
    p_fx.add_argument("duration", type=float, nargs="?", default=10.0, help="Duration in seconds (default 10)")

    # monitor
    p_mon = subparsers.add_parser("monitor", help="Listen for hardware button press/release events")
    p_mon.add_argument("duration", type=float, nargs="?", default=10.0, help="Duration in seconds (default 10)")

    # selftest
    subparsers.add_parser("selftest", help="Run self-test and protocol validation")

    # daemon
    p_daemon = subparsers.add_parser("daemon", help="Run background daemon for auto-profile, macros, and RGB")
    p_daemon.add_argument("--config", help="Custom path to daemon configuration JSON", default=None)

    # gui
    subparsers.add_parser("gui", help="Launch interactive desktop GUI application")

    if not argv:
        argv = ["gui"]

    parsed = parser.parse_args(argv)

    if parsed.command == "gui":
        from .gui import run_gui
        run_gui()
        return

    if parsed.command == "daemon":
        try:
            cmd_daemon(parsed)
        except KeyboardInterrupt:
            print("\nDaemon terminated.")
            sys.exit(0)
        return

    try:
        with G300sDevice() as dev:
            handlers = {
                "info": cmd_info,
                "dump": cmd_dump,
                "save": cmd_save,
                "restore": cmd_restore,
                "profile": cmd_profile,
                "dpi-level": cmd_dpi_level,
                "led-live": cmd_led_live,
                "rate-live": cmd_rate_live,
                "led": cmd_profile_led,
                "rate": cmd_profile_rate,
                "dpi": cmd_profile_dpi,
                "btn": cmd_profile_btn,
                "ledfx": cmd_ledfx,
                "monitor": cmd_monitor,
                "selftest": cmd_selftest
            }
            handler = handlers.get(parsed.command)
            if handler:
                handler(dev, parsed)
    except G300sError as e:
        print(f"Error: {e}", file=sys.stderr)
        sys.exit(1)
    except KeyboardInterrupt:
        print("\nAborted by user.")
        sys.exit(130)


if __name__ == "__main__":
    main()
