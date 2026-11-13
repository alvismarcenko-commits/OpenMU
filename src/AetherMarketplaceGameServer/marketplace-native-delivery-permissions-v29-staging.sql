-- Isolated candidate only. Role installation requires separate deployment review.
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='aether_v29_custody_owner') THEN
        CREATE ROLE aether_v29_custody_owner NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
        CREATE ROLE aether_v29_runtime NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
    END IF;
END $$;
GRANT USAGE ON SCHEMA aether_marketplace,data TO aether_v29_custody_owner,aether_v29_runtime;
GRANT SELECT ON ALL TABLES IN SCHEMA aether_marketplace TO aether_v29_custody_owner,aether_v29_runtime;
GRANT SELECT ON data."Character",data."Item",data."ItemStorage" TO aether_v29_custody_owner,aether_v29_runtime;
-- PostgreSQL row locks require an UPDATE privilege; only the non-login owner receives it.
GRANT UPDATE("ItemStorageId") ON data."Item" TO aether_v29_custody_owner;
GRANT INSERT,UPDATE ON aether_marketplace.player_listing,aether_marketplace.native_item_custody,
    aether_marketplace.native_custody_delivery TO aether_v29_custody_owner;
GRANT UPDATE ON aether_marketplace.purchase_reservation TO aether_v29_custody_owner;
GRANT EXECUTE ON FUNCTION aether_marketplace.verify_ac_settlement_accounting_v1(uuid),
    aether_marketplace.verify_ac_settlement_accounting_v2(uuid) TO aether_v29_custody_owner,aether_v29_runtime;
GRANT CREATE ON SCHEMA aether_marketplace TO aether_v29_custody_owner;
ALTER FUNCTION aether_marketplace.prepare_native_listing_v29(uuid,uuid,uuid,uuid,text,bigint) SECURITY DEFINER;
ALTER FUNCTION aether_marketplace.prepare_native_listing_v29(uuid,uuid,uuid,uuid,text,bigint) OWNER TO aether_v29_custody_owner;
ALTER FUNCTION aether_marketplace.authorize_native_delivery_v29(uuid) SECURITY DEFINER;
ALTER FUNCTION aether_marketplace.authorize_native_delivery_v29(uuid) OWNER TO aether_v29_custody_owner;
ALTER FUNCTION aether_marketplace.guard_native_custody_v29() SECURITY DEFINER;
ALTER FUNCTION aether_marketplace.guard_native_custody_v29() OWNER TO aether_v29_custody_owner;
REVOKE CREATE ON SCHEMA aether_marketplace FROM aether_v29_custody_owner;
GRANT EXECUTE ON FUNCTION aether_marketplace.prepare_native_listing_v29(uuid,uuid,uuid,uuid,text,bigint),
    aether_marketplace.authorize_native_delivery_v29(uuid) TO aether_v29_runtime;
-- Runtime receives no table mutation, wallet settlement, DDL or role membership grants.
