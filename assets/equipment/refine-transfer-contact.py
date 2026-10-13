import bpy
scene=bpy.context.scene
for name in ['ROBOT_upper','ROBOT_lower']:
 o=bpy.data.objects[name]
 for v in o.data.vertices: v.co.z*=.2
 for m in o.modifiers:
  if m.type=='BEVEL': m.width=.001;m.segments=1
# 기준 블레이드 높이는 .13. 캐리지는 어깨 .118을 따른다.
for name,z,h in [('CARRIAGE_robot_slide',.118,.014),('CARRIAGE_robot_shoulder',.118,.010),('JOINT_elbow_bridge',.121,.008),('JOINT_wrist_riser',.127,.006)]:
 o=bpy.data.objects[name];o.location.z=z;o.dimensions.z=h
bpy.data.objects['STATIC_robot_lift_column'].location.z=.125
bpy.data.objects['STATIC_robot_lift_column'].dimensions.z=.25
for o in bpy.data.objects:
 if o.name.startswith('STATIC_robot_lift_guide'):o.location.z=.125;o.dimensions.z=.24
bpy.data.objects['STATIC_cassette_back'].name='COVER_cassette_back'
scene.camera.data.ortho_scale=1.55
result={'arm_vertical_scale':.2,'shoulder_offset_m':-.012,'lower_offset_m':-.006,'cassette_back':'COVER_cassette_back'}

# revision 7: 원격 기준 자세에서도 승강 캐리지와 관절의 높이를 연결한다.
bpy.data.objects['ROBOT_upper'].location.z=.118
bpy.data.objects['ROBOT_lower'].location.z=.124
