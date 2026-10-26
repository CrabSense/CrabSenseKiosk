#!/usr/bin/env python3
"""
ups_full_dump.py — in RA MOI thong tin doc duoc tu UPS CyberPower, lien tuc.

Nguon doi chieu (open source):
  * NUT drivers/cps-hid.c      - bang HID2NUT rieng cho CyberPower
  * NUT drivers/libhid.c       - bang ten usage
  * USB HID Usage Tables for HID Power Devices v1.1 (pdcv11.pdf)

Diem khac biet so voi doc "tho":
  * Dung COLLECTION PATH (UPS.PowerSummary / UPS.Input / UPS.Output) vi cung
    mot usage 0x84.0x30 mang nghia khac nhau o moi collection -> neu chi doc
    report id se doc sai (vd 0x0F la dien ap vao, 0x12 moi la dien ap ra).
  * Tinh dung do dai tung report tu descriptor de get_feature_report khong bi
    tu choi (report 0x28/0x29 co 7 byte, khong phai 64).
  * Gan ten NUT (battery.charge, input.voltage, ups.load...) de doi chieu
    voi tai lieu NUT.

CHI DOC: khong bao gio goi send_feature_report()
-> khong the tat UPS, khong doi cau hinh, khong kich hoat self-test.

Cai dat:  py -m pip install hidapi
Dung:
    py ups_full_dump.py                # in lai moi 5 giay
    py ups_full_dump.py --quiet        # chi bang tom tat
    py ups_full_dump.py --json         # JSON
    py ups_full_dump.py --nut          # in ten bien NUT
    py ups_full_dump.py --selftest     # kiem tra parser, khong can UPS
"""
from __future__ import annotations

import argparse
import json
import math
import sys
import time
from datetime import datetime

from ups_monitor import VENDOR_ID, find_ups_path

# =====================================================================
# Bang usage — Power Device Page 0x84 (PDC v1.0 + v1.1)
# =====================================================================
USAGE_PD = {
    0x01: "iName", 0x02: "PresentStatus", 0x03: "ChangedStatus",
    0x04: "UPS", 0x05: "PowerSupply",
    0x10: "BatterySystem", 0x11: "BatterySystemID", 0x12: "Battery",
    0x13: "BatteryID", 0x14: "Charger", 0x15: "ChargerID",
    0x16: "PowerConverter", 0x17: "PowerConverterID",
    0x18: "OutletSystem", 0x19: "OutletSystemID",
    0x1A: "Input", 0x1B: "InputID", 0x1C: "Output", 0x1D: "OutputID",
    0x1E: "Flow", 0x1F: "FlowID", 0x20: "Outlet", 0x21: "OutletID",
    0x22: "Gang", 0x23: "GangID", 0x24: "PowerSummary", 0x25: "PowerSummaryID",
    0x30: "Voltage", 0x31: "Current", 0x32: "Frequency",
    0x33: "ApparentPower", 0x34: "ActivePower", 0x35: "PercentLoad",
    0x36: "Temperature", 0x37: "Humidity", 0x38: "BadCount",
    0x40: "ConfigVoltage", 0x41: "ConfigCurrent", 0x42: "ConfigFrequency",
    0x43: "ConfigApparentPower", 0x44: "ConfigActivePower",
    0x45: "ConfigPercentLoad", 0x46: "ConfigTemperature", 0x47: "ConfigHumidity",
    0x50: "SwitchOnControl", 0x51: "SwitchOffControl", 0x52: "ToggleControl",
    0x53: "LowVoltageTransfer", 0x54: "HighVoltageTransfer",
    0x55: "DelayBeforeReboot", 0x56: "DelayBeforeStartup",
    0x57: "DelayBeforeShutdown", 0x58: "Test", 0x59: "ModuleReset",
    0x5A: "AudibleAlarmControl",
    0x60: "Present", 0x61: "Good", 0x62: "InternalFailure",
    0x63: "VoltageOutOfRange", 0x64: "FrequencyOutOfRange", 0x65: "Overload",
    0x66: "OverCharged", 0x67: "OverTemperature", 0x68: "ShutdownRequested",
    0x69: "ShutdownImminent", 0x6B: "SwitchOnOff", 0x6C: "Switchable",
    0x6D: "Used", 0x6E: "Boost", 0x6F: "Buck", 0x70: "Initialized",
    0x71: "Tested", 0x72: "AwaitingPower", 0x73: "CommunicationLost",
    0xFD: "iManufacturer", 0xFE: "iProduct", 0xFF: "iSerialNumber",
}

