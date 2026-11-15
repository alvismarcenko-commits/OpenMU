-- Additive schema only. Never executed by Website startup. Run on an isolated staging DB.
CREATE SCHEMA IF NOT EXISTS aether_prestige;
CREATE TABLE IF NOT EXISTS aether_prestige.rules (
 realm text PRIMARY KEY CHECK(realm IN ('origin','legacy','evolution','ascension')),
 resets_required integer NOT NULL CHECK(resets_required>0),
 app_per_gr integer NOT NULL CHECK(app_per_gr>0),
 activated boolean NOT NULL DEFAULT false,
 revision integer NOT NULL DEFAULT 1 CHECK(revision>0)
);
INSERT INTO aether_prestige.rules(realm,resets_required,app_per_gr) VALUES
 ('origin',25,100),('legacy',50,50),('evolution',75,20),('ascension',100,10) ON CONFLICT DO NOTHING;
CREATE TABLE IF NOT EXISTS aether_prestige.characters (
 realm text NOT NULL REFERENCES aether_prestige.rules(realm),
 character_id uuid NOT NULL,
 website_owner bigint NOT NULL CHECK(website_owner>0),
 game_account text NOT NULL,
 name text NOT NULL,
 gr_count numeric NOT NULL DEFAULT 0 CHECK(gr_count>=0 AND gr_count=trunc(gr_count) AND gr_count NOT IN ('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric)),
 balance numeric NOT NULL DEFAULT 0 CHECK(balance>=0 AND balance=trunc(balance) AND balance NOT IN ('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric)),
 revision bigint NOT NULL DEFAULT 0 CHECK(revision>=0),
 observed_resets integer NOT NULL DEFAULT 0 CHECK(observed_resets>=0),
 migration_state text NOT NULL DEFAULT 'quarantined' CHECK(migration_state IN ('quarantined','approved')),
 PRIMARY KEY(realm,character_id)
);
CREATE TABLE IF NOT EXISTS aether_prestige.receipts (
 realm text NOT NULL,character_id uuid NOT NULL,operation_id text NOT NULL,
 request_hash text NOT NULL,response jsonb NOT NULL,
 PRIMARY KEY(realm,character_id,operation_id),
 FOREIGN KEY(realm,character_id) REFERENCES aether_prestige.characters(realm,character_id)
);
CREATE TABLE IF NOT EXISTS aether_prestige.ledger (
 id uuid PRIMARY KEY,realm text NOT NULL,character_id uuid NOT NULL,
 operation_id text NOT NULL,kind text NOT NULL CHECK(kind IN ('gr','purchase','refund')),
 delta numeric NOT NULL CHECK(delta<>0 AND delta=trunc(delta) AND delta NOT IN ('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric)),
 reference text NOT NULL,created_at bigint NOT NULL,
 UNIQUE(realm,character_id,operation_id),
 FOREIGN KEY(realm,character_id) REFERENCES aether_prestige.characters(realm,character_id)
);
CREATE TABLE IF NOT EXISTS aether_prestige.purchases (
 id uuid PRIMARY KEY,realm text NOT NULL,character_id uuid NOT NULL,product_id text NOT NULL,
 price numeric NOT NULL CHECK(price>0 AND price=trunc(price)),quantity integer NOT NULL CHECK(quantity>0),
 catalog_revision text NOT NULL,status text NOT NULL CHECK(status IN ('delivered','refunded')),
 created_at bigint NOT NULL,
 FOREIGN KEY(realm,character_id) REFERENCES aether_prestige.characters(realm,character_id)
);
CREATE TABLE IF NOT EXISTS aether_prestige.entitlements (
 purchase_id uuid PRIMARY KEY REFERENCES aether_prestige.purchases(id),
 realm text NOT NULL,character_id uuid NOT NULL,kind text NOT NULL,value text NOT NULL,
 FOREIGN KEY(realm,character_id) REFERENCES aether_prestige.characters(realm,character_id)
);
CREATE TABLE IF NOT EXISTS aether_prestige.audit (
 id uuid PRIMARY KEY,actor text NOT NULL,action text NOT NULL,realm text NOT NULL,
 character_id uuid NOT NULL,reference text NOT NULL,created_at bigint NOT NULL
);
CREATE OR REPLACE FUNCTION aether_prestige.immutable() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN RAISE EXCEPTION 'append-only prestige history' USING ERRCODE='55000'; END; $$;
DROP TRIGGER IF EXISTS immutable_ledger ON aether_prestige.ledger;
CREATE TRIGGER immutable_ledger BEFORE UPDATE OR DELETE ON aether_prestige.ledger FOR EACH ROW EXECUTE FUNCTION aether_prestige.immutable();
DROP TRIGGER IF EXISTS immutable_receipts ON aether_prestige.receipts;
CREATE TRIGGER immutable_receipts BEFORE UPDATE OR DELETE ON aether_prestige.receipts FOR EACH ROW EXECUTE FUNCTION aether_prestige.immutable();
DROP TRIGGER IF EXISTS immutable_audit ON aether_prestige.audit;
CREATE TRIGGER immutable_audit BEFORE UPDATE OR DELETE ON aether_prestige.audit FOR EACH ROW EXECUTE FUNCTION aether_prestige.immutable();
-- Native context calls this in the SAME DbTransaction as EF character changes.
-- Caller must verify persisted native originals under its native character lock first.
CREATE OR REPLACE FUNCTION aether_prestige.complete_gr(
 p_realm text,p_character uuid,p_operation text,p_revision bigint,p_resets integer,p_hash text)
 RETURNS jsonb LANGUAGE plpgsql AS $$
DECLARE c aether_prestige.characters; r aether_prestige.rules; old aether_prestige.receipts; result jsonb;
BEGIN
 IF length(p_operation)<16 OR length(p_operation)>80 THEN RAISE EXCEPTION 'invalid operation'; END IF;
 SELECT * INTO STRICT c FROM aether_prestige.characters WHERE realm=p_realm AND character_id=p_character FOR UPDATE;
 SELECT * INTO old FROM aether_prestige.receipts WHERE realm=p_realm AND character_id=p_character AND operation_id=p_operation;
 IF FOUND THEN
  IF old.request_hash<>p_hash THEN RAISE EXCEPTION 'operation conflict' USING ERRCODE='40001'; END IF;
  RETURN old.response;
 END IF;
 SELECT * INTO STRICT r FROM aether_prestige.rules WHERE realm=p_realm FOR SHARE;
 IF NOT r.activated OR c.migration_state<>'approved' THEN RAISE EXCEPTION 'prestige disabled or migration unapproved' USING ERRCODE='55000'; END IF;
 IF c.revision<>p_revision OR p_resets<r.resets_required THEN RAISE EXCEPTION 'stale revision or insufficient resets' USING ERRCODE='40001'; END IF;
 UPDATE aether_prestige.characters SET gr_count=gr_count+1,balance=balance+r.app_per_gr,revision=revision+1,observed_resets=0 WHERE realm=p_realm AND character_id=p_character RETURNING * INTO c;
 result=jsonb_build_object('gr_count',c.gr_count::text,'balance_app',c.balance::text,'revision',c.revision::text,'level',1,'resets',0,'stat_bonus',0,'app_granted',r.app_per_gr::text);
 INSERT INTO aether_prestige.ledger VALUES(gen_random_uuid(),p_realm,p_character,p_operation,'gr',r.app_per_gr,p_operation,extract(epoch from now())::bigint);
 INSERT INTO aether_prestige.receipts VALUES(p_realm,p_character,p_operation,p_hash,result);
 INSERT INTO aether_prestige.audit VALUES(gen_random_uuid(),'native','gr',p_realm,p_character,p_operation,extract(epoch from now())::bigint);
 RETURN result;
END; $$;
REVOKE ALL ON SCHEMA aether_prestige FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA aether_prestige FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA aether_prestige FROM PUBLIC;
