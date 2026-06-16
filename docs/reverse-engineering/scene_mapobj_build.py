# -*- coding: utf-8 -*-
import json

dengA=[[-196.852,87.769,117.997],[-194.231,89.174,123.617],[-191.667,90.309,129.116],[-189.012,91.657,134.809],[-186.277,93.129,140.673],[-183.742,94.399,146.111],[-180.59,95.628,152.869],[-177.688,96.972,159.093],[-174.95,98.261,164.966],[-172.185,99.413,170.894],[-169.367,100.564,176.938],[-165.841,101.915,184.499],[-162.716,102.861,191.2],[-159.897,103.409,197.247],[-156.994,104.091,203.472],[-154.094,104.436,209.69],[-151.339,105.049,215.598],[-148.491,105.465,221.706],[-144.765,105.745,229.697],[-141.98,105.824,235.669],[-139.112,105.902,241.819],[-136.247,106.115,247.963],[-133.317,105.99,254.248],[-130.446,105.73,260.404],[-130.446,99.542,260.404],[-130.446,93.354,260.404],[-130.446,87.166,260.404],[-130.446,80.978,260.404],[-132.979,79.85,254.971],[-135.879,79.799,248.752],[-138.891,79.747,242.295],[-141.707,79.759,236.254],[-144.578,79.77,230.097],[-147.418,79.726,224.007],[-150.255,79.401,217.923],[-152.986,78.868,212.067],[-155.884,78.2,205.851],[-158.752,77.601,199.702],[-161.588,77.07,193.62],[-164.535,76.338,187.3],[-167.212,75.333,181.56],[-169.971,74.195,175.642],[-172.671,72.992,169.852],[-175.596,71.723,163.58],[-178.404,70.589,157.557],[-181.874,69.116,150.117],[-184.715,67.578,144.025],[-187.61,66.176,137.816],[-190.596,64.771,131.412],[-193.551,63.167,125.075],[-196.389,61.833,118.988],[-199.282,60.434,112.785],[-199.282,66.912,112.785],[-199.282,73.358,112.785],[-199.282,79.676,112.785],[-199.313,86.233,112.718]]
dengB=[[-182.939,121.997,167.559],[-180.763,126.866,172.226],[-178.366,130.307,177.365],[-175.896,132.143,182.663],[-173.278,132.733,188.278],[-170.662,131.508,193.887],[-168.162,128.672,199.248],[-166.83,122.881,202.106],[-167.401,116.426,200.88],[-169.336,110.986,196.73],[-171.752,108.041,191.55],[-174.279,106.51,186.13],[-177.094,106.182,180.095],[-179.678,107.131,174.553],[-182.065,110.299,169.433],[-183.456,115.815,166.451]]
deng_pos=[{"pos":p,"scale":[1,1,1]} for p in dengA+dengB]

archives={0:["Datas/Scene/scn0000.bin","SCN0000"],1:["Datas/Scene/scn0001.bin","SCN0001"],2:["Datas/Scene/scn0002.bin","SCN0002"],3:["Datas/Scene/scn0003.bin","SCN0003"],4:["Datas/Scene/scn0004.bin","SCN0004"],5:["Datas/Scene/scn0005.bin","SCN0005"],6:["Datas/Scene/scn0006.bin","SCN0006"],7:["Datas/Scene/scn0007.bin","SCN0007"],8:["Datas/Scene/scn0008.bin","SCN0008"],9:["Datas/Scene/scn0009.bin","SCN0009"],10:["Datas/Scene/scn0010.bin","SCN0010"],11:["Datas/Scene/scn0011.bin","SCN0011"],12:["Datas/Scene/scn0012.bin","SCN0012"],13:["Datas/Scene/scn0013.bin","SCN0013"],14:["Datas/Scene/scn0014.bin","SCN0014"],15:["Datas/Scene/scn0015.bin","SCN0015"],16:["Datas/Scene/scn0016.bin","SCN0016"],17:["Datas/Scene/scn0017.bin","SCN0017"],18:["Datas/Scene/scn0018.bin","SCN0018"],19:["Datas/Scene/scn0019.bin","SCN0019"],20:["Datas/Scene/scn0020.bin","SCN0020"],21:["Datas/Scene/scn0021.bin","SCN0021"],22:["Datas/Scene/scn0022.bin","SCN0022"],23:["Datas/Scene/scn0023.bin","SCN0023"],24:["Datas/Scene/scn0024.bin","SCN0024"],25:["Datas/Scene/scn0025.bin","SCN0025"],26:["Datas/Scene/scn0026.bin","SCN0026"],27:["Datas/Scene/scn0027.bin","SCN0027"],28:["Datas/Scene/scn0028.bin","SCN0028"],29:["Datas/Scene/scn0029.bin","SCN0029"],30:["Datas/Scene/scn0030.bin","SCN0030"],31:["Datas/Scene/MerryRoomA.bin","MERRYROOMA"],32:["Datas/Scene/MerryRoomB.bin","MERRYROOMB"],33:["Datas/Scene/MerryRoomC.bin","MERRYROOMC"],34:["Datas/Scene/ScnCommunityHall.bin",None],35:["Datas/Scene/ScnMyHouse.bin","SCNMYHOUSE"],36:["Datas/Scene/ScnCommunity.bin",None],37:["Datas/Scene/ScnRoom.bin","SCNROOM"],38:["Datas/Scene/ScnMerryRoom.bin","SCNMERRYROOM"],39:["Datas/Scene/ScnRoom_Night.bin","SCNROOM_NIGHT"]}

def mo(name,arc,msh=None,hrc=None,mot=None,inst=None):
    o={"name":name,"archive":arc}
    if msh:o["msh"]=msh
    if hrc:o["hrc"]=hrc
    if mot:o["mot"]=mot
    o["instances"]=inst if inst is not None else [{"pos":[0,0,0],"scale":[1,1,1]}]
    return o
def gp(name,arc,msh,hrc,mot=None,n=1):
    return mo(name,arc,msh,hrc,mot,[{"pos":[0,0,0],"scale":[1,1,1]} for _ in range(n)])

M={}
M[0]=[mo("huishou","Datas/scene/Mapobj/huishou.bin","huishou.msh","huishou.hrc","huishou.mot",[{"pos":[-56.555,101.897,275.672],"scale":[1,1,1]}]),
      mo("zhaopai","Datas/scene/Mapobj/zhaopai.bin","zhaopai.msh","zhaopai.hrc","zhaopai.mot",[{"pos":[-183.498,2.294,239.456],"scale":[1,1,1]}])]
M[1]=[]
M[2]=[]
# case 3 box: 16x16 isometric diamond grid (origin x -154.523, y 0, z 0, step 10.301131)
#   x = originX + (row+col)*step ; z = (col-row)*step ; y = 0
box_step=10.301131
box_inst=[]
for r in range(16):
    for c in range(16):
        box_inst.append({"pos":[round(-154.523+(r+c)*box_step,3),0.0,round((c-r)*box_step,3)],"scale":[1,1,1]})
M[3]=[mo("box","Datas/scene/Mapobj/box.bin","box.msh","box.hrc",None,box_inst),
      mo("ball","Datas/scene/Mapobj/ball.bin","ball.msh","ball.hrc","ball.mot")]
M[4]=[mo("sea_up","Datas/scene/Mapobj/sea_up.bin","sea_up.msh","sea_up.hrc","sea_up.mot",[{"pos":[0,0,0],"scale":[1,1,1]}]),
      mo("sea_down","Datas/scene/Mapobj/sea_down.bin","sea_down.msh","sea_down.hrc","sea_down.mot",[{"pos":[0,0,0],"scale":[1,1,1]}]),
      mo("chuan","Datas/scene/Mapobj/chuan.bin","chuan.msh","chuan.hrc","chuan.mot",[{"pos":[-125.0,-33.0,519.0],"scale":[1,1,1]}]),
      mo("beach","Datas/scene/Mapobj/beach.bin","lang.msh","lang.hrc","lang.mot",[{"pos":[0,0,0],"scale":[1,1,1]}]),
      mo("sea","Datas/scene/Mapobj/sea.bin","sea.msh","sea.hrc","sea.mot",[{"pos":[0,0,0],"scale":[1,1,1]}])]