# =====================================================================
# Bang usage — Battery System Page 0x85 (PDC v1.1, muc 4.2.x)
# =====================================================================
USAGE_BAT = {
    0x01: "SMBBatteryMode", 0x14: "OptionalMfgFunction5",
    0x15: "ConnectionToSMBus", 0x16: "OutputConnection",
    0x17: "ChargerConnection", 0x18: "BatteryInsertion", 0x19: "Usenext",
    0x1A: "OKToUse", 0x1B: "BatterySupported", 0x1C: "SelectorRevision",
    0x1D: "ChargingIndicator",
    0x28: "ManufacturerAccess", 0x29: "RemainingCapacityLimit",
    0x2A: "RemainingTimeLimit", 0x2B: "AtRate", 0x2C: "CapacityMode",
    0x2D: "BroadcastToCharger", 0x2E: "PrimaryBattery",
    0x2F: "ChargeController",
    0x40: "TerminateCharge", 0x41: "TerminateDischarge",
    0x42: "BelowRemainingCapacityLimit", 0x43: "RemainingTimeLimitExpired",
    0x44: "Charging", 0x45: "Discharging", 0x46: "FullyCharged",
    0x47: "FullyDischarged", 0x48: "ConditioningFlag",
    0x65: "AbsoluteStateOfCharge", 0x66: "RemainingCapacity",
    0x67: "FullChargeCapacity", 0x68: "RunTimeToEmpty",
    0x69: "AverageTimeToEmpty", 0x6A: "AverageTimeToFull",
    0x6B: "CycleCount",
    0x80: "BattPackModelLevel", 0x81: "InternalChargeController",
    0x82: "PrimaryBatterySupport", 0x83: "DesignCapacity",
    0x84: "SpecificationInfo", 0x85: "ManufacturerDate",
    0x86: "SerialNumber", 0x87: "iManufacturerName", 0x88: "iDevicename",
    0x89: "iDeviceChemistry", 0x8A: "ManufacturerData",
    0x8B: "Rechargable", 0x8C: "WarningCapacityLimit",
    0x8D: "CapacityGranularity1", 0x8E: "CapacityGranularity2",
    0x8F: "iOEMInformation",
    0xD0: "ACPresent",
}

USAGE_TABLES = {0x84: USAGE_PD, 0x85: USAGE_BAT}

# =====================================================================
# Bien NUT — lay tu NUT drivers/cps-hid.c (cps_hid2nut)
# Key = duong dan collection + usage name
# =====================================================================
NUT_VARS = {
    "UPS.PowerSummary.RemainingCapacity": ("battery.charge", "%"),
    "UPS.PowerSummary.RunTimeToEmpty": ("battery.runtime", "s"),
    "UPS.PowerSummary.RemainingCapacityLimit": ("battery.charge.low", "%"),
    "UPS.PowerSummary.WarningCapacityLimit": ("battery.charge.warning", "%"),
    "UPS.PowerSummary.RemainingTimeLimit": ("battery.runtime.low", "s"),
    "UPS.PowerSummary.ConfigVoltage": ("battery.voltage.nominal", "0.1V"),
    "UPS.PowerSummary.Voltage": ("battery.voltage", "0.1V"),
    "UPS.PowerSummary.FullChargeCapacity": ("battery.status", "%"),
    "UPS.PowerSummary.DesignCapacity": ("(unmapped) designcapacity", ""),
    "UPS.PowerSummary.CapacityMode": ("(unmapped) capacitymode", ""),
    "UPS.PowerSummary.CapacityGranularity1": ("(unmapped) capacitygranularity1", ""),
    "UPS.PowerSummary.CapacityGranularity2": ("(unmapped) capacitygranularity2", ""),
    "UPS.PowerSummary.AudibleAlarmControl": ("ups.beeper.status", ""),
    "UPS.PowerSummary.Temperature": ("ups.temperature", "K"),
    "UPS.PowerSummary.iProduct": ("ups.model", "stringidx"),
    "UPS.PowerSummary.iSerialNumber": ("ups.serial", "stringidx"),
    "UPS.PowerSummary.iManufacturer": ("ups.mfr", "stringidx"),
    "UPS.PowerSummary.iDeviceChemistry": ("battery.type", "stringidx"),
    "UPS.PowerSummary.iOEMInformation": ("battery.mfr.date", "stringidx"),
    "UPS.Input.Voltage": ("input.voltage", "V"),
    "UPS.Input.ConfigVoltage": ("input.voltage.nominal", "V"),
    "UPS.Input.Frequency": ("input.frequency", "0.1Hz"),
    "UPS.Input.LowVoltageTransfer": ("input.transfer.low", "V"),
    "UPS.Input.HighVoltageTransfer": ("input.transfer.high", "V"),
    "UPS.Output.Voltage": ("output.voltage", "V"),
    "UPS.Output.ConfigVoltage": ("output.voltage.nominal", "V"),
    "UPS.Output.Frequency": ("output.frequency", "0.1Hz"),
    "UPS.Output.PercentLoad": ("ups.load", "%"),
    "UPS.Output.ApparentPower": ("ups.power", "VA"),
    "UPS.Output.ConfigApparentPower": ("ups.power.nominal", "VA"),
    "UPS.Output.ActivePower": ("ups.realpower", "W"),
    "UPS.Output.ConfigActivePower": ("ups.realpower.nominal", "W"),
    "UPS.Output.Test": ("ups.test.result", ""),
    "UPS.Output.Boost": ("ups.status:BOOST", "bool"),
    "UPS.Output.Overload": ("ups.status:OVER", "bool"),
    "UPS.Output.DelayBeforeShutdown": ("ups.timer.shutdown", "s"),
    "UPS.Output.DelayBeforeStartup": ("ups.timer.start", "s"),
    "UPS.Output.DelayBeforeReboot": ("ups.timer.reboot", "s"),
    "UPS.PowerSummary.PresentStatus.ACPresent": ("ups.status:OL", "bool"),
    "UPS.PowerSummary.PresentStatus.Charging": ("ups.status:CHRG", "bool"),
    "UPS.PowerSummary.PresentStatus.Discharging": ("ups.status:OB|DISCHRG", "bool"),
    "UPS.PowerSummary.PresentStatus.BelowRemainingCapacityLimit": ("ups.status:LB", "bool"),
    "UPS.PowerSummary.PresentStatus.FullyCharged": ("(flag) fullycharged", "bool"),
    "UPS.PowerSummary.PresentStatus.RemainingTimeLimitExpired": ("(flag) timelimitexpired", "bool"),
}

