-- AetherMU Marketplace v4.15
-- REVIEW-ONLY MIGRATION CANDIDATE
-- Never apply to production automatically.
-- Currency: Zen or AetherCoins. APP is not a Marketplace currency.
-- This schema reserves purchases; it does not debit balances or deliver items.

CREATE SCHEMA IF NOT EXISTS aether_marketplace;

CREATE TABLE aether_marketplace.player_listing (
    listing_id uuid PRIMARY KEY,
    seller_character_id uuid NOT NULL,
    realm text NOT NULL,
    item_instance_id uuid NOT NULL,
    currency text NOT NULL,
    price bigint NOT NULL,
    status text NOT NULL DEFAULT 'ACTIVE',
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT listing_price_positive CHECK (price > 0),
    CONSTRAINT listing_currency_valid CHECK (currency IN ('Zen', 'AetherCoins')),
    CONSTRAINT listing_status_valid CHECK (
        status IN (
            'DRAFT',
            'RESERVED',
            'ACTIVE',
            'SOLD',
            'CANCELLED',
            'RECONCILIATION_REQUIRED'
        )
    ),
    CONSTRAINT listing_realm_not_empty CHECK (length(trim(realm)) > 0),
    CONSTRAINT listing_item_unique UNIQUE (item_instance_id)
);

CREATE TABLE aether_marketplace.purchase_reservation (
    purchase_id uuid PRIMARY KEY,
    listing_id uuid NOT NULL REFERENCES aether_marketplace.player_listing(listing_id),
    buyer_character_id uuid NOT NULL,
    seller_character_id uuid NOT NULL,
    realm text NOT NULL,
    currency text NOT NULL,
    price bigint NOT NULL,
    status text NOT NULL DEFAULT 'RESERVED',
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT purchase_price_positive CHECK (price > 0),
    CONSTRAINT purchase_currency_valid CHECK (currency IN ('Zen', 'AetherCoins')),
    CONSTRAINT purchase_no_self_trade CHECK (buyer_character_id <> seller_character_id),
    CONSTRAINT purchase_status_valid CHECK (
        status IN (
            'RESERVED',
            'PAYMENT_PENDING',
            'PAYMENT_CONFIRMED',
            'DELIVERY_PENDING',
            'COMPLETED',
            'CANCELLED',
            'RECONCILIATION_REQUIRED'
        )
    )
);

-- One unresolved purchase per listing.
-- COMPLETED reservations also block reuse of the sold listing.
CREATE UNIQUE INDEX purchase_listing_single_owner
ON aether_marketplace.purchase_reservation(listing_id)
WHERE status <> 'CANCELLED';

-- Reservation must be performed inside a database transaction.
-- The transaction must lock the listing row (SELECT ... FOR UPDATE),
-- verify ACTIVE status, immutable item/price/currency/realm,
-- insert purchase_reservation, and change listing to RESERVED.
-- On conflict or uncertainty: fail closed, do not charge or deliver.
-- Cancellation/retry must be controlled by a reconciliation workflow.