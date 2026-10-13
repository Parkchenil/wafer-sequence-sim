# 블록아웃 검수 후 힉스필드 장면에 기구 디테일을 추가한다.
import bpy, math
from mathutils import Vector, Matrix
scene=bpy.context.scene
def v(p): return Vector((p[0],-p[2],p[1]))
white=bpy.data.materials['ceramic_white'];steel=bpy.data.materials['machined_aluminium'];graphite=bpy.data.materials['anodised_graphite'];blue=bpy.data.materials['axis_blue'];gold=bpy.data.materials['probe_gold']
def mat(name,color,metal=0,rough=.4):
 m=bpy.data.materials.new(name);m.diffuse_color=(*color,1);m.use_nodes=True;p=m.node_tree.nodes.get('Principled BSDF');p.inputs['Base Color'].default_value=(*color,1);p.inputs['Metallic'].default_value=metal;p.inputs['Roughness'].default_value=rough;return m
black=mat('black_polymer',(.012,.018,.024),0,.55)
cyan=mat('display_glass',(.02,.31,.40),.2,.24)
green=mat('status_green',(.035,.40,.11),.1,.25)
red=mat('status_red',(.55,.035,.018),.1,.32)

def box(name,pos,size,material,bevel=.002):
 bpy.ops.mesh.primitive_cube_add(size=1,location=v(pos));o=bpy.context.object;o.name=name;o.dimensions=(size[0],size[2],size[1]);bpy.ops.object.transform_apply(location=False,rotation=False,scale=True);o.data.materials.append(material)
 if bevel:
  b=o.modifiers.new('모서리 가공','BEVEL');b.width=bevel;b.segments=2;o.modifiers.new('면 법선','WEIGHTED_NORMAL')
 return o

def cyl(name,pos,r,h,material,segments=32):
 bpy.ops.mesh.primitive_cylinder_add(vertices=segments,radius=r,depth=h,location=v(pos));o=bpy.context.object;o.name=name;o.data.materials.append(material)
 for p in o.data.polygons:p.use_smooth=len(p.vertices)==4
 return o

def rod(name,a,b,r,material):
 av=v(a);bv=v(b);d=bv-av
 bpy.ops.mesh.primitive_cylinder_add(vertices=16,radius=r,depth=d.length,location=(av+bv)/2);o=bpy.context.object;o.name=name;o.rotation_euler=d.to_track_quat('Z','Y').to_euler();o.data.materials.append(material)
 for p in o.data.polygons:p.use_smooth=len(p.vertices)==4
 return o

def ring(name,pos,r,thick,material):
 bpy.ops.mesh.primitive_torus_add(major_radius=r,minor_radius=thick,major_segments=64,minor_segments=8,location=v(pos));o=bpy.context.object;o.name=name;o.data.materials.append(material)
 for p in o.data.polygons:p.use_smooth=True
 return o

# 하부 3분할 서비스 패널과 고무 풋.
for i,x in enumerate([-.575,0,.575]):
 box('COVER_service_door_'+str(i),(x,-.24,.519),(.557,.265,.012),white,.008)
 box('COVER_handle_recess_'+str(i),(x+.19,-.21,.527),(.026,.079,.006),graphite,.004)
 box('COVER_handle_'+str(i),(x+.19,-.21,.535),(.012,.055,.012),steel,.003)
 for dx in [-.245,.245]:
  for yy in [-.35,-.13]:rod('COVER_fastener_'+str(i)+'_'+str(dx)+'_'+str(yy),(x+dx,yy,.526),(x+dx,yy,.530),.003,steel)
for x in [-.74,.74]:
 for z in [-.43,.43]:
  cyl('STATIC_foot_'+str(x)+'_'+str(z),(x,-.414,z),.044,.03,graphite)
  cyl('STATIC_foot_pad_'+str(x)+'_'+str(z),(x,-.435,z),.047,.012,black)

# 두 레일과 슬라이더가 척을 지지한다. 고정 레일은 XY 움직임에 포함하지 않는다.
for z in [-.15,.15]:
 box('STATIC_linear_rail_'+str(z),(.35,.038,z),(.45,.02,.018),steel,.002)
 for x in [.15,.55]:cyl('STATIC_rail_screw_'+str(x)+'_'+str(z),(x,.049,z),.003,.003,graphite,12)
for x in [.20,.50]:box('STAGE_bearing_'+str(x),(x,.057,0),(.045,.018,.35),graphite)
ring('STAGE_chuck_outer_ring',(.35,.111,0),.155,.0015,graphite)
for a in range(0,360,45):
 angle=math.radians(a);cyl('STAGE_flange_screw_'+str(a),(.35+.17*math.cos(angle),.073,.17*math.sin(angle)),.003,.004,steel,12)
box('STAGE_front_encoder',(.35,.065,.205),(.22,.013,.013),steel)
for i in range(11):box('STAGE_encoder_tick_'+str(i),(.255+i*.019,.065,.212),(.001,.008,.0008),graphite,0)

# 카세트 슬롯은 25개의 실제 간격을 가진 좌우 지지 레일로 표현한다.
for i in range(25):
 y=.023+i*.006
 for side in [-1,1]:box('STATIC_slot_'+str(i)+'_'+str(side),(-.69+side*.143,y,.28),(.021,.002,.285),steel,.0005)
box('STATIC_cassette_lip',(-.69,.018,.115),(.344,.016,.018),white)
box('STATIC_load_sensor',(-.50,.043,.20),(.018,.035,.032),graphite)

# 얼라이너 회전판의 중심축, 마커, 측면 모터와 레벨링 볼트.
ring('ALIGNER_rotor_rim',(-.12,.108,.34),.148,.001,graphite)
box('ALIGNER_rotor_index',(-.12,.112,.195),(.008,.001,.012),gold,.0005)
box('STATIC_aligner_motor',(-.12,.048,.51),(.065,.067,.047),steel,.005)
for dx in [-.12,.12]:
 for dz in [-.12,.12]:cyl('STATIC_aligner_bolt_'+str(dx)+'_'+str(dz),(-.12+dx,.012,.34+dz),.004,.008,steel,12)

# 측정 헤드의 핀 콜릿과 나사, 가이드 뒤쪽의 구동부.
for i in range(4):
 x=.35+(i-1.5)*.003
 cyl('PROBE_collet_'+str(i),(x,.176,0),.0016,.012,steel,16)
for dx in [-.029,.029]:
 for dz in [-.017,.017]:cyl('PROBE_cap_screw_'+str(dx)+'_'+str(dz),(.35+dx,.241,dz),.0028,.004,graphite,12)
box('PROBE_front_plate',(.35,.208,.031),(.068,.044,.003),steel,.002)
box('PROBE_contact_indicator',(.375,.213,.033),(.004,.01,.001),cyan,.0005)
box('STATIC_z_motor',(.35,.434,-.06),(.062,.09,.068),graphite,.006)
rod('STATIC_z_guide_left',(.32,.22,-.045),(.32,.397,-.045),.006,steel)
rod('STATIC_z_guide_right',(.38,.22,-.045),(.38,.397,-.045),.006,steel)
box('STATIC_cable_tray',(.54,.26,-.28),(.018,.40,.042),graphite)
for i in range(12):box('STATIC_chain_link_'+str(i),(.54,.079+i*.027,-.253),(.025,.016,.013),black,.002)
box('STATIC_optical_mount',(.63,.23,-.24),(.13,.02,.13),steel)
ring('STATIC_camera_focus_ring',(.63,.271,-.20),.028,.003,graphite)
cyl('STATIC_camera_aperture',(.63,.264,-.20),.019,.003,black)

# 로봇은 local +X 세그먼트 단위로 디테일을 묶어 같은 rigid transform을 사용한다.
# 추가 메시를 원본 세그먼트에 결합해 움직임에서 나사/플랜지가 분리되지 않게 한다.
for role,length,width in [('ROBOT_upper',.36,.055),('ROBOT_lower',.36,.055),('ROBOT_blade',.18,.055)]:
 main=bpy.data.objects[role];start=main.location.copy();parts=[]
 if role!='ROBOT_blade':
  for xx in [.025,length-.025]:
   o=cyl(role+'_joint_temp',(0,0,0),.025,.014,graphite,32);o.location=start+v((xx,.03,0));parts.append(o)
  strip=box(role+'_insert_temp',(0,0,0),(length-.08,.003,.023),blue,.002);strip.location=start+v((length/2,.026,0));parts.append(strip)
 else:
  # 포크 끝의 두 지지 패드. 웨이퍼 중심은 끝쪽 지지부에 놓인다.
  for zz in [-.022,.022]:
   o=box(role+'_pad_temp',(0,0,0),(.058,.004,.012),black,.001);o.location=start+v((.14,.005,zz));parts.append(o)
 bpy.ops.object.select_all(action='DESELECT');main.select_set(True)
 for o in parts:o.select_set(True)
 bpy.context.view_layer.objects.active=main;bpy.ops.object.join()

# 작업자 모니터, 정지 버튼, 상태등은 시퀀스 장비의 외형을 명확히 한다.
box('STATIC_console_post',(.78,.18,.28),(.035,.36,.035),steel)
box('STATIC_console_body',(.78,.365,.29),(.24,.18,.04),graphite,.012)
box('STATIC_console_screen',(.78,.373,.312),(.21,.137,.003),cyan,.004)
box('STATIC_console_status',(.78,.334,.315),(.16,.006,.001),white,.001)
rod('STATIC_estop_base',(.83,.293,.315),(.83,.293,.326),.015,gold)
rod('STATIC_estop',(.83,.293,.327),(.83,.293,.34),.010,red)
cyl('STATIC_lamp_stem',(.79,.59,-.40),.012,.15,steel,24)
cyl('LAMP_status',(.79,.685,-.40),.022,.045,green,32)
cyl('STATIC_lamp_cap',(.79,.713,-.40),.024,.01,graphite,32)

# 가공 파트마다 서로 다른 재질은 유지하되 불필요한 촘촘한 선 표시는 넣지 않는다.
scene.render.resolution_x=1200;scene.render.resolution_y=800
result={'objects':len(bpy.data.objects),'meshes':sum(o.type=='MESH' for o in bpy.data.objects),'material_count':len(bpy.data.materials),'wafer_diameter_m':.30,'slot_count':25,'pin_count':4}
