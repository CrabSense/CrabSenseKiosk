#!/usr/bin/env python3
"""
ups_full_dump.py — in RA TAT CA thong tin doc duoc tu UPS CyberPower, lien tuc.

Khac ups_monitor.py: script nay khong dung danh sach truong cung, ma tu parse
HID report descriptor cua firmware -> biet chinh xac tung report id co bao nhieu
field, kich thuoc bit, usage page/usage, logical min/max, unit/exponent.
Nho vay no in duoc ca nhung field la (vendor-specific) ma hang khong cong bo.

CHI DOC: chi goi get_feature_report(). Khong bao gio send_feature_report()
-> khong the tat UPS, khong doi cau hinh, khong kich hoat self-test.

Cai dat (1 lan):
    py -m pip install hidapi

Dung:
    py ups_full_dump.py                 # in lai moi 5 giay (Ctrl+C de dung)
    py ups_full_dump.py --interval 2
    py ups_full_dump.py --once          # in 1 lan
    py ups_full_dump.py --json          # JSON: moi field thanh 1 key
    py ups_full_dump.py --quiet         # chi in bang tom tat
    py ups_full_dump.py --selftest      # kiem tra parser, khong can UPS
"""
from __future__ import annotations

import argparse
import json
import sys
import time
from datetime import datetime

from ups_monitor import VENDOR_ID, find_ups_path

# ---- Field DA DOI CHIEU voi thiet bi that, tach theo report id ----
# Key: (report_id, usage_page, usage). Cung 1 usage (vd 0x30 Voltage) mang nghia
# khac nhau trong tung collection nen phai khoa theo report id.
RID_NAMES = {
    (0x08, 0x85, 0x66): ("RemainingCapacity", 1, "%"),
    (0x08, 0x85, 0x68): ("RunTimeToEmpty", 1, "s"),
    (0x08, 0x85, 0x2A): ("RemainingCapacityLimit", 1, "s"),
    (0x0A, 0x84, 0x30): ("BatteryVoltage", 10, "V"),
    (0x0B, 0x85, 0xD0): ("ACPresent", 1, ""),
    (0x0B, 0x85, 0x44): ("Charging", 1, ""),
    (0x0B, 0x85, 0x45): ("Discharging", 1, ""),
    (0x0B, 0x85, 0x42): ("BelowRemainingCapacityLimit", 1, ""),
    (0x0B, 0x85, 0x46): ("RemainingTimeLimitExpired", 1, ""),
    (0x0B, 0x85, 0x43): ("FullyCharged", 1, ""),
    (0x0E, 0x84, 0x40): ("InputVoltage", 1, "V"),
    (0x0F, 0x84, 0x30): ("OutputVoltage", 1, "V"),
    (0x10, 0x84, 0x53): ("LowVoltageTransfer", 1, "V"),
    (0x11, 0x84, 0x54): ("HighVoltageTransfer", 1, "V"),
    (0x12, 0x84, 0x30): ("InputVoltageMeasured", 1, "V"),
    (0x13, 0x84, 0x35): ("PercentLoad", 1, "%"),
    (0x14, 0x84, 0x58): ("TemperatureMode", 1, ""),
    (0x18, 0x84, 0x44): ("OutputVoltageConfig", 10, "V"),
}

# Fallback: usage da doi chieu o bat ky report nao
MEASURED = {
    (0x85, 0xD0): ("ACPresent", 1, ""),
    (0x85, 0x44): ("Charging", 1, ""),
    (0x85, 0x45): ("Discharging", 1, ""),
    (0x85, 0x42): ("BelowRemainingCapacityLimit", 1, ""),
    (0x85, 0x46): ("RemainingTimeLimitExpired", 1, ""),
    (0x85, 0x43): ("FullyCharged", 1, ""),
    (0x85, 0x66): ("RemainingCapacity", 1, "%"),
    (0x85, 0x68): ("RunTimeToEmpty", 1, "s"),
    (0x84, 0x35): ("PercentLoad", 1, "%"),
}

# ---- Ten theo HID Usage Tables, CHUA doi chieu -> in kem dau '?' ----
SPEC_NAMES = {
    0x84: {
        0x01: "iName", 0x02: "PresentStatus", 0x03: "ChangedStatus", 0x04: "UPS",
        0x05: "PowerSupply", 0x10: "BatterySystem", 0x11: "BatterySystemID",
        0x12: "Battery", 0x13: "BatteryID", 0x14: "BatteryCapacity",
        0x15: "BatteryEnergy", 0x16: "BatteryVoltage", 0x17: "BatteryCurrent",
        0x18: "BatteryTemperature", 0x1A: "PowerSummary", 0x1C: "PowerConverter",
        0x24: "Flow", 0x28: "Outlet", 0x30: "Voltage", 0x31: "Current",
        0x32: "Frequency", 0x33: "ApparentPower", 0x34: "ActivePower",
        0x35: "PercentLoad", 0x36: "Temperature", 0x37: "Humidity",
        0x38: "BadCount", 0x39: "ConfigVoltage", 0x3A: "ConfigCurrent",
        0x3B: "ConfigFrequency", 0x3C: "ConfigApparentPower",
        0x3D: "ConfigActivePower", 0x3E: "ConfigPercentLoad",
        0x3F: "ConfigTemperature",
        0x41: "SwitchOnControl", 0x42: "SwitchOffControl", 0x43: "ToggleControl",
        0x44: "LowVoltageTransfer", 0x45: "HighVoltageTransfer",
        0x46: "DelayBeforeReboot", 0x47: "DelayBeforeStartup",
        0x48: "DelayBeforeShutdown", 0x49: "Test", 0x4A: "AudibleAlarmControl",
        0x53: "LowVoltageTransfer2", 0x54: "HighVoltageTransfer2",
        0x57: "DelayBeforeStartup2", 0x58: "DelayBeforeShutdown2",
        0x66: "Test2", 0x68: "DelayBeforeShutdown3",
        0x83: "OutletSystem", 0x89: "ConfigActivePower2",
        0x8B: "ConfigPercentLoad2", 0x8C: "ConfigActivePower3",
        0x8D: "ConfigPercentLoad3", 0x8E: "ConfigTemperature2",
        0x8F: "ConfigHumidity2",
        0xFD: "iProduct", 0xFE: "iManufacturer", 0xFF: "iSerialNumber",
    },
    0x85: {
        0x01: "SMBBatteryMode", 0x29: "RemainingCapacityLimit",
        0x2A: "RemainingCapacityLimit2", 0x2C: "CapacityMode",
        0x40: "BelowRemainingCapacityLimit", 0x43: "FullyCharged",
        0x44: "Charging", 0x45: "Discharging",
        0x46: "RemainingTimeLimitExpired", 0x47: "RemainingTimeLimit",
        0x48: "DesignCapacity", 0x49: "FullChargeCapacity",
        0x53: "DesignCapacity2", 0x54: "FullChargeCapacity2",
        0x56: "ManufacturerDate", 0x57: "CapacityMode2",
        0x58: "CapacityGranularity1", 0x59: "CapacityGranularity2",
        0x5A: "AudibleAlarmControl", 0x66: "RemainingCapacity",
        0x67: "FullChargeCapacity3", 0x68: "RunTimeToEmpty",
        0x6E: "RemainingCapacityLimit3", 0xD0: "ACPresent",
    },
}

# Ma unit HID -> ky hieu
UNIT_SYMBOLS = {
    0x00F0D121: "V",
    0x00E0D121: "J",
    0x00D121: "W",
    0x00100001: "A",
    0x00010001: "K",
    0x00F001: "Hz",
    0x1001: "s",
}


class Field:
    __slots__ = ("usage_page", "usage", "bit_offset", "bit_size", "count",
                 "logical_min", "logical_max", "unit", "unit_exp", "report_id")

    def __init__(self, usage_page, usage, bit_offset, bit_size, count,
                 logical_min, logical_max, unit, unit_exp, report_id=0):
        self.usage_page = usage_page
        self.usage = usage
        self.bit_offset = bit_offset
        self.bit_size = bit_size
        self.count = count
        self.logical_min = logical_min
        self.logical_max = logical_max
        self.unit = unit
        self.unit_exp = unit_exp
        self.report_id = report_id

    @property
    def measured(self):
        return RID_NAMES.get((self.report_id, self.usage_page, self.usage)) \
            or MEASURED.get((self.usage_page, self.usage))

    def label(self):
        hit = self.measured
        if hit:
            return hit[0]
        spec = SPEC_NAMES.get(self.usage_page, {}).get(self.usage)
        if spec:
            return spec + "?"
        return f"usage_{self.usage_page:02x}_{self.usage:02x}"

    def values(self, payload: bytes):
        """payload = report bytes KHONG gom report id."""
        if self.bit_size == 0 or self.count == 0:
            return []
        data = int.from_bytes(payload, "little")
        mask = (1 << self.bit_size) - 1
        signed = self.logical_min < 0
        out = []
        for i in range(self.count):
            raw = (data >> (self.bit_offset + i * self.bit_size)) & mask
            if signed and raw >= (1 << (self.bit_size - 1)):
                raw -= 1 << self.bit_size
            out.append(raw)
        return out

    def physical(self, raw: int):
        """Chuoi gia tri vat ly CHI khi da doi chieu; nguoc lai None."""
        hit = self.measured
        if not hit or not hit[2]:
            return None
        _, scale, unit = hit
        text = f"{raw / scale:.1f}" if scale != 1 else str(raw)
        return f"{text} {unit}"


def _signed(raw: int, size: int) -> int:
    if size == 0:
        return 0
    bits = size * 8
    return raw - (1 << bits) if raw >= (1 << (bits - 1)) else raw


def parse_descriptor(desc: bytes):
    """Tra ve {"input"|"output"|"feature": {report_id: [Field, ...]}}."""
    result = {"input": {}, "output": {}, "feature": {}}
    g = dict(usage_page=0, logical_min=0, logical_max=0, report_size=0,
             report_count=0, report_id=0, unit=0, unit_exp=0)
    stack = []
    usages = []
    usage_min = usage_max = None
    offsets = {}
    i, n = 0, len(desc)

    while i < n:
        prefix = desc[i]
        i += 1
        size = prefix & 0x03
        size = 4 if size == 3 else size
        typ = (prefix >> 2) & 0x03
        tag = (prefix >> 4) & 0x0F
        raw = int.from_bytes(desc[i:i + size], "little") if size else 0
        i += size

        if typ == 1:  # Global
            if tag == 0:
                g["usage_page"] = raw
            elif tag == 1:
                g["logical_min"] = _signed(raw, size)
            elif tag == 2:
                g["logical_max"] = _signed(raw, size) if g["logical_min"] < 0 else raw
            elif tag == 5:
                nib = raw & 0x0F
                g["unit_exp"] = nib - 16 if nib >= 8 else nib
            elif tag == 6:
                g["unit"] = raw
            elif tag == 7:
                g["report_size"] = raw
            elif tag == 8:
                g["report_id"] = raw
            elif tag == 9:
                g["report_count"] = raw
            elif tag == 10:
                stack.append(dict(g))
            elif tag == 11 and stack:
                g = stack.pop()
        elif typ == 2:  # Local
            if tag == 0:
                if size == 4:
                    usages.append((raw >> 16, raw & 0xFFFF))
                else:
                    usages.append((g["usage_page"], raw))
            elif tag == 1:
                usage_min = raw
            elif tag == 2:
                usage_max = raw
        else:  # Main: 8=Input 9=Output 0xB=Feature 0xA=Collection 0xC=EndCollection
            if tag in (8, 9, 11):
                kind = {8: "input", 9: "output", 11: "feature"}[tag]
                fields = list(usages)
                if usage_min is not None and usage_max is not None:
                    fields += [(g["usage_page"], u) for u in range(usage_min, usage_max + 1)]
                rid = g["report_id"]
                key = (kind, rid)
                offset = offsets.get(key, 0)
                size_bits = g["report_size"]

                if not fields:
                    offset += size_bits * g["report_count"]  # padding
                elif len(fields) == 1:
                    result[kind].setdefault(rid, []).append(
                        Field(fields[0][0], fields[0][1], offset, size_bits,
                              g["report_count"], g["logical_min"], g["logical_max"],
                              g["unit"], g["unit_exp"], rid))
                    offset += size_bits * g["report_count"]
                else:
                    each = max(1, g["report_count"] // len(fields))
                    for page, usage in fields:
                        result[kind].setdefault(rid, []).append(
                            Field(page, usage, offset, size_bits, each,
                                  g["logical_min"], g["logical_max"],
                                  g["unit"], g["unit_exp"], rid))
                        offset += size_bits * each

                offsets[key] = offset
            usages = []
            usage_min = usage_max = None

    return result


def _hexdump(payload: bytes) -> str:
    return " ".join(f"{b:02x}" for b in payload)


def dump_device(dev, fields_by_kind, verbose=True):
    """In toan bo field cua moi feature report. Tra ve dict cho --json."""
    out = {}
    per_report = {}
    print(f"\n===== {datetime.now().strftime('%H:%M:%S')}  =====================")

    readable = 0
    for rid in sorted(fields_by_kind["feature"]):
        fields = fields_by_kind["feature"][rid]
        try:
            raw = dev.get_feature_report(rid, 64)
        except OSError:
            if verbose:
                print(f"\n[0x{rid:02x}] khong doc duoc (driver tu choi)")
            continue
        if not raw or raw[0] != rid:
            if verbose:
                print(f"\n[0x{rid:02x}] khong co du lieu")
            continue

        payload = bytes(raw[1:])
        readable += 1
        rows = []
        for f in fields:
            vals = f.values(payload)
            if not vals:
                continue
            label = f.label()
            if len(vals) == 1:
                phys = f.physical(vals[0])
                rows.append((label, vals[0], phys))
                out[label] = vals[0]
            else:
                rows.append((label, vals, None))
                out[label] = vals

        per_report[rid] = {"raw": _hexdump(payload), "fields": rows}
        if verbose:
            print(f"\n[0x{rid:02x}] {len(payload)} byte: {_hexdump(payload)}")
            for label, val, phys in rows:
                if isinstance(val, list):
                    print(f"   {label:<28} raw={val}")
                elif phys is None:
                    print(f"   {label:<28} raw={val}")
                else:
                    print(f"   {label:<28} raw={val:<10} {phys}")

    if verbose:
        print(f"\n[feature] doc duoc {readable}/{len(fields_by_kind['feature'])} report")
        if fields_by_kind["input"]:
            ids = ", ".join(f"0x{r:02x}" for r in sorted(fields_by_kind["input"]))
            print(f"[input]   report id co trong descriptor: {ids}")
            print("          interrupt IN - UPS chi gui khi co thay doi")

    out["_reports"] = per_report
    return out


def print_summary(data):
    """Bang tom tat nhung gi da doi chieu duoc."""
    online = data.get("ACPresent")
    if online is None:
        grid = "n/a"
    else:
        grid = "CO" if online else "MAT"

    runtime = data.get("RunTimeToEmpty")
    runtime_min = f"{runtime // 60} phut" if isinstance(runtime, int) else "n/a"

    battery_v = data.get("BatteryVoltage")
    battery_v_text = f"{battery_v / 10:.1f} V" if isinstance(battery_v, int) else "n/a"

    charge = data.get("RemainingCapacity")
    load = data.get("PercentLoad")

    print("\n--- TOM TAT ---")
    print(f"  dien luoi        : {grid}")
    print(f"  pin              : {charge if charge is not None else 'n/a'} %")
    print(f"  thoi gian con lai: {runtime_min}")
    print(f"  dien ap pin      : {battery_v_text}")
    print(f"  dien ap vao      : {data.get('InputVoltage', 'n/a')} V"
          + ("  (gia tri cuoi - dang chay pin)" if online == 0 else ""))
    print(f"  tai              : {load if load is not None else 'n/a'} %")
    for flag in ("Charging", "Discharging", "FullyCharged",
                 "BelowRemainingCapacityLimit", "RemainingTimeLimitExpired"):
        if flag in data:
            print(f"  {flag:<32}: {data[flag]}")


def run_selftest():
    fails = 0

    def check(name, got, want):
        nonlocal fails
        if got != want:
            print(f"FAIL {name}: got={got!r} want={want!r}")
            fails += 1

    # 1. bitfield 6 x 1 bit: byte 0x04 -> bit2 bat
    f = Field(0x85, 0xD0, 0, 1, 6, 0, 1, 0, 0, 0x0B)
    vals = f.values(b"\x04")
    check("bitfield len", len(vals), 6)
    check("ACPresent bit0", vals[0], 0)
    check("Discharging bit2", vals[2], 1)

    # 2. u16 little endian
    check("u16", Field(0x85, 0x68, 0, 16, 1, 0, 0xFFFF, 0, 0).values(b"\x0c\x30")[0], 12300)

    # 3. signed 8 bit
    check("signed -3", Field(0x84, 0x30, 0, 8, 1, -5, 5, 0, 0).values(b"\xfd")[0], -3)

    # 4. he so chia: 360 -> 36.0 V (report 0x0A = dien ap pin)
    check("voltage scale",
          Field(0x84, 0x30, 0, 16, 1, 0, 0xFFFF, 0, 0, 0x0A).physical(360), "36.0 V")

    # 5. parser: 0x0B co 6 field input + 6 field feature
    partial = bytes.fromhex(
        "0504" "a101" "0924" "a100" "0902" "a102"
        "850b" "0585" "09d0" "0944" "0945" "0942" "0946" "0943"
        "1500" "2501" "7501" "9506" "8122" "7502" "9501" "8103"
        "09d0" "0944" "0945" "0942" "0946" "0943"
        "1500" "2501" "7501" "9506" "b1a2" "7502" "9501" "b103"
    )
    parsed = parse_descriptor(partial)
    check("feature 0x0b fields", len(parsed["feature"].get(0x0B, [])), 6)
    check("input 0x0b fields", len(parsed["input"].get(0x0B, [])), 6)

    # 6. bit offset field thu 5 = 4
    check("bit offset #5", parsed["feature"][0x0B][4].bit_offset, 4)

    if fails == 0:
        print("SELFTEST OK (7 kiem tra)")
        return 0
    print(f"SELFTEST FAIL ({fails} loi)")
    return 1


def main(argv=None):
    ap = argparse.ArgumentParser(description="In tat ca du lieu UPS doc duoc (chi doc)")
    ap.add_argument("--once", action="store_true", help="in mot lan roi thoat")
    ap.add_argument("--json", action="store_true", help="in JSON thay vi bang")
    ap.add_argument("--interval", type=float, default=5.0, help="giay giua 2 lan in")
    ap.add_argument("--quiet", action="store_true", help="chi in bang tom tat")
    ap.add_argument("--selftest", action="store_true", help="kiem tra parser")
    args = ap.parse_args(argv)

    if args.selftest:
        return run_selftest()

    # Khi stdout bi redirect (chay nhu service / pipe) Python buffer theo block
    # -> ep line buffering de log ra ngay.
    try:
        sys.stdout.reconfigure(line_buffering=True)
    except (AttributeError, ValueError):
        pass

    import hid

    path = find_ups_path()
    if path is None:
        print("KHONG TIM THAY UPS CyberPower (VID_0764) qua HID.")
        return 2

    dev = hid.device()
    dev.open_path(path)
    try:
        model = dev.get_product_string() or "?"
    except OSError:
        model = "?"

    try:
        desc = bytes(dev.get_report_descriptor())
    except (OSError, AttributeError):
        print("Khong doc duoc HID report descriptor.")
        dev.close()
        return 3

    fields_by_kind = parse_descriptor(desc)
    verbose = not args.quiet and not args.json
    if verbose:
        print(f"UPS: {model}   descriptor {len(desc)} byte   "
              f"feature report: {len(fields_by_kind['feature'])}")
        print("Chi doc (get_feature_report). Ctrl+C de dung.")

    try:
        while True:
            data = dump_device(dev, fields_by_kind, verbose=verbose)
            if args.json:
                print(json.dumps({
                    "ts": datetime.now().astimezone().isoformat(),
                    "model": model,
                    "source": "hid-descriptor",
                    "fields": data,
                }, ensure_ascii=False, default=str), flush=True)
            else:
                print_summary(data)
            if args.once:
                return 0
            time.sleep(args.interval)
    except KeyboardInterrupt:
        print("\nDa dung.")
        return 0
    finally:
        dev.close()


if __name__ == "__main__":
    sys.exit(main())