# Gia tri vat ly da doi chieu tren chinh thiet bi nay.
# Key = ten usage trong dung collection -> (he so chia, don vi, ghi chu)
SCALE = {
    ("UPS.PowerSummary", "Voltage"): (10, "V", "pin 3x12V: 360 -> 36.0V"),
    ("UPS.PowerSummary", "ConfigVoltage"): (10, "V", "dinh muc pin"),
    ("UPS.PowerSummary", "RemainingCapacity"): (1, "%", ""),
    ("UPS.PowerSummary", "FullChargeCapacity"): (1, "%", ""),
    ("UPS.PowerSummary", "RemainingCapacityLimit"): (1, "%", ""),
    ("UPS.PowerSummary", "WarningCapacityLimit"): (1, "%", ""),
    ("UPS.PowerSummary", "RunTimeToEmpty"): (1, "s", ""),
    ("UPS.PowerSummary", "RemainingTimeLimit"): (1, "s", ""),
    ("UPS.Input", "Voltage"): (1, "V", "0 khi mat dien luoi"),
    ("UPS.Input", "ConfigVoltage"): (1, "V", "dien ap danh dinh"),
    ("UPS.Input", "LowVoltageTransfer"): (1, "V", ""),
    ("UPS.Input", "HighVoltageTransfer"): (1, "V", ""),
    ("UPS.Output", "Voltage"): (1, "V", "dien ap ra"),
    ("UPS.Output", "PercentLoad"): (1, "%", ""),
    ("UPS.Output", "ConfigActivePower"): (1, "W", "cong suat thuc dinh muc"),
    ("UPS.Output", "DelayBeforeShutdown"): (1, "s", ""),
    ("UPS.Output", "DelayBeforeStartup"): (1, "s", ""),
}

# =====================================================================
# Cong suat SUY RA — may KHONG do W/A tren duong USB.
#
# Da quet toan bo 0x00..0x60 (ke ca report khong khai bao) va doi chieu log
# NUT cua OLS1000E cung dong: khong ton tai usage 0x31 (Current),
# 0x33 (ApparentPower), 0x34 (ActivePower), 0x32 (Frequency) o bat ky report
# nao. LCD hien W/A vi no doc truc tiep tu DSP, khong qua HID.
#
# Nen moi so duoi day deu co dau ~ va co cong thuc goc:
#   real_nom = report 0x18  (1350 W — may tu khai)
#   pf_out   = real_nom / VA_nominal  (1350/1500 = 0.9, khop datasheet)
#   out_W    = load% x real_nom / 100
#   out_VA   = out_W / pf_out
#   out_A    = out_VA / output.voltage
#   in_W     = out_W / hieu_suat   (chi khi co dien luoi, mat dien -> 0)
#   in_A     = (in_W / pf_in) / input.voltage
# =====================================================================
DEFAULT_OUTPUT_PF = 0.9    # datasheet: Power Factor 0.9 (1350/1500)
DEFAULT_INPUT_PF = 0.98    # datasheet: Input Power Factor 0.98
DEFAULT_EFFICIENCY = 0.90  # online double conversion; ECO >0.95


