-- AetherMU Marketplace v4.15
-- REVIEW-ONLY: apply only to disposable test database.
-- Requires marketplace-transaction-v415.sql.
-- No payment, inventory mutation, or native delivery is performed here.

CREATE OR REPLACE FUNCTION aether_marketplace.reserve_purchase(
    p_purchase_id uuid,
    p_listing_id uuid,
    p_buyer_character_id uuid
)
RETURNS text
LANGUAGE plpgsql
AS $function$
DECLARE
    v_listing aether_marketplace.player_listing%ROWTYPE;
    v_existing aether_marketplace.purchase_reservation%ROWTYPE;
BEGIN
    IF p_purchase_id IS NULL OR p_listing_id IS NULL
       OR p_buyer_character_id IS NULL
       OR p_purchase_id = '00000000-0000-0000-0000-000000000000'::uuid
       OR p_listing_id = '00000000-0000-0000-0000-000000000000'::uuid
       OR p_buyer_character_id = '00000000-0000-0000-0000-000000000000'::uuid
    THEN
        RETURN 'INVALID_REQUEST';
    END IF;

    -- Serialize concurrent attempts using the same PurchaseId.
    -- Hash collisions only cause additional waiting, never authorization.
    PERFORM pg_advisory_xact_lock(
        hashtextextended(p_purchase_id::text, 0)
    );

    -- Serialize competing buyers of the same listing.
    SELECT *
    INTO v_listing
    FROM aether_marketplace.player_listing
    WHERE listing_id = p_listing_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN 'LISTING_NOT_FOUND';
    END IF;

    -- Idempotent retry is accepted only for the same purchase identity.
    SELECT *
    INTO v_existing
    FROM aether_marketplace.purchase_reservation
    WHERE purchase_id = p_purchase_id;

    IF FOUND THEN
        IF v_existing.listing_id = p_listing_id
           AND v_existing.buyer_character_id = p_buyer_character_id
           AND v_existing.seller_character_id = v_listing.seller_character_id
           AND v_existing.realm = v_listing.realm
           AND v_existing.currency = v_listing.currency
           AND v_existing.price = v_listing.price
        THEN
            RETURN 'ALREADY_RESERVED';
        END IF;

        RETURN 'PURCHASE_ID_CONFLICT';
    END IF;

    IF v_listing.status <> 'ACTIVE' THEN
        RETURN 'LISTING_NOT_ACTIVE';
    END IF;

    IF v_listing.seller_character_id = p_buyer_character_id THEN
        RETURN 'SELF_PURCHASE_DENIED';
    END IF;

    INSERT INTO aether_marketplace.purchase_reservation (
        purchase_id,
        listing_id,
        buyer_character_id,
        seller_character_id,
        realm,
        currency,
        price,
        status
    )
    VALUES (
        p_purchase_id,
        p_listing_id,
        p_buyer_character_id,
        v_listing.seller_character_id,
        v_listing.realm,
        v_listing.currency,
        v_listing.price,
        'RESERVED'
    );

    UPDATE aether_marketplace.player_listing
    SET status = 'RESERVED', updated_at = now()
    WHERE listing_id = p_listing_id
      AND status = 'ACTIVE';

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Listing state changed unexpectedly';
    END IF;

    RETURN 'RESERVED';
END;
$function$;