-- V5.10.28 isolated candidate. Never applied automatically.
-- Requires v3 settlement, v1 accounting proof, and existing identity schemas.
-- Identity arguments MUST come from the authenticated GameServer, never a client.
-- No native delivery activation or Castle Siege payouts.

CREATE OR REPLACE FUNCTION aether_marketplace.verify_ac_settlement_accounting_v2(p_purchase_id uuid)
RETURNS text LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog
AS $function$
DECLARE
    v_result text;
    v_purchase aether_marketplace.purchase_reservation%ROWTYPE;
    v_settlement aether_marketplace.settlement_ledger%ROWTYPE;
    v_buyer uuid;
    v_seller uuid;
    v_treasury uuid := '00000000-0000-0000-0000-000000000001';
    v_reference text := 'ac-marketplace:' || p_purchase_id::text;
BEGIN
    v_result := aether_marketplace.verify_ac_settlement_accounting_v1(p_purchase_id);
    IF v_result IS DISTINCT FROM 'ACCOUNTING_PROOF_VALID' THEN RETURN v_result; END IF;
    SELECT * INTO STRICT v_purchase FROM aether_marketplace.purchase_reservation
    WHERE purchase_id = p_purchase_id;
    SELECT * INTO STRICT v_settlement FROM aether_marketplace.settlement_ledger
    WHERE purchase_id = p_purchase_id;
    IF v_settlement.fee_amount IS DISTINCT FROM v_purchase.price / 20
       OR v_settlement.seller_amount IS DISTINCT FROM v_purchase.price - v_purchase.price / 20 THEN
        RETURN 'SETTLEMENT_MISMATCH';
    END IF;
    SELECT openmu_account_id INTO v_buyer FROM aether_marketplace.native_account_binding
    WHERE realm = v_purchase.realm AND character_id = v_purchase.buyer_character_id;
    SELECT openmu_account_id INTO v_seller FROM aether_marketplace.native_account_binding
    WHERE realm = v_purchase.realm AND character_id = v_purchase.seller_character_id;
    IF v_buyer IS NULL OR v_seller IS NULL OR v_buyer = v_seller
       OR v_buyer = v_treasury OR v_seller = v_treasury THEN RETURN 'INVALID_IDENTITIES'; END IF;
    IF (SELECT count(*) FROM aether_marketplace.ac_wallet_ledger l
        WHERE l.realm = v_purchase.realm
          AND l.balance_after::numeric = l.balance_before::numeric + l.amount::numeric
          AND ((l.reference = v_reference || ':buyer' AND l.account_id = v_buyer
                AND l.operation_type = 'MARKETPLACE_DEBIT' AND l.amount = -v_purchase.price)
            OR (l.reference = v_reference || ':seller' AND l.account_id = v_seller
                AND l.operation_type = 'MARKETPLACE_CREDIT' AND l.amount = v_settlement.seller_amount)
            OR (l.reference = v_reference || ':treasury' AND l.account_id = v_treasury
                AND l.operation_type = 'MARKETPLACE_CREDIT' AND l.amount = v_settlement.fee_amount))) <> 3 THEN
        RETURN 'WALLET_PROOF_MISSING';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM aether_marketplace.castle_treasury_ledger
                   WHERE purchase_id = p_purchase_id AND realm = v_purchase.realm
                     AND amount = v_settlement.fee_amount) THEN RETURN 'TREASURY_PROOF_MISSING'; END IF;
    RETURN 'ACCOUNTING_PROOF_VALID';
END;
$function$;

CREATE OR REPLACE FUNCTION aether_marketplace.settle_authorized_ac_purchase_v4(
    p_purchase_id uuid, p_realm text, p_buyer_character_id uuid, p_buyer_account_id uuid)
RETURNS text LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog
AS $function$
DECLARE
    v_purchase aether_marketplace.purchase_reservation%ROWTYPE;
    v_listing aether_marketplace.player_listing%ROWTYPE;
    v_result text;
    v_committed boolean;
BEGIN
    -- Lock order: purchase, listing, identity bindings, settlement, ordered wallets.
    SELECT * INTO v_purchase FROM aether_marketplace.purchase_reservation
    WHERE purchase_id = p_purchase_id FOR UPDATE;
    IF NOT FOUND THEN RETURN 'PURCHASE_NOT_FOUND'; END IF;
    IF v_purchase.realm IS DISTINCT FROM p_realm
       OR v_purchase.buyer_character_id IS DISTINCT FROM p_buyer_character_id
       OR NOT EXISTS (
           SELECT 1 FROM aether_marketplace.native_account_binding b
           JOIN aether_marketplace.marketplace_account_identity i
             ON i.realm = b.realm AND i.marketplace_account_id = b.marketplace_account_id
            AND i.openmu_account_id = b.openmu_account_id
           WHERE b.realm = p_realm AND b.character_id = p_buyer_character_id
             AND b.openmu_account_id = p_buyer_account_id) THEN RETURN 'UNAUTHORIZED'; END IF;
    IF v_purchase.currency <> 'AetherCoins' THEN RETURN 'INVALID_CURRENCY'; END IF;
    SELECT * INTO v_listing FROM aether_marketplace.player_listing
    WHERE listing_id = v_purchase.listing_id FOR UPDATE;
    IF NOT FOUND OR v_listing.realm IS DISTINCT FROM v_purchase.realm
       OR v_listing.seller_character_id IS DISTINCT FROM v_purchase.seller_character_id
       OR v_listing.price IS DISTINCT FROM v_purchase.price
       OR v_listing.currency IS DISTINCT FROM v_purchase.currency THEN RETURN 'LISTING_MISMATCH'; END IF;
    PERFORM 1 FROM aether_marketplace.native_account_binding
    WHERE realm = v_purchase.realm
      AND character_id IN (v_purchase.buyer_character_id, v_purchase.seller_character_id)
    ORDER BY character_id FOR SHARE;
    SELECT status = 'COMMITTED' INTO v_committed FROM aether_marketplace.settlement_ledger
    WHERE purchase_id = p_purchase_id FOR UPDATE;
    IF coalesce(v_committed, false) THEN
        -- Repair historical paid reservations without rewriting any ledger.
        v_result := aether_marketplace.verify_ac_settlement_accounting_v2(p_purchase_id);
        IF v_result IS DISTINCT FROM 'ACCOUNTING_PROOF_VALID' THEN RETURN v_result; END IF;
        IF v_purchase.status = 'RESERVED' AND v_listing.status = 'RESERVED' THEN
            UPDATE aether_marketplace.purchase_reservation SET status = 'DELIVERY_PENDING', updated_at = now()
            WHERE purchase_id = p_purchase_id;
        ELSIF v_purchase.status NOT IN ('DELIVERY_PENDING', 'COMPLETED') THEN RETURN 'INVALID_PURCHASE_STATE'; END IF;
        RETURN 'ALREADY_SETTLED';
    END IF;
    IF v_purchase.status <> 'RESERVED' OR v_listing.status <> 'RESERVED' THEN RETURN 'INVALID_PURCHASE_STATE'; END IF;
    -- v3 does not compare listing_id; reject mismatched prepared evidence here.
    IF EXISTS (SELECT 1 FROM aether_marketplace.settlement_ledger
               WHERE purchase_id = p_purchase_id AND listing_id IS DISTINCT FROM v_purchase.listing_id) THEN
        RETURN 'SETTLEMENT_MISMATCH';
    END IF;
    v_result := aether_marketplace.settle_verified_ac_purchase_v3(p_purchase_id);
    IF v_result IS DISTINCT FROM 'SETTLED' THEN RETURN v_result; END IF;
    v_result := aether_marketplace.verify_ac_settlement_accounting_v2(p_purchase_id);
    IF v_result IS DISTINCT FROM 'ACCOUNTING_PROOF_VALID' THEN
        -- An exception rolls back ALL wallet and ledger writes in this statement.
        RAISE EXCEPTION 'Settlement accounting failed: %', v_result;
    END IF;
    UPDATE aether_marketplace.purchase_reservation SET status = 'DELIVERY_PENDING', updated_at = now()
    WHERE purchase_id = p_purchase_id;
    RETURN 'SETTLED';
END;
$function$;

REVOKE ALL ON FUNCTION aether_marketplace.verify_ac_settlement_accounting_v2(uuid) FROM PUBLIC;
REVOKE ALL ON FUNCTION aether_marketplace.settle_authorized_ac_purchase_v4(uuid,text,uuid,uuid) FROM PUBLIC;
-- No runtime grants: legacy function privileges need review before activation.
