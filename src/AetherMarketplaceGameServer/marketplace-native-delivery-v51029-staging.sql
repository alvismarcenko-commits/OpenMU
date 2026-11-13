-- Isolated candidate. Must be installed in the SAME database as OpenMU data.
-- No runtime permissions or production activation are granted here.
CREATE TABLE aether_marketplace.native_delivery_store (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    store_id uuid NOT NULL UNIQUE DEFAULT gen_random_uuid()
);
INSERT INTO aether_marketplace.native_delivery_store(singleton) VALUES(true);
ALTER TABLE aether_marketplace.native_item_custody DROP CONSTRAINT custody_status_valid;
ALTER TABLE aether_marketplace.native_item_custody ADD CONSTRAINT custody_status_valid
CHECK (status IN ('ACQUIRING','HELD','DELIVERED','RECONCILIATION_REQUIRED'));
DROP INDEX aether_marketplace.native_item_one_active_custody;
CREATE UNIQUE INDEX native_item_one_active_custody ON aether_marketplace.native_item_custody(item_instance_id)
WHERE status IN ('ACQUIRING','HELD','RECONCILIATION_REQUIRED');
CREATE TABLE aether_marketplace.native_realm_custody (
    realm text PRIMARY KEY, storage_id uuid NOT NULL UNIQUE REFERENCES data."ItemStorage"("Id")
);

CREATE OR REPLACE FUNCTION aether_marketplace.prepare_native_listing_v29(
    p_listing uuid,p_item uuid,p_seller uuid,p_account uuid,p_realm text,p_price bigint)
RETURNS uuid LANGUAGE plpgsql SECURITY INVOKER SET search_path=pg_catalog
AS $$
DECLARE inventory uuid; escrow uuid;
BEGIN
    IF p_listing IS NULL OR p_item IS NULL OR p_seller IS NULL OR p_account IS NULL OR p_price < 20 THEN
        RAISE EXCEPTION 'INVALID_LISTING';
    END IF;
    SELECT ch."InventoryId" INTO inventory FROM data."Character" ch
    JOIN aether_marketplace.native_account_binding b ON b.character_id=ch."Id" AND b.realm=p_realm
    WHERE ch."Id"=p_seller AND ch."AccountId"=p_account AND b.openmu_account_id=p_account;
    SELECT storage_id INTO escrow FROM aether_marketplace.native_realm_custody WHERE realm=p_realm;
    IF inventory IS NULL OR escrow IS NULL OR inventory=escrow THEN RAISE EXCEPTION 'INVALID_CUSTODY'; END IF;
    PERFORM pg_advisory_xact_lock(hashtextextended(p_listing::text,29));
    IF EXISTS (SELECT 1 FROM aether_marketplace.player_listing WHERE listing_id=p_listing) THEN
        IF EXISTS (SELECT 1 FROM aether_marketplace.player_listing l
                   JOIN aether_marketplace.native_item_custody c ON c.listing_id=l.listing_id
                   WHERE l.listing_id=p_listing AND l.item_instance_id=p_item AND l.seller_character_id=p_seller
                     AND l.realm=p_realm AND l.currency='AetherCoins' AND l.price=p_price
                     AND c.escrow_storage_id=escrow AND c.status='ACQUIRING' AND l.status='DRAFT') THEN
            RETURN escrow;
        END IF;
        RAISE EXCEPTION 'LISTING_ID_CONFLICT';
    END IF;
    PERFORM 1 FROM data."Item" WHERE "Id"=p_item AND "ItemStorageId"=inventory FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'NOT_ITEM_OWNER'; END IF;
    INSERT INTO aether_marketplace.player_listing(listing_id,seller_character_id,realm,item_instance_id,currency,price,status)
    VALUES(p_listing,p_seller,p_realm,p_item,'AetherCoins',p_price,'DRAFT');
    INSERT INTO aether_marketplace.native_item_custody(listing_id,item_instance_id,escrow_storage_id,seller_character_id,status)
    VALUES(p_listing,p_item,escrow,p_seller,'ACQUIRING');
    RETURN escrow;
END $$;
REVOKE ALL ON FUNCTION aether_marketplace.prepare_native_listing_v29(uuid,uuid,uuid,uuid,text,bigint) FROM PUBLIC;

CREATE TABLE aether_marketplace.native_custody_delivery (
    purchase_id uuid PRIMARY KEY REFERENCES aether_marketplace.purchase_reservation(purchase_id),
    item_id uuid NOT NULL,
    buyer_character_id uuid NOT NULL,
    buyer_account_id uuid NOT NULL,
    escrow_storage_id uuid NOT NULL,
    realm text NOT NULL,
    status text NOT NULL DEFAULT 'PENDING' CHECK (status IN ('PENDING','COMPLETED')),
    completed_at timestamptz,
    CHECK ((status='PENDING' AND completed_at IS NULL) OR (status='COMPLETED' AND completed_at IS NOT NULL))
);
CREATE UNIQUE INDEX native_delivery_one_pending_item ON aether_marketplace.native_custody_delivery(item_id)
WHERE status='PENDING';

