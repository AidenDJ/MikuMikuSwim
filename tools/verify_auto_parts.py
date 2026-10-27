"""Exercise real PMX imports with intact, hidden bone, and hidden part labels."""
from pathlib import Path
import subprocess,json,shutil

root=Path(__file__).resolve().parents[1]
game=Path('D:/Program Files/Steam/steamapps/common/Subnautica')
out=root/'build/auto-parts-017';out.mkdir(exist_ok=True)
shutil.copy2(root/'build/compatibility-test/ModelCompatibilityProbe.exe',out)
for name in ['UnityEngine.dll','UnityEngine.CoreModule.dll','UnityEngine.SharedInternalsModule.dll']:
    shutil.copy2(game/'Subnautica_Data/Managed'/name,out/name)
rows=[]
for number,path in enumerate((game/'BepInEx/plugins/PlayerModelReplacer/Models').rglob('*.pmx')):
    manifest=out/'current-path.txt';manifest.write_text(str(path),encoding='utf-8')
    for mode in ['--verify','--anonymous','--structure-only']:
        result=subprocess.run([str(out/'ModelCompatibilityProbe.exe'),'@'+str(manifest),mode],capture_output=True,timeout=30)
        text=result.stdout.decode('utf-8-sig',errors='replace')+result.stderr.decode('utf-8',errors='replace')
        (out/f'{number}-{mode[2:]}.log').write_text(text,encoding='utf-8')
        concise=[line for line in text.splitlines() if line.startswith(('PHYSICS','ANONYMOUS','SECONDARY','VALID_LONG'))]
        rows.append(dict(path=str(path),mode=mode,exit_code=result.returncode,results=concise))
        if result.returncode or mode=='--anonymous': print(path.parent.name,mode,result.returncode,'; '.join(concise),flush=True)
(out/'verification.json').write_text(json.dumps(rows,ensure_ascii=False,indent=2),encoding='utf-8')
print('CHECKS',len(rows),'FAILURES',sum(r['exit_code']!=0 for r in rows),flush=True)
raise SystemExit(any(r['exit_code'] for r in rows))
