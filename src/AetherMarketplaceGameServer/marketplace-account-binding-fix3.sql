-- AetherMU Native Account Binding v1
-- Candidate schema only. Not an authentication authority.
-- Production activation forbidden until trusted GameServer
-- identity verification and database permissions are integrated.

CREATE SCHEMA IF NOT EXISTS aether_marketplace;

CREATE TABLE IF NOT EXISTS
aether_marketplace.native_account_binding
(
    realm text NOT NULL,
    character_id uuid NOT NULL,
    openmu_account_id uuid NOT NULL,
    marketplace_account_id bigint NOT NULL,
    verified_at timestamptz NOT NULL DEFAULT now(),

    PRIMARY KEY (realm, character_id),

    CONSTRAINT native_binding_realm_valid
        CHECK (length(trim(realm)) > 0),

    CONSTRAINT native_binding_character_valid
        CHECK (
            character_id <>
            '00000000-0000-0000-0000-000000000000'::uuid
        ),

    CONSTRAINT native_binding_account_valid
        CHECK (
            openmu_account_id <>
            '00000000-0000-0000-0000-000000000000'::uuid
        ),

    CONSTRAINT native_binding_marketplace_valid
        CHECK (marketplace_account_id > 0)
);

CREATE INDEX IF NOT EXISTS native_binding_account_idx
ON aether_marketplace.native_account_binding
(realm, openmu_account_id);

CREATE UNIQUE INDEX IF NOT EXISTS
native_binding_marketplace_account_unique
ON aether_marketplace.native_account_binding
(realm, marketplace_account_id, character_id);

CREATE OR REPLACE FUNCTION
aether_marketplace.validate_native_trade_accounts(
    p_realm text,
    p_buyer_character_id uuid,
    p_seller_character_id uuid
)
RETURNS text
LANGUAGE plpgsql
AS $$
DECLARE
    v_buyer_account uuid;
    v_seller_account uuid;
    v_buyer_marketplace bigint;
    v_seller_marketplace bigint;
BEGIN
    IF p_realm IS NULL
       OR length(trim(p_realm)) = 0
       OR p_buyer_character_id IS NULL
       OR p_seller_character_id IS NULL
       OR p_buyer_character_id = p_seller_character_id THEN
        RETURN 'INVALID_IDENTITIES';
    END IF;

    SELECT openmu_account_id, marketplace_account_id
    INTO v_buyer_account, v_buyer_marketplace
    FROM aether_marketplace.native_account_binding
    WHERE realm = p_realm
      AND character_id = p_buyer_character_id;

    IF NOT FOUND THEN
        RETURN 'BUYER_IDENTITY_NOT_VERIFIED';
    END IF;

    SELECT openmu_account_id, marketplace_account_id
    INTO v_seller_account, v_seller_marketplace
    FROM aether_marketplace.native_account_binding
    WHERE realm = p_realm
      AND character_id = p_seller_character_id;

    IF NOT FOUND THEN
        RETURN 'SELLER_IDENTITY_NOT_VERIFIED';
    END IF;

    IF v_buyer_account = v_seller_account
       OR v_buyer_marketplace = v_seller_marketplace THEN
        RETURN 'SAME_ACCOUNT_TRADE_BLOCKED';
    END IF;

    RETURN 'VERIFIED';
END;
$$;