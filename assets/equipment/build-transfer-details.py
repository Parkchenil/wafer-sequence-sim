# 기존 revision 4에 추가하는 반송 기구. 객체 삭제 없이 수정·추가만 한다.
import bpy, math
from mathutils import Vector
scene=bpy.context.scene
def v(p): return Vector((p[0],-p[2],p[1]))
steel=bpy.data.materials['machined_aluminium']; dark=bpy.data.materials['anodised_graphite']; blue=bpy.data.materials['axis_blue']; gold=bpy.data.materials['probe_gold']
def box(name,p,size,mat):
 bpy.ops.mesh.primitive_cube_add(size=1,location=v(p));o=bpy.context.object;o.name=name;o.dimensions=(size[0],size[2],size[1]);bpy.ops.object.transform_apply(location=False,rotation=False,scale=True);o.data.materials.append(mat);return o
# 6 mm 슬롯 피치 안에서 읽히는 얇은 블레이드. 지지 패드 상면은 원점 +2 mm.
o=bpy.data.objects['ROBOT_blade']
for vertex in o.data.vertices: vertex.co.z*=2/7
for mod in o.modifiers:
 if mod.type=='BEVEL': mod.width=.0003;mod.segments=1
box('STATIC_robot_lift_column',(-.30,.06,-.245),(.095,.36,.05),dark)
for x in [-.334,-.266]:box('STATIC_robot_lift_guide_'+str(x),(x,.06,-.21),(.010,.34,.012),steel)
# CARRIAGE는 앱에서 Y만 이동. 원점 높이는 기준 blade Y=.13에 대응한다.
box('CARRIAGE_robot_slide',(-.30,.075,-.198),(.092,.060,.030),blue)
box('CARRIAGE_robot_shoulder',(-.30,.072,-.18),(.075,.036,.075),steel)
# 각 관절의 독립 메시: 앱이 같은 관절좌표로 매핑한다.
box('JOINT_elbow_bridge',(.06,.085,-.18),(.032,.030,.036),dark)
box('JOINT_wrist_riser',(.42,.110,-.18),(.027,.040,.038),steel)
# 얼라이너 패드는 상면 .120 m. 블레이드 진입 높이와 분리한다.
for i,a in enumerate([30,150,270]):
 r=math.radians(a);box('ALIGNER_rotor_pad_'+str(i),(-.12+.105*math.cos(r),.1155,.34+.105*math.sin(r)),(.024,.009,.020),dark)
box('STATIC_aligner_sensor_post',(.046,.135,.34),(.012,.070,.026),dark)
box('STATIC_aligner_sensor_lens',(.037,.144,.34),(.006,.009,.015),gold)
box('STATIC_foup_mapping_sensor',(-.505,.115,.102),(.020,.190,.018),blue)
# 슬롯 1/5/10/15/20/25 식별용 표시. 촘촘한 텍스트 대신 기하 눈금만 추가한다.
for n in [0,4,9,14,19,24]:box('STATIC_slot_marker_'+str(n),(-.515,.024+n*.006,.117),(.014,.0015,.003),gold)
# 반송부 검수 카메라. 기존 카메라 객체를 유지한다.
cam=bpy.data.objects['CAM_main'];cam.location=v((-1.48,.98,1.30));cam.rotation_euler=(v((-.36,.10,.16))-cam.location).to_track_quat('-Z','Y').to_euler();cam.data.type='ORTHO';cam.data.ortho_scale=1.35;scene.camera=cam
scene.render.engine='BLENDER_EEVEE';scene.render.resolution_x=960;scene.render.resolution_y=720;scene.render.resolution_percentage=100
result={'pad_top_m':.002,'slot_bottom_m':.024,'aligner_bottom_m':.120,'objects':len(bpy.data.objects),'added_roles':['CARRIAGE','JOINT']}
