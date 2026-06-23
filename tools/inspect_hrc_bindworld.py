#!/usr/bin/env python3
# Port of HrcLoader bind-world computation to check whether a mapobj's leaf-bone bind-world is identity
# (so the rigid-bind baking is a no-op) or carries translation/scale (so baking re-transforms an already
# world-baked mesh -> "flying"/"too large"/"broken"). Run from repo root.
import struct, sys, os, math

ROOT = "assets/sdox_offline/Extracted/SCENE/MAPOBJ"

def mat_id():
    return [[1.0 if r==c else 0.0 for c in range(4)] for r in range(4)]
def matmul(a,b):
    return [[sum(a[r][k]*b[k][c] for k in range(4)) for c in range(4)] for r in range(4)]
def transpose(m):
    return [[m[c][r] for c in range(4)] for r in range(4)]

def load_hrc(path):
    d=open(path,"rb").read()
    if d[:8]!=b"Hierachy": return None
    bc=struct.unpack_from("<i",d,12)[0]
    if bc<=0 or 16+bc*112>len(d): return None
    names=[];raw=[];child=[];sib=[]
    for i in range(bc):
        o=16+i*112
        R=[[struct.unpack_from("<f",d,o+(r*4+c)*4)[0] for c in range(4)] for r in range(4)]
        raw.append(R)
        child.append(struct.unpack_from("<i",d,o+64)[0])
        sib.append(struct.unpack_from("<i",d,o+68)[0])
        n=0
        while n<28 and d[o+84+n]!=0: n+=1
        names.append(d[o+84:o+84+n].decode("ascii","replace"))
    local=[transpose(R) for R in raw]   # row-major -> column-vector (Unity LocalRest)
    parent=[-1]*bc
    seen=[False]*bc
    # Visit(0, child[0]) — HRC own child/sibling links
    stack=[(0,child[0])]
    def visit(pi,ci):
        while 0<ci<bc and not seen[ci]:
            seen[ci]=True; parent[ci]=pi
            visit(ci,child[ci])
            ci=sib[ci]
    visit(0,child[0])
    bind=[None]*bc
    for i in range(bc):
        bind[i]= local[i] if parent[i]<0 else matmul(bind[parent[i]], local[i])
    # leaves = bones with no children
    haschild=[False]*bc
    for i in range(bc):
        p=parent[i]
        if 0<=p<bc: haschild[p]=True
    leaves=[i for i in range(bc) if not haschild[i]]
    return dict(bc=bc,names=names,parent=parent,bind=bind,local=local,leaves=leaves)

def scale_of(m):
    sx=math.sqrt(m[0][0]**2+m[1][0]**2+m[2][0]**2)
    sy=math.sqrt(m[0][1]**2+m[1][1]**2+m[2][1]**2)
    sz=math.sqrt(m[0][2]**2+m[1][2]**2+m[2][2]**2)
    return (sx,sy,sz)

def describe(m):
    tx,ty,tz=m[0][3],m[1][3],m[2][3]
    sx=math.sqrt(m[0][0]**2+m[1][0]**2+m[2][0]**2)
    sy=math.sqrt(m[0][1]**2+m[1][1]**2+m[2][1]**2)
    sz=math.sqrt(m[0][2]**2+m[1][2]**2+m[2][2]**2)
    ident = abs(tx)<1e-3 and abs(ty)<1e-3 and abs(tz)<1e-3 and all(abs(s-1)<1e-3 for s in (sx,sy,sz))
    return f"T=({tx:.1f},{ty:.1f},{tz:.1f}) S=({sx:.3f},{sy:.3f},{sz:.3f}){' [IDENTITY]' if ident else ''}"

def main():
    targets = sys.argv[1:] or [
        "SCN0011/.. (use folder names)",  # placeholder; pass explicit paths below
    ]
    # explicit prop list: (label, relpath to .hrc)
    props = [
        ("SCN0014 SEA_SCREEN(ok-anim)", "14_HAIDI/SEA_SCREEN/SEA_SCREEN.HRC"),
        ("SCN0014 GUANG(anim)",    "14_HAIDI/GUANG/GUANG.HRC"),
        ("SCN0016 DI1(anim)",      "16/DI/1/DI1.HRC"),
        ("SCN0016 JIGUANG1(anim)", "16/JIGUANG1/JIGUANG1.HRC"),
        ("SCN0011 DINGDENG(anim)", "DINGDENG/DINGDENG.HRC"),
        ("SCN0011 DING(anim)",     "DING/DING.HRC"),
        ("SCN0011 DENGGUANG(stat)","DENGGUANG/DENGGUANG.HRC"),
        ("SCN0011 SCREEN(stat)",   "SCREEN/SCREEN.HRC"),
        ("SCN0016 FANGZI1(stat)",  "16/FANGZI/1/FANGZI1.HRC"),
        ("SCN0016 SKY(anim)",      "16/SKY/SKY.HRC"),
        ("SCN0016 CHENGSHI(stat)", "16/FANGZI/CHENGSHI/CHENGSHI.HRC"),
        ("SCN0015 SHU1(anim)",     "15_DONGHUA/SHU1/15_SHU1.HRC"),
        ("SCN0015 HUA(anim)",      "15_DONGHUA/HUA/15_HUA.HRC"),
        ("SCN0019 ZHUAN(stat)",    "PK/ZHUAN/ZHUAN.HRC"),
        ("SCN0019 SHAN(stat)",     "PK/SHAN/SHAN.HRC"),
        ("SCN0014 SHANHU-BAI(ok)", "14_HAIDI/SHANHU-BAI/SHANHU-BAI.HRC"),
        ("SCN0009 GUATAN(ok)",     "GUATAN/GUATAN.HRC"),
        ("SCN0008 ZIMU",           "ZIMU/ZIMU.HRC"),
        ("SCN0008 JINZITA",        "JINZITA/JINZITA.HRC"),
    ]
    for label, rel in props:
        path=os.path.join(ROOT, rel)
        if not os.path.isfile(path):
            print(f"{label:28} MISSING {rel}"); continue
        h=load_hrc(path)
        if h is None:
            print(f"{label:28} not-hrc"); continue
        leafdesc=[]
        for li in h["leaves"][:4]:
            os_=scale_of(h["local"][li])   # bone's OWN local scale (the fix would apply this)
            leafdesc.append(f"{h['names'][li]} bindS={tuple(round(x,3) for x in scale_of(h['bind'][li]))} ownS={tuple(round(x,3) for x in os_)}")
        print(f"{label:28} bones={h['bc']:3} leaves={len(h['leaves'])} :: " + " | ".join(leafdesc))

if __name__=="__main__":
    main()