M[5]=[mo("christmas","Datas/scene/Mapobj/christmas.bin","christmas.msh","christmas.hrc","christmas.mot",[{"pos":[105.0,-33.0,236.0],"scale":[1,1,1]}]),
      mo("merrychristmas","Datas/scene/Mapobj/merrychristmas.bin","merrychristmas.msh","merrychristmas.hrc","merrychristmas.mot",[{"pos":[0,0,0],"scale":[1,1,1]}])]
M[6]=[mo("deng","Datas/scene/Mapobj/deng.bin","deng.msh","deng.hrc",None,deng_pos),
      mo("zhuanpan","Datas/scene/Mapobj/zhuanpan.bin","zhuanpan.msh","zhuanpan.hrc","zhuanpan.mot",[{"pos":[114.56,0.0,329.529],"scale":[1,1,1]}])]
M[7]=[mo("sky","Datas/scene/Mapobj/sky.bin","sky.msh","sky.hrc","sky.mot",[{"pos":[7.935,0.0,38.449],"scale":[1,1,1]}])]
M[8]=[mo("jinzita","Datas/scene/Mapobj/jinzita.bin","jinzita.msh","jinzita.hrc","jinzita.mot"),
      mo("zimu","Datas/scene/Mapobj/zimu.bin","zimu.msh","zimu.hrc","zimu.mot")]
M[9]=[mo("guatan","Datas/scene/Mapobj/guatan.bin","guatan.msh","guatan.hrc","guatan.mot",
        [{"pos":[-45.79,0.0,0.0],"scale":[1,1,1]},{"pos":[129.21,0.0,-83.0],"scale":[1,1,1]},
         {"pos":[-95.79,0.0,50.0],"scale":[0.65,0.65,0.65]},{"pos":[179.21,0.0,-83.0],"scale":[0.65,0.65,0.65]}])]
M[10]=[mo("house","Datas/scene/Mapobj/house.bin","house.msh","house.hrc",None,[{"pos":[0,0,0],"scale":[1,1,1]},{"pos":[2168.0,0.0,0.0],"scale":[1,1,1]}]),
       mo("mao","Datas/scene/Mapobj/mao.bin","mao.msh","mao.hrc","mao.mot",[{"pos":[-140.636,69.757,170.121],"scale":[1,1,1]}]),
       mo("qiqiu","Datas/scene/Mapobj/qiqiu.bin","qiqiu.msh","qiqiu.hrc","qiqiu.mot"),
       mo("mao1","Datas/scene/Mapobj/mao1.bin","mao1.msh","mao1.hrc","mao1.mot",[{"pos":[140.636,69.754,170.121],"scale":[1,1,1]}])]
M[11]=[gp("screen","Datas/scene/Mapobj/screen.bin","screen.msh","screen.hrc"),gp("caidai","Datas/scene/Mapobj/caidai.bin","caidai.msh","caidai.hrc"),
       gp("dengguang","Datas/scene/Mapobj/dengguang.bin","dengguang.msh","dengguang.hrc"),gp("dideng","Datas/scene/Mapobj/dideng.bin","dideng.msh","dideng.hrc"),
       gp("dingdeng","Datas/scene/Mapobj/dingdeng.bin","dingdeng.msh","dingdeng.hrc","dingdeng.mot"),gp("ding","Datas/scene/Mapobj/ding.bin","ding.msh","ding.hrc","ding.mot"),
       gp("jiguang","Datas/scene/Mapobj/jiguang.bin","jiguang.msh","jiguang.hrc"),gp("laba","Datas/scene/Mapobj/laba.bin","laba.msh","laba.hrc","laba.mot")]
