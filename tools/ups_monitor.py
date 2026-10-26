#!/usr/bin/env python3
"""
ups_monitor.py — doc trang thai UPS CyberPower (HID Power Device) va in ra terminal.

CHI DOC: chi goi get_feature_report(), khong bao gio send_feature_report()
-> khong the tat UPS, khong doi cau hinh, khong gui self-test.

Cai dat (1 lan):
    py -m pip install hidapi

Dung:
    py ups_monitor.py              # theo doi, 5 giay/lan
    py ups_monitor.py --once       # in 1 lan
    py ups_monitor.py --json       # in JSON (de day vao telemetry CrabSense)
    py ups_monitor.py --selftest   # kiem tra logic giai ma, khong can UPS
"""
from __future__ import annotations

import argparse
import json
import sys
import time
from datetime import datetime

# ---- Hang so HID Power Device (usage page 0x84 / 0x85) ----
REPORT_PRESENT_STATUS = 0x0B   # 6 bit: ACPresent, Charging, Discharging, ...
REPORT_BATTERY = 0x08          # RemainingCapacity(8b), RunTimeToEmpty(16b), RemainingCapacityLimit(16b)
REPORT_CONFIG_VOLTAGE = 0x09   # 16b, don vi 0.1 V
REPORT_VOLTAGE = 0x0A          # 16b, don vi 0.1 V  (dien ap pin)
REPORT_INPUT_VOLTAGE = 0x0F    # 16b  (PowerSummary.Voltage = dien ap vao)
REPORT_LOAD = 0x13             # 8b, % tai

# Thu tu bit trong report 0x0B, theo report descriptor cua firmware
PRESENT_BITS = [
    "ac_present",
    "charging",
    "discharging",
    "below_capacity_limit",
    "runtime_limit_expired",
    "fully_charged",
]

VENDOR_ID = 0x0764          # CyberPower
USAGE_PAGE_POWER_DEVICE = 0x84


def find_ups_path(vendor_id: int = VENDOR_ID):
    """Tra ve HID path cua UPS (interface Power Device), hoac None."""
    try:
        import hid
    except ImportError:
        raise SystemExit("Thieu thu vien hidapi. Chay: py -m pip install hidapi")

    fallback = None
    for d in hid.enumerate():
        if d.get("vendor_id") != vendor_id:
            continue
        if d.get("usage_page") == USAGE_PAGE_POWER_DEVICE:
            return d["path"]
        fallback = fallback or d["path"]
    return fallback


def _report(dev, report_id: int, length: int):
    """Doc feature report, tra ve list[int] (byte dau la report id)."""
    try:
        raw = dev.get_feature_report(report_id, length)
    except OSError:
        return None
    if not raw or raw[0] != report_id:
        return None
    return list(raw)


def _u16(raw, offset):
    return raw[offset] | (raw[offset + 1] << 8)


def decode_present_status(raw):
    bits = raw[1]
    return {name: bool(bits & (1 << i)) for i, name in enumerate(PRESENT_BITS)}


def decode_battery(raw):
    """Report 0x08: [id, remainingCapacity%, runtimeLow, runtimeHigh, limitLow, limitHigh]."""
    if len(raw) < 6:
        return {"charge_pct": None, "runtime_sec": None, "capacity_limit_sec": None}
    return {
        "charge_pct": raw[1],
        "runtime_sec": _u16(raw, 2),
        "capacity_limit_sec": _u16(raw, 4),
    }


def read_ups(dev):
    """Doc toan bo truong co the doc duoc. Tra ve dict (None neu thieu)."""
    out = {
        "online": None,
        "status": "UNKNOWN",
        "charge_pct": None,
        "runtime_min": None,
        "battery_voltage": None,
        "input_voltage": None,
        "load_pct": None,
        "charging": None,
        "raw_status_bits": None,
    }

    ps = _report(dev, REPORT_PRESENT_STATUS, 2)
    if ps:
        bits = decode_present_status(ps)
        out["raw_status_bits"] = ps[1]
        out["charging"] = bits["charging"]
        if bits["ac_present"]:
            out["online"] = True
            out["status"] = "AC"
        else:
            out["online"] = False
            charge = out["charge_pct"]
            low = bits["below_capacity_limit"] or bits["runtime_limit_expired"]
            out["status"] = "CRITICAL" if low else "BATTERY"

    bat = _report(dev, REPORT_BATTERY, 7)
    if bat:
        d = decode_battery(bat)
        out["charge_pct"] = d["charge_pct"]
        if d["runtime_sec"] is not None:
            out["runtime_min"] = round(d["runtime_sec"] / 60)

    v = _report(dev, REPORT_VOLTAGE, 3)
    if v:
        out["battery_voltage"] = round(_u16(v, 1) / 10, 1)

    vin = _report(dev, REPORT_INPUT_VOLTAGE, 3)
    if vin:
        val = _u16(vin, 1)
        out["input_voltage"] = round(val / 10, 1) if val else 0.0

    load = _report(dev, REPORT_LOAD, 2)
    if load:
        out["load_pct"] = load[1]

    # Sau khi co du lieu pin, sua lai CRITICAL cho chinh xac
    if out["online"] is False and out["charge_pct"] is not None:
        if out["charge_pct"] <= 20:
            out["status"] = "CRITICAL"

    return out