def va_nominal(model: str, real_nom) -> float | None:
    """Lay VA dinh muc: uu tien so trong ten model (OLS1500... = 1500 VA),
    fallback real_nom / pf_out."""
    import re
    m = re.search(r"(\d{3,5})", model or "")
    if m and int(m.group(1)) >= 500:
        return float(m.group(1))
    if real_nom:
        return float(real_nom) / DEFAULT_OUTPUT_PF
    return None


def derive_power(nut: dict, va_nom, out_pf=DEFAULT_OUTPUT_PF,
                 in_pf=DEFAULT_INPUT_PF, ef=DEFAULT_EFFICIENCY) -> dict:
    """Suy ra W/VA/A. Tra ve dict rong neu thieu du lieu goc."""
    load = nut.get("ups.load")
    real_nom = nut.get("ups.realpower.nominal")
    out_v = nut.get("output.voltage")
    in_v = nut.get("input.voltage")
    if load is None or not real_nom:
        return {}

    out_w = load / 100.0 * float(real_nom)
    res = {"output.realpower_est": out_w,
           "output.power_est": out_w / out_pf}
    if out_v:
        res["output.current_est"] = res["output.power_est"] / float(out_v)

    # Mat dien luoi => dau vao = 0 W that su, khong phai uoc luong.
    if in_v:
        in_w = out_w / ef
        res["input.realpower_est"] = in_w
        res["input.power_est"] = in_w / in_pf
        res["input.current_est"] = res["input.power_est"] / float(in_v)
    elif in_v == 0:
        res["input.realpower_est"] = 0.0
        res["input.power_est"] = 0.0
        res["input.current_est"] = 0.0

    res["_basis"] = {
        "load_pct": load,
        "output.realpower.nominal_measured": real_nom,
        "va_nominal": va_nom,
        "output_pf": out_pf,
        "input_pf": in_pf,
        "efficiency": ef,
        "estimated": True,
        "note": "may khong do W/A tren USB; suy ra tu load% x cong suat dinh muc",
    }
    return res

ENUMS = {
    ("UPS.Output", "Test"): {0: "no test", 1: "quick test", 2: "deep test", 3: "abort", 4: "passed", 5: "failed"},
    ("UPS.PowerSummary", "AudibleAlarmControl"): {1: "disabled", 2: "enabled", 3: "muted"},
    ("UPS.PowerSummary", "CapacityMode"): {0: "mAh/mWh", 1: "percent", 2: "percent?"},
}


class Field:
    __slots__ = ("page", "usage", "offset", "size", "count", "path", "kind",
                 "rid", "logical_min", "logical_max", "units", "unit_exp")

    def __init__(self, page, usage, offset, size, count, path, kind, rid,
                 logical_min=0, logical_max=0, units=0, unit_exp=0):
        self.page = page
        self.usage = usage
        self.offset = offset
        self.size = size
        self.count = count
        self.path = path          # vd "UPS.PowerSummary.PresentStatus"
        self.kind = kind
        self.rid = rid
        self.logical_min = logical_min
        self.logical_max = logical_max
        self.units = units
        self.unit_exp = unit_exp

    @property
    def name(self):
        return USAGE_TABLES.get(self.page, {}).get(self.usage, f"usage_{self.page:02x}_{self.usage:02x}")

    @property
    def full_path(self):
        return f"{self.path}.{self.name}" if self.path else self.name

    def nut(self):
        return NUT_VARS.get(self.full_path)

    def values(self, payload: bytes):
        if self.size == 0 or self.count == 0:
            return []
        raw = int.from_bytes(payload, "little")
        mask = (1 << self.size) - 1
        signed = self.logical_min < 0
        out = []
        for i in range(self.count):
            v = (raw >> (self.offset + i * self.size)) & mask
            if signed and v >= (1 << (self.size - 1)):
                v -= 1 << self.size
            out.append(v)
        return out

    def interpret(self, value: int) -> str:
        enum = ENUMS.get((self.path, self.name))
        if enum and value in enum:
            return enum[value]
        hit = SCALE.get((self.path, self.name))
        if hit:
            div, unit, _note = hit
            text = f"{value / div:.1f}" if div != 1 else str(value)
            return f"{text} {unit}".strip()
        return ""


def _signed(raw: int, size: int) -> int:
    if size == 0:
        return 0
    bits = size * 8
    return raw - (1 << bits) if raw >= (1 << (bits - 1)) else raw


