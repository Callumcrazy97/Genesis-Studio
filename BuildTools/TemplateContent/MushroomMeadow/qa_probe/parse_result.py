import json,sys
t=open(sys.argv[1],encoding='utf-8-sig').read()
i=t.index('{')
d=json.loads(t[i:])
print(d['message'])
dd=d['data']
print([k for k in dd.keys()])
for c in dd['checks']:
    if not c['passed']: print("FAIL",c['name'],'|',c['detail'])
if len(sys.argv)>2:
    for c in dd['checks']:
        if c['passed'] and any(x in c['name'] for x in sys.argv[2].split(',')): print("ok  ",c['name'],'|',c['detail'])
for k in dd:
    if k not in ('checks',): 
        v=dd[k]
        s=json.dumps(v)
        print(k, s[:1500])
