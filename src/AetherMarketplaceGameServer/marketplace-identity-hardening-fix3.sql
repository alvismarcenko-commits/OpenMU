-- AetherMU Marketplace Identity Hardening v1
-- Staging only. Identity bindings must originate from
-- an authenticated and authorized GameServer workflow.

CREATE OR REPLACE FUNCTION
aether_marketplace.reject_native_binding_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION 'NATIVE_ACCOUNT_BINDING_IMMUTABLE';
END;
$$;

CREATE TRIGGER native_binding_no_update
BEFORE UPDATE ON aether_marketplace.native_account_binding
FOR EACH ROW
EXECUTE FUNCTION aether_marketplace.reject_native_binding_mutation();

CREATE TRIGGER native_binding_no_delete
BEFORE DELETE ON aether_marketplace.native_account_binding
FOR EACH ROW
EXECUTE FUNCTION aether_marketplace.reject_native_binding_mutation();

CREATE TABLE aether_marketplace.marketplace_account_identity (
    realm text NOT NULL,
    marketplace_account_id bigint NOT NULL,
    openmu_account_id uuid NOT NULL,
    PRIMARY KEY (realm, marketplace_account_id),
    UNIQUE (realm, openmu_account_id),
    UNIQUE (realm, marketplace_account_id, openmu_account_id),
    CHECK (length(trim(realm)) > 0),
    CHECK (marketplace_account_id > 0),
    CHECK (openmu_account_id <> '00000000-0000-0000-0000-000000000000'::uuid)
);

ALTER TABLE aether_marketplace.native_account_binding
ADD CONSTRAINT native_binding_verified_account_fk
FOREIGN KEY (realm, marketplace_account_id, openmu_account_id)
REFERENCES aether_marketplace.marketplace_account_identity
(realm, marketplace_account_id, openmu_account_id)
ON UPDATE RESTRICT
ON DELETE RESTRICT;