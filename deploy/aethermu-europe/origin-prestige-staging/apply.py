"""Apply the explicit Origin activation to a stopped, backed-up isolated fixture."""
import argparse
from pathlib import Path
import subprocess

HERE=Path(__file__).resolve().parent
def activation_sql():
    paths=[HERE/'guard.sql',*(HERE/'migrations'/name for name in
           ('001-prestige.sql','002-delivery.sql','003-gr-app-100.sql')),HERE/'activate.sql']
    return '\n'.join(p.read_text(encoding='utf-8-sig') for p in paths)

def apply(container,database,user):
    return subprocess.run(['docker','exec','-i',container,'psql','-X','--single-transaction',
                           '-v','ON_ERROR_STOP=1','-U',user,'-d',database],
                          input=activation_sql(),text=True,capture_output=True)

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--container',required=True)
    parser.add_argument('--database',required=True)
    parser.add_argument('--user',required=True)
    args=parser.parse_args()
    result=apply(args.container,args.database,args.user)
    print(result.stdout,end='');print(result.stderr,end='')
    raise SystemExit(result.returncode)