def parse_descriptor(desc: bytes):
    """Tra ve {"input"|"output"|"feature": {rid: [Field]}}, co collection path."""
    result = {"input": {}, "output": {}, "feature": {}}
    g = dict(page=0, lmin=0, lmax=0, rsize=0, rcount=0, rid=0, unit=0, uexp=0)
    stack = []
    collections: list[str] = []
    usages: list[tuple[int, int]] = []
    umin = umax = None
    offsets: dict[tuple[str, int], int] = {}
    i, n = 0, len(desc)

    def add(kind, rid, page, usage, offset, size, count):
        path = ".".join(collections)
        result[kind].setdefault(rid, []).append(
            Field(page, usage, offset, size, count, path, kind, rid,
                  g["lmin"], g["lmax"], g["unit"], g["uexp"]))

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
                g["page"] = raw
            elif tag == 1:
                g["lmin"] = _signed(raw, size)
            elif tag == 2:
                g["lmax"] = _signed(raw, size) if g["lmin"] < 0 else raw
            elif tag == 5:
                nib = raw & 0x0F
                g["uexp"] = nib - 16 if nib >= 8 else nib
            elif tag == 6:
                g["unit"] = raw
            elif tag == 7:
                g["rsize"] = raw
            elif tag == 8:
                g["rid"] = raw
            elif tag == 9:
                g["rcount"] = raw
            elif tag == 10:
                stack.append(dict(g))
            elif tag == 11 and stack:
                g = stack.pop()
        elif typ == 2:  # Local
            if tag == 0:
                if size == 4:
                    usages.append((raw >> 16, raw & 0xFFFF))
                else:
                    usages.append((g["page"], raw))
            elif tag == 1:
                umin = raw
            elif tag == 2:
                umax = raw
        else:  # Main
            if tag == 0xA:  # Collection
                page, usage = (usages[0] if usages else (g["page"], 0))
                table = USAGE_TABLES.get(page, {})
                name = table.get(usage)
                if not name:
                    # page 0xFF01 = khoi vendor rieng cua CyberPower, khong co
                    # trong PDC spec lan NUT -> khong the dat ten
                    name = (f"VENDOR_{page:04x}_{usage:02x}" if page not in (0x84, 0x85)
                            else f"col_{usage:02x}")
                collections.append(name)
            elif tag == 0xC:  # EndCollection
                if collections:
                    collections.pop()
            elif tag in (8, 9, 11):  # Input / Output / Feature
                kind = {8: "input", 9: "output", 11: "feature"}[tag]
                items = list(usages)
                if umin is not None and umax is not None:
                    items += [(g["page"], u) for u in range(umin, umax + 1)]
                key = (kind, g["rid"])
                offset = offsets.get(key, 0)
                rsize, rcount = g["rsize"], g["rcount"]

                if not items:
                    offset += rsize * rcount
                elif len(items) == 1:
                    add(kind, g["rid"], items[0][0], items[0][1], offset, rsize, rcount)
                    offset += rsize * rcount
                else:
                    each = max(1, rcount // len(items))
                    for page, usage in items:
                        add(kind, g["rid"], page, usage, offset, rsize, each)
                        offset += rsize * each
                offsets[key] = offset
            usages = []
            umin = umax = None

    return result


def report_lengths(fields_by_kind):
    """Do dai byte CAN THIET cua tung report (gom 1 byte report id)."""
    lens = {}
    for kind, by_id in fields_by_kind.items():
        lens[kind] = {}
        for rid, fields in by_id.items():
            bits = max((f.offset + f.size * f.count) for f in fields)
            lens[kind][rid] = 1 + math.ceil(bits / 8)
    return lens


def read_all(dev, fields_by_kind, lens):
    """Doc moi feature report. Tra ve list[(rid, payload, fields)]."""
    out = []
    for rid in sorted(fields_by_kind["feature"]):
        want = lens["feature"][rid]
        try:
            raw = dev.get_feature_report(rid, want)
        except OSError as exc:
            out.append((rid, None, exc))
            continue
        if not raw or raw[0] != rid:
            out.append((rid, None, "no data"))
            continue
        out.append((rid, bytes(raw[1:want]), fields_by_kind["feature"][rid]))
    return out


def print_report(reports, verbose=True, show_nut=False):
    data = {}
    ok = 0
    if verbose:
        print(f"\n===== {datetime.now().strftime('%H:%M:%S')}  =====================")
    for rid, payload, fields in reports:
        if payload is None:
            if verbose:
                print(f"\n[0x{rid:02x}] {fields}")
            continue
        ok += 1
        if verbose:
            print(f"\n[0x{rid:02x}] {len(payload)}B: " + " ".join(f"{b:02x}" for b in payload))
        for f in fields:
            vals = f.values(payload)
            if not vals:
                continue
            nut = f.nut()
            key = nut[0] if nut else f.full_path
            if len(vals) == 1:
                data[key] = vals[0]
                if verbose:
                    line = f"   {f.full_path:<46} {vals[0]:>7}"
                    text = f.interpret(vals[0])
                    if text:
                        line += f"   {text}"
                    if show_nut and nut:
                        line += f"   -> NUT {nut[0]}"
                    print(line)
            else:
                data[key] = vals
                if verbose:
                    print(f"   {f.full_path:<46} {vals}")
    if verbose:
        print(f"\n[feature] doc duoc {ok}/{len(reports)} report")

    nuts = {k: v for k, v in data.items() if "." in k and not k.startswith("(")}
    return {"raw": data, "nut": nuts}


def print_summary(nut, est=None):
    def g(name, default=None):
        return nut.get(name, default)

    online = g("ups.status:OL")
    charge = g("battery.charge")
    runtime = g("battery.runtime")
    batt_v = g("battery.voltage")
    in_v = g("input.voltage")
    out_v = g("output.voltage")
    load = g("ups.load")
    pnom = g("ups.realpower.nominal")

    def v(fmt, x, unit=""):
        return f"{fmt.format(x)}{unit}" if isinstance(x, (int, float)) else "n/a"

    print("\n--- TOM TAT (theo bien NUT) ---")
    print(f"  dien luoi (OL)     : {'CO' if online else 'MAT' if online == 0 else 'n/a'}")
    print(f"  battery.charge     : {v('{:.0f}', charge, ' %')}")
    print(f"  battery.runtime    : {v('{:.0f}', runtime and runtime, ' s')}"
          + (f"  (~{runtime // 60} phut)" if isinstance(runtime, int) else ""))
    print(f"  battery.voltage    : {v('{:.1f}', batt_v and batt_v / 10, ' V')}")
    print(f"  input.voltage      : {v('{:.0f}', in_v, ' V')}")
    print(f"  output.voltage     : {v('{:.0f}', out_v, ' V')}")
    print(f"  ups.load           : {v('{:.0f}', load, ' %')}")
    print(f"  ups.realpower.nom. : {v('{:.0f}', pnom, ' W')}")
    for flag in ("ups.status:CHRG", "ups.status:OB|DISCHRG", "ups.status:LB"):
        if flag in nut:
            print(f"  {flag:<19}: {nut[flag]}")

    if est:
        b = est["_basis"]
        print("\n--- CONG SUAT SUY RA (~ = uoc luong, may KHONG do W/A tren USB) ---")
        print(f"  output  W  ~ {est['output.realpower_est']:8.1f}")
        print(f"          VA ~ {est['output.power_est']:8.1f}")
        if "output.current_est" in est:
            print(f"          A  ~ {est['output.current_est']:8.2f}")
        print(f"  input   W  ~ {est['input.realpower_est']:8.1f}"
              + ("   (mat dien luoi -> 0 that)" if not in_v else ""))
        print(f"          VA ~ {est['input.power_est']:8.1f}")
        if "input.current_est" in est:
            print(f"          A  ~ {est['input.current_est']:8.2f}")
        print(f"  goc: load {b['load_pct']}% x {b['output.realpower.nominal_measured']} W"
              f" | VA {b['va_nominal']:.0f} | PF {b['output_pf']}"
              f" | hieu suat {b['efficiency']} | PF vao {b['input_pf']}")
        print(f"  do phan giai: 1% load = {b['output.realpower.nominal_measured'] / 100:.1f} W")


def print_fields(fields_by_kind, model, mfr, serial):
    """Liet ke moi du lieu lay duoc: doc tu may (M) / tinh (T) / khong co."""
    print(f"UPS: {mfr} / {model} / serial={serial}\n")
    print(f"{'report':>7}  {'duong dan / y nghia':<48} {'bien NUT':<34} nguon")
    print("-" * 118)

    seen_nut = set()
    for rid in sorted(fields_by_kind["feature"]):
        for f in fields_by_kind["feature"][rid]:
            nut = f.nut()
            nutname = nut[0] if nut else "-"
            if nut:
                seen_nut.add(nut[0])
            extra = ""
            if f.count > 1:
                extra = f"  x{f.count}"
            # string index: doc duoc so, nhung khong doi ra chuoi duoc tren Windows
            src = "I" if (nut and len(nut) > 1 and nut[1] == "stringidx") else "M"
            print(f"  0x{rid:02x}   {f.full_path + extra:<48} {nutname:<34} {src}")
    print("-" * 118)
    print("M = doc truc tiep tu UPS qua HID feature report (so do that)")
    print("I = chi la chi so (index) cua string descriptor; Windows khong doc duoc chuoi")

    # Du lieu suy ra
    print("\nSUY RA (may khong do tren USB):")
    for label, formula in (
        ("output.realpower_est  (W)", "load% x ConfigActivePower(0x18) / 100"),
        ("output.power_est      (VA)", "output W / PF 0.9"),
        ("output.current_est    (A)", "output VA / output.voltage(0x12)"),
        ("input.realpower_est   (W)", "output W / hieu suat (0 khi mat dien)"),
        ("input.power_est       (VA)", "input W / PF vao 0.98"),
        ("input.current_est     (A)", "input VA / input.voltage(0x0f)"),
    ):
        print(f"  {label:<30} = {formula}")

    # Khong co
    print("\nKHONG CO TREN THIET BI NAY (da quet 0x00-0x60):")
    for label, why in (
        ("Current (A) usage 0x31", "khong khai bao trong descriptor"),
        ("ApparentPower (VA) usage 0x33", "khong khai bao trong descriptor"),
        ("ActivePower (W) usage 0x34", "khong khai bao trong descriptor"),
        ("Frequency (Hz) usage 0x32", "khong khai bao trong descriptor"),
        ("ups.firmware (0xff0100d0)", "firmware CPS khong co field nay"),
        ("ups.serial / ups.mfr", "chi co string index, HID Windows khong doc duoc"),
        ("khoi vendor 0xff01 (0x25-0x2d)", "doc ra 0, chua ai decode cong khai"),
    ):
        print(f"  {label:<32} {why}")


def run_selftest():
    fails = 0

    def check(name, got, want):
        nonlocal fails
        if got != want:
            print(f"FAIL {name}: got={got!r} want={want!r}")
            fails += 1

    desc = bytes.fromhex(
        # App collection -> PowerSummary (physical) -> PresentStatus (logical)
        "0584" "0904" "a101" "0924" "a100" "0902" "a102"
        "850b" "0585"
        "09d009440945094209460943"
        "15002501750195068122" "750295018103"
        "09d009440945094209460943"
        "1500250175019506b1a2" "75029501b103"
        "c0"
        # Battery reports (page 0x85)
        "0585" "8508"
        "0966" "15002564" "75089501b1a2"
        "0968" "1500" "27ffff0000" "75109501b1a2"
        "092a" "1500" "265802" "75109501b1a2"
        # Tro lai page 0x84: pin
        "0584" "8509" "0940" "1500" "27ffff0000" "75109501b1a2"
        "850a" "0930" "1500" "27ffff0000" "75109501b1a2"
        "c0"                      # het PowerSummary
        "0584" "091a" "a100"      # Input (usage 0x1A)
        "850e" "0940" "1500" "26ff00" "75089501b122"
        "850f" "0930" "1500" "26ff01" "75109501b1a2"
        "c0"
        "091c" "a100"             # Output (usage 0x1C)
        "8512" "0930" "1500" "26ff01" "75109501b1a2"
        "8513" "0935" "1500" "26ff00" "75089501b1a2"
        "8518" "0944" "1500" "27ffff0000" "75109501b1a2"
        "c0"
        "c0"                      # het App collection
    )
    p = parse_descriptor(desc)
    lens = report_lengths(p)

    # 1. bitfield: byte 0x04 -> bit2 bat (Discharging)
    f0b = {f.name: f for f in p["feature"][0x0B]}
    check("0x0b co 6 field", len(f0b), 6)
    check("bit2 Discharging", f0b["Discharging"].values(b"\x04")[0], 1)
    check("bit0 ACPresent", f0b["ACPresent"].values(b"\x04")[0], 0)

    # 2. do dai report tinh tu descriptor (fix loi 0x28/0x29)
    check("len feature 0x0b", lens["feature"][0x0B], 2)
    check("len input 0x0b", lens["input"][0x0B], 2)
    check("len feature 0x08", lens["feature"][0x08], 6)

    # 3. collection path — loi co ban cua ban truoc
    check("path 0x0a", p["feature"][0x0A][0].path, "UPS.PowerSummary")
    check("path 0x0b", p["feature"][0x0B][0].path, "UPS.PowerSummary.PresentStatus")
    check("path 0x0e", p["feature"][0x0E][0].path, "UPS.Input")
    check("path 0x12", p["feature"][0x12][0].path, "UPS.Output")

    # 4. ten usage dung (0x44 = ConfigActivePower, khong phai Charging)
    check("0x18 usage name", p["feature"][0x18][0].name, "ConfigActivePower")
    check("0x53 = LowVoltageTransfer", USAGE_PD[0x53], "LowVoltageTransfer")
    check("0x46 = FullyCharged", USAGE_BAT[0x46], "FullyCharged")

    # 5. scale: pin 360 -> 36.0 V
    fv = p["feature"][0x0A][0]
    check("battery voltage scale", fv.interpret(360), "36.0 V")

    # 6. NUT mapping
    check("nut battery.voltage", fv.nut()[0], "battery.voltage")

    # 7. VA dinh muc: lay tu ten model, fallback tu real_nom / pf
    check("VA tu model", va_nominal("OLS1500ERT2U", 1350), 1500.0)
    check("VA fallback", va_nominal("khong-co-so", 1350), 1500.0)

    # 8. Suy ra cong suat — load 20%, nominal 1350 W, 230 V, co dien luoi
    nut = {"ups.load": 20, "ups.realpower.nominal": 1350,
           "output.voltage": 230, "input.voltage": 230}
    est = derive_power(nut, 1500)
    check("out W", round(est["output.realpower_est"], 4), 270.0)
    check("out VA", round(est["output.power_est"], 4), 300.0)
    check("out A", round(est["output.current_est"], 4), round(300 / 230, 4))
    check("in W", round(est["input.realpower_est"], 4), round(270 / 0.9, 4))

    # 9. Mat dien luoi -> dau vao = 0 (su that vat ly, khong phai uoc luong)
    nut["input.voltage"] = 0
    est0 = derive_power(nut, 1500)
    check("in W khi mat dien", est0["input.realpower_est"], 0.0)
    check("out W khi mat dien", round(est0["output.realpower_est"], 4), 270.0)

    # 10. Thieu du lieu goc -> khong bia so
    check("thieu load -> rong", derive_power({"output.voltage": 230}, 1500), {})

    if fails == 0:
        print("SELFTEST OK (22 kiem tra)")
        return 0
    print(f"SELFTEST FAIL ({fails} loi)")
    return 1


def main(argv=None):
    ap = argparse.ArgumentParser(description="In moi du lieu UPS doc duoc (chi doc)")
    ap.add_argument("--once", action="store_true")
    ap.add_argument("--json", action="store_true")
    ap.add_argument("--nut", action="store_true", help="in them ten bien NUT")
    ap.add_argument("--interval", type=float, default=5.0)
    ap.add_argument("--quiet", action="store_true", help="chi bang tom tat")
    ap.add_argument("--selftest", action="store_true")
    ap.add_argument("--fields", action="store_true",
                    help="liet ke moi du lieu lay duoc roi thoat")
    ap.add_argument("--pf-out", type=float, default=DEFAULT_OUTPUT_PF,
                    help="he so cong suat dau ra (datasheet 0.9)")
    ap.add_argument("--pf-in", type=float, default=DEFAULT_INPUT_PF,
                    help="he so cong suat dau vao (datasheet 0.98)")
    ap.add_argument("--eff", type=float, default=DEFAULT_EFFICIENCY,
                    help="hieu suat online (0.90; ECO >0.95)")
    args = ap.parse_args(argv)

    if args.selftest:
        return run_selftest()

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
        mfr = dev.get_manufacturer_string() or "?"
    except OSError:
        mfr = "?"
    try:
        serial = dev.get_serial_number_string() or "(rong)"
    except OSError:
        serial = "(loi)"

    fields_by_kind = parse_descriptor(bytes(dev.get_report_descriptor()))
    lens = report_lengths(fields_by_kind)
    verbose = not args.quiet and not args.json

    if args.fields:
        print_fields(fields_by_kind, model, mfr, serial)
        dev.close()
        return 0

    if verbose:
        print(f"UPS: {mfr} / {model} / serial={serial}")
        print(f"     feature report: {len(fields_by_kind['feature'])}"
              f"   input report: {len(fields_by_kind['input'])}"
              f"   (VID 0x{VENDOR_ID:04x})")
        print("Chi doc (get_feature_report). Ctrl+C de dung.")
        print("Luu y: report 0x25-0x2D nam trong usage page 0xFF01 (vendor rieng")
        print("       cua CyberPower, khong co trong spec PDC va khong co trong NUT)")
        print("       -> khong the dat ten, va tren may nay doc ra toan 0.")

    try:
        while True:
            reports = read_all(dev, fields_by_kind, lens)
            result = print_report(reports, verbose=verbose, show_nut=args.nut)
            va_nom = va_nominal(model, result["nut"].get("ups.realpower.nominal"))
            est = derive_power(result["nut"], va_nom,
                               out_pf=args.pf_out, in_pf=args.pf_in,
                               ef=args.eff)
            if args.json:
                print(json.dumps({
                    "ts": datetime.now().astimezone().isoformat(),
                    "model": model,
                    "manufacturer": mfr,
                    "serial": serial,
                    "source": "hid-descriptor+nut",
                    **result,
                    "estimated": est or None,
                }, ensure_ascii=False, default=str), flush=True)
            else:
                print_summary(result["nut"], est)
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
