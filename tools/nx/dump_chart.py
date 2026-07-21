# -*- coding: utf-8 -*-
"""
Capture the DECRYPTED chart from a running NXPatch.exe.

HOW TO USE (needs the real private server, so the game reaches gameplay):
  1. Launch the game normally (NXPatchLauncher / your usual way) and log in.
  2. Run:  python dump_chart.py
  3. In-game, enter the song you want (e.g. sdom2818 "3" by Laur) and start playing.
  4. This prints the decrypt seed and writes the plaintext note stream to
     dumped_<name>.bin, then decodes frame_type / note_type usage so the NEW
     bomb / scroll types stand out vs the classic set {1,2,3,4,5,9,10}.

RE facts (NXPatch.exe, ImageBase 0x400000, verified from memory dump):
  0xb1c950  decrypt(seed, start, end):  seed*=0x3d09; *p -= (seed>>16)
  0xd6e9b0  container loader (filename@this+0xe0, blob@fileoff 0x2F4, seed2 from ddrm hdr [ctx+0x90])
"""
import os, sys, struct, time, frida
from collections import Counter

# dumps land right next to this script
OUTDIR = os.path.dirname(os.path.abspath(__file__))

JS = r'''
var DEC = ptr('0xb1c950');   // decrypt(seed, start, end)  __cdecl-ish, args on stack
Interceptor.attach(DEC, {
  onEnter: function () {
    var sp = this.context.esp;
    this.seed  = sp.add(4).readU32();
    this.start = sp.add(8).readPointer();
    this.end   = sp.add(0xc).readPointer();
    this.len   = this.end.sub(this.start).toInt32();
  },
  onLeave: function () {
    if (this.len > 4096) {   // the chart blob (skip the tiny 0x34 seed-block decrypts)
      try {
        var buf = this.start.readByteArray(this.len);
        send({t:'blob', seed:this.seed>>>0, len:this.len}, buf);
      } catch (e) { send({t:'log', m:'read fail '+e}); }
    } else {
      send({t:'log', m:'small decrypt seed=0x'+(this.seed>>>0).toString(16)+' len='+this.len});
    }
  }
});
send({t:'log', m:'decrypt hook installed on 0xb1c950'});
'''

def decode_frames(body):
    """body = decrypted note stream (StepFile offset 300 onward). Enumerate types."""
    def u32(o): return struct.unpack_from('<I', body, o)[0]
    def i16(o): return struct.unpack_from('<h', body, o)[0]
    def u16(o): return struct.unpack_from('<H', body, o)[0]
    ft=Counter(); nt=Counter(); slotbits=Counter()
    off=0; n=len(body); frames=0
    while off+8<=n and frames<200000:
        meas=u32(off); f=i16(off+4); iv=u16(off+6); off+=8
        ft[f]+=1; frames+=1
        if iv>20000 or off+iv*4>n: break
        for i in range(iv):
            v=u32(off+4*i)
            if f in (2,3,4,5,6,7,8):
                if (v & 0xffff)!=0:
                    nt[body[off+4*i+3]]+=1
                    slotbits[v & 0xff000000]+=1
        off+=iv*4
    return ft, nt, slotbits, frames

blobs=[]
def on_msg(m,d):
    if m['type']=='send':
        p=m['payload']
        if p.get('t')=='log': print('[H]',p['m'])
        elif p.get('t')=='blob' and d:
            fn=f'{OUTDIR}/dumped_seed{p["seed"]:08x}_len{p["len"]}.bin'
            open(fn,'wb').write(d)
            print(f'\n>>> DECRYPTED BLOB seed=0x{p["seed"]:08x} len={p["len"]} -> {fn}')
            ft,nt,sb,fr=decode_frames(d)
            print('    frames:',fr)
            print('    frame_type histogram:',dict(sorted(ft.items())))
            print('    note_type  histogram:',dict(sorted(nt.items())))
            print('    slot high-byte(attr) :',{hex(k):v for k,v in sorted(sb.items())})
            print('    NEW frame_types (not in classic {1,2,3,4,5,9,10}):',
                  sorted(set(ft)-{0,1,2,3,4,5,9,10,11}))
            print('    NEW note_types  (not in classic {0,2,3}):',
                  sorted(set(nt)-{0,2,3}))
    elif m['type']=='error': print('[JS-ERR]',m.get('description'))

def main():
    dev=frida.get_local_device()
    try:
        session=dev.attach('NXPatch.exe')
    except Exception as e:
        print('Could not attach to NXPatch.exe — is it running & logged in?', e); return
    s=session.create_script(JS); s.on('message',on_msg); s.load()
    print('Attached. Now enter & play the target song in-game. Ctrl-C to stop.')
    try:
        while True: time.sleep(1)
    except KeyboardInterrupt:
        print('stopped')

if __name__=='__main__':
    main()
