"""Read-only world configuration and S6 client asset validation (Python 3.10+)."""

import argparse
import collections
import hashlib
import json
import math
from pathlib import Path
import struct
import sys


def decode_map_file(payload):
    """Decode the legacy client map container; this does not change the input."""
    mask = bytes.fromhex("d17352f6d29acb273eaf593137b3e7a2")
    previous = 94
    decoded = bytearray()
    for index, value in enumerate(payload):
        decoded.append(((value ^ mask[index % len(mask)]) - previous) & 255)
        previous = (value + 61) & 255
    return bytes(decoded)


def object_errors(payload, world_number=None, extent=25600):
    """Validate a decoded OBJ header, record count and all coordinate fields."""
    if len(payload) < 4:
        return [{"reason": "truncated_header"}]
    version, world, count = struct.unpack_from("<BBh", payload)
    if version != 0 or count < 0 or len(payload) != 4 + 30 * count:
        return [{"reason": "invalid_container", "world": world, "count": count}]
    errors = []
    if world_number is not None and world != world_number:
        errors.append({"reason": "wrong_world", "world": world})
    for index in range(count):
        kind, *values = struct.unpack_from("<h7f", payload, 4 + 30 * index)
        for field, value in zip(("x", "y", "z", "angle_x", "angle_y", "angle_z", "scale"), values):
            if not math.isfinite(value):
                errors.append({"index": index, "type": kind, "field": field, "reason": "non_finite", "value": str(value)})
        if all(math.isfinite(value) for value in values[:2]) and not all(0 <= value < extent for value in values[:2]):
            errors.append({"index": index, "type": kind, "reason": "outside_world", "x": values[0], "y": values[1]})
    return errors


def terrain_errors(payload):
    """Server ATT has a three-byte header and exactly one byte per map cell."""
    return [] if len(payload) == 65539 and payload[:3] == b"\x00\xff\xff" else [{"bytes": len(payload), "header": payload[:3].hex()}]


def rectangle_valid(rectangle):
    return all(isinstance(rectangle.get(key), int) and not isinstance(rectangle[key], bool) for key in ("X1", "X2", "Y1", "Y2")) and 0 <= rectangle["X1"] <= rectangle["X2"] <= 255 and 0 <= rectangle["Y1"] <= rectangle["Y2"] <= 255


def merchant_errors(items, definitions, columns=8, rows=15):
    """Check every occupied cell, including distinct slots whose rectangles overlap."""
    occupied = {}
    errors = []
    for item in items:
        definition = definitions.get(item.get("DefinitionId"))
        slot = item.get("ItemSlot")
        if definition is None:
            errors.append({"item": item.get("Id"), "reason": "missing_definition"})
            continue
        width, height = definition.get("Width", 0), definition.get("Height", 0)
        if not isinstance(slot, int) or not isinstance(width, int) or not isinstance(height, int) or slot < 0 or width <= 0 or height <= 0:
            errors.append({"item": item.get("Id"), "reason": "invalid_dimensions_or_slot"})
            continue
        x, y = slot % columns, slot // columns
        if x + width > columns or y + height > rows:
            errors.append({"item": item.get("Id"), "slot": slot, "reason": "outside_store"})
            continue
        conflicts = set()
        for xx in range(x, x + width):
            for yy in range(y, y + height):
                cell = (item.get("ItemStorageId"), xx, yy)
                if cell in occupied:
                    conflicts.add(occupied[cell])
                occupied[cell] = item.get("Id")
        if conflicts:
            errors.append({"item": item.get("Id"), "slot": slot, "reason": "overlap", "other_items": sorted(conflicts)})
    return errors


def client_folder(number):
    if number in (9, 32):
        return 10
    if 11 <= number <= 17 or number == 52:
        return 12
    if 18 <= number <= 23 or number == 53:
        return 19
    if 24 <= number <= 29 or number == 36:
        return 25
    if 45 <= number <= 50:
        return 47
    return number + 1


def validate(snapshot, client_root=None):
    """Validate a detached snapshot; never connect to a database or apply corrections."""
    findings = []

    def record(check, identity, errors, status=None):
        findings.append({"check": check, "identity": identity, "status": status or ("FAIL" if errors else "PASS"), "details": errors})

    required = ("GameMapDefinition", "MonsterDefinition", "MonsterSpawnArea", "ExitGate", "ItemDefinition", "ShopItems")
    for table in required:
        if table not in snapshot:
            record("input_table", table, ["Missing required input table"], "NOT TESTED")
    maps = {m["Id"]: m for m in snapshot.get("GameMapDefinition", [])}
    monsters = {m["Id"]: m for m in snapshot.get("MonsterDefinition", [])}
    definitions = {item["Id"]: item for item in snapshot.get("ItemDefinition", [])}
    grids = {}
    for identity, world in maps.items():
        try:
            payload = bytes.fromhex(world["terrain_hex"])
        except (KeyError, TypeError, ValueError):
            record("terrain", identity, ["Missing or invalid terrain_hex"], "NOT TESTED")
            continue
        errors = terrain_errors(payload)
        record("terrain", identity, errors)
        if not errors:
            grids[identity] = payload[3:]
    for gate in snapshot.get("ExitGate", []):
        if not gate.get("IsSpawnGate"):
            continue
        identity = gate.get("Id")
        if not rectangle_valid(gate) or gate.get("MapId") not in maps:
            record("respawn_gate", identity, ["Invalid bounds or map reference"])
            continue
        grid = grids.get(gate["MapId"])
        if grid is None:
            record("respawn_gate", identity, ["Terrain unavailable or invalid"], "NOT TESTED")
            continue
        walkable = any(grid[y * 256 + x] in (0, 1) for y in range(gate["Y1"], gate["Y2"] + 1) for x in range(gate["X1"], gate["X2"] + 1))
        record("respawn_gate", identity, [] if walkable else ["No walkable cell"])
    for spawn in snapshot.get("MonsterSpawnArea", []):
        errors = []
        if not rectangle_valid(spawn):
            errors.append("Invalid bounds")
        if spawn.get("MonsterDefinitionId") not in monsters:
            errors.append("Missing monster definition")
        if spawn.get("GameMapId") is not None and spawn["GameMapId"] not in maps:
            errors.append("Missing map")
        if not isinstance(spawn.get("Quantity"), int) or spawn["Quantity"] <= 0:
            errors.append("Non-positive quantity")
        # Null-map event templates and stage-blocked event terrain are not invalid spawns.
        record("monster_spawn", spawn.get("Id"), errors)
    grouped = collections.defaultdict(list)
    for item in snapshot.get("ShopItems", []):
        grouped[item.get("ItemStorageId")].append(item)
    for store, items in grouped.items():
        record("merchant_inventory", store, merchant_errors(items, definitions))
    if client_root is None:
        record("client_assets", None, ["No client directory supplied"], "NOT TESTED")
    else:
        client_root = Path(client_root)
        for folder in sorted({client_folder(m["Number"]) for m in maps.values()}):
            for suffix in ("att", "map", "obj"):
                path = client_root / f"World{folder}" / f"EncTerrain{folder}.{suffix}"
                if not path.is_file():
                    record("client_resource", str(path), ["Missing resource"])
                    continue
                payload = decode_map_file(path.read_bytes())
                if suffix == "obj":
                    errors = object_errors(payload, folder)
                elif suffix == "map":
                    errors = [] if len(payload) == 196610 and payload[:2] == bytes((0, folder)) else ["Invalid MAP header or length"]
                else:
                    mask = bytes.fromhex("fccfab")
                    payload = bytes(value ^ mask[index % 3] for index, value in enumerate(payload))
                    errors = [] if len(payload) in (65540, 131076) and payload[:4] == bytes((0, folder, 255, 255)) else ["Invalid ATT header or length"]
                # Decorations can legitimately be placed beyond the walkable grid.
                # Report that extent check for review; non-finite transforms remain FAIL.
                review = "PARTIAL" if errors and all(isinstance(error, dict) and error.get("reason") == "outside_world" for error in errors) else None
                record("client_resource", str(path), errors, review)
    counts = dict(collections.Counter(f["status"] for f in findings))
    status = "FAIL" if counts.get("FAIL") else "PARTIAL" if counts.get("NOT TESTED") or counts.get("PARTIAL") else "PASS"
    return {"schema_version": 1, "status": status, "read_only": True, "counts": counts, "findings": findings}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--snapshot", type=Path, required=True)
    parser.add_argument("--client-data", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.output.resolve() == args.snapshot.resolve() or (args.client_data and args.output.resolve().is_relative_to(args.client_data.resolve())):
        parser.error("Report output must be separate from the input snapshot and client assets")
    try:
        snapshot = json.loads(args.snapshot.read_text(encoding="utf-8-sig"))
        result = validate(snapshot, args.client_data)
        result["input_sha256"] = hashlib.sha256(args.snapshot.read_bytes()).hexdigest()
        args.output.write_text(json.dumps(result, indent=2, allow_nan=False), encoding="utf-8")
    except (OSError, ValueError, TypeError, KeyError) as error:
        print(f"World validation could not complete: {type(error).__name__}", file=sys.stderr)
        return 2
    print(json.dumps({"status": result["status"], "counts": result["counts"]}))
    return 1 if result["status"] == "FAIL" else 2 if result["status"] == "PARTIAL" else 0


if __name__ == "__main__":
    sys.exit(main())
