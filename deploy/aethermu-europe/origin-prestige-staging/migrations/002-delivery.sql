-- Additive staging migration. Run after 001, never on startup or on openmu.
-- Caller owns the transaction (psql --single-transaction or managed fixture transaction).
ALTER TABLE aether_prestige.purchases DROP CONSTRAINT IF EXISTS purchases_status_check;
ALTER TABLE aether_prestige.purchases ADD CONSTRAINT purchases_status_check CHECK(status IN ('pending','delivered','failed','refunded'));
CREATE TABLE IF NOT EXISTS aether_prestige.supply_windows (
 realm text NOT NULL REFERENCES aether_prestige.rules(realm),product_id text NOT NULL,
 period text NOT NULL CHECK(period IN ('day','week')),window_id bigint NOT NULL,
 reserved integer NOT NULL DEFAULT 0 CHECK(reserved>=0),
 PRIMARY KEY(realm,product_id,period,window_id)
);
REVOKE ALL ON aether_prestige.supply_windows FROM PUBLIC;
CREATE TABLE IF NOT EXISTS aether_prestige.delivery (
 purchase_id uuid PRIMARY KEY REFERENCES aether_prestige.purchases(id),
 realm text NOT NULL,character_id uuid NOT NULL,
 item_group integer NOT NULL CHECK(item_group=12),
 item_number integer NOT NULL CHECK(item_number=15),
 quantity integer NOT NULL CHECK(quantity=1),
 status text NOT NULL CHECK(status IN ('pending','delivered','refunded')),
 item_id uuid UNIQUE, created_at bigint NOT NULL,delivered_at bigint,
 CHECK((status='delivered' AND item_id IS NOT NULL AND delivered_at IS NOT NULL) OR (status<>'delivered' AND item_id IS NULL AND delivered_at IS NULL)),
 FOREIGN KEY(realm,character_id) REFERENCES aether_prestige.characters(realm,character_id)
);
CREATE OR REPLACE FUNCTION aether_prestige.delivery_terminal_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF OLD.status<>'pending' OR NEW.purchase_id<>OLD.purchase_id OR NEW.realm<>OLD.realm OR NEW.character_id<>OLD.character_id OR NEW.item_group<>OLD.item_group OR NEW.item_number<>OLD.item_number OR NEW.quantity<>OLD.quantity OR NEW.created_at<>OLD.created_at THEN
  RAISE EXCEPTION 'immutable delivery identity or terminal state';
 END IF;
 RETURN NEW;
END; $$;
DROP TRIGGER IF EXISTS terminal_delivery ON aether_prestige.delivery;
CREATE TRIGGER terminal_delivery BEFORE UPDATE ON aether_prestige.delivery FOR EACH ROW EXECUTE FUNCTION aether_prestige.delivery_terminal_guard();
REVOKE ALL ON aether_prestige.delivery FROM PUBLIC;
REVOKE ALL ON FUNCTION aether_prestige.delivery_terminal_guard() FROM PUBLIC;
