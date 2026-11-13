-- AetherMU Marketplace Native Ownership Fix3
-- ISOLATED TEST CANDIDATE ONLY.
-- Not authorized for production or live GameServer integration.
-- Requires existing OpenMU and Marketplace schemas.
-- Does not create escrow custody or debit currency.

CREATE TABLE IF NOT EXISTS aether_marketplace.native_item_custody (
    listing_id uuid PRIMARY KEY
        REFERENCES aether_marketplace.player_listing(listing_id),
    item_instance_id uuid NOT NULL,
    escrow_storage_id uuid NOT NULL,
    seller_character_id uuid NOT NULL,
    status text NOT NULL DEFAULT 'HELD',
    created_at timestamptz NOT NULL DEFAULT now(),
    delivered_at timestamptz,
    CONSTRAINT custody_status_valid
        CHECK (status IN ('HELD', 'DELIVERED', 'RECONCILIATION_REQUIRED'))
);

CREATE UNIQUE INDEX IF NOT EXISTS native_item_one_active_custody
ON aether_marketplace.native_item_custody(item_instance_id)
WHERE status IN ('HELD', 'RECONCILIATION_REQUIRED');

CREATE OR REPLACE FUNCTION aether_marketplace.transfer_reserved_native_item(
    p_purchase_id uuid,
    p_item_id uuid,
    p_escrow_storage_id uuid,
    p_target_slot integer
)
RETURNS text
LANGUAGE plpgsql
AS $function$
DECLARE
    v_purchase aether_marketplace.purchase_reservation%ROWTYPE;
    v_listing aether_marketplace.player_listing%ROWTYPE;
    v_custody aether_marketplace.native_item_custody%ROWTYPE;
    v_target_storage uuid;
    v_current_storage uuid;
    v_current_slot integer;
BEGIN
    IF p_purchase_id IS NULL OR p_item_id IS NULL
       OR p_escrow_storage_id IS NULL
       OR p_target_slot IS NULL OR p_target_slot < 0
       OR p_purchase_id = '00000000-0000-0000-0000-000000000000'::uuid
       OR p_item_id = '00000000-0000-0000-0000-000000000000'::uuid
       OR p_escrow_storage_id = '00000000-0000-0000-0000-000000000000'::uuid
    THEN
        RETURN 'INVALID_REQUEST';
    END IF;

    SELECT * INTO v_purchase
    FROM aether_marketplace.purchase_reservation
    WHERE purchase_id = p_purchase_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN 'PURCHASE_NOT_FOUND';
    END IF;

    SELECT * INTO v_listing
    FROM aether_marketplace.player_listing
    WHERE listing_id = v_purchase.listing_id
    FOR UPDATE;

    IF NOT FOUND
       OR v_listing.item_instance_id <> p_item_id
       OR v_listing.seller_character_id <> v_purchase.seller_character_id
       OR v_listing.realm <> v_purchase.realm
       OR v_listing.currency <> v_purchase.currency
       OR v_listing.price <> v_purchase.price
    THEN
        RETURN 'LISTING_MISMATCH';
    END IF;

    SELECT * INTO v_custody
    FROM aether_marketplace.native_item_custody
    WHERE listing_id = v_purchase.listing_id
    FOR UPDATE;

    IF NOT FOUND
       OR v_custody.item_instance_id <> p_item_id
       OR v_custody.escrow_storage_id <> p_escrow_storage_id
       OR v_custody.seller_character_id <> v_purchase.seller_character_id
    THEN
        RETURN 'CUSTODY_MISMATCH';
    END IF;

    -- SECURITY GATE FIX3:
    -- Reservation status is not proof of payment.
    -- Until a durable, independently verified settlement ledger
    -- is integrated into the SAME atomic transaction, ownership
    -- transfer must remain disabled.
    --
    -- Do not remove this gate based on application-side flags.
    -- A future implementation must also verify replay identity,
    -- target inventory ownership, and successful settlement.
    RETURN 'PAYMENT_PROOF_NOT_IMPLEMENTED';

    -- Only an externally verified, paid delivery may proceed.
    IF v_purchase.status <> 'DELIVERY_PENDING'
       OR v_listing.status <> 'RESERVED'
       OR v_custody.status <> 'HELD'
    THEN
        RETURN 'NOT_READY';
    END IF;

    SELECT "InventoryId" INTO v_target_storage
    FROM data."Character"
    WHERE "Id" = v_purchase.buyer_character_id;

    IF v_target_storage IS NULL
       OR v_target_storage = p_escrow_storage_id
    THEN
        RETURN 'INVALID_TARGET';
    END IF;

    -- Serialize inventory slot allocation.
    PERFORM 1
    FROM data."ItemStorage"
    WHERE "Id" = v_target_storage
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN 'TARGET_STORAGE_NOT_FOUND';
    END IF;

    SELECT "ItemStorageId", "ItemSlot"
    INTO v_current_storage, v_current_slot
    FROM data."Item"
    WHERE "Id" = p_item_id
    FOR UPDATE;

    IF NOT FOUND OR v_current_storage IS DISTINCT FROM p_escrow_storage_id THEN
        RETURN 'ITEM_NOT_IN_ESCROW';
    END IF;

    IF EXISTS (
        SELECT 1 FROM data."Item"
        WHERE "ItemStorageId" = v_target_storage
          AND "ItemSlot" = p_target_slot
          AND "Id" <> p_item_id
    ) THEN
        RETURN 'SLOT_OCCUPIED';
    END IF;

    UPDATE data."Item"
    SET "ItemStorageId" = v_target_storage,
        "ItemSlot" = p_target_slot
    WHERE "Id" = p_item_id
      AND "ItemStorageId" = p_escrow_storage_id;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Concurrent native item transfer conflict';
    END IF;

    UPDATE aether_marketplace.native_item_custody
    SET status = 'DELIVERED', delivered_at = now()
    WHERE listing_id = v_listing.listing_id
      AND status = 'HELD';

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Native custody update conflict';
    END IF;

    -- Payment completion and player state reconciliation
    -- remain responsibilities of the orchestrating service.
    RETURN 'TRANSFERRED';
END;
$function$;