-- AetherMU Europe - AC Wallet v1
-- STAGING ONLY. No production deployment.
-- AC is independent from WCoins, APP and Zen.

CREATE SCHEMA IF NOT EXISTS aether_marketplace;

CREATE TABLE IF NOT EXISTS aether_marketplace.ac_wallet (
    realm text NOT NULL,
    account_id uuid NOT NULL,
    balance bigint NOT NULL DEFAULT 0,
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (realm, account_id),
    CONSTRAINT ac_wallet_realm_valid
        CHECK (length(trim(realm)) > 0),
    CONSTRAINT ac_wallet_balance_nonnegative
        CHECK (balance >= 0)
);

CREATE TABLE IF NOT EXISTS aether_marketplace.ac_wallet_ledger (
    operation_id uuid PRIMARY KEY,
    realm text NOT NULL,
    account_id uuid NOT NULL,
    amount bigint NOT NULL,
    balance_before bigint NOT NULL,
    balance_after bigint NOT NULL,
    operation_type text NOT NULL,
    reference text NOT NULL UNIQUE,
    created_at timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT ac_ledger_wallet_fk
        FOREIGN KEY (realm, account_id)
        REFERENCES aether_marketplace.ac_wallet(realm, account_id),

    CONSTRAINT ac_ledger_amount_nonzero
        CHECK (amount <> 0),

    CONSTRAINT ac_ledger_balance_valid
        CHECK (
            balance_before >= 0
            AND balance_after >= 0
            AND balance_after::numeric =
                balance_before::numeric + amount::numeric
        ),

    CONSTRAINT ac_ledger_type_valid
        CHECK (
            operation_type IN (
                'CREDIT',
                'DEBIT',
                'MARKETPLACE_DEBIT',
                'MARKETPLACE_CREDIT',
                'REFUND',
                'ADMIN_ADJUSTMENT'
            )
        ),

    CONSTRAINT ac_ledger_reference_valid
        CHECK (length(trim(reference)) > 0)
);

CREATE INDEX IF NOT EXISTS ac_wallet_ledger_account_idx
    ON aether_marketplace.ac_wallet_ledger
    (realm, account_id, created_at);