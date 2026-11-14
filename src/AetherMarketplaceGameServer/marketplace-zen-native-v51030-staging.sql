-- V5.10.30 additive migration; requires the verified V5.10.29 schema.
CREATE OR REPLACE FUNCTION aether_marketplace.prepare_native_listing_v30(
    p_listing uuid,p_item uuid,p_seller uuid,p_account uuid,p_realm text,p_price bigint,p_currency text)
RETURNS uuid LANGUAGE plpgsql SECURITY INVOKER SET search_path=pg_catalog
AS $$
DECLARE inventory uuid; escrow uuid;
BEGIN
    IF p_listing IS NULL OR p_item IS NULL OR p_seller IS NULL OR p_account IS NULL OR p_price IS NULL OR p_price <= 0 OR p_currency IS NULL OR p_currency NOT IN ('Zen','AetherCoins') OR (p_currency='AetherCoins' AND p_price < 20) THEN
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
                     AND l.realm=p_realm AND l.currency=p_currency AND l.price=p_price
                     AND c.escrow_storage_id=escrow AND c.status='ACQUIRING' AND l.status='DRAFT') THEN
            RETURN escrow;
        END IF;
        RAISE EXCEPTION 'LISTING_ID_CONFLICT';
    END IF;
    PERFORM 1 FROM data."Item" WHERE "Id"=p_item AND "ItemStorageId"=inventory FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'NOT_ITEM_OWNER'; END IF;
    INSERT INTO aether_marketplace.player_listing(listing_id,seller_character_id,realm,item_instance_id,currency,price,status)
    VALUES(p_listing,p_seller,p_realm,p_item,p_currency,p_price,'DRAFT');
    INSERT INTO aether_marketplace.native_item_custody(listing_id,item_instance_id,escrow_storage_id,seller_character_id,status)
    VALUES(p_listing,p_item,escrow,p_seller,'ACQUIRING');
    RETURN escrow;
END $$;

CREATE OR REPLACE FUNCTION aether_marketplace.authorize_native_delivery_v29(p_purchase uuid)
RETURNS text LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog
AS $$
DECLARE
    p aether_marketplace.purchase_reservation%ROWTYPE;
    c aether_marketplace.native_item_custody%ROWTYPE;
    b uuid;
BEGIN
    SELECT * INTO p FROM aether_marketplace.purchase_reservation WHERE purchase_id=p_purchase FOR UPDATE;
    IF NOT FOUND THEN RETURN 'PURCHASE_NOT_FOUND'; END IF;
    IF p.status NOT IN ('DELIVERY_PENDING','COMPLETED') THEN RETURN 'NOT_PAID'; END IF;
    IF aether_marketplace.verify_native_settlement_v30(p_purchase) IS DISTINCT FROM 'ACCOUNTING_PROOF_VALID'
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
RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog
AS $$
DECLARE
    c aether_marketplace.native_item_custody%ROWTYPE;
    d aether_marketplace.native_custody_delivery%ROWTYPE;
    target uuid;
BEGIN
    SELECT * INTO c FROM aether_marketplace.native_item_custody
    WHERE item_instance_id=OLD."Id" AND status IN ('HELD','ACQUIRING','RETURN_PENDING') FOR UPDATE;
    IF NOT FOUND THEN
        IF TG_OP='DELETE' THEN RETURN OLD; END IF;
        RETURN NEW;
    END IF;
    IF TG_OP='DELETE' THEN RAISE EXCEPTION 'CUSTODY_ITEM_DELETE_FORBIDDEN'; END IF;
    IF c.status='RETURN_PENDING' THEN
        IF NOT EXISTS(SELECT 1 FROM aether_marketplace.native_listing_return r
             JOIN data."Character" ch ON ch."Id"=r.seller_character_id AND ch."AccountId"=r.seller_account_id
             WHERE r.listing_id=c.listing_id AND r.status='PENDING' AND r.item_id=OLD."Id"
               AND r.escrow_storage_id=OLD."ItemStorageId" AND ch."InventoryId"=NEW."ItemStorageId")
           OR (to_jsonb(NEW)-'ItemStorageId'-'ItemSlot') IS DISTINCT FROM (to_jsonb(OLD)-'ItemStorageId'-'ItemSlot')
        THEN RAISE EXCEPTION 'INVALID_CUSTODY_RETURN'; END IF;
        UPDATE aether_marketplace.native_item_custody SET status='RETURNED',delivered_at=now() WHERE listing_id=c.listing_id;
        UPDATE aether_marketplace.native_listing_return SET status='COMPLETED',completed_at=now() WHERE listing_id=c.listing_id;
        RETURN NEW;
    END IF;
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
    IF NOT FOUND OR aether_marketplace.verify_native_settlement_v30(d.purchase_id)
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


CREATE TABLE aether_marketplace.native_zen_settlement (
 purchase_id uuid PRIMARY KEY REFERENCES aether_marketplace.purchase_reservation(purchase_id),
 listing_id uuid NOT NULL, realm text NOT NULL,
 buyer_character_id uuid NOT NULL, seller_character_id uuid NOT NULL,
 buyer_account_id uuid NOT NULL, seller_account_id uuid NOT NULL,
 buyer_inventory_id uuid NOT NULL, seller_inventory_id uuid NOT NULL,
 gross_amount bigint NOT NULL CHECK(gross_amount BETWEEN 1 AND 2147483647),
 buyer_before integer NOT NULL CHECK(buyer_before>=0), buyer_after integer NOT NULL CHECK(buyer_after>=0),
 seller_before integer NOT NULL CHECK(seller_before>=0), seller_after integer NOT NULL CHECK(seller_after>=0),
 committed_at timestamptz NOT NULL DEFAULT now(),
 CHECK(buyer_before::bigint-buyer_after::bigint=gross_amount),
 CHECK(seller_after::bigint-seller_before::bigint=gross_amount),
 CHECK(buyer_account_id<>seller_account_id), CHECK(buyer_inventory_id<>seller_inventory_id)
);

CREATE OR REPLACE FUNCTION aether_marketplace.reject_native_zen_ledger_mutation()
RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN RAISE EXCEPTION 'NATIVE_ZEN_LEDGER_IMMUTABLE'; END $$;
CREATE TRIGGER native_zen_immutable BEFORE UPDATE OR DELETE ON aether_marketplace.native_zen_settlement
FOR EACH ROW EXECUTE FUNCTION aether_marketplace.reject_native_zen_ledger_mutation();

CREATE OR REPLACE FUNCTION aether_marketplace.verify_native_settlement_v30(p_purchase uuid)
RETURNS text LANGUAGE plpgsql SECURITY INVOKER SET search_path=pg_catalog AS $$
DECLARE p aether_marketplace.purchase_reservation%ROWTYPE;
BEGIN
 SELECT * INTO p FROM aether_marketplace.purchase_reservation WHERE purchase_id=p_purchase;
 IF NOT FOUND THEN RETURN 'PURCHASE_NOT_FOUND'; END IF;
 IF p.currency='AetherCoins' THEN RETURN aether_marketplace.verify_ac_settlement_accounting_v2(p_purchase); END IF;
 IF p.currency<>'Zen' THEN RETURN 'INVALID_CURRENCY'; END IF;
 IF NOT EXISTS (
  SELECT 1 FROM aether_marketplace.native_zen_settlement z
  JOIN aether_marketplace.settlement_ledger s ON s.purchase_id=z.purchase_id
  JOIN aether_marketplace.native_account_binding b ON b.realm=z.realm AND b.character_id=z.buyer_character_id AND b.openmu_account_id=z.buyer_account_id
  JOIN aether_marketplace.native_account_binding v ON v.realm=z.realm AND v.character_id=z.seller_character_id AND v.openmu_account_id=z.seller_account_id
  JOIN data."Character" bc ON bc."Id"=z.buyer_character_id AND bc."AccountId"=z.buyer_account_id AND bc."InventoryId"=z.buyer_inventory_id
  JOIN data."Character" sc ON sc."Id"=z.seller_character_id AND sc."AccountId"=z.seller_account_id AND sc."InventoryId"=z.seller_inventory_id
  WHERE z.purchase_id=p_purchase AND z.listing_id=p.listing_id AND z.realm=p.realm
    AND z.buyer_character_id=p.buyer_character_id AND z.seller_character_id=p.seller_character_id
    AND z.gross_amount=p.price AND s.listing_id=p.listing_id AND s.realm=p.realm
    AND s.buyer_character_id=p.buyer_character_id AND s.seller_character_id=p.seller_character_id
    AND s.currency='Zen' AND s.gross_amount=p.price AND s.fee_amount=0 AND s.seller_amount=p.price
    AND s.status='COMMITTED' AND s.debit_reference=p_purchase AND s.committed_at IS NOT NULL
 ) THEN RETURN 'ZEN_ACCOUNTING_PROOF_MISSING'; END IF;
 RETURN 'ACCOUNTING_PROOF_VALID';
