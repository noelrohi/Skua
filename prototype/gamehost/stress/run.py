import subprocess,struct,threading,time,sys
H="/Users/rohi/sandbox/skua-proto/prototype/gamehost/target/release/skua-gamehost"
p=subprocess.Popen([H]+sys.argv[2:]+[sys.argv[1]],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.DEVNULL)
last={'f':''}; lost={'v':False}
def reader():
    while True:
        h=p.stdout.read(4)
        if len(h)<4: return
        b=p.stdout.read(struct.unpack('<I',h)[0])
        if b[:1]==b'F': last['f']=b[1:].decode()
        if b[:1]==b'L' and (b'device lost' in b or b'panic' in b or b'outstanding' in b): lost['v']=True; print('  ', b[2:200].decode(errors='replace'))
threading.Thread(target=reader,daemon=True).start()
t=time.time()
while time.time()-t<60 and not lost['v'] and p.poll() is None: time.sleep(1)
print(sys.argv[2:] or ['(default)'], 'after %.0fs'%(time.time()-t), 'last trace:', last['f'], 'DEVICE LOST' if lost['v'] else 'ok', 'alive' if p.poll() is None else 'exited')
p.stdin.close(); p.wait()
