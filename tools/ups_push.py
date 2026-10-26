#!/usr/bin/env python3
"""
ups_push.py — doc UPS CyberPower qua USB HID roi day len Kiosk.

Luong: UPS --USB HID--> ups_push.py --HTTP--> Kiosk (/api/ups/telemetry)
       Kiosk --TelemetryOutbox--> CrabSenseBE (/api/iot/sensor-data/batch)

UPS la thiet bi RIENG (khong phai ESP32/ACD301): khong co MAC/IP, khong dang
ky controller. Kiosk nhan qua endpoint /api/ups/telemetry nen khong bi chan boi
ControllerRegistry, nhung van dung outbox de retry khi BE mat ket noi.

CHI DOC HID: khong goi send_feature_report -> khong the tat UPS / doi cau hinh.

Dung:
    py tools/ups_push.py                       # 20s/lan, day len 127.0.0.1:8090
    py tools/ups_push.py --url http://kiosk:8090
    py tools/ups_push.py --device-code UPS-OLS1500ERT2U --interval 30
    py tools/ups_push.py --once                # gui 1 lan roi thoat
    py tools/ups_push.py --print               # in payload, khong gui
    py tools/ups_push.py --selftest            # kiem tra phan tao payload
"""
from __future__ import annotations

import argparse
import json
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone

from ups_full_dump import (derive_power, parse_descriptor, print_report,
                           read_all, report_lengths, va_nominal)
from ups_monitor import find_ups_path

DEFAULT_URL = "http://127.0.0.1:8090"
DEFAULT_DEVICE_CODE = "UPS-OLS1500ERT2U"


def build_readings(nut: dict, est: dict) -> list[dict]:
    """Chuyen bien NUT + so suy ra thanh danh sach reading cho Kiosk.

    Chi gui field co gia tri; khong bao gio gui NaN (Kiosk se tu choi).
    """
    def raw(key, div=1):
        value = nut.get(key)
        if not isinstance(value, (int, float)) or isinstance(value, bool):
            return None
        return round(value / div, 3)

    def derived(key):
        value = est.get(key) if est else None
        if not isinstance(value, (int, float)):
            return None
        return round(float(value), 3)

    rows = [
        ("ups_input_voltage", raw("input.voltage"), "V"),
        ("ups_output_voltage", raw("output.voltage"), "V"),
        ("ups_load", raw("ups.load"), "%"),
        ("ups_battery_charge", raw("battery.charge"), "%"),
        ("ups_battery_runtime", raw("battery.runtime"), "s"),
        ("ups_battery_voltage", raw("battery.voltage", 10), "V"),
        ("ups_status_online", raw("ups.status:OL"), "state"),
        ("ups_status_charging", raw("ups.status:CHRG"), "state"),
        ("ups_status_discharging", raw("ups.status:OB|DISCHRG"), "state"),
        ("ups_status_low_battery", raw("ups.status:LB"), "state"),
        ("ups_output_power", derived("output.realpower_est"), "W"),
        ("ups_output_apparent_power", derived("output.power_est"), "VA"),
        ("ups_output_current", derived("output.current_est"), "A"),
        ("ups_input_power", derived("input.realpower_est"), "W"),
        ("ups_input_apparent_power", derived("input.power_est"), "VA"),
        ("ups_input_current", derived("input.current_est"), "A"),
    ]
    return [{"sensor": code, "val": value, "unit": unit}
            for code, value, unit in rows if value is not None]


def build_payload(device_code, model, manufacturer, serial, nut, est) -> dict:
    return {
        "deviceCode": device_code,
        "model": model,
        "manufacturer": manufacturer,
        "serial": serial,
        "measuredAt": datetime.now(timezone.utc).isoformat(),
        "readings": build_readings(nut, est),
    }


