import subprocess,struct,threading,time,sys
H=sys.argv[1]; SWF=sys.argv[2]
p=subprocess.Popen([H,SWF],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.DEVNULL)
def reader():
    while p.stdout.read(65536): pass
threading.Thread(target=reader,daemon=True).start()
def fp():
    o=subprocess.run(['footprint',str(p.pid)],capture_output=True,text=True).stdout
    return o.split('Footprint:')[1].split('(')[0].strip() if 'Footprint:' in o else '?'
out=[]
for t in (10,30,60,90):
    time.sleep(t-(out[-1][0] if out else 0)); out.append((t,fp()))
print(sys.argv[3], ' '.join(f'{t}s={v}' for t,v in out))
p.stdin.close(); p.wait()
