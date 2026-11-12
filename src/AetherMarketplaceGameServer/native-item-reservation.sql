-- Reviewed deployment artifact only. Never applied automatically by the adapter.
-- Purchase/escrow service writes immutable JSON serialized as ReservedNativeItem.
-- Its OpenMuAccountId must come from the trusted account linkage, not the web request.
CREATE TABLE aether_marketplace.native_item_reservation (
    delivery_id uuid PRIMARY KEY REFERENCES aether_marketplace.delivery_journal(delivery_id),
    item_instance_id uuid NOT NULL UNIQUE,
    snapshot jsonb NOT NULL,
    native_started_at timestamptz NULL,
    CHECK ((snapshot->'Request'->>'DeliveryId')::uuid = delivery_id),
    CHECK ((snapshot->'Request'->>'ItemInstanceId')::uuid = item_instance_id)
);
-- GameServer role: SELECT plus UPDATE(native_started_at) only.
-- No INSERT, DELETE, snapshot UPDATE or claim reset privileges for the game/web roles.