END $$;

CREATE OR REPLACE FUNCTION aether_marketplace.settle_authorized_zen_purchase_v30(
 p_purchase uuid,p_realm text,p_buyer uuid,p_account uuid,p_max_money integer)
RETURNS text LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE
 p aether_marketplace.purchase_reservation%ROWTYPE;
 l aether_marketplace.player_listing%ROWTYPE;
 s aether_marketplace.settlement_ledger%ROWTYPE;
 buyer_inventory uuid; seller_inventory uuid; seller_account uuid;
 buyer_balance integer; seller_balance integer;
BEGIN
 SELECT * INTO p FROM aether_marketplace.purchase_reservation WHERE purchase_id=p_purchase FOR UPDATE;
 IF NOT FOUND THEN RETURN 'PURCHASE_NOT_FOUND'; END IF;
 IF p.currency<>'Zen' THEN RETURN 'INVALID_CURRENCY'; END IF;
 IF p.realm IS DISTINCT FROM p_realm OR p.buyer_character_id IS DISTINCT FROM p_buyer
 OR NOT EXISTS(SELECT 1 FROM aether_marketplace.native_account_binding b
  WHERE b.realm=p_realm AND b.character_id=p_buyer AND b.openmu_account_id=p_account)
 THEN RETURN 'UNAUTHORIZED'; END IF;
 SELECT * INTO l FROM aether_marketplace.player_listing WHERE listing_id=p.listing_id FOR UPDATE;
 IF NOT FOUND OR l.realm<>p.realm OR l.price<>p.price OR l.currency<>p.currency
  OR l.seller_character_id<>p.seller_character_id THEN RETURN 'LISTING_MISMATCH'; END IF;
 SELECT * INTO s FROM aether_marketplace.settlement_ledger WHERE purchase_id=p_purchase FOR UPDATE;
 IF NOT FOUND THEN RETURN 'SETTLEMENT_NOT_PREPARED'; END IF;
 IF s.status='COMMITTED' THEN
  IF p.status NOT IN ('DELIVERY_PENDING','COMPLETED')
   OR aether_marketplace.verify_native_settlement_v30(p_purchase)<>'ACCOUNTING_PROOF_VALID'
  THEN RETURN 'SETTLEMENT_MISMATCH'; END IF;
  RETURN 'ALREADY_SETTLED';
 END IF;
 IF p.status<>'RESERVED' OR l.status<>'RESERVED' OR s.status<>'PENDING'
  OR s.currency<>'Zen' OR s.listing_id<>p.listing_id OR s.realm<>p.realm
  OR s.buyer_character_id<>p.buyer_character_id OR s.seller_character_id<>p.seller_character_id
  OR s.gross_amount<>p.price OR s.fee_amount<>0 OR s.seller_amount<>p.price
 THEN RETURN 'SETTLEMENT_MISMATCH'; END IF;
 IF p_max_money IS NULL OR p_max_money<=0 OR p.price>p_max_money THEN RETURN 'INVALID_PRICE'; END IF;
 IF NOT EXISTS(SELECT 1 FROM aether_marketplace.native_item_custody c
  JOIN data."Item" i ON i."Id"=c.item_instance_id AND i."ItemStorageId"=c.escrow_storage_id
  WHERE c.listing_id=p.listing_id AND c.item_instance_id=l.item_instance_id AND c.status='HELD'
    AND c.seller_character_id=p.seller_character_id) THEN RETURN 'CUSTODY_MISMATCH'; END IF;
 SELECT bc."InventoryId" INTO buyer_inventory FROM data."Character" bc
 WHERE bc."Id"=p.buyer_character_id AND bc."AccountId"=p_account;
 SELECT sc."InventoryId",sc."AccountId" INTO seller_inventory,seller_account FROM data."Character" sc
 JOIN aether_marketplace.native_account_binding b ON b.realm=p.realm AND b.character_id=sc."Id" AND b.openmu_account_id=sc."AccountId"
 WHERE sc."Id"=p.seller_character_id;
 IF buyer_inventory IS NULL OR seller_inventory IS NULL OR buyer_inventory=seller_inventory OR p_account=seller_account
 THEN RETURN 'INVALID_IDENTITIES'; END IF;
 -- Ordering protects opposite-direction purchases. The native ORM also compares
 -- the old Money value, preventing a stale session from overwriting this commit.
 PERFORM 1 FROM data."ItemStorage" WHERE "Id" IN(buyer_inventory,seller_inventory) ORDER BY "Id" FOR UPDATE;
 SELECT "Money" INTO buyer_balance FROM data."ItemStorage" WHERE "Id"=buyer_inventory;
 SELECT "Money" INTO seller_balance FROM data."ItemStorage" WHERE "Id"=seller_inventory;
 IF buyer_balance IS NULL OR seller_balance IS NULL OR buyer_balance<0 OR seller_balance<0 THEN RETURN 'INVALID_BALANCE'; END IF;
 IF buyer_balance<p.price THEN RETURN 'INSUFFICIENT_FUNDS'; END IF;
 IF seller_balance::bigint+p.price>p_max_money THEN RETURN 'BALANCE_OVERFLOW'; END IF;
 UPDATE data."ItemStorage" SET "Money"="Money"-p.price::integer WHERE "Id"=buyer_inventory;
 UPDATE data."ItemStorage" SET "Money"="Money"+p.price::integer WHERE "Id"=seller_inventory;
 INSERT INTO aether_marketplace.native_zen_settlement
 (purchase_id,listing_id,realm,buyer_character_id,seller_character_id,buyer_account_id,seller_account_id,
  buyer_inventory_id,seller_inventory_id,gross_amount,buyer_before,buyer_after,seller_before,seller_after)
 VALUES(p_purchase,p.listing_id,p.realm,p.buyer_character_id,p.seller_character_id,p_account,seller_account,
  buyer_inventory,seller_inventory,p.price,buyer_balance,buyer_balance-p.price::integer,seller_balance,seller_balance+p.price::integer);
 UPDATE aether_marketplace.settlement_ledger SET status='COMMITTED',debit_reference=p_purchase,committed_at=now() WHERE purchase_id=p_purchase;
 UPDATE aether_marketplace.purchase_reservation SET status='DELIVERY_PENDING',updated_at=now() WHERE purchase_id=p_purchase;
 IF aether_marketplace.verify_native_settlement_v30(p_purchase)<>'ACCOUNTING_PROOF_VALID' THEN RAISE EXCEPTION 'ZEN_ACCOUNTING_MISMATCH'; END IF;
 RETURN 'SETTLED';
END $$;

DO $$ BEGIN
 IF NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='aether_v30_settlement_runtime') THEN
  CREATE ROLE aether_v30_settlement_runtime NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
 END IF;
END $$;
GRANT USAGE ON SCHEMA aether_marketplace,data TO aether_v30_settlement_runtime;
GRANT SELECT ON aether_marketplace.native_zen_settlement TO aether_v29_custody_owner,aether_v29_runtime;
GRANT SELECT,INSERT ON aether_marketplace.native_zen_settlement TO aether_v29_custody_owner;
GRANT SELECT ON aether_marketplace.purchase_reservation,aether_marketplace.native_account_binding,
 aether_marketplace.native_delivery_store TO aether_v30_settlement_runtime;
GRANT UPDATE("Money") ON data."ItemStorage" TO aether_v29_custody_owner;
GRANT UPDATE ON aether_marketplace.settlement_ledger TO aether_v29_custody_owner;
GRANT CREATE ON SCHEMA aether_marketplace TO aether_v29_custody_owner;
ALTER FUNCTION aether_marketplace.prepare_native_listing_v30(uuid,uuid,uuid,uuid,text,bigint,text) SECURITY DEFINER;
ALTER FUNCTION aether_marketplace.prepare_native_listing_v30(uuid,uuid,uuid,uuid,text,bigint,text) OWNER TO aether_v29_custody_owner;
ALTER FUNCTION aether_marketplace.settle_authorized_zen_purchase_v30(uuid,text,uuid,uuid,integer) OWNER TO aether_v29_custody_owner;
REVOKE CREATE ON SCHEMA aether_marketplace FROM aether_v29_custody_owner;
REVOKE ALL ON FUNCTION aether_marketplace.prepare_native_listing_v30(uuid,uuid,uuid,uuid,text,bigint,text),
 aether_marketplace.verify_native_settlement_v30(uuid),
 aether_marketplace.settle_authorized_zen_purchase_v30(uuid,text,uuid,uuid,integer),
 aether_marketplace.reject_native_zen_ledger_mutation() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION aether_marketplace.verify_native_settlement_v30(uuid) TO aether_v29_custody_owner,aether_v29_runtime,aether_v30_settlement_runtime;
GRANT EXECUTE ON FUNCTION aether_marketplace.prepare_native_listing_v30(uuid,uuid,uuid,uuid,text,bigint,text) TO aether_v29_runtime;
GRANT EXECUTE ON FUNCTION aether_marketplace.settle_authorized_zen_purchase_v30(uuid,text,uuid,uuid,integer) TO aether_v30_settlement_runtime;

ALTER TABLE aether_marketplace.native_item_custody DROP CONSTRAINT custody_status_valid;
ALTER TABLE aether_marketplace.native_item_custody ADD CONSTRAINT custody_status_valid
 CHECK(status IN('ACQUIRING','HELD','DELIVERED','RECONCILIATION_REQUIRED','RETURN_PENDING','RETURNED'));
DROP INDEX aether_marketplace.native_item_one_active_custody;
CREATE UNIQUE INDEX native_item_one_active_custody ON aether_marketplace.native_item_custody(item_instance_id)
 WHERE status IN('ACQUIRING','HELD','RECONCILIATION_REQUIRED','RETURN_PENDING');
CREATE TABLE aether_marketplace.native_listing_return (
 listing_id uuid PRIMARY KEY REFERENCES aether_marketplace.player_listing(listing_id),
 item_id uuid NOT NULL, seller_character_id uuid NOT NULL, seller_account_id uuid NOT NULL,
 escrow_storage_id uuid NOT NULL, realm text NOT NULL,
 status text NOT NULL DEFAULT 'PENDING' CHECK(status IN('PENDING','COMPLETED')),
 created_at timestamptz NOT NULL DEFAULT now(),completed_at timestamptz,
 CHECK((status='PENDING' AND completed_at IS NULL) OR (status='COMPLETED' AND completed_at IS NOT NULL))
);
CREATE OR REPLACE FUNCTION aether_marketplace.cancel_native_listing_v30(p_listing uuid,p_realm text,p_seller uuid,p_account uuid)
RETURNS text LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE l aether_marketplace.player_listing%ROWTYPE; c aether_marketplace.native_item_custody%ROWTYPE;
BEGIN
 SELECT * INTO l FROM aether_marketplace.player_listing WHERE listing_id=p_listing FOR UPDATE;
 IF NOT FOUND THEN RETURN 'LISTING_NOT_FOUND'; END IF;
 IF l.realm IS DISTINCT FROM p_realm OR l.seller_character_id IS DISTINCT FROM p_seller
  OR NOT EXISTS(SELECT 1 FROM aether_marketplace.native_account_binding b JOIN data."Character" ch ON ch."Id"=b.character_id AND ch."AccountId"=b.openmu_account_id
     WHERE b.realm=p_realm AND b.character_id=p_seller AND b.openmu_account_id=p_account) THEN RETURN 'UNAUTHORIZED'; END IF;
 IF l.status='CANCELLED' AND EXISTS(SELECT 1 FROM aether_marketplace.native_listing_return WHERE listing_id=p_listing) THEN RETURN 'ALREADY_CANCELLED'; END IF;
 IF l.status<>'ACTIVE' OR EXISTS(SELECT 1 FROM aether_marketplace.purchase_reservation WHERE listing_id=p_listing AND status<>'CANCELLED') THEN RETURN 'NOT_CANCELLABLE'; END IF;
 SELECT * INTO c FROM aether_marketplace.native_item_custody WHERE listing_id=p_listing FOR UPDATE;
 IF NOT FOUND OR c.status<>'HELD' OR c.item_instance_id<>l.item_instance_id
 OR NOT EXISTS(SELECT 1 FROM data."Item" WHERE "Id"=c.item_instance_id AND "ItemStorageId"=c.escrow_storage_id)
 THEN RETURN 'CUSTODY_MISMATCH'; END IF;
 INSERT INTO aether_marketplace.native_listing_return(listing_id,item_id,seller_character_id,seller_account_id,escrow_storage_id,realm)
 VALUES(p_listing,c.item_instance_id,p_seller,p_account,c.escrow_storage_id,p_realm);
 UPDATE aether_marketplace.native_item_custody SET status='RETURN_PENDING' WHERE listing_id=p_listing;
 UPDATE aether_marketplace.player_listing SET status='CANCELLED',updated_at=now() WHERE listing_id=p_listing;
 RETURN 'CANCELLED';
END $$;

CREATE OR REPLACE FUNCTION aether_marketplace.reserve_authorized_purchase_v30(p_purchase uuid,p_listing uuid,p_buyer uuid,p_account uuid,p_realm text)
RETURNS text LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE result text; prepared text;
BEGIN
 IF NOT EXISTS(SELECT 1 FROM aether_marketplace.native_account_binding b
  JOIN data."Character" ch ON ch."Id"=b.character_id AND ch."AccountId"=b.openmu_account_id
  WHERE b.realm=p_realm AND b.character_id=p_buyer AND b.openmu_account_id=p_account)
 OR NOT EXISTS(SELECT 1 FROM aether_marketplace.player_listing WHERE listing_id=p_listing AND realm=p_realm)
 THEN RETURN 'UNAUTHORIZED'; END IF;
 result:=aether_marketplace.reserve_purchase(p_purchase,p_listing,p_buyer);
 IF result NOT IN('RESERVED','ALREADY_RESERVED') THEN RETURN result; END IF;
 IF EXISTS(SELECT 1 FROM aether_marketplace.purchase_reservation WHERE purchase_id=p_purchase AND status='RESERVED') THEN
  prepared:=aether_marketplace.prepare_settlement(p_purchase);
  IF prepared NOT IN('PREPARED','ALREADY_PREPARED') THEN RAISE EXCEPTION 'SETTLEMENT_PREPARATION_FAILED'; END IF;
 END IF;
 RETURN result;
END $$;

CREATE OR REPLACE FUNCTION aether_marketplace.settle_authorized_ac_entry_v30(p_purchase uuid,p_realm text,p_buyer uuid,p_account uuid)
RETURNS text LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT aether_marketplace.settle_authorized_ac_purchase_v4(p_purchase,p_realm,p_buyer,p_account)
$$;
GRANT SELECT,INSERT,UPDATE ON aether_marketplace.native_listing_return TO aether_v29_custody_owner;
GRANT SELECT ON aether_marketplace.native_listing_return TO aether_v29_runtime;
GRANT SELECT,INSERT,UPDATE ON aether_marketplace.ac_wallet TO aether_v29_custody_owner;
-- PostgreSQL requires an UPDATE privilege for the existing v4 FOR SHARE lock.
-- Only the NOLOGIN function owner receives it; runtime cannot edit identity bindings.
GRANT UPDATE(character_id) ON aether_marketplace.native_account_binding TO aether_v29_custody_owner;
GRANT INSERT ON aether_marketplace.ac_wallet_ledger,aether_marketplace.castle_treasury_ledger,aether_marketplace.settlement_ledger,aether_marketplace.purchase_reservation TO aether_v29_custody_owner;
GRANT EXECUTE ON FUNCTION aether_marketplace.reserve_purchase(uuid,uuid,uuid),aether_marketplace.prepare_settlement(uuid),
 aether_marketplace.settle_authorized_ac_purchase_v4(uuid,text,uuid,uuid),aether_marketplace.settle_verified_ac_purchase_v3(uuid),
 aether_marketplace.validate_native_trade_accounts(text,uuid,uuid) TO aether_v29_custody_owner;
GRANT CREATE ON SCHEMA aether_marketplace TO aether_v29_custody_owner;
ALTER FUNCTION aether_marketplace.cancel_native_listing_v30(uuid,text,uuid,uuid) OWNER TO aether_v29_custody_owner;
ALTER FUNCTION aether_marketplace.reserve_authorized_purchase_v30(uuid,uuid,uuid,uuid,text) OWNER TO aether_v29_custody_owner;
ALTER FUNCTION aether_marketplace.settle_authorized_ac_entry_v30(uuid,text,uuid,uuid) OWNER TO aether_v29_custody_owner;
REVOKE CREATE ON SCHEMA aether_marketplace FROM aether_v29_custody_owner;
REVOKE ALL ON FUNCTION aether_marketplace.cancel_native_listing_v30(uuid,text,uuid,uuid),
 aether_marketplace.reserve_authorized_purchase_v30(uuid,uuid,uuid,uuid,text),
 aether_marketplace.settle_authorized_ac_entry_v30(uuid,text,uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION aether_marketplace.cancel_native_listing_v30(uuid,text,uuid,uuid),
 aether_marketplace.reserve_authorized_purchase_v30(uuid,uuid,uuid,uuid,text),
 aether_marketplace.settle_authorized_ac_entry_v30(uuid,text,uuid,uuid) TO aether_v30_settlement_runtime;
GRANT aether_v29_runtime TO aether_v30_settlement_runtime;
GRANT SELECT ON data."ItemOptionLink",data."ItemItemOfItemSet" TO aether_v29_runtime;
