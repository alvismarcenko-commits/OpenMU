"""Pin the marked Origin candidate to its verified loopback ConnectServer."""
from pathlib import Path
import argparse
import hashlib
import json
import re

EXPECTED_MAIN='94191e71652c2e8809b8c042eed9a79ae52a1e5258b30ff553db53687188aec3'

def configure(client):
    manifest_path=client/'aether-client-manifest.json'
    manifest=json.loads(manifest_path.read_text(encoding='utf-8-sig'))
    if manifest.get('edition')!='Aether Client v2' or manifest.get('realm')!='origin':
        raise ValueError('Refusing an unmarked or non-Origin client')
    with (client/'Main.exe').open('rb') as stream:
        if hashlib.file_digest(stream,'sha256').hexdigest()!=EXPECTED_MAIN:
            raise ValueError('Unverified client executable')
    path=client/'config.ini'
    source=path.read_text(encoding='utf-8-sig')
    section=re.compile(r'(?im)(^\[CONNECTION SETTINGS\]\s*\n)([^\[]*)')
    matches=list(section.finditer(source))
    if len(matches)!=1:raise ValueError('Expected one connection settings section')
    block=matches[0].group(2)
    for key,value in [('ServerIP','127.0.0.1'),('ServerPort','44416')]:
        block,count=re.subn(r'(?im)^'+key+r'=[^\r\n]*',key+'='+value,block)
        if count!=1:raise ValueError('Expected one '+key+' setting')
    updated=source[:matches[0].start(2)]+block+source[matches[0].end(2):]
    path.write_text(updated,encoding='utf-8')
    manifest['connect_port']=44416
    manifest['files']['config.ini']=hashlib.sha256(path.read_bytes()).hexdigest()
    manifest_path.write_text(json.dumps(manifest,indent=2),encoding='utf-8')
    return {'realm':'origin','host':'127.0.0.1','port':44416,'changed':source!=updated}

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('client',type=Path)
    print(json.dumps(configure(parser.parse_args().client.resolve())))