def format_line(ts, state):
    if state["online"] is None:
        return f"{ts}  KHONG DOC DUOC UPS"
    pct = f"{state['charge_pct']:3d}%" if state["charge_pct"] is not None else " n/a"
    if state["online"]:
        runtime = "n/a (dang co dien luoi)"
    elif state["runtime_min"] is not None:
        runtime = f"{state['runtime_min']} phut con lai"
    else:
        runtime = "n/a"
    extra = []
    if state["input_voltage"] is not None:
        extra.append(f"vao {state['input_voltage']:.0f}V")
    if state["battery_voltage"] is not None:
        extra.append(f"pin {state['battery_voltage']:.1f}V")
    if state["load_pct"] is not None:
        extra.append(f"tai {state['load_pct']}%")
    tail = ("  (" + ", ".join(extra) + ")") if extra else ""
    return f"{ts}  {state['status']:<9} pin {pct}  {runtime}{tail}"


def run_selftest():
    fails = 0

    def check(name, got, want):
        nonlocal fails
        if got != want:
            print(f"FAIL {name}: got={got!r} want={want!r}")
            fails += 1

    # 0x0B bit0 = ACPresent
    check("ac_present bit0", decode_present_status([0x0B, 0b00000001])["ac_present"], True)
    check("discharging bit2", decode_present_status([0x0B, 0b00000100])["discharging"], True)

    # 0x08: capacity 64%, runtime 12300s (205 min), limit 300s
    d = decode_battery([0x08, 64, 0x0C, 0x30, 0x2C, 0x01])
    check("charge_pct", d["charge_pct"], 64)
    check("runtime_sec", d["runtime_sec"], 12300)
    check("capacity_limit_sec", d["capacity_limit_sec"], 300)

    # 0x0A = 360 -> 36.0V
    check("voltage scale", round((360 | (0 << 8)) / 10, 1), 36.0)

    # format_line khong crash voi du lieu thieu
    line = format_line("00:00:00", {
        "online": False, "status": "BATTERY", "charge_pct": None,
        "runtime_min": None, "battery_voltage": None,
        "input_voltage": None, "load_pct": None,
    })
    check("format no-crash", line.startswith("00:00:00  BATTERY"), True)

    if fails == 0:
        print("SELFTEST OK (6 kiem tra)")
        return 0
    print(f"SELFTEST FAIL ({fails} loi)")
    return 1


def main(argv=None):
    ap = argparse.ArgumentParser(description="Doc trang thai UPS CyberPower (chi doc)")
    ap.add_argument("--once", action="store_true", help="in mot lan roi thoat")
    ap.add_argument("--json", action="store_true", help="in JSON (1 dong/lan)")
    ap.add_argument("--interval", type=int, default=5, help="giay giua 2 lan doc")
    ap.add_argument("--selftest", action="store_true", help="kiem tra logic giai ma")
    args = ap.parse_args(argv)

    if args.selftest:
        return run_selftest()

    import hid

    path = find_ups_path()
    if path is None:
        print("KHONG TIM THAY UPS CyberPower (VID_0764) qua HID.")
        print("Kiem tra: cap USB Type-B da cam, UPS dang bat.")
        return 2

    dev = hid.device()
    dev.open_path(path)
    try:
        model = dev.get_product_string() or "CyberPower"
    except OSError:
        model = "CyberPower"
    if not args.json:
        print(f"UPS: {model}  |  chi doc  |  Ctrl+C de dung\n")

    try:
        while True:
            state = read_ups(dev)
            ts = datetime.now().strftime("%H:%M:%S")

            if args.json:
                payload = {
                    "ts": datetime.now().astimezone().isoformat(),
                    "online": state["online"],
                    "status": state["status"],
                    "chargePct": state["charge_pct"],
                    "runtimeMin": state["runtime_min"],
                    "batteryVoltage": state["battery_voltage"],
                    "inputVoltage": state["input_voltage"],
                    "loadPct": state["load_pct"],
                    "charging": state["charging"],
                    "model": model,
                    "source": "hid-power-device",
                }
                print(json.dumps(payload, ensure_ascii=False), flush=True)
            else:
                print(format_line(ts, state), flush=True)

            if args.once:
                return 0
            time.sleep(args.interval)
    except KeyboardInterrupt:
        if not args.json:
            print("\nDa dung.")
        return 0
    finally:
        dev.close()


if __name__ == "__main__":
    sys.exit(main())