M[12]=[gp("fifa_guanggao","Datas/scene/Mapobj/fifa_guanggao.bin","fifa_guanggao.msh","fifa_guanggao.hrc"),gp("fifa_renqun","Datas/scene/Mapobj/fifa_renqun.bin","fifa_renqun.msh","fifa_renqun.hrc"),
       gp("fifa_shanguang","Datas/scene/Mapobj/fifa_shanguang.bin","fifa_shanguang.msh","fifa_shanguang.hrc"),gp("fifa_qiubei","Datas/scene/Mapobj/fifa_qiubei.bin","fifa_qiubei.msh","fifa_qiubei.hrc","fifa_qiubei.mot")]
M[13]=[gp("fifanight_guanggao","Datas/scene/Mapobj/fifanight_guanggao.bin","fifa_guanggao.msh","fifa_guanggao.hrc"),gp("fifanight_renqun","Datas/scene/Mapobj/fifanight_renqun.bin","fifa_renqun.msh","fifa_renqun.hrc"),
       gp("fifanight_shanguang","Datas/scene/Mapobj/fifanight_shanguang.bin","fifa_shanguang.msh","fifa_shanguang.hrc"),gp("fifa_qiubei","Datas/scene/Mapobj/fifa_qiubei.bin","fifa_qiubei.msh","fifa_qiubei.hrc","fifa_qiubei.mot")]
haidi=["guang","shanhu-bai","shanhu-hong","shanhu-lv","shanhuzhi-bai","shanhuzhi-hong","shanhuzhi-lv","sea_screen","tv"]
haidi_mot={"guang","sea_screen","tv"}
M[14]=[gp(n,"Datas/scene/Mapobj/14_haidi/%s.bin"%n,"%s.msh"%n,"%s.hrc"%n,("%s.mot"%n if n in haidi_mot else None)) for n in haidi]
M[15]=[mo("UV","Datas/scene/Mapobj/15_donghua/UV.bin","15_UV.msh","15_UV.hrc",None,[{"pos":[0,0,0],"scale":[1,1,1]}]),
       mo("hua","Datas/scene/Mapobj/15_donghua/hua.bin","15_hua.msh","15_hua.hrc","15_hua.mot",[{"pos":[0,0,0],"scale":[1,1,1]}]),
       mo("shu1","Datas/scene/Mapobj/15_donghua/shu1.bin","15_shu1.msh","15_shu1.hrc","15_shu1.mot",[{"pos":[-145.149,38.736,419.705],"scale":[1,1,1]}]),
       mo("shu2","Datas/scene/Mapobj/15_donghua/shu2.bin","15_shu2.msh","15_shu2.hrc","15_shu2.mot",[{"pos":[-79.26,40.744,433.129],"scale":[1,1,1]}]),
       mo("shu3","Datas/scene/Mapobj/15_donghua/shu3.bin","15_shu3.msh","15_shu3.hrc","15_shu3.mot",[{"pos":[-18.138,40.17,435.768],"scale":[1,1,1]}]),
       mo("shu4","Datas/scene/Mapobj/15_donghua/shu4.bin","15_shu4.msh","15_shu4.hrc","15_shu4.mot",[{"pos":[133.498,38.343,415.02],"scale":[1,1,1]}])]
