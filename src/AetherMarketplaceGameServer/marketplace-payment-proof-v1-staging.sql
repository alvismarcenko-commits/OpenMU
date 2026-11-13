-- AetherMU Payment Proof V1
-- STAGING ONLY. NOT AUTHORIZED FOR PRODUCTION.
-- Read-only accounting evidence, NOT buyer authorization.
-- Does not enable native inventory delivery.

CREATE OR REPLACE FUNCTION
aether_marketplace.verify_ac_settlement_accounting_v1(
    p_purchase_id uuid
)
RETURNS text
LANGUAGE plpgsql
SECURITY INVOKER
SET search_path = pg_catalog
AS $function$
DECLARE
    v_settlement aether_marketplace.settlement_ledger%ROWTYPE;
    v_purchase aether_marketplace.purchase_reservation%ROWTYPE;
    v_buyer_account uuid;
    v_reference text;
    v_debit_count integer;
BEGIN
    SELECT *
    INTO v_settlement
    FROM aether_marketplace.settlement_ledger
    WHERE purchase_id = p_purchase_id;

    IF NOT FOUND THEN
        RETURN 'SETTLEMENT_NOT_FOUND';
    END IF;

    IF v_settlement.status <> 'COMMITTED'
       OR v_settlement.currency <> 'AetherCoins'
       OR v_settlement.debit_reference IS DISTINCT FROM p_purchase_id
       OR v_settlement.committed_at IS NULL
    THEN
        RETURN 'SETTLEMENT_NOT_VERIFIED';
    END IF;

    SELECT *
    INTO v_purchase
    FROM aether_marketplace.purchase_reservation
    WHERE purchase_id = p_purchase_id;

    IF NOT FOUND
       OR v_purchase.listing_id IS DISTINCT FROM v_settlement.listing_id
       OR v_purchase.realm IS DISTINCT FROM v_settlement.realm
       OR v_purchase.buyer_character_id IS DISTINCT FROM v_settlement.buyer_character_id
       OR v_purchase.seller_character_id IS DISTINCT FROM v_settlement.seller_character_id
       OR v_purchase.currency <> 'AetherCoins'
       OR v_purchase.price IS DISTINCT FROM v_settlement.gross_amount
    THEN
        RETURN 'PURCHASE_MISMATCH';
    END IF;

    SELECT b.openmu_account_id
    INTO v_buyer_account
    FROM aether_marketplace.native_account_binding b
    WHERE b.realm = v_settlement.realm
      AND b.character_id = v_settlement.buyer_character_id;

    IF NOT FOUND THEN
        RETURN 'BUYER_IDENTITY_MISSING';
    END IF;

    v_reference := 'ac-marketplace:' || p_purchase_id::text || ':buyer';

    SELECT count(*)
    INTO v_debit_count
    FROM aether_marketplace.ac_wallet_ledger l
    WHERE l.realm = v_settlement.realm
      AND l.account_id = v_buyer_account
      AND l.operation_type = 'MARKETPLACE_DEBIT'
      AND l.reference = v_reference
      AND l.amount = -v_settlement.gross_amount
      AND l.balance_after = l.balance_before + l.amount;

    IF v_debit_count <> 1 THEN
        RETURN 'BUYER_DEBIT_PROOF_MISSING';
    END IF;

    RETURN 'ACCOUNTING_PROOF_VALID';
END;
$function$;

REVOKE ALL ON FUNCTION
aether_marketplace.verify_ac_settlement_accounting_v1(uuid)
FROM PUBLIC;