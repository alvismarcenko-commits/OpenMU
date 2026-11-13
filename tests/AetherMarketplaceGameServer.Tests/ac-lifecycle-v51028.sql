\set ON_ERROR_STOP on
-- Synthetic fixtures only; runner supplies a fresh disposable PostgreSQL server.
CREATE FUNCTION pg_temp.check_result(actual text, expected text) RETURNS void
LANGUAGE plpgsql AS $$ BEGIN
    IF actual IS DISTINCT FROM expected THEN RAISE EXCEPTION 'Expected %, got %', expected, actual; END IF;
END $$;

CREATE FUNCTION pg_temp.fail_paid_transition() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.status = 'DELIVERY_PENDING' THEN
        RAISE EXCEPTION 'injected status write failure' USING ERRCODE = 'ZX002';
    END IF;
    RETURN NEW;
END $$;

DO $test$
DECLARE
    buyer uuid := gen_random_uuid(); seller uuid := gen_random_uuid();
    account uuid := gen_random_uuid(); seller_account uuid := gen_random_uuid();
    listing uuid := gen_random_uuid(); purchase uuid := gen_random_uuid();
    treasury uuid := '00000000-0000-0000-0000-000000000001';
    before_ledger jsonb;
BEGIN
    INSERT INTO aether_marketplace.marketplace_account_identity VALUES ('Origin',1,account),('Origin',2,seller_account);
    INSERT INTO aether_marketplace.native_account_binding(realm,character_id,openmu_account_id,marketplace_account_id)
    VALUES ('Origin',buyer,account,1),('Origin',seller,seller_account,2);
    INSERT INTO aether_marketplace.ac_wallet(realm,account_id,balance)
    VALUES ('Origin',account,2000),('Origin',seller_account,0),('Origin',treasury,0);
    INSERT INTO aether_marketplace.player_listing(listing_id,seller_character_id,realm,item_instance_id,currency,price)
    VALUES (listing,seller,'Origin',gen_random_uuid(),'AetherCoins',1000);
    PERFORM pg_temp.check_result(aether_marketplace.reserve_purchase(purchase,listing,buyer),'RESERVED');
    PERFORM pg_temp.check_result(aether_marketplace.prepare_settlement(purchase),'PREPARED');
    PERFORM pg_temp.check_result(aether_marketplace.settle_authorized_ac_purchase_v4(purchase,'Legacy',buyer,account),'UNAUTHORIZED');
    PERFORM pg_temp.check_result(aether_marketplace.settle_authorized_ac_purchase_v4(purchase,'Origin',gen_random_uuid(),account),'UNAUTHORIZED');
    PERFORM pg_temp.check_result(aether_marketplace.settle_authorized_ac_purchase_v4(purchase,'Origin',buyer,gen_random_uuid()),'UNAUTHORIZED');
    RAISE NOTICE 'PASS wrong realm, character, account';
    CREATE TRIGGER injected_transition_failure BEFORE UPDATE ON aether_marketplace.purchase_reservation
    FOR EACH ROW EXECUTE FUNCTION pg_temp.fail_paid_transition();
    BEGIN
        PERFORM aether_marketplace.settle_authorized_ac_purchase_v4(purchase,'Origin',buyer,account);
        RAISE EXCEPTION 'Expected status update to fail';
    EXCEPTION WHEN SQLSTATE 'ZX002' THEN NULL;
    END;
    DROP TRIGGER injected_transition_failure ON aether_marketplace.purchase_reservation;
    PERFORM pg_temp.check_result((SELECT status FROM aether_marketplace.settlement_ledger WHERE purchase_id=purchase),'PENDING');
    PERFORM pg_temp.check_result((SELECT balance::text FROM aether_marketplace.ac_wallet WHERE account_id=account),'2000');
    IF EXISTS (SELECT 1 FROM aether_marketplace.ac_wallet_ledger)
       OR EXISTS (SELECT 1 FROM aether_marketplace.castle_treasury_ledger)
    THEN RAISE EXCEPTION 'Accounting survived failed status transition'; END IF;
    RAISE NOTICE 'PASS status-write failure rolls back settlement and treasury';
    -- An outer transaction failure after settlement must restore every write.
    BEGIN
        PERFORM pg_temp.check_result(aether_marketplace.settle_authorized_ac_purchase_v4(purchase,'Origin',buyer,account),'SETTLED');
        RAISE EXCEPTION 'injected failure' USING ERRCODE = 'ZX001';
    EXCEPTION WHEN SQLSTATE 'ZX001' THEN NULL;
    END;
    PERFORM pg_temp.check_result((SELECT status FROM aether_marketplace.purchase_reservation WHERE purchase_id=purchase),'RESERVED');
    PERFORM pg_temp.check_result((SELECT balance::text FROM aether_marketplace.ac_wallet WHERE account_id=account),'2000');
    IF EXISTS (SELECT 1 FROM aether_marketplace.ac_wallet_ledger) THEN RAISE EXCEPTION 'Ledger survived rollback'; END IF;
    RAISE NOTICE 'PASS PostgreSQL failure rolls back wallets, ledger, reservation';
    PERFORM pg_temp.check_result(aether_marketplace.settle_authorized_ac_purchase_v4(purchase,'Origin',buyer,account),'SETTLED');
    PERFORM pg_temp.check_result((SELECT status FROM aether_marketplace.purchase_reservation WHERE purchase_id=purchase),'DELIVERY_PENDING');
    PERFORM pg_temp.check_result((SELECT balance::text FROM aether_marketplace.ac_wallet WHERE account_id=account),'1000');
    PERFORM pg_temp.check_result((SELECT balance::text FROM aether_marketplace.ac_wallet WHERE account_id=seller_account),'950');
    PERFORM pg_temp.check_result((SELECT balance::text FROM aether_marketplace.ac_wallet WHERE account_id=treasury),'50');
    PERFORM pg_temp.check_result(aether_marketplace.verify_ac_settlement_accounting_v2(purchase),'ACCOUNTING_PROOF_VALID');
    RAISE NOTICE 'PASS AC settlement, status, seller payout, treasury, proof';
    SELECT jsonb_agg(to_jsonb(l) ORDER BY reference) INTO before_ledger FROM aether_marketplace.ac_wallet_ledger l;
    PERFORM pg_temp.check_result(aether_marketplace.settle_authorized_ac_purchase_v4(purchase,'Origin',buyer,account),'ALREADY_SETTLED');
    UPDATE aether_marketplace.purchase_reservation SET status='RESERVED' WHERE purchase_id=purchase;
    PERFORM pg_temp.check_result(aether_marketplace.settle_authorized_ac_purchase_v4(purchase,'Origin',buyer,account),'ALREADY_SETTLED');
    PERFORM pg_temp.check_result((SELECT status FROM aether_marketplace.purchase_reservation WHERE purchase_id=purchase),'DELIVERY_PENDING');
    IF before_ledger IS DISTINCT FROM (SELECT jsonb_agg(to_jsonb(l) ORDER BY reference) FROM aether_marketplace.ac_wallet_ledger l)
    THEN RAISE EXCEPTION 'Replay changed historical ledger'; END IF;
    RAISE NOTICE 'PASS replay and historical RESERVED repair preserve ledger';
    UPDATE aether_marketplace.castle_treasury_ledger SET realm='Legacy' WHERE purchase_id=purchase;
    PERFORM pg_temp.check_result(aether_marketplace.verify_ac_settlement_accounting_v2(purchase),'TREASURY_PROOF_MISSING');
    UPDATE aether_marketplace.castle_treasury_ledger SET realm='Origin' WHERE purchase_id=purchase;
    UPDATE aether_marketplace.ac_wallet_ledger SET operation_type='CREDIT' WHERE reference='ac-marketplace:'||purchase::text||':seller';
    PERFORM pg_temp.check_result(aether_marketplace.verify_ac_settlement_accounting_v2(purchase),'WALLET_PROOF_MISSING');
    RAISE NOTICE 'PASS tampered seller/treasury evidence rejected';
    listing := gen_random_uuid(); purchase := gen_random_uuid();
    INSERT INTO aether_marketplace.player_listing(listing_id,seller_character_id,realm,item_instance_id,currency,price)
    VALUES (listing,seller,'Origin',gen_random_uuid(),'AetherCoins',1001);
    PERFORM pg_temp.check_result(aether_marketplace.reserve_purchase(purchase,listing,buyer),'RESERVED');
    PERFORM pg_temp.check_result(aether_marketplace.prepare_settlement(purchase),'PREPARED');
    PERFORM pg_temp.check_result(aether_marketplace.settle_authorized_ac_purchase_v4(purchase,'Origin',buyer,account),'INSUFFICIENT_FUNDS');
    PERFORM pg_temp.check_result((SELECT status FROM aether_marketplace.purchase_reservation WHERE purchase_id=purchase),'RESERVED');
    PERFORM pg_temp.check_result((SELECT count(*)::text FROM aether_marketplace.ac_wallet_ledger),'3');
    RAISE NOTICE 'PASS insufficient funds has no side effects';
END;
$test$;

SELECT pg_temp.check_result(has_function_privilege('public',
 'aether_marketplace.settle_authorized_ac_purchase_v4(uuid,text,uuid,uuid)','EXECUTE')::text,'false');
\echo PASS public execution denied
