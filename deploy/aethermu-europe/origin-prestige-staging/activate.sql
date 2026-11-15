-- Run after guard.sql and the pinned companion Prestige migrations, in ONE transaction.
DO $$
BEGIN
 IF EXISTS (SELECT 1 FROM aether_prestige.rules WHERE realm<>'origin' AND activated) THEN
  RAISE EXCEPTION 'Other realm activation needs separate review';
 END IF;
 IF NOT EXISTS (SELECT 1 FROM aether_prestige.rules WHERE realm='origin'
   AND resets_required=25 AND app_per_gr=100 AND revision>=2) THEN
  RAISE EXCEPTION 'Origin must exchange 25 resets for exactly 100 APP';
 END IF;
 IF EXISTS (SELECT 1 FROM config."AttributeDefinition" WHERE "Id"='c7f3a820-4d61-4b8e-9a32-72e5c9d104bf'
   AND ("Designation"<>'Grand Resets' OR "GameConfigurationId"<>(SELECT "Id" FROM config."GameConfiguration"))) THEN
  RAISE EXCEPTION 'Grand Reset attribute identity conflicts with existing configuration';
 END IF;
 IF EXISTS (SELECT 1 FROM data."StatAttribute" a
   JOIN aether_marketplace.native_account_binding b ON b.character_id=a."CharacterId" AND b.realm='Origin'
   WHERE a."DefinitionId"='c7f3a820-4d61-4b8e-9a32-72e5c9d104bf' AND a."Value"<>0) THEN
  RAISE EXCEPTION 'Existing legacy Grand Reset stat bonuses require migration review';
 END IF;
END $$;

-- Same definition and zero-valued class defaults as GrandResetAttributesPlugIn.
-- Existing values and existing class definitions are never overwritten.
INSERT INTO config."AttributeDefinition" ("Id","Designation","Description","MaximumValue","GameConfigurationId")
SELECT 'c7f3a820-4d61-4b8e-9a32-72e5c9d104bf','Grand Resets',
 'Grand reset quantity of current character.',NULL,"Id" FROM config."GameConfiguration"
ON CONFLICT ("Id") DO NOTHING;
INSERT INTO config."StatAttributeDefinition" ("Id","AttributeId","CharacterClassId","BaseValue","IncreasableByPlayer")
SELECT gen_random_uuid(),'c7f3a820-4d61-4b8e-9a32-72e5c9d104bf',c."Id",0,false
FROM config."CharacterClass" c WHERE NOT EXISTS (SELECT 1 FROM config."StatAttributeDefinition" s
 WHERE s."CharacterClassId"=c."Id" AND s."AttributeId"='c7f3a820-4d61-4b8e-9a32-72e5c9d104bf');

-- Read verified existing ownership; do not modify any Marketplace table.
INSERT INTO aether_prestige.characters
 (realm,character_id,website_owner,game_account,name,migration_state,observed_resets)
SELECT 'origin',c."Id",b.marketplace_account_id,b.openmu_account_id::text,c."Name",'approved',
 coalesce((SELECT a."Value"::integer FROM data."StatAttribute" a
  WHERE a."CharacterId"=c."Id" AND a."DefinitionId"='89a891a7-f9f9-4ab5-af36-12056e53a5f7'),0)
FROM aether_marketplace.native_account_binding b JOIN data."Character" c
 ON c."Id"=b.character_id AND c."AccountId"=b.openmu_account_id
WHERE b.realm='Origin'
ON CONFLICT (realm,character_id) DO NOTHING;
DO $$
BEGIN
 IF EXISTS (SELECT 1 FROM aether_marketplace.native_account_binding b
  JOIN aether_prestige.characters p ON p.realm='origin' AND p.character_id=b.character_id
  WHERE b.realm='Origin' AND (p.website_owner<>b.marketplace_account_id
   OR p.game_account<>b.openmu_account_id::text OR p.migration_state<>'approved')) THEN
  RAISE EXCEPTION 'Conflicting or unapproved existing Prestige ownership';
 END IF;
END $$;

UPDATE aether_prestige.rules SET activated=true WHERE realm='origin';
UPDATE config."PlugInConfiguration" SET "CustomConfiguration"=("CustomConfiguration"::jsonb ||
 jsonb_build_object('PrestigeEnabled',true,'GrandResetEnabled',true,'PrestigeRealm','origin',
 'PrestigeResetsRequired',25,'LevelAfterGrandReset',1,'PrestigeDeliveryEnabled',false))::text
WHERE "TypeId"='6a9d585d-79d7-4674-b6ea-7e87392fa501';
UPDATE config."PlugInConfiguration" SET "IsActive"=true WHERE "TypeId" IN (
 '6a9d585d-79d7-4674-b6ea-7e87392fa501','90b35404-aade-4f22-b5d2-4cd59b8bb4c8',
 'f94d795c-f67b-48ad-8112-ea45da39d10a','79f2c2c2-2e4c-4f4b-8a74-4227d1209d27');
