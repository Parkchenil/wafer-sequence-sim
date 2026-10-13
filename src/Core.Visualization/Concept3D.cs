using Contracts;
using System.Numerics;

namespace Core.Visualization;

// 월드 단위는 개념 배치 단위다. 웨이퍼 지름은 월드 1.72에 대응한다.
public readonly record struct Rgb(byte R, byte G, byte B);
public sealed record Face3D(Vector3[] Vertices, Rgb Color, string Part)
{
    public Vector3[]? Normals { get; init; }
    public float Metallic { get; init; }
    public float Roughness { get; init; } = 1;
    public bool LinearColor { get; init; }
    public Matrix4x4 Transform { get; init; } = Matrix4x4.Identity;
}
public sealed record ProjectedFace(Vector2[] Vertices, Rgb Color, float Depth, string Part);
public readonly record struct Camera3D(float Yaw, float Pitch, float Zoom)
{
    public Vector3? Target { get; init; }
    public static Camera3D Default => new(-0.65f, 0.48f, 1);
    public Camera3D Clamp()
    {
        var yaw=float.IsFinite(Yaw)?MathF.IEEERemainder(Yaw,2*MathF.PI):0;
        var pitch=float.IsFinite(Pitch)?Math.Clamp(Pitch,-.05f,1.35f):.48f;
        var zoom=float.IsFinite(Zoom)?Math.Clamp(Zoom,.45f,3.5f):1;
        return new Camera3D(yaw,pitch,zoom) { Target=Target };
    }
}
public static class Concept3D
{
    public const float WaferRadius = .86f, WaferSurfaceY = .5025f, ProbePinLength = .3f, ProbePinOffsetY = -.27f;
    public const float ZDisplayMagnification = 100f;
    public static readonly Vector3 ChuckHome = new(.85f, .22f, 0);
    public static readonly Vector3 LoadPoint = new(-2.05f, .54f, .58f);
    public static readonly Vector3 AlignerPoint = new(-.58f, .44f, 1f);
    public static readonly Vector3 RobotHome = new(-.82f, .54f, -.56f);
    private static readonly Rgb Cabinet = new(190, 205, 213), Steel = new(73, 103, 123), Dark = new(34, 53, 67), Blue = new(65, 171, 211), Gold = new(229, 185, 77), Wafer = new(137, 185, 199);
    public static float WorldPerUm(double waferDiameterMm) => 2 * WaferRadius / (float)(Math.Max(1, waferDiameterMm) * 1000);
    public static Vector3 StageCenter(MotionSnapshot motion, double waferDiameterMm)
    {
        var scale = WorldPerUm(waferDiameterMm);
        return ChuckHome + new Vector3(-(float)motion.StageXUm * scale, 0, (float)motion.StageYUm * scale);
    }
    public static Vector3 SiteWorld(MotionSnapshot motion, SitePoint site, double waferDiameterMm)
    {
        var scale = WorldPerUm(waferDiameterMm);
        return StageCenter(motion, waferDiameterMm) + new Vector3((float)site.XUm * scale, WaferSurfaceY - ChuckHome.Y + .01f, -(float)site.YUm * scale);
    }
    public static float ProbeTipY(double zUm, double waferDiameterMm) => WaferSurfaceY + Math.Max(0, (float)zUm) * WorldPerUm(waferDiameterMm) * ZDisplayMagnification;
    public static float ProbeHeadY(double zUm, double waferDiameterMm) => ProbeTipY(zUm, waferDiameterMm) - ProbePinOffsetY + ProbePinLength / 2;
    public static Vector3 RobotTarget(StepEvent? step)
    {
        if(step is null)return RobotHome;
        return step.Phase switch
        {
            PhaseKind.PickFromSlot or PhaseKind.ReturnToSlot => LoadPoint,
            PhaseKind.Align => AlignerPoint,
            PhaseKind.PlaceOnChuck or PhaseKind.PickFromChuck => ChuckHome+new Vector3(0,.35f,0),
            _ => RobotHome
        };
    }
    public static Vector3 RobotSource(StepEvent? step)
    {
        if(step is null)return RobotHome;
        return step.Phase switch
        {
            PhaseKind.Align when step.Note.Contains("놓기") => LoadPoint,
            PhaseKind.PlaceOnChuck => AlignerPoint,
            PhaseKind.ReturnToSlot => ChuckHome+new Vector3(0,.35f,0),
            _ => RobotHome
        };
    }
    public static Vector3 RobotBladePoint(EquipmentSnapshot snapshot,StepEvent? activeStep,TimeSpan robotMoveTime)
    {
        if(activeStep is null||activeStep.Module!=ModuleId.Robot)return RobotHome;
        var elapsed=Math.Clamp((snapshot.SimTime-activeStep.Start).TotalSeconds/Math.Max(.001,robotMoveTime.TotalSeconds),0,1);
        // 관절 좌표가 없는 엔진의 소유권과 단계 시간을 기준으로 한 추정 경로.
        var eased=(float)(elapsed*elapsed*(3-2*elapsed));
        return Vector3.Lerp(RobotSource(activeStep),RobotTarget(activeStep),eased);
    }
    public static IReadOnlyList<Face3D> Build(EquipmentSnapshot snapshot, bool cutaway = true, bool closeUp = false, double waferDiameterMm = 300, StepEvent? activeStep = null, TimeSpan? robotMoveTime = null)
    {
        var faces = new List<Face3D>();
        void Box(string part, Vector3 center, Vector3 size, Rgb color)
        {
            var h=size/2;var x=center.X;var y=center.Y;var z=center.Z;
            var v=new[]{new Vector3(x-h.X,y-h.Y,z-h.Z),new Vector3(x+h.X,y-h.Y,z-h.Z),new Vector3(x+h.X,y+h.Y,z-h.Z),new Vector3(x-h.X,y+h.Y,z-h.Z),new Vector3(x-h.X,y-h.Y,z+h.Z),new Vector3(x+h.X,y-h.Y,z+h.Z),new Vector3(x+h.X,y+h.Y,z+h.Z),new Vector3(x-h.X,y+h.Y,z+h.Z)};
            foreach(var ids in new[]{new[]{0,3,2,1},new[]{4,5,6,7},new[]{0,1,5,4},new[]{1,2,6,5},new[]{2,3,7,6},new[]{3,0,4,7}})faces.Add(new Face3D(ids.Select(i=>v[i]).ToArray(),color,part));
        }
        void Cylinder(string part, Vector3 center, float radius, float height, Rgb color, int segments=20)
        {
            var low=Enumerable.Range(0,segments).Select(i=>center+new Vector3(radius*MathF.Cos(i*2*MathF.PI/segments),-height/2,radius*MathF.Sin(i*2*MathF.PI/segments))).ToArray();
            var high=low.Select(p=>p+new Vector3(0,height,0)).ToArray();faces.Add(new Face3D(high,color,part));
            for(var i=0;i<segments;i++){var j=(i+1)%segments;faces.Add(new Face3D([low[i],low[j],high[j],high[i]],color,part));}
        }
        void Rod(string part, Vector3 a, Vector3 b, float width, Rgb color)
        {
            var d=b-a;if(d.Length()<.001f)return;var axis=Vector3.Normalize(d);
            var side=Vector3.Normalize(Vector3.Cross(axis,MathF.Abs(axis.Y)<.9f?Vector3.UnitY:Vector3.UnitX))*width;
            var up=Vector3.Normalize(Vector3.Cross(axis,side))*width;
            var v=new[]{a-side-up,a+side-up,a+side+up,a-side+up,b-side-up,b+side-up,b+side+up,b-side+up};
            foreach(var ids in new[]{new[]{0,1,5,4},new[]{1,2,6,5},new[]{2,3,7,6},new[]{3,0,4,7},new[]{4,5,6,7}})faces.Add(new Face3D(ids.Select(i=>v[i]).ToArray(),color,part));
        }
        var stage=StageCenter(snapshot.Motion,waferDiameterMm);
        if(!closeUp)
        {
            Box("base",new(0,-.09f,0),new(5.2f,.18f,3.3f),Steel);
            for(var x=-1;x<=1;x+=2)for(var z=-1;z<=1;z+=2)Box("frame",new(x*2.5f,1.15f,z*1.55f),new(.07f,2.3f,.07f),Cabinet);
            if(!cutaway)
            {
                Box("roof",new(0,2.32f,0),new(5.2f,.13f,3.3f),Cabinet);
                Box("side",new(-2.58f,1.1f,0),new(.09f,2.2f,3.3f),Cabinet);
                Box("front",new(0,1.1f,1.62f),new(5.2f,2.2f,.08f),Cabinet);
            }
            else
            {
                // 절개 상태에서는 얇은 상부 레일만 남긴다.
                Box("top rail",new(0,2.3f,-1.55f),new(5.1f,.06f,.06f),Cabinet);
                Box("top rail",new(2.5f,2.3f,0),new(.06f,.06f,3.1f),Cabinet);
            }
            Box("load port",new(-2.05f,.8f,.58f),new(.72f,1.6f,.84f),Dark);
            var blade=RobotBladePoint(snapshot,activeStep,robotMoveTime??TimeSpan.FromSeconds(2.5));
            var pivot=new Vector3(-1.18f,.54f,-.15f);var elbow=Vector3.Lerp(pivot,blade,.55f)+new Vector3(0,0,-.2f);
            Cylinder("robot joint",pivot,.16f,.24f,Steel);Rod("robot arm",pivot,elbow,.065f,Blue);Cylinder("robot joint",elbow,.10f,.18f,Steel);Rod("robot blade",elbow,blade,.075f,Blue);
            Cylinder("aligner",new(-.58f,.32f,1f),.4f,.16f,Steel);
            var angle=(float)(snapshot.Motion.AlignerAngleDeg*Math.PI/180);
            Rod("aligner index",AlignerPoint,new Vector3(-.58f+.32f*MathF.Cos(angle),.44f,1f+.32f*MathF.Sin(angle)),.018f,Gold);
            Box("operator panel",new(2.35f,1.55f,.95f),new(.14f,.52f,.5f),Dark);
            Cylinder("status lamp",new(2.2f,2.53f,1.15f),.08f,.25f,snapshot.State==EquipmentState.Alarm?new Rgb(225,76,63):snapshot.State==EquipmentState.Running?new Rgb(79,211,135):Gold);
            for(var i=0;i<25;i++)
            {
                var y=.18f+i*.05f;
                Box("cassette slot",new(-2.05f,y,.58f),new(.58f,.008f,.66f),Steel);
            }
            foreach(var wafer in snapshot.Wafers.Where(w=>w.Location is WaferLocation.LoadPort or WaferLocation.Done))
            {
                var y=.18f+(Math.Clamp(wafer.Slot,1,25)-1)*.05f+.013f;
                Cylinder("wafer:"+wafer.WaferId,new(-2.05f,y,.58f),.28f,.012f,wafer.Location==WaferLocation.Done?new Rgb(91,197,140):Wafer,16);
            }
            foreach(var wafer in snapshot.Wafers.Where(w=>w.Location==WaferLocation.RobotArm))Cylinder("wafer:"+wafer.WaferId,blade+new Vector3(0,.03f,0),.3f,.015f,Wafer,20);
            foreach(var wafer in snapshot.Wafers.Where(w=>w.Location==WaferLocation.Aligner))Cylinder("wafer:"+wafer.WaferId,AlignerPoint,.38f,.025f,Wafer,24);
        }
        if(closeUp)
        {
            foreach(var wafer in snapshot.Wafers.Where(w=>w.Location is WaferLocation.LoadPort or WaferLocation.Done))
                Cylinder("wafer:"+wafer.WaferId,new(-2.05f,.193f+(Math.Clamp(wafer.Slot,1,25)-1)*.05f,.58f),.28f,.012f,Wafer,16);
            foreach(var wafer in snapshot.Wafers.Where(w=>w.Location==WaferLocation.RobotArm))
                Cylinder("wafer:"+wafer.WaferId,RobotBladePoint(snapshot,activeStep,robotMoveTime??TimeSpan.FromSeconds(2.5))+new Vector3(0,.03f,0),.3f,.015f,Wafer,20);
            foreach(var wafer in snapshot.Wafers.Where(w=>w.Location==WaferLocation.Aligner))
                Cylinder("wafer:"+wafer.WaferId,AlignerPoint,.38f,.025f,Wafer,24);
        }
        Box("XY stage",stage,new(2.25f,.24f,2.25f),Dark);
        Cylinder("vacuum chuck",stage+new Vector3(0,.18f,0),.93f,.12f,snapshot.Motion.VacuumConfirmed?Blue:Steel);
        foreach(var wafer in snapshot.Wafers.Where(w=>w.Location==WaferLocation.Chuck))Cylinder("wafer:"+wafer.WaferId,stage+new Vector3(0,.27f,0),WaferRadius,.025f,Wafer,32);
        if(snapshot.Wafers.Any(w=>w.Location==WaferLocation.Chuck))
            for(var i=0;i<snapshot.Sites.Count;i++)Cylinder("measurement site",SiteWorld(snapshot.Motion,snapshot.Sites[i],waferDiameterMm),.022f,.008f,i==snapshot.CurrentSiteIndex?Gold:Blue,8);
        var head=new Vector3(ChuckHome.X,ProbeHeadY(snapshot.Motion.ZUm,waferDiameterMm),ChuckHome.Z);
        Box("Z probe head",head,new(.5f,.32f,.45f),Steel);
        for(var i=0;i<4;i++)Cylinder("probe pin",head+new Vector3((i-1.5f)*.09f,ProbePinOffsetY,0),.018f,ProbePinLength,snapshot.Motion.ProbeInContact?Gold:Cabinet,8);
        Box("camera",new(1.55f,1.55f,-.75f),new(.35f,.55f,.35f),Dark);
        Cylinder("camera lens",new(1.55f,1.22f,-.75f),.12f,.09f,snapshot.Motion.CameraFocused?Blue:Steel);
        return faces;
    }
    public static Vector2? ProjectPoint(Vector3 point,Camera3D camera,float width,float height,bool closeUp=false)
    {
        var c=camera.Clamp();var target=closeUp?new Vector3(.85f,.65f,0):new Vector3(0,.75f,0);
        var eye=new Vector3(MathF.Sin(c.Yaw)*7,3+MathF.Sin(c.Pitch)*6,MathF.Cos(c.Yaw)*7)+target;
        var view=Matrix4x4.CreateLookAt(eye,target,Vector3.UnitY);
        var fov=.85f/(c.Zoom*(closeUp?1.65f:1f));
        var projection=Matrix4x4.CreatePerspectiveFieldOfView(Math.Clamp(fov,.12f,2.3f),Math.Max(.1f,width/Math.Max(1,height)),.1f,100);
        var v=Vector4.Transform(new Vector4(point,1),view*projection);
        if(v.W<=.01f)return null;
        var p=new Vector2((v.X/v.W+1)*width/2,(1-v.Y/v.W)*height/2);
        return float.IsFinite(p.X)&&float.IsFinite(p.Y)?p:null;
    }
    public static IReadOnlyList<ProjectedFace> Project(IReadOnlyList<Face3D> faces,Camera3D camera,float width,float height,bool closeUp=false)
    {
        var result=new List<ProjectedFace>();var c=camera.Clamp();
        var target=closeUp?new Vector3(.85f,.65f,0):new Vector3(0,.75f,0);
        var eye=new Vector3(MathF.Sin(c.Yaw)*7,3+MathF.Sin(c.Pitch)*6,MathF.Cos(c.Yaw)*7)+target;
        var view=Matrix4x4.CreateLookAt(eye,target,Vector3.UnitY);
        var fov=.85f/(c.Zoom*(closeUp?1.65f:1f));
        var projection=Matrix4x4.CreatePerspectiveFieldOfView(Math.Clamp(fov,.12f,2.3f),Math.Max(.1f,width/Math.Max(1,height)),.1f,100);
        var transform=view*projection;
        foreach(var face in faces)
        {
            var points=new Vector2[face.Vertices.Length];float depth=0;var valid=true;
            for(var i=0;i<points.Length;i++)
            {
                var v=Vector4.Transform(new Vector4(face.Vertices[i],1),transform);
                if(v.W<=.01f){valid=false;break;}
                points[i]=new Vector2((v.X/v.W+1)*width/2,(1-v.Y/v.W)*height/2);
                if(!float.IsFinite(points[i].X)||!float.IsFinite(points[i].Y)){valid=false;break;}
                depth+=Vector3.Transform(face.Vertices[i],view).Z;
            }
            if(!valid)continue;
            var a=face.Vertices[1]-face.Vertices[0];var b=face.Vertices[2]-face.Vertices[0];
            var normal=Vector3.Cross(a,b);var length=normal.Length();var light=length<.00001f?0f:MathF.Abs(Vector3.Dot(normal/length,Vector3.Normalize(new Vector3(.4f,.8f,.5f))));
            var shade=.48f+.52f*light;byte Lit(byte value)=>(byte)Math.Clamp((int)(value*shade),0,255);
            result.Add(new ProjectedFace(points,new Rgb(Lit(face.Color.R),Lit(face.Color.G),Lit(face.Color.B)),depth/points.Length,face.Part));
        }
        result.Sort((a,b)=>a.Depth.CompareTo(b.Depth));return result;
    }
}
