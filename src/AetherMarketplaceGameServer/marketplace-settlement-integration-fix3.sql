-- AetherMU Marketplace Settlement Integration Fix3
-- Isolated staging candidate. No money movement or item delivery.
-- Dependencies:
-- marketplace-transaction-v415.sql
-- marketplace-settlement-ledger-fix3.sql

CREATE OR REPLACE FUNCTION aether_marketplace.prepare_settlement(
    p_purchase_id uuid
)
RETURNS text
LANGUAGE plpgsql
AS $function$
DECLARE
    v_purchase aether_marketplace.purchase_reservation%ROWTYPE;
    v_listing aether_marketplace.player_listing%ROWTYPE;
    v_existing aether_marketplace.settlement_ledger%ROWTYPE;
    v_fee bigint;
    v_seller_amount bigint;
BEGIN
    IF p_purchase_id IS NULL THEN
        RETURN 'INVALID_PURCHASE';
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

    IF NOT FOUND THEN
        RETURN 'LISTING_NOT_FOUND';
    END IF;

    IF v_listing.status <> 'RESERVED'
       OR v_listing.seller_character_id
          IS DISTINCT FROM v_purchase.seller_character_id
       OR v_listing.realm IS DISTINCT FROM v_purchase.realm
       OR v_listing.currency IS DISTINCT FROM v_purchase.currency
       OR v_listing.price IS DISTINCT FROM v_purchase.price
       OR v_purchase.buyer_character_id = v_purchase.seller_character_id
    THEN
        RETURN 'RESERVATION_MISMATCH';
    END IF;

    IF v_purchase.status <> 'RESERVED' THEN
        RETURN 'INVALID_PURCHASE_STATUS';
    END IF;

    IF v_purchase.price <= 0
       OR v_purchase.currency NOT IN ('Zen', 'AetherCoins')
    THEN
        RETURN 'INVALID_PRICE_OR_CURRENCY';
    END IF;

    -- AC fee: integer division, rounded down.
    -- Zen fee: zero.
    v_fee := CASE
        WHEN v_purchase.currency = 'AetherCoins'
        THEN v_purchase.price / 20
        ELSE 0
    END;

    v_seller_amount := v_purchase.price - v_fee;

    SELECT * INTO v_existing
    FROM aether_marketplace.settlement_ledger
    WHERE purchase_id = p_purchase_id
    FOR UPDATE;

    IF FOUND THEN
        IF v_existing.listing_id IS DISTINCT FROM v_purchase.listing_id
           OR v_existing.realm IS DISTINCT FROM v_purchase.realm
           OR v_existing.buyer_character_id
              IS DISTINCT FROM v_purchase.buyer_character_id
           OR v_existing.seller_character_id
              IS DISTINCT FROM v_purchase.seller_character_id
           OR v_existing.currency IS DISTINCT FROM v_purchase.currency
           OR v_existing.gross_amount IS DISTINCT FROM v_purchase.price
           OR v_existing.fee_amount IS DISTINCT FROM v_fee
           OR v_existing.seller_amount IS DISTINCT FROM v_seller_amount
        THEN
            RETURN 'SETTLEMENT_MISMATCH';
        END IF;

        IF v_existing.status = 'PENDING' THEN
            RETURN 'ALREADY_PREPARED';
        END IF;

        RETURN 'SETTLEMENT_ALREADY_ADVANCED';
    END IF;

    INSERT INTO aether_marketplace.settlement_ledger (
        purchase_id,
        listing_id,
        realm,
        buyer_character_id,
        seller_character_id,
        currency,
        gross_amount,
        fee_amount,
        seller_amount,
        status
    )
    VALUES (
        v_purchase.purchase_id,
        v_purchase.listing_id,
        v_purchase.realm,
        v_purchase.buyer_character_id,
        v_purchase.seller_character_id,
        v_purchase.currency,
        v_purchase.price,
        v_fee,
        v_seller_amount,
        'PENDING'
    );


    RETURN 'PREPARED';
END;
$function$;