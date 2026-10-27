"""Check restored YYB native-hair counts against the pre-inference baseline."""
from pathlib import Path
import subprocess,json,re,shutil

root=Path(__file__).resolve().parents[1]
out=root/'build/yyb-hair-018';out.mkdir(exist_ok=True)
shutil.copy2(root/'build/compatibility-test/ModelCompatibilityProbe.exe',out)
game=Path('D:/Program Files/Steam/steamapps/common/Subnautica')
for name in ['UnityEngine.dll','UnityEngine.CoreModule.dll','UnityEngine.SharedInternalsModule.dll']:
    shutil.copy2(game/'Subnautica_Data/Managed'/name,out/name)
baseline=json.loads((root/'build/compat-016/verification.json').read_text(encoding='utf-8'))
results=[]
for number,prior in enumerate(baseline):
    path=Path(prior['path']);manifest=out/'current-path.txt';manifest.write_text(str(path),encoding='utf-8')
    run=subprocess.run([str(out/'ModelCompatibilityProbe.exe'),'@'+str(manifest),'--verify'],capture_output=True,timeout=30)
    text=run.stdout.decode('utf-8-sig',errors='replace')+run.stderr.decode('utf-8',errors='replace')
    (out/f'{number}-verify.log').write_text(text,encoding='utf-8')
    if run.returncode: raise RuntimeError(path.name+' failed: '+text[-500:])
    yyb='yyb' in str(path).lower()
    if yyb:
        before=next(x for x in prior['results'] if x.startswith('SECONDARY'))
        wanted=int(re.search(r'hair=(\d+)',before).group(1))
        actual=int(re.search(r'SECONDARY hair=(\d+)',text).group(1))
        if actual!=wanted: raise RuntimeError(f'{path.name}: YYB hair changed: {wanted} -> {actual}')
        print(path.parent.name,f'hair={actual}, matches 0.1.16',flush=True)
    results.append(dict(path=str(path),exit_code=run.returncode,yyb_baseline_matched=yyb))
(out/'verification.json').write_text(json.dumps(results,ensure_ascii=False,indent=2),encoding='utf-8')
print('MODELS',len(results),'YYB BASELINES',sum(x['yyb_baseline_matched'] for x in results),'FAILURES 0')
