# 힉스필드 3D Jutsu에서 실행하는 개념 장비의 1차 구조.
import bpy, math
from mathutils import Vector
scene=bpy.context.scene
scene.unit_settings.system='METRIC'
scene.unit_settings.scale_length=1

def v(p): return Vector((p[0],-p[2],p[1]))
def mat(name,color,metal=0,rough=.4):
 m=bpy.data.materials.new(name);m.diffuse_color=(*color,1);m.use_nodes=True
 p=m.node_tree.nodes.get('Principled BSDF');p.inputs['Base Color'].default_value=(*color,1);p.inputs['Metallic'].default_value=metal;p.inputs['Roughness'].default_value=rough
 return m
white=mat('ceramic_white',(.72,.76,.78),.15,.35)
steel=mat('machined_aluminium',(.40,.47,.53),.7,.29)
graphite=mat('anodised_graphite',(.045,.065,.09),.45,.38)
blue=mat('axis_blue',(.025,.22,.32),.55,.32)
silicon=mat('silicon',(.075,.12,.21),.8,.16)
gold=mat('probe_gold',(.58,.34,.065),.8,.22)

def box(name,pos,size,material,bevel=.003):
 bpy.ops.mesh.primitive_cube_add(size=1,location=v(pos));o=bpy.context.object;o.name=name;o.dimensions=(size[0],size[2],size[1]);bpy.ops.object.transform_apply(location=False,rotation=False,scale=True)
 o.data.materials.append(material)
 if bevel:
  b=o.modifiers.new('모서리 가공','BEVEL');b.width=bevel;b.segments=2
  o.modifiers.new('면 법선','WEIGHTED_NORMAL')
 return o

def cyl(name,pos,r,h,material,segments=48):
 bpy.ops.mesh.primitive_cylinder_add(vertices=segments,radius=r,depth=h,location=v(pos));o=bpy.context.object;o.name=name;o.data.materials.append(material)
 for p in o.data.polygons:p.use_smooth=len(p.vertices)==4
 return o

box('STATIC_bed',(0,-.05,0),(1.8,.1,1.08),steel,.012)
box('STATIC_lower_cabinet',(0,-.25,0),(1.74,.3,1.02),white,.015)
box('STATIC_rear_service',(0,.24,-.505),(1.74,.48,.07),white,.008)
for x in [-.865,.865]:box('STATIC_column_'+str(x),(x,.30,.49),(.028,.6,.028),steel,.003)
box('STATIC_front_trim',(0,-.108,.49),(1.77,.04,.04),white,.005)
box('STATIC_back_header',(0,.6,-.49),(1.77,.04,.04),white,.005)
for x in [-.865,.865]:box('STATIC_top_rail_'+str(x),(x,.6,0),(.028,.03,1.0),steel,.003)
box('STATIC_loadport_mount',(-.69,.012,.28),(.35,.024,.35),graphite)
for x in [-.858,-.522]:box('STATIC_cassette_wall_'+str(x),(x,.10,.28),(.012,.18,.33),white)
box('STATIC_cassette_back',(-.69,.10,.445),(.348,.18,.012),graphite)
cyl('STATIC_aligner_base',(-.12,.045,.34),.158,.09,graphite)
cyl('ALIGNER_rotor',(-.12,.101,.34),.15,.02,steel)
box('STATIC_xy_base',(.35,.015,0),(.48,.03,.48),graphite)
box('STAGE_carriage',(.35,.058,0),(.39,.025,.39),blue)
cyl('STAGE_chuck',(.35,.092,0),.158,.044,steel,64)
cyl('WAFER_template',(.35,.1145,0),.15,.001,silicon,96)
box('PROBE_holder',(.35,.207,0),(.085,.065,.060),white,.007)
for i in range(4):cyl('PROBE_pin_'+str(i),(.35+(i-1.5)*.003,.145,0),.0008,.060,gold,12)
for x in [.12,.61]:box('STATIC_gantry_column_'+str(x),(x,.25,-.23),(.045,.50,.045),steel,.004)
box('STATIC_gantry_bridge',(.365,.5,-.23),(.55,.055,.065),white,.004)
box('STATIC_probe_support',(.35,.32,-.12),(.06,.055,.25),graphite,.004)
box('STATIC_probe_guide',(.35,.29,0),(.058,.16,.05),steel,.003)
cyl('STATIC_robot_pedestal',(-.30,.0375,-.18),.065,.075,graphite)
# 관절 세그먼트는 로컬 +X 축을 따라 분리한다.
for name,start,length,thick,material in [('ROBOT_upper',(-.30,.075,-.18),.36,.045,white),('ROBOT_lower',(.06,.09,-.18),.36,.035,steel),('ROBOT_blade',(.42,.13,-.18),.18,.006,graphite)]:
 o=box(name,(start[0]+length/2,start[1],start[2]),(length,thick,.055),material,.007 if thick>.01 else .002)
 # 원점을 시작 관절로 옮기되 월드 형상을 보존한다.
 offset=v((length/2,0,0));o.data.transform(__import__('mathutils').Matrix.Translation(offset));o.location=v(start)
box('STATIC_camera_body',(.63,.35,-.20),(.075,.11,.075),graphite,.008)
cyl('STATIC_camera_lens',(.63,.282,-.20),.027,.032,steel,32)
box('STATIC_camera_bracket',(.63,.44,-.23),(.10,.045,.1),steel)

bpy.ops.object.camera_add(location=v((-2.3,1.9,2.85)));cam=bpy.context.object;cam.name='CAM_main';cam.data.lens=48
cam.rotation_euler=(v((0,.10,0))-cam.location).to_track_quat('-Z','Y').to_euler();scene.camera=cam
bpy.ops.object.light_add(type='SUN',location=v((-2,3,2)));key=bpy.context.object;key.name='LGT_key';key.data.energy=2.2;key.data.angle=.3;key.rotation_euler=(v((0,.08,0))-key.location).to_track_quat('-Z','Y').to_euler()
bpy.ops.object.light_add(type='POINT',location=v((1.2,1.8,1)));fill=bpy.context.object;fill.name='LGT_fill';fill.data.energy=170;fill.data.shadow_soft_size=.8
scene.world=bpy.data.worlds.new('Studio_world')
scene.world.use_nodes=True
scene.world.node_tree.nodes['Background'].inputs['Color'].default_value=(.16,.16,.16,1)
scene.world.node_tree.nodes['Background'].inputs['Strength'].default_value=.45
scene.render.engine='BLENDER_EEVEE';scene.render.resolution_x=1000;scene.render.resolution_y=700;scene.render.resolution_percentage=100
scene.render.image_settings.media_type='IMAGE';scene.render.image_settings.file_format='PNG'
scene.view_settings.view_transform='AgX'
result={'objects':len(bpy.data.objects),'hero_wafer_diameter_m':.3,'probe_tip_y':.115,'camera':cam.name,'roles':['STATIC','STAGE','PROBE','ROBOT','ALIGNER','WAFER'],'bounds_m':[1.8,1.0,1.08]}
