-- AetherMU Marketplace Settlement Ledger Fix3
-- STAGING / ISOLATED TEST ONLY.
-- This file defines accounting records, not real payment processing.

CREATE SCHEMA IF NOT EXISTS aether_marketplace;

CREATE TABLE IF NOT EXISTS aether_marketplace.settlement_ledger (
    purchase_id uuid PRIMARY KEY,
    listing_id uuid NOT NULL,
    realm text NOT NULL,
    buyer_character_id uuid NOT NULL,
    seller_character_id uuid NOT NULL,
    currency text NOT NULL
        CHECK (currency IN ('Zen', 'AetherCoins')),
    gross_amount bigint NOT NULL CHECK (gross_amount > 0),
    fee_amount bigint NOT NULL CHECK (fee_amount >= 0),
    seller_amount bigint NOT NULL CHECK (seller_amount >= 0),
    status text NOT NULL DEFAULT 'PENDING'
        CHECK (status IN (
            'PENDING',
            'FUNDS_RESERVED',
            'COMMITTED',
            'RECONCILIATION_REQUIRED'
        )),
    debit_reference uuid,
    created_at timestamptz NOT NULL DEFAULT now(),
    committed_at timestamptz,
    CONSTRAINT settlement_amounts_balance
        CHECK (gross_amount = fee_amount + seller_amount),
    CONSTRAINT settlement_fee_policy
        CHECK (
            (currency = 'Zen' AND fee_amount = 0)
            OR
            (currency = 'AetherCoins'
             AND fee_amount = gross_amount / 20)
        ),
    CONSTRAINT settlement_distinct_parties
        CHECK (buyer_character_id <> seller_character_id),
    CONSTRAINT settlement_committed_requires_debit
        CHECK (
            status <> 'COMMITTED'
            OR (debit_reference IS NOT NULL AND committed_at IS NOT NULL)
        )
);

CREATE UNIQUE INDEX IF NOT EXISTS settlement_unique_debit
ON aether_marketplace.settlement_ledger(debit_reference)
WHERE debit_reference IS NOT NULL;

CREATE TABLE IF NOT EXISTS aether_marketplace.castle_treasury_ledger (
    purchase_id uuid PRIMARY KEY
        REFERENCES aether_marketplace.settlement_ledger(purchase_id),
    realm text NOT NULL,
    amount bigint NOT NULL CHECK (amount > 0),
    created_at timestamptz NOT NULL DEFAULT now()
);

-- Castle Siege payout 50/30/20 is intentionally not activated here.
-- The treasury ledger records accruals only.
-- No debit, credit, or delivery is executed by this script.