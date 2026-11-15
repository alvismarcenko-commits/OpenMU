"""Regression cases for malformed assets and world configuration defects."""

import copy
import hashlib
import json
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import unittest
import world_validate as world


class WorldValidationTests(unittest.TestCase):
    def snapshot(self):
        terrain = bytearray(b"\0\xff\xff" + bytes(65536))
        terrain[3 + 115 * 256 + 107] = 4
        return {"GameMapDefinition": [{"Id": "arena", "Number": 6, "terrain_hex": terrain.hex()}], "MonsterDefinition": [{"Id": "monster"}], "MonsterSpawnArea": [], "ExitGate": [], "ItemDefinition": [], "ShopItems": []}

    def test_non_finite_each_transform_field(self):
        for index in range(7):
            for invalid in (float("nan"), float("inf"), float("-inf")):
                with self.subTest(field=index, invalid=str(invalid)):
                    values = [100, 100, 0, 0, 0, 0, 1]
                    values[index] = invalid
                    payload = struct.pack("<BBhh7f", 0, 3, 1, 86, *values)
                    self.assertTrue(any(e["reason"] == "non_finite" for e in world.object_errors(payload, 3)))

    def test_finite_valid_object(self):
        self.assertEqual([], world.object_errors(struct.pack("<BBhh7f", 0, 3, 1, 86, 100, 200, -50, 0, 360, 0, 1), 3))

    def test_negative_and_outside_xy(self):
        for coordinate in (-1, 25600):
            payload = struct.pack("<BBhh7f", 0, 3, 1, 86, coordinate, 100, 0, 0, 0, 0, 1)
            self.assertEqual("outside_world", world.object_errors(payload, 3)[0]["reason"])

    def test_truncated_and_count_mismatch(self):
        for payload in (b"", b"\0\3\1\0", b"\0\3\xff\xff"):
            self.assertTrue(world.object_errors(payload))

    def test_wrong_world(self):
        self.assertEqual("wrong_world", world.object_errors(b"\0\2\0\0", 3)[0]["reason"])

    def test_karutan_legacy_rejected(self):
        self.assertTrue(world.terrain_errors(b"\0\xff" + bytes(65536) + b"\0\0"))
        self.assertEqual([], world.terrain_errors(b"\0\xff\xff" + bytes(65536)))

    def test_arena_blocked_and_corrected_gate(self):
        snapshot = self.snapshot()
        gate = {"Id": "gate", "MapId": "arena", "IsSpawnGate": True, "X1": 107, "X2": 107, "Y1": 115, "Y2": 115}
        snapshot["ExitGate"] = [gate]
        self.assertEqual("FAIL", world.validate(snapshot)["status"])
        gate["X1"] = gate["X2"] = 101
        self.assertEqual("PARTIAL", world.validate(snapshot)["status"])
        self.assertEqual("PASS", next(f["status"] for f in world.validate(snapshot)["findings"] if f["check"] == "respawn_gate"))

    def test_merchant_duplicate_slot_and_distinct_slot_overlap(self):
        definitions = {"sword": {"Width": 1, "Height": 3}, "shield": {"Width": 2, "Height": 2}}
        items = [{"Id": "a", "DefinitionId": "sword", "ItemStorageId": "store", "ItemSlot": 73}, {"Id": "b", "DefinitionId": "sword", "ItemStorageId": "store", "ItemSlot": 73}]
        self.assertEqual("overlap", world.merchant_errors(items, definitions)[0]["reason"])
        items[1]["ItemSlot"] = 81
        self.assertEqual("overlap", world.merchant_errors(items, definitions)[0]["reason"])
        items[1]["ItemSlot"] = 76
        self.assertEqual([], world.merchant_errors(items, definitions))
        items[1]["DefinitionId"] = "shield"
        items[1]["ItemSlot"] = 79
        self.assertEqual("outside_store", world.merchant_errors(items, definitions)[0]["reason"])

    def test_same_slot_in_different_stores_is_valid(self):
        items = [{"Id": str(n), "DefinitionId": "sword", "ItemStorageId": str(n), "ItemSlot": 73} for n in (1, 2)]
        self.assertEqual([], world.merchant_errors(items, {"sword": {"Width": 1, "Height": 3}}))

    def test_monster_reference_bounds_and_quantity(self):
        snapshot = self.snapshot()
        snapshot["MonsterSpawnArea"] = [{"Id": "invalid", "MonsterDefinitionId": "missing", "GameMapId": "missing", "X1": 256, "X2": 1, "Y1": 0, "Y2": 0, "Quantity": 0}]
        finding = next(f for f in world.validate(snapshot)["findings"] if f["check"] == "monster_spawn")
        self.assertEqual(4, len(finding["details"]))

    def test_event_template_without_map_is_valid(self):
        snapshot = self.snapshot()
        snapshot["MonsterSpawnArea"] = [{"Id": "event", "MonsterDefinitionId": "monster", "GameMapId": None, "X1": 14, "X2": 14, "Y1": 95, "Y2": 95, "Quantity": 1}]
        self.assertEqual("PASS", next(f["status"] for f in world.validate(snapshot)["findings"] if f["check"] == "monster_spawn"))

    def test_missing_inputs_not_pass(self):
        self.assertEqual("PARTIAL", world.validate({})["status"])

    def test_read_only_and_missing_resources(self):
        snapshot = self.snapshot()
        before = copy.deepcopy(snapshot)
        with tempfile.TemporaryDirectory() as directory:
            sentinel = Path(directory) / "sentinel"
            sentinel.write_bytes(b"unchanged")
            result = world.validate(snapshot, directory)
            self.assertEqual(3, sum(f["status"] == "FAIL" for f in result["findings"]))
            self.assertEqual(b"unchanged", sentinel.read_bytes())
            self.assertEqual([sentinel], list(Path(directory).iterdir()))
        self.assertEqual(before, snapshot)

    def test_cli_refuses_input_overwrite(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "snapshot.json"
            path.write_text(json.dumps(self.snapshot()))
            digest = hashlib.sha256(path.read_bytes()).digest()
            result = subprocess.run([sys.executable, str(Path(world.__file__)), "--snapshot", str(path), "--output", str(path)], capture_output=True)
            self.assertEqual(2, result.returncode)
            self.assertEqual(digest, hashlib.sha256(path.read_bytes()).digest())


if __name__ == "__main__":
    unittest.main()
