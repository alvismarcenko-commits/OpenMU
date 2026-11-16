-- GR-APP-100-v1. Explicit fresh Prestige schema upgrade only; never Website startup.
-- Keep 001/002 immutable. Existing balances/history need a separately reviewed migration.
DO $$
BEGIN
 IF EXISTS (SELECT 1 FROM aether_prestige.rules WHERE revision<2 OR app_per_gr<>100
     OR resets_required<>(CASE realm WHEN 'origin' THEN 25 WHEN 'legacy' THEN 50 WHEN 'evolution' THEN 75 WHEN 'ascension' THEN 100 END)) THEN
  IF EXISTS (SELECT 1 FROM aether_prestige.characters)
     OR EXISTS (SELECT 1 FROM aether_prestige.receipts)
     OR EXISTS (SELECT 1 FROM aether_prestige.ledger)
     OR EXISTS (SELECT 1 FROM aether_prestige.purchases)
     OR EXISTS (SELECT 1 FROM aether_prestige.audit)
     OR EXISTS (SELECT 1 FROM aether_prestige.rules WHERE activated) THEN
   RAISE EXCEPTION 'Existing Prestige data or activation: read-only migration analysis required; no automatic conversion' USING ERRCODE='55000';
  END IF;
  UPDATE aether_prestige.rules SET app_per_gr=100,
   resets_required=CASE realm WHEN 'origin' THEN 25 WHEN 'legacy' THEN 50 WHEN 'evolution' THEN 75 WHEN 'ascension' THEN 100 END,
   revision=2, activated=false;
 END IF;
 IF (SELECT count(*) FROM aether_prestige.rules)<>4 THEN
  RAISE EXCEPTION 'Four realm rules required';
 END IF;
END; $$;
-- Native context calls this in the SAME DbTransaction as EF character changes.
-- Caller must verify persisted native originals under its native character lock first.
CREATE OR REPLACE FUNCTION aether_prestige.complete_gr(
 p_realm text,p_character uuid,p_operation text,p_revision bigint,p_resets integer,p_hash text)
 RETURNS jsonb LANGUAGE plpgsql AS $$
DECLARE c aether_prestige.characters; r aether_prestige.rules; old aether_prestige.receipts; result jsonb;
BEGIN
 IF p_hash IS NULL OR p_operation IS NULL OR p_revision IS NULL OR p_resets IS NULL THEN RAISE EXCEPTION 'null input'; END IF;
 IF length(p_operation)<16 OR length(p_operation)>80 THEN RAISE EXCEPTION 'invalid operation'; END IF;
 SELECT * INTO STRICT c FROM aether_prestige.characters WHERE realm=p_realm AND character_id=p_character FOR UPDATE;
 SELECT * INTO old FROM aether_prestige.receipts WHERE realm=p_realm AND character_id=p_character AND operation_id=p_operation;
 IF FOUND THEN
  IF old.request_hash<>p_hash THEN RAISE EXCEPTION 'operation conflict' USING ERRCODE='40001'; END IF;
  RETURN old.response;
 END IF;
 SELECT * INTO STRICT r FROM aether_prestige.rules WHERE realm=p_realm FOR SHARE;
 IF NOT r.activated OR c.migration_state<>'approved' THEN RAISE EXCEPTION 'prestige disabled or migration unapproved' USING ERRCODE='55000'; END IF;
 IF r.revision<2 OR r.app_per_gr<>100 OR r.resets_required<>(CASE p_realm WHEN 'origin' THEN 25 WHEN 'legacy' THEN 50 WHEN 'evolution' THEN 75 WHEN 'ascension' THEN 100 END) THEN
  RAISE EXCEPTION 'GR ruleset mismatch; migration review required' USING ERRCODE='55000';
 END IF;
 IF p_revision IS NULL OR p_resets IS NULL OR p_hash IS NULL OR p_operation IS NULL OR c.revision<>p_revision OR p_resets<r.resets_required THEN RAISE EXCEPTION 'stale revision or insufficient resets' USING ERRCODE='40001'; END IF;
 UPDATE aether_prestige.characters SET gr_count=gr_count+1,balance=balance+r.app_per_gr,revision=revision+1,observed_resets=0 WHERE realm=p_realm AND character_id=p_character RETURNING * INTO c;
 result=jsonb_build_object('gr_count',c.gr_count::text,'balance_app',c.balance::text,'revision',c.revision::text,'level',1,'resets',0,'stat_bonus',0,'app_granted',r.app_per_gr::text);
 INSERT INTO aether_prestige.ledger VALUES(gen_random_uuid(),p_realm,p_character,p_operation,'gr',r.app_per_gr,p_operation,extract(epoch from now())::bigint);
 INSERT INTO aether_prestige.receipts VALUES(p_realm,p_character,p_operation,p_hash,result);
 INSERT INTO aether_prestige.audit VALUES(gen_random_uuid(),'native','gr',p_realm,p_character,p_operation,extract(epoch from now())::bigint);
 RETURN result;
END; $$;
REVOKE ALL ON FUNCTION aether_prestige.complete_gr(text,uuid,text,bigint,integer,text) FROM PUBLIC;
