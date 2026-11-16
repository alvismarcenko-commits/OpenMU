"""Real PostgreSQL activation tests against a fresh clone, never the source DB."""
from pathlib import Path
import importlib.util
import json
import os
import subprocess
import unittest
import uuid

HERE=Path(__file__).resolve().parent
ACTIVATION=HERE.parent.parent/'deploy/aethermu-europe/origin-prestige-staging/apply.py'
spec=importlib.util.spec_from_file_location('origin_activation',ACTIVATION)
activation=importlib.util.module_from_spec(spec);spec.loader.exec_module(activation)

class OriginActivationTests(unittest.TestCase):
    @classmethod
    def sql(cls,sql,database=None,required=True):
        p=subprocess.run(['docker','exec','-i',cls.container,'psql','-X','-qAt','-v','ON_ERROR_STOP=1',
                          '-U','aether_client','-d',database or cls.database],input=sql,text=True,capture_output=True)
        if required and p.returncode:raise AssertionError(p.stderr)
        return p

    @classmethod
    def fingerprint(cls):
        names=[('data',x) for x in ('Character','StatAttribute','Item','ItemStorage')]
        names += [('aether_marketplace',x) for x in cls.sql("SELECT tablename FROM pg_tables WHERE schemaname='aether_marketplace' ORDER BY tablename").stdout.splitlines()]
        return {s+'.'+t:cls.sql(f'''SELECT md5(coalesce(string_agg(to_jsonb(x)::text,E'\\n' ORDER BY to_jsonb(x)::text),'')) FROM "{s}"."{t}" x''').stdout.strip() for s,t in names}

    @classmethod
    def setUpClass(cls):
        cls.container=os.environ['AETHER_ORIGIN_TEST_CONTAINER']
        source=os.environ['AETHER_ORIGIN_SOURCE_DB']
        token=uuid.uuid4().hex
        cls.database='aether_playable_ascension_'+token
        p=subprocess.run(['docker','exec',cls.container,'createdb','-U','aether_client','-T',source,cls.database],capture_output=True,text=True)
        if p.returncode:raise AssertionError(p.stderr)
        cls.sql(f"COMMENT ON DATABASE {cls.database} IS 'aether-ascension:{token}'")
        if cls.sql("SELECT count(*) FROM information_schema.schemata WHERE schema_name='aether_prestige'").stdout.strip()!='0':
            raise AssertionError('Use the preserved pre-activation source fixture')
        cls.before=cls.fingerprint()
        p=activation.apply(cls.container,cls.database,'aether_client')
        if p.returncode:raise AssertionError(p.stderr)
        print('Isolated activation test database:',cls.database,flush=True)

    def test_configuration_and_registered_plugin_ids(self):
        result=json.loads(self.sql('''SELECT jsonb_build_object('active',bool_and("IsActive"),'count',count(*))
        FROM config."PlugInConfiguration" WHERE "TypeId" IN ('6a9d585d-79d7-4674-b6ea-7e87392fa501',
        '90b35404-aade-4f22-b5d2-4cd59b8bb4c8','f94d795c-f67b-48ad-8112-ea45da39d10a','79f2c2c2-2e4c-4f4b-8a74-4227d1209d27')''').stdout)
        self.assertEqual(result,{'active':True,'count':4})
        config=json.loads(self.sql('''SELECT "CustomConfiguration" FROM config."PlugInConfiguration"
        WHERE "TypeId"='6a9d585d-79d7-4674-b6ea-7e87392fa501' ''').stdout)
        for key,value in {'PrestigeEnabled':True,'GrandResetEnabled':True,'PrestigeRealm':'origin',
                          'PrestigeResetsRequired':25,'LevelAfterGrandReset':1,'RequiredLevel':400,
                          'RequiredMoney':1,'LevelAfterReset':10,'LogOut':True}.items():self.assertEqual(config[key],value)
        self.assertEqual(self.sql("SELECT resets_required||':'||app_per_gr||':'||activated FROM aether_prestige.rules WHERE realm='origin'").stdout.strip(),'25:100:true')

    def test_inventory_marketplace_and_progress_unchanged(self):
        self.assertEqual(self.fingerprint(),self.before)

    def test_class_counters_and_verified_ownership_only(self):
        self.assertEqual(self.sql('''SELECT count(*) FROM config."CharacterClass" c WHERE NOT EXISTS
        (SELECT 1 FROM config."StatAttributeDefinition" s WHERE s."CharacterClassId"=c."Id"
        AND s."AttributeId"='c7f3a820-4d61-4b8e-9a32-72e5c9d104bf' AND s."BaseValue"=0 AND NOT s."IncreasableByPlayer")''').stdout.strip(),'0')
        self.assertEqual(self.sql("SELECT count(*) FROM aether_prestige.rules WHERE realm<>'origin' AND activated").stdout.strip(),'0')
        self.assertEqual(self.sql('''SELECT count(*) FROM aether_prestige.characters p WHERE NOT EXISTS
        (SELECT 1 FROM aether_marketplace.native_account_binding b WHERE b.realm='Origin'
        AND b.character_id=p.character_id AND b.marketplace_account_id=p.website_owner
        AND b.openmu_account_id::text=p.game_account)''').stdout.strip(),'0')

    def test_reapplication_preserves_ids_and_balances(self):
        query='''SELECT jsonb_build_object('profiles',(SELECT jsonb_agg(to_jsonb(p) ORDER BY character_id)
        FROM aether_prestige.characters p),'class_defaults',(SELECT jsonb_agg(to_jsonb(s) ORDER BY "Id")
        FROM config."StatAttributeDefinition" s WHERE "AttributeId"='c7f3a820-4d61-4b8e-9a32-72e5c9d104bf'))'''
        before=self.sql(query).stdout
        p=activation.apply(self.container,self.database,'aether_client');self.assertEqual(p.returncode,0,p.stderr)
        self.assertEqual(self.sql(query).stdout,before)

    def test_sql_24_rejected_25_awards100_replay_once(self):
        character=self.sql("SELECT character_id FROM aether_prestige.characters ORDER BY character_id LIMIT 1").stdout.strip()
        operation=str(uuid.uuid4())
        call=lambda resets:f"aether_prestige.complete_gr('origin','{character}','{operation}',0,{resets},'native-test')"
        rejected=self.sql('BEGIN;SELECT '+call(24)+';ROLLBACK;',required=False)
        self.assertNotEqual(rejected.returncode,0)
        accepted=self.sql('BEGIN;SELECT '+call(25)+';SELECT '+call(25)+f";SELECT jsonb_build_object('balance',balance::text,'gr',gr_count::text) FROM aether_prestige.characters WHERE realm='origin' AND character_id='{character}';ROLLBACK;")
        rows=[json.loads(x) for x in accepted.stdout.splitlines()]
        self.assertEqual(rows[0],rows[1]);self.assertEqual(rows[0]['app_granted'],'100')
        self.assertEqual(rows[0]['level'],1);self.assertEqual(rows[0]['resets'],0)
        self.assertEqual(rows[2],{'balance':'100','gr':'1'})
        self.assertEqual(self.sql('SELECT sum(balance)::text FROM aether_prestige.characters').stdout.strip(),'0')

    def test_unowned_database_refused_before_schema_write(self):
        name='aether_origin_unowned_test_'+uuid.uuid4().hex
        p=subprocess.run(['docker','exec',self.container,'createdb','-U','aether_client',name],capture_output=True,text=True)
        self.assertEqual(p.returncode,0,p.stderr)
        rejected=activation.apply(self.container,name,'aether_client')
        self.assertNotEqual(rejected.returncode,0)
        self.assertEqual(self.sql("SELECT count(*) FROM pg_tables WHERE schemaname NOT IN ('pg_catalog','information_schema')",database=name).stdout.strip(),'0')

if __name__=='__main__':unittest.main(verbosity=2)