CREATE OR REPLACE FUNCTION aether_marketplace.authorize_native_delivery_v29(p_purchase uuid)
RETURNS text LANGUAGE plpgsql SECURITY INVOKER SET search_path=pg_catalog
AS $$
DECLARE
    p aether_marketplace.purchase_reservation%ROWTYPE;
    c aether_marketplace.native_item_custody%ROWTYPE;
    b uuid;
BEGIN
    SELECT * INTO p FROM aether_marketplace.purchase_reservation WHERE purchase_id=p_purchase FOR UPDATE;
    IF NOT FOUND THEN RETURN 'PURCHASE_NOT_FOUND'; END IF;
    IF p.status NOT IN ('DELIVERY_PENDING','COMPLETED') THEN RETURN 'NOT_PAID'; END IF;
    IF aether_marketplace.verify_ac_settlement_accounting_v2(p_purchase) IS DISTINCT FROM 'ACCOUNTING_PROOF_VALID'
    THEN RETURN 'INVALID_PAYMENT_PROOF'; END IF;
    SELECT * INTO c FROM aether_marketplace.native_item_custody WHERE listing_id=p.listing_id FOR UPDATE;
    IF NOT FOUND OR c.seller_character_id IS DISTINCT FROM p.seller_character_id
       OR NOT EXISTS (SELECT 1 FROM aether_marketplace.player_listing l WHERE l.listing_id=p.listing_id
                      AND l.item_instance_id=c.item_instance_id AND l.realm=p.realm
                      AND l.price=p.price AND l.currency=p.currency AND l.seller_character_id=p.seller_character_id)
    THEN RETURN 'CUSTODY_MISMATCH'; END IF;
    SELECT openmu_account_id INTO STRICT b FROM aether_marketplace.native_account_binding
    WHERE realm=p.realm AND character_id=p.buyer_character_id;
    IF EXISTS (SELECT 1 FROM aether_marketplace.native_custody_delivery d WHERE d.purchase_id=p_purchase) THEN
        IF NOT EXISTS (SELECT 1 FROM aether_marketplace.native_custody_delivery d
                       WHERE d.purchase_id=p_purchase AND d.item_id=c.item_instance_id
                         AND d.buyer_character_id=p.buyer_character_id AND d.buyer_account_id=b
                         AND d.escrow_storage_id=c.escrow_storage_id AND d.realm=p.realm)
        THEN RETURN 'DELIVERY_MISMATCH'; END IF;
        RETURN 'ALREADY_AUTHORIZED';
    END IF;
    IF c.status <> 'HELD' OR p.status <> 'DELIVERY_PENDING'
       OR NOT EXISTS (SELECT 1 FROM data."Item" WHERE "Id"=c.item_instance_id AND "ItemStorageId"=c.escrow_storage_id)
    THEN RETURN 'ITEM_NOT_IN_CUSTODY'; END IF;
    INSERT INTO aether_marketplace.native_custody_delivery
        (purchase_id,item_id,buyer_character_id,buyer_account_id,escrow_storage_id,realm)
    VALUES(p_purchase,c.item_instance_id,p.buyer_character_id,b,c.escrow_storage_id,p.realm);
    RETURN 'AUTHORIZED';
END $$;

CREATE OR REPLACE FUNCTION aether_marketplace.guard_native_custody_v29()
RETURNS trigger LANGUAGE plpgsql SECURITY INVOKER SET search_path=pg_catalog
AS $$
DECLARE
    c aether_marketplace.native_item_custody%ROWTYPE;
    d aether_marketplace.native_custody_delivery%ROWTYPE;
    target uuid;
