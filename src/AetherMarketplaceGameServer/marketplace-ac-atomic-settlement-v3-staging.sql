-- AetherMU Marketplace AC Atomic Settlement V3
-- ISOLATED STAGING CANDIDATE ONLY.
-- NOT AUTHORIZED FOR PRODUCTION.
-- Does not authorize native item delivery.
-- PAYMENT_PROOF_NOT_IMPLEMENTED remains active in native ownership.
-- AetherMU AC Atomic Settlement v1
-- STAGING ONLY. Not a native item delivery authorization.

CREATE OR REPLACE FUNCTION aether_marketplace.settle_verified_ac_purchase_v3(
    p_purchase_id uuid
)
RETURNS text
LANGUAGE plpgsql
AS $$
DECLARE
    v_identity_result text;
    v_purchase record;
    v_settlement record;
    v_buyer_account uuid;
    v_seller_account uuid;
    v_buyer_balance bigint;
    v_seller_balance bigint;
    v_treasury_balance bigint;
    v_treasury_account uuid :=
        '00000000-0000-0000-0000-000000000001';
    v_debit_reference text;
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

    IF v_purchase.currency <> 'AetherCoins'
       OR v_purchase.status <> 'RESERVED' THEN
        RETURN 'INVALID_PURCHASE_STATE';
    END IF;

    SELECT * INTO v_settlement
    FROM aether_marketplace.settlement_ledger
    WHERE purchase_id = p_purchase_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN 'SETTLEMENT_NOT_PREPARED';
    END IF;

    IF v_settlement.status = 'COMMITTED' THEN
        RETURN 'ALREADY_SETTLED';
    END IF;

    IF v_settlement.status <> 'PENDING' THEN
        RETURN 'INVALID_SETTLEMENT_STATE';
    END IF;

    IF v_settlement.currency <> 'AetherCoins'
       OR v_settlement.realm <> v_purchase.realm
       OR v_settlement.gross_amount <> v_purchase.price
       OR v_settlement.buyer_character_id <>
          v_purchase.buyer_character_id
       OR v_settlement.seller_character_id <>
          v_purchase.seller_character_id
       OR v_settlement.fee_amount <>
          v_purchase.price / 20
       OR v_settlement.seller_amount <>
          v_purchase.price - (v_purchase.price / 20) THEN
        RETURN 'SETTLEMENT_MISMATCH';
    END IF;

    -- TEST-ONLY identity mapping.
    -- Character UUIDs are used as wallet identities until
    -- the native character-to-account bridge is integrated.
        v_identity_result :=
        aether_marketplace.validate_native_trade_accounts(
            v_purchase.realm,
            v_purchase.buyer_character_id,
            v_purchase.seller_character_id
        );

    IF v_identity_result <> 'VERIFIED' THEN
        RETURN v_identity_result;
    END IF;
SELECT openmu_account_id
    INTO v_buyer_account
    FROM aether_marketplace.native_account_binding
    WHERE realm = v_purchase.realm
      AND character_id = v_purchase.buyer_character_id;

    IF NOT FOUND THEN
        RETURN 'BUYER_IDENTITY_NOT_VERIFIED';
    END IF;
    SELECT openmu_account_id
    INTO v_seller_account
    FROM aether_marketplace.native_account_binding
    WHERE realm = v_purchase.realm
      AND character_id = v_purchase.seller_character_id;

    IF NOT FOUND THEN
        RETURN 'SELLER_IDENTITY_NOT_VERIFIED';
    END IF;

    IF v_buyer_account = v_seller_account THEN
        RETURN 'SELF_TRADE';
    END IF;

    -- Minimum AC price: avoids zero-value fee ledger entries.
    IF v_purchase.price < 20 THEN
        RETURN 'AC_PRICE_BELOW_MINIMUM';
    END IF;
    -- Wallet rows must exist before settlement.
    -- Deterministic ordering avoids opposite-direction deadlocks.
    PERFORM 1
    FROM aether_marketplace.ac_wallet
    WHERE realm = v_purchase.realm
      AND account_id IN (
          v_buyer_account,
          v_seller_account,
          v_treasury_account
      )
    ORDER BY account_id
    FOR UPDATE;

    IF (
        SELECT count(*)
        FROM aether_marketplace.ac_wallet
        WHERE realm = v_purchase.realm
          AND account_id IN (
              v_buyer_account,
              v_seller_account,
              v_treasury_account
          )
    ) <> 3 THEN
        RETURN 'WALLET_NOT_FOUND';
    END IF;

    SELECT balance INTO v_buyer_balance
    FROM aether_marketplace.ac_wallet
    WHERE realm = v_purchase.realm
      AND account_id = v_buyer_account;

    SELECT balance INTO v_seller_balance
    FROM aether_marketplace.ac_wallet
    WHERE realm = v_purchase.realm
      AND account_id = v_seller_account;

    SELECT balance INTO v_treasury_balance
    FROM aether_marketplace.ac_wallet
    WHERE realm = v_purchase.realm
      AND account_id = v_treasury_account;

    IF v_buyer_balance < v_purchase.price THEN
        RETURN 'INSUFFICIENT_FUNDS';
    END IF;

    IF v_seller_balance >
       9223372036854775807 - v_settlement.seller_amount
       OR v_treasury_balance >
       9223372036854775807 - v_settlement.fee_amount THEN
        RETURN 'BALANCE_OVERFLOW';
    END IF;

    v_debit_reference := 'ac-marketplace:' || p_purchase_id::text;

    UPDATE aether_marketplace.ac_wallet
    SET balance = balance - v_purchase.price,
        updated_at = now()
    WHERE realm = v_purchase.realm
      AND account_id = v_buyer_account;

    UPDATE aether_marketplace.ac_wallet
    SET balance = balance + v_settlement.seller_amount,
        updated_at = now()
    WHERE realm = v_purchase.realm
      AND account_id = v_seller_account;

    UPDATE aether_marketplace.ac_wallet
    SET balance = balance + v_settlement.fee_amount,
        updated_at = now()
    WHERE realm = v_purchase.realm
      AND account_id = v_treasury_account;

    INSERT INTO aether_marketplace.ac_wallet_ledger
    (operation_id, realm, account_id, amount,
     balance_before, balance_after, operation_type, reference)
    VALUES
    (gen_random_uuid(), v_purchase.realm, v_buyer_account,
     -v_purchase.price, v_buyer_balance,
     v_buyer_balance - v_purchase.price,
     'MARKETPLACE_DEBIT', v_debit_reference || ':buyer'),

    (gen_random_uuid(), v_purchase.realm, v_seller_account,
     v_settlement.seller_amount, v_seller_balance,
     v_seller_balance + v_settlement.seller_amount,
     'MARKETPLACE_CREDIT', v_debit_reference || ':seller'),

    (gen_random_uuid(), v_purchase.realm, v_treasury_account,
     v_settlement.fee_amount, v_treasury_balance,
     v_treasury_balance + v_settlement.fee_amount,
     'MARKETPLACE_CREDIT', v_debit_reference || ':treasury');

    -- Record the 5% AC fee for Castle Siege distribution.
    -- Same PostgreSQL transaction as wallet and settlement updates.
    INSERT INTO aether_marketplace.castle_treasury_ledger (
        purchase_id,
        realm,
        amount
    )
    VALUES (
        p_purchase_id,
        v_purchase.realm,
        v_settlement.fee_amount
    );
    UPDATE aether_marketplace.settlement_ledger
    SET status = 'COMMITTED',
        debit_reference = p_purchase_id,
        committed_at = now()
    WHERE purchase_id = p_purchase_id;

    RETURN 'SETTLED';
END;
$$;