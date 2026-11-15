-- Explicit operator activation for a nonce-owned, single-configuration x1 fixture.
DO $$
BEGIN
 IF current_database() !~ '^aether_playable_ascension_[0-9a-f]{32}$'
 OR NOT EXISTS (SELECT 1 FROM pg_database WHERE datname=current_database()
   AND shobj_description(oid,'pg_database')='aether-ascension:'||right(current_database(),32)) THEN
  RAISE EXCEPTION 'Refusing a database without the isolated staging ownership marker';
 END IF;
 IF (SELECT count(*) FROM config."GameConfiguration")<>1
 OR EXISTS (SELECT 1 FROM config."GameConfiguration" WHERE "ExperienceRate"<>1 OR "MasterExperienceRate"<>1)
 OR EXISTS (SELECT 1 FROM config."GameServerDefinition" WHERE "ExperienceRate"<>1) THEN
  RAISE EXCEPTION 'Origin x1 activation requires one verified x1 configuration';
 END IF;
 IF (SELECT count(*) FROM config."PlugInConfiguration" WHERE "TypeId" IN (
   '6a9d585d-79d7-4674-b6ea-7e87392fa501','90b35404-aade-4f22-b5d2-4cd59b8bb4c8',
   'f94d795c-f67b-48ad-8112-ea45da39d10a','79f2c2c2-2e4c-4f4b-8a74-4227d1209d27'))<>4 THEN
  RAISE EXCEPTION 'Required registered Reset/Grand Reset/ResetInfo plugin configuration is missing';
 END IF;
 IF NOT EXISTS (SELECT 1 FROM config."PlugInConfiguration"
  WHERE "TypeId"='6a9d585d-79d7-4674-b6ea-7e87392fa501'
   AND "CustomConfiguration"::jsonb->>'ResetStats'='true'
   AND "CustomConfiguration"::jsonb->>'ReplacePointsPerReset'='true') THEN
  RAISE EXCEPTION 'Compatible explicit Reset configuration is required';
 END IF;
END $$;