BEGIN
    SELECT * INTO c FROM aether_marketplace.native_item_custody
    WHERE item_instance_id=OLD."Id" AND status IN ('HELD','ACQUIRING') FOR UPDATE;
    IF NOT FOUND THEN
        IF TG_OP='DELETE' THEN RETURN OLD; END IF;
        RETURN NEW;
    END IF;
    IF TG_OP='DELETE' THEN RAISE EXCEPTION 'CUSTODY_ITEM_DELETE_FORBIDDEN'; END IF;
    IF c.status='ACQUIRING' THEN
        IF NEW."ItemStorageId" IS DISTINCT FROM c.escrow_storage_id
           OR NOT EXISTS (SELECT 1 FROM data."Character" WHERE "Id"=c.seller_character_id AND "InventoryId"=OLD."ItemStorageId")
           OR (to_jsonb(NEW)-'ItemStorageId'-'ItemSlot') IS DISTINCT FROM (to_jsonb(OLD)-'ItemStorageId'-'ItemSlot')
        THEN RAISE EXCEPTION 'INVALID_CUSTODY_ACQUISITION'; END IF;
        UPDATE aether_marketplace.native_item_custody SET status='HELD' WHERE listing_id=c.listing_id;
        UPDATE aether_marketplace.player_listing SET status='ACTIVE',updated_at=now() WHERE listing_id=c.listing_id AND status='DRAFT';
        IF NOT FOUND THEN RAISE EXCEPTION 'INVALID_CUSTODY_LISTING'; END IF;
        RETURN NEW;
    END IF;
    IF OLD."ItemStorageId" IS DISTINCT FROM c.escrow_storage_id THEN
        RAISE EXCEPTION 'CUSTODY_SOURCE_MISMATCH';
    END IF;
    IF NEW."ItemStorageId" IS NOT DISTINCT FROM OLD."ItemStorageId" THEN
        IF to_jsonb(NEW) IS DISTINCT FROM to_jsonb(OLD) THEN RAISE EXCEPTION 'CUSTODY_ITEM_IMMUTABLE'; END IF;
        RETURN NEW;
    END IF;
    SELECT * INTO d FROM aether_marketplace.native_custody_delivery
    WHERE item_id=OLD."Id" AND status='PENDING' FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'DELIVERY_NOT_AUTHORIZED'; END IF;
    PERFORM 1 FROM aether_marketplace.purchase_reservation
    WHERE purchase_id=d.purchase_id AND status='DELIVERY_PENDING' AND realm=d.realm
      AND buyer_character_id=d.buyer_character_id AND listing_id=c.listing_id FOR UPDATE;
    IF NOT FOUND OR aether_marketplace.verify_ac_settlement_accounting_v2(d.purchase_id)
        IS DISTINCT FROM 'ACCOUNTING_PROOF_VALID' THEN RAISE EXCEPTION 'INVALID_PAYMENT_PROOF'; END IF;
    IF NOT EXISTS (SELECT 1 FROM aether_marketplace.player_listing l
                   JOIN aether_marketplace.purchase_reservation p ON p.listing_id=l.listing_id
                   WHERE p.purchase_id=d.purchase_id AND l.status='RESERVED' AND l.item_instance_id=OLD."Id"
                     AND l.realm=d.realm AND l.price=p.price AND l.currency=p.currency
                     AND l.seller_character_id=c.seller_character_id AND p.seller_character_id=c.seller_character_id)
    THEN RAISE EXCEPTION 'LISTING_MISMATCH'; END IF;
    SELECT ch."InventoryId" INTO target FROM data."Character" ch
    JOIN aether_marketplace.native_account_binding b ON b.character_id=ch."Id" AND b.realm=d.realm
    WHERE ch."Id"=d.buyer_character_id AND ch."AccountId"=d.buyer_account_id AND b.openmu_account_id=d.buyer_account_id;
    IF target IS NULL OR target=c.escrow_storage_id OR NEW."ItemStorageId" IS DISTINCT FROM target
       OR (to_jsonb(NEW)-'ItemStorageId'-'ItemSlot') IS DISTINCT FROM (to_jsonb(OLD)-'ItemStorageId'-'ItemSlot')
    THEN RAISE EXCEPTION 'INVALID_NATIVE_TRANSFER'; END IF;
    -- These writes participate in the native ORM save transaction. A failed save
    -- rolls back ownership AND completion; a lost acknowledgement needs no second move.
    UPDATE aether_marketplace.native_custody_delivery SET status='COMPLETED',completed_at=now() WHERE purchase_id=d.purchase_id;
    UPDATE aether_marketplace.native_item_custody SET status='DELIVERED',delivered_at=now() WHERE listing_id=c.listing_id;
    UPDATE aether_marketplace.purchase_reservation SET status='COMPLETED',updated_at=now() WHERE purchase_id=d.purchase_id;
    UPDATE aether_marketplace.player_listing SET status='SOLD',updated_at=now() WHERE listing_id=c.listing_id;
    RETURN NEW;
END $$;

CREATE TRIGGER marketplace_native_custody_guard BEFORE UPDATE OR DELETE ON data."Item"
FOR EACH ROW EXECUTE FUNCTION aether_marketplace.guard_native_custody_v29();
REVOKE ALL ON FUNCTION aether_marketplace.authorize_native_delivery_v29(uuid) FROM PUBLIC;
REVOKE ALL ON FUNCTION aether_marketplace.guard_native_custody_v29() FROM PUBLIC;
