# AetherMU Marketplace Delivery Bridge v1.0

Status: ISOLATED PROTOTYPE.

Contains:
- Delivery request/result contracts
- Inventory gateway interface
- Delivery journal interface
- Coordinator
- Fake-backed unit tests

NOT IMPLEMENTED:
- Real OpenMU inventory writes
- PostgreSQL delivery journal
- Durable idempotency
- Crash recovery
- Payment settlement
- Website API integration

Never connect this prototype directly to a live GameServer.