m16=[]
for i in range(1,22): m16.append(gp("di%d"%i,"Datas/scene/Mapobj/16/di/%d.bin"%i,"di%d.msh"%i,"di%d.hrc"%i,"di%d.mot"%i))
for i in [1,2,3,4,5,7,8,9,10]: m16.append(gp("fangzi%d"%i,"Datas/scene/Mapobj/16/fangzi/%d.bin"%i,"fangzi%d.msh"%i,"fangzi%d.hrc"%i))
m16.append(gp("chengshi","Datas/scene/Mapobj/16/fangzi/chengshi.bin","chengshi.msh","chengshi.hrc"))
m16.append(gp("deng2","Datas/scene/Mapobj/16/fangzi/deng2.bin","deng2.msh","deng2.hrc"))
m16.append(gp("paizi","Datas/scene/Mapobj/16/fangzi/paizi.bin","paizi.msh","paizi.hrc"))
m16.append(mo("xintiandi","Datas/scene/Mapobj/16/fangzi/xintiandi.bin","xintiandi.msh","xintiandi.hrc",None,[{"pos":[0,-120.0,0],"scale":[1,1,1]}]))
m16.append(gp("sky","Datas/scene/Mapobj/16/sky.bin","sky.msh","sky.hrc","sky.mot"))
for i in [1,2,3]: m16.append(gp("jiguang%d"%i,"Datas/scene/Mapobj/16/jiguang%d.bin"%i,"jiguang%d.msh"%i,"jiguang%d.hrc"%i,"jiguang%d.mot"%i))
M[16]=m16
ditie=["dianshi","die1","die2","diguang","fushou1","fushou2","guangquan","laba1","laba2","sky","zhan","zhuzi"]
M[17]=[gp(n,"Datas/scene/Mapobj/17_ditie/%s.bin"%n,"%s.msh"%n,"%s.hrc"%n,(None if n=="dianshi" else "%s.mot"%n)) for n in ditie]
boat=[("beijing",1),("guajian1",1),("guajian2",1),("nihong",0),("qiao",1),("boat_screen",0),("shuimo",0),("water",0),("zhuandeng",0)]
M[18]=[gp(n,"Datas/scene/Mapobj/18_Boat/%s.bin"%n,"%s.msh"%n,"%s.hrc"%n,("%s.mot"%n if mt else None)) for n,mt in boat]
pk=["guang%d"%i for i in range(1,5)]+["laba%d"%i for i in range(1,12)]+["shan","zhuan"]
M[19]=[gp(n,"Datas/scene/Mapobj/pk/%s.bin"%n,"%s.msh"%n,"%s.hrc"%n,(None if n=="shan" else "%s.mot"%n)) for n in pk]
M[20]=[gp("tv1","Datas/scene/Mapobj/19_subway/vt1.bin","tv1.msh","tv1.hrc"),gp("tv6","Datas/scene/Mapobj/19_subway/vt6.bin","tv6.msh","tv6.hrc"),gp("SUBWAY04","Datas/scene/Mapobj/19_subway/SUBWAY04.bin","SUBWAY04.msh","SUBWAY04.hrc")]
saloon=[gp("laba%d"%i,"Datas/scene/Mapobj/saloon/laba/%d.bin"%i,"laba%d.msh"%i,"laba%d.hrc"%i,"laba%d.mot"%i) for i in range(1,13)]
saloon+=[gp("deng%d"%i,"Datas/scene/Mapobj/saloon/deng/%d.bin"%i,"deng%d.msh"%i,"deng%d.hrc"%i) for i in range(1,13)]
saloon+=[gp("zhumen","Datas/scene/Mapobj/saloon/zhumen.bin","zhumen.msh","zhumen.hrc","zhumen.mot")]
M[21]=saloon
M[22]=[gp("fenmu_dong1","Datas/scene/Mapobj/fenmu/dong1.bin","guang4.msh","guang4.hrc"),gp("fenmu_gui","Datas/scene/Mapobj/fenmu/gui.bin","laba11.msh","laba11.hrc","laba11.mot"),
       gp("fenmu_lanhuo","Datas/scene/Mapobj/fenmu/lanhuo.bin","shan.msh","shan.hrc"),gp("fenmu_gui2","Datas/scene/Mapobj/fenmu/gui2.bin","laba12.msh","laba12.hrc","laba12.mot"),
       gp("sheguang","Datas/scene/Mapobj/fenmu/sheguang.bin","sheguang.msh","sheguang.hrc","sheguang.mot"),gp("sheguang2","Datas/scene/Mapobj/fenmu/sheguang2.bin","sheguang2.msh","sheguang2.hrc","sheguang2.mot"),
       gp("sheguang3","Datas/scene/Mapobj/fenmu/sheguang3.bin","sheguang3.msh","sheguang3.hrc","sheguang3.mot"),gp("fenmu_dong2","Datas/scene/Mapobj/fenmu/dong2.bin","donghua2.msh","donghua2.hrc")]
M[23]=[gp("chuanghu","Datas/scene/Mapobj/jiaoshi/chuanghu.bin","chuanghu.msh","chuanghu.hrc"),gp("chuanghufanse","Datas/scene/Mapobj/jiaoshi/chuanghufanse.bin","chuanghufanse.msh","chuanghufanse.hrc")]+[gp("laba%d"%i,"Datas/scene/Mapobj/jiaoshi/laba%d.bin"%i,"laba%d.msh"%i,"laba%d.hrc"%i,"laba%d.mot"%i) for i in range(1,11)]
M[24]=[gp("donghua","Datas/scene/Mapobj/xuejing/donghua.bin","donghua.msh","donghua.hrc","donghua.mot"),gp("biaodonghua","Datas/scene/Mapobj/xuejing/biaodonghua.bin","biaodonghua.msh","biaodonghua.hrc")]
M[25]=[gp("donghua","Datas/scene/Mapobj/chuntian/donghua.bin","chuntiandonghua.msh","chuntiandonghua.hrc"),
       gp("huteidonghua","Datas/scene/Mapobj/chuntian/huteidonghua.bin","hudeichuntiandonghua.msh","hudeichuntiandonghua.hrc","hudeichuntiandonghua.mot"),
       gp("hudiedonghua2","Datas/scene/Mapobj/chuntian/hudiedonghua2.bin","hudeichuntiandonghua2.msh","hudeichuntiandonghua2.hrc","hudeichuntiandonghua2.mot"),
       gp("hudiedonghua3","Datas/scene/Mapobj/chuntian/hudiedonghua3.bin","hudeichuntiandonghua3.msh","hudeichuntiandonghua3.hrc","hudeichuntiandonghua3.mot"),
       gp("hudiedonghua4","Datas/scene/Mapobj/chuntian/hudiedonghua4.bin","hudeichuntiandonghua4.msh","hudeichuntiandonghua4.hrc","hudeichuntiandonghua4.mot"),
       gp("caodonghua","Datas/scene/Mapobj/chuntian/caodonghua.bin","caochuntiandonghua.msh","caochuntiandonghua.hrc","caochuntiandonghua.mot")]
lq=[("che1",1),("che2",1),("che3",1),("che4",1),("deng1",1),("deng2",1),("deng3",1),("huo",0),("laba1",1),("laba2",1),("laba3",1),("laba4",1),("xiaodeng",0)]
M[26]=[gp(n,"Datas/scene/Mapobj/lanqiuchang/%s.bin"%n,"%s.msh"%n,"%s.hrc"%n,("%s.mot"%n if mt else None)) for n,mt in lq]
M[27]=[]
niao=[("chuan","chuan.msh","chuan.hrc","chuan.mot"),("deng1","deng1_.msh","deng1_.hrc",None),("deng2","deng2_.msh","deng2_.hrc",None),("deng3","deng3_.msh","deng3_.hrc",None),("deng4","deng4_.msh","deng4_.hrc",None),("feichuan","feichuan.msh","feichuan.hrc","feichuan.mot"),("pengshui","pengshui_.msh","pengshui_.hrc",None)]
M[28]=[gp(n,"Datas/scene/Mapobj/niaochao/%s.bin"%n,msh,hrc,mot) for n,msh,hrc,mot in niao]
M[29]=[gp("jiuba","Datas/scene/Mapobj/jiku/jiuba.bin","jiuba.msh","jiuba.hrc"),gp("pingmu","Datas/scene/Mapobj/jiku/pingmu.bin","pingmu.msh","pingmu.hrc")]
M[30]=[gp("kongtongfengche","Datas/scene/Mapobj/katonggonglu/fengchedonghua.bin","kongtongfengche.msh","kongtongfengche.hrc","kongtongfengche.mot")]
M[31]=[gp("hunliA","Datas/scene/Mapobj/MerryRoomA/hunli_A.bin","hunliA.msh","hunliA.hrc")]
M[32]=[gp("hunliB","Datas/scene/Mapobj/MerryRoomB/hunli_B.bin","hunliB.msh","hunliB.hrc"),gp("hunliBhua","Datas/scene/Mapobj/MerryRoomB/hunli_Bhua.bin","hunliBhua.msh","hunliBhua.hrc")]
M[33]=[gp("hunliC","Datas/scene/Mapobj/MerryRoomC/hunli_C.bin","hunliC.msh","hunliC.hrc"),gp("hunliChua","Datas/scene/Mapobj/MerryRoomC/hunli_Chua.bin","hunliChua.msh","hunliChua.hrc")]
M[34]=[]
M[35]=[gp("computer","Datas/scene/Mapobj/3dhouse/coumputer.bin","computer.msh","computer.hrc")]
M[36]=[]
roomobj=[("dianshi","tvdh.msh","tvdh.hrc",None)]+[("laba%d"%i,"labadh%d.msh"%i,"labadh%d.hrc"%i,"labadh%d.mot"%i) for i in range(1,5)]+[("guang%d"%i,"guang%d.msh"%i,"guang%d.hrc"%i,None) for i in range(1,9)]
M[37]=[gp(n,"Datas/scene/Mapobj/Room_obj/%s.bin"%n,msh,hrc,mot) for n,msh,hrc,mot in roomobj]+[gp("taizi","Datas/scene/Mapobj/Room_obj/taizi.bin","taizi.msh","taizi.hrc")]
M[38]=[gp(n,"Datas/scene/Mapobj/Room_obj/%s.bin"%n,msh,hrc,mot) for n,msh,hrc,mot in roomobj]
rn=[("lang","Room_scene.msh","Room_scene.hrc"),("tang1","tang1.msh","tang1.hrc"),("tang2","tang2.msh","tang2.hrc"),("xin","xin.msh","xin.hrc"),("deng","deng.msh","deng.hrc")]
M[39]=[gp(n,"Datas/scene/Mapobj/Room_night_obj/%s.bin"%n,msh,hrc) for n,msh,hrc in rn]

# ---- non-mapobj extras: billboards / point-lights / particle effects (decoded from code literals) ----
def bb(tex,pos,scale=None):
    o={"kind":"billboard","texture":tex,"pos":[float(v) for v in pos]}
    if scale: o["scale"]=scale
    return o
def li(pos): return {"kind":"light","pos":[float(v) for v in pos]}
def pt(pos,s,eff="0x6c"): return {"kind":"particle","effect":eff,"pos":[float(v) for v in pos],"scale":[float(s)]*3}
E={}
E[1]=[bb("guangxiao_.tga",[77.0,147.475,336.24]),bb("guangxiao_.tga",[-626.482,147.475,336.349])]
E[5]=[bb("guangxiao_.tga",[-97.7,115.0,222.4])]
E[22]=[bb("03_.tga",[-163.43,40.0,135.51],[100,100,1]),bb("03_.tga",[-11.6,30.0,200.11],[100,100,1]),bb("03_.tga",[182.96,28.0,21.6],[100,100,1])]
E[24]=[bb("guang_.tga",[432.109,210.371,-3.208],[200,200,1]),bb("guang_.tga",[-498.497,212.36,192.66],[200,200,1]),bb("guang_.tga",[75.41,213.7,314.22],[200,200,1])]
E[28]=[li([468.834,137.709,467.449]),li([-185.057,131.23,463.951]),li([-269.018,163.505,-180.453]),li([243.408,125.984,-48.953])]
E[31]=[pt([66,193,213],20),pt([126,182,220],10),pt([62,174,207],20),pt([-115,186,223],10),pt([-118,41,160],20),pt([194,47,217],20)]
E[32]=[pt([5,117,-75],10),pt([-23,113,-72],10),pt([56,112,-76],10),pt([65,102,-71],10),pt([86,85,-75],5),pt([-60,101,-75],5),pt([-75,81,-76],5)]
E[33]=[pt([-5,130,40],15),pt([-20,135,27],15),pt([15,140,36],10),pt([100,126,-133],5),pt([75,120,-133],6),pt([-88,118,-127],6),pt([-110,123,-135],5),pt([-96,47,-215],5),pt([99,45,-220],5)]

notes={1:"glow billboards only, no 3D mapobj (see effects).",2:"Shared empty body with id 27; loads base scene only.",
3:"box = 256-instance 16x16 isometric diamond grid (x=originX+(r+c)*step, z=(c-r)*step; originX -154.523, step 10.301131); ball at origin.",
4:"Underwater/sea. Only chuan has a non-origin position; the 'beach' group uses lang.msh/.hrc/.mot.",
5:"Christmas. christmas at (105,-33,236); also a guangxiao_.tga glow billboard.",
6:"Spinning-wheel stage. 'deng' = 72 light beads along an arc (real .data positions). zhuanpan spins.",
9:"GUATAN - same archive instantiated 4x; copies 3 and 4 scaled to 0.65.",
10:"house instantiated 2x (2nd far duplicate at x=2168).",
16:"38 mapobjs; only xintiandi is offset (y -120). di1-21/sky/jiguang1-3 animated.",
27:"case 0x1b - shared empty body; base scene only.",
34:"No switch case: loads base scene.msh/.hrc only (no mapobjs).",
36:"No switch case: loads base scene.msh/.hrc only (no mapobjs).",
38:"Same Room_obj set as id 37 but WITHOUT 'taizi' (13 vs 14 objects)."}

bss_origin=set([11,12,13,14,17,18,19,20,21,22,23,24,25,26,28,29,30,31,32,33,35,37,38,39])

scenes=[]
for i in range(40):
    a,folder=archives[i]
    s={"id":i,"hex":"0x%02x"%i,"archive":a,"folder":folder,
       "base":{"msh":"scene.msh","hrc":"scene.hrc"},
       "instancePositions":("origin" if i in bss_origin else "explicit"),
       "mapobjs":M.get(i,[])}
    if i in E: s["effects"]=E[i]
    if i in notes: s["notes"]=notes[i]
    scenes.append(s)

doc={
 "_about":"SDO stand-alone: scene id -> 3D stage background + mapobj load table. Extracted from FUN_004b43c0 (Scene_LoadBackground) in 030_scene_004b43c0.c, verified against sdo_stand_alone.exe.",
 "_sourceFunction":"FUN_004b43c0 / Scene_LoadBackground_004b43c0 (switch on scene id)",
 "_paths":"Asset paths use the original game-root layout (Datas\\... with backslashes); case-insensitive. Slashes normalized to / here.",
 "_idRemaps":{"0x0c->0x0d":"when flag DAT_00674f04+0xad9 != 1","0x25->0x26":"when flag DAT_00674f04+0x82 == 1","0x1e->0x1f":"always","0x1c->0x1d":"always","note":"These swap to a day/night or variant scene before loading; the 0x1c & 0x1e case bodies are still listed for reference."},
 "_instancePositions":{"explicit":"pos values are real (code literals or initialized .data).","origin":"engine position table is zero-filled BSS; every instance is placed at identity and the mesh geometry is already world-positioned. In Unity, import these meshes WITHOUT recentering (keep model-space origin), or pull positions from the .bin/.msh."},
 "_effects":"Non-mapobj extras (not msh/hrc geometry) live in the per-scene 'effects' array: kind 'billboard' (2D textured quad, has texture[+scale]), 'light' (point light), or 'particle' (Effect_Play emitter, has effect id + uniform scale). Positions are decoded code literals (explicit).",
 "_excluded":"Per-scene .dds/.tga texture-animation sets used by mapobj materials are not enumerated (they belong to the .bin archives).",
 "_base":"Every scene also loads scene.msh + scene.hrc from its own archive (the base stage geometry) before mapobjs.",
 "scenes":scenes
}
out="H:/65_remake/docs/reverse-engineering/SDO_SCENE_MAPOBJ_TABLE.json"
open(out,"w",encoding="utf-8").write(json.dumps(doc,ensure_ascii=False,indent=1))
print("scenes:",len(scenes))
print("mapobj groups:",sum(len(s["mapobjs"]) for s in scenes))
print("instances:",sum(len(m.get("instances",[])) for s in scenes for m in s["mapobjs"]))
print("effects:",sum(len(s.get("effects",[])) for s in scenes))
print("written:",out)
