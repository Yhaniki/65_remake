# -*- coding: utf-8 -*-
"""Spawn NXPatch.exe (with NXStart handshake) so its protector unpacks the code in
memory, then dump the main module's .text to disk for static analysis."""
import ctypes, ctypes.wintypes as wt, time, sys, struct
import frida

EXE = r'H:\sdo\Super Dance Online\NXPatch.exe'
CWD = r'H:\sdo\Super Dance Online'
Gs0=0x7AE373C3; Gs1=0x3BA8EB43
MAP='Local\\NXPatchV25Session'
STAMP='--nx=_ry8Sfdm40N9wv7tg2dI1eiodUU9WxWkduk0Ib-o'

def rotl(x,n): return ((x<<n)|(x>>(32-n)))&0xffffffff
def compute(nonce):
    num=Gs0
    for i in range(8):
        num^=ord(nonce[i]); num=(num*16777619)&0xffffffff; num=rotl(num,5)
    num^=Gs1; num=rotl(num,7)
    return num
nonce='DEADBEEF'
seal='--nxs='+nonce+('%08X'%compute(nonce))
print('seal',seal)

# --- create shared memory 128B and write seal+NUL (kept alive by this process) ---
k32=ctypes.windll.kernel32
INVALID=wt.HANDLE(-1).value
PAGE_RW=0x04; FILE_MAP_WRITE=0x0002
k32.CreateFileMappingW.restype=wt.HANDLE
k32.CreateFileMappingW.argtypes=[wt.HANDLE,ctypes.c_void_p,wt.DWORD,wt.DWORD,wt.DWORD,wt.LPCWSTR]
hMap=k32.CreateFileMappingW(INVALID,None,PAGE_RW,0,128,MAP)
print('CreateFileMapping',hex(hMap or 0),'err',k32.GetLastError())
k32.MapViewOfFile.restype=ctypes.c_void_p
k32.MapViewOfFile.argtypes=[wt.HANDLE,wt.DWORD,wt.DWORD,wt.DWORD,ctypes.c_size_t]
pView=k32.MapViewOfFile(hMap,FILE_MAP_WRITE,0,0,128)
buf=(seal+'\0').encode('ascii')
ctypes.memset(pView,0,128)
ctypes.memmove(pView,buf,min(len(buf),127))

# --- frida spawn with argv, resume, wait, dump ---
dev=frida.get_local_device()
pid=dev.spawn([EXE, STAMP, seal], cwd=CWD)
print('spawned pid',pid)
session=dev.attach(pid)
dev.resume(pid)
print('resumed; sleeping to let protector unpack...')
time.sleep(6.0)

JS=r'''
var m = Process.enumerateModules()[0];
var base = m.base;
var end = m.base.add(m.size);
send({tag:'mod', name:m.name, base:m.base.toString(), size:m.size});
var ranges = Process.enumerateRanges('r--');
var CH = 0x80000;
for (var i=0;i<ranges.length;i++){
  var r = ranges[i];
  // intersect with module
  var a = r.base; var b = r.base.add(r.size);
  if (b.compare(base)<=0 || a.compare(end)>=0) continue;
  if (a.compare(base)<0) a = base;
  if (b.compare(end)>0) b = end;
  var sz = b.sub(a).toInt32();
  var off = 0;
  while (off < sz){
    var n = Math.min(CH, sz-off);
    try {
      var buf = a.add(off).readByteArray(n);
      send({tag:'chunk', addr:a.add(off).sub(base).toInt32(), n:n, prot:r.protection}, buf);
    } catch(e){ send({tag:'chunkerr', addr:a.add(off).sub(base).toInt32(), msg:''+e}); }
    off += n;
  }
}
send({tag:'done'});
'''
IMGSIZE=0xe56000
script=session.create_script(JS)
out=bytearray(IMGSIZE)
covered=[]
modinfo={}
finished={'v':False}
def on_msg(msg,data):
    if msg['type']=='send':
        p=msg['payload']; t=p.get('tag')
        if t=='mod':
            modinfo.update(p); print('module',p['name'],'base',p['base'],'size',hex(p['size']))
        elif t=='chunk':
            o=p['addr']; n=p['n']
            if data and 0<=o<IMGSIZE: out[o:o+len(data)]=data; covered.append((o,len(data),p.get('prot')))
        elif t=='chunkerr':
            print('chunkerr',hex(p['addr']),p['msg'])
        elif t=='done':
            finished['v']=True
    elif msg['type']=='error':
        print('JS ERROR',msg.get('description'))
script.on('message',on_msg)
script.load()
t0=time.time()
while not finished['v'] and time.time()-t0<60: time.sleep(0.2)

open('nxp_img.bin','wb').write(out); print('covered regions',len(covered),'bytes',sum(c[1] for c in covered))
print('dumped .text bytes:',len(out),'base=',modinfo.get('base'))
try: dev.kill(pid)
except Exception as e: print('kill',e)
print('DONE')