def post_payload(url: str, payload: dict, timeout: float = 8.0) -> tuple[bool, str]:
    body = json.dumps(payload).encode("utf-8")
    request = urllib.request.Request(
        url.rstrip("/") + "/api/ups/telemetry",
        data=body,
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return True, response.read(200).decode("utf-8", "replace")
    except urllib.error.HTTPError as exc:
        return False, f"HTTP {exc.code}: {exc.read(300).decode('utf-8', 'replace')}"
    except (urllib.error.URLError, OSError) as exc:
        return False, str(exc)


def read_ups(dev, fields_by_kind, lens, model=None) -> dict:
    reports = read_all(dev, fields_by_kind, lens)
    nut = print_report(reports, verbose=False)["nut"]
    est = derive_power(nut, va_nominal(model, nut.get("ups.realpower.nominal")))
    return {"nut": nut, "est": est}


def open_device():
    import hid

    path = find_ups_path()
    if path is None:
        return None
    dev = hid.device()
    dev.open_path(path)
    return dev


def device_identity(dev) -> tuple[str, str, str]:
    def safe(fn):
        try:
            return fn() or ""
        except OSError:
            return ""

    return (safe(dev.get_product_string),
            safe(dev.get_manufacturer_string),
            safe(dev.get_serial_number_string))


def run_selftest() -> int:
    fails = 0

    def check(name, got, want):
        nonlocal fails
        if got != want:
            print(f"FAIL {name}: got={got!r} want={want!r}")
            fails += 1

    nut = {
        "input.voltage": 230, "output.voltage": 229, "ups.load": 20,
        "battery.charge": 59, "battery.runtime": 10500, "battery.voltage": 360,
        "ups.status:OL": 1, "ups.status:CHRG": 0,
        "ups.status:OB|DISCHRG": 0, "ups.status:LB": 0,
        "ups.realpower.nominal": 1350,
    }
    est = derive_power(nut, va_nominal("OLS1500ERT2U", 1350))
    readings = {r["sensor"]: r["val"] for r in build_readings(nut, est)}

    check("co du 16 reading", len(readings), 16)
    check("battery voltage 0.1V", readings["ups_battery_voltage"], 36.0)
    check("output W", readings["ups_output_power"], 270.0)
    check("output VA", readings["ups_output_apparent_power"], 300.0)
    check("output A", readings["ups_output_current"], round(300 / 229, 3))
    check("input W", readings["ups_input_power"], 300.0)
    check("status online", readings["ups_status_online"], 1)

    # Mat dien luoi: dau vao 0 W, khong phai bo trong
    nut["input.voltage"] = 0
    est0 = derive_power(nut, 1500)
    zero = {r["sensor"]: r["val"] for r in build_readings(nut, est0)}
    check("input W khi mat dien", zero["ups_input_power"], 0.0)

    # Thieu du lieu -> bo field, khong gui NaN
    thin = build_readings({"ups.load": None}, {})
    check("thieu du lieu -> rong", thin, [])

    # Payload dung contract cua Kiosk
    payload = build_payload("UPS-TEST", "m", "mfr", "", nut, est0)
    check("payload co deviceCode", payload["deviceCode"], "UPS-TEST")
    check("payload co readings", len(payload["readings"]) > 0, True)
    check("moi reading co du 3 khoa",
          all(set(r) == {"sensor", "val", "unit"} for r in payload["readings"]), True)

    if fails == 0:
        print("SELFTEST OK (13 kiem tra)")
        return 0
    print(f"SELFTEST FAIL ({fails} loi)")
    return 1


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="Day du lieu UPS len Kiosk (chi doc HID)")
    ap.add_argument("--url", default=DEFAULT_URL, help="dia chi Kiosk")
    ap.add_argument("--device-code", default=DEFAULT_DEVICE_CODE)
    ap.add_argument("--interval", type=float, default=20.0)
    ap.add_argument("--once", action="store_true")
    ap.add_argument("--print", dest="dry_run", action="store_true",
                    help="in payload, khong gui")
    ap.add_argument("--selftest", action="store_true")
    args = ap.parse_args(argv)

    if args.selftest:
        return run_selftest()

    try:
        sys.stdout.reconfigure(line_buffering=True)
    except (AttributeError, ValueError):
        pass

    dev = None
    fields_by_kind = lens = None
    model = manufacturer = serial = ""

    while True:
        ok = False
        try:
            if dev is None:
                dev = open_device()
                if dev is None:
                    print("KHONG TIM THAY UPS CyberPower (VID_0764). Thu lai sau 10s.")
                    time.sleep(10)
                    continue
                model, manufacturer, serial = device_identity(dev)
                fields_by_kind = parse_descriptor(bytes(dev.get_report_descriptor()))
                lens = report_lengths(fields_by_kind)
                print(f"UPS: {manufacturer} / {model} / serial={serial or '(rong)'}")
                print(f"Gui len {args.url}  deviceCode={args.device_code}  {args.interval:.0f}s/lan")

            data = read_ups(dev, fields_by_kind, lens, model)
            payload = build_payload(args.device_code, model, manufacturer, serial,
                                    data["nut"], data["est"])

            if args.dry_run:
                ok = True
                print(json.dumps(payload, ensure_ascii=False))
            else:
                ok, message = post_payload(args.url, payload)
                stamp = datetime.now().strftime("%H:%M:%S")
                if ok:
                    print(f"{stamp} OK {len(payload['readings'])} reading"
                          f" | charge={data['nut'].get('battery.charge')}%"
                          f" load={data['nut'].get('ups.load')}%")
                else:
                    # Kiosk chua chay / BE loi: giu vong lap, thu lai lan sau
                    print(f"{stamp} LOI {message}")

            if args.once:
                return 0 if ok else 1
            time.sleep(args.interval)

        except KeyboardInterrupt:
            print("\nDa dung.")
            return 0
        except OSError as exc:
            # Rut USB / driver loi -> dong va mo lai o vong sau
            print(f"Mat ket noi UPS ({exc}); mo lai...")
            try:
                dev.close()
            except Exception:
                pass
            dev = None
            time.sleep(5)


if __name__ == "__main__":
    sys.exit(main())
