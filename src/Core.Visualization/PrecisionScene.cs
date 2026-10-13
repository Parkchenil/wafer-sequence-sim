using Contracts;
using System.Numerics;
namespace Core.Visualization;

public sealed class PrecisionScene
{
    private readonly GlbScene scene;
    private readonly Face3D[] fixedFaces;
    public PrecisionScene(GlbScene scene)
    {
        this.scene=scene;
        fixedFaces=scene.Parts.Where(p=>p.Name.StartsWith("STATIC_",StringComparison.Ordinal))
            .SelectMany(p=>p.Faces).ToArray();
    }
    public static PrecisionScene LoadEmbedded()
    {
        using var stream=typeof(PrecisionScene).Assembly.GetManifestResourceStream("Core.Visualization.precision-assembly.glb")
            ?? throw new FileNotFoundException("내장 precision-assembly.glb 리소스 누락");
        return new PrecisionScene(GlbScene.Load(stream));
    }
    public IReadOnlyList<GlbPart> Parts=>scene.Parts;
    public IReadOnlyList<Face3D> Build(EquipmentSnapshot snapshot,bool cutaway,bool closeUp,double waferDiameterMm,StepEvent? robotStep=null,TimeSpan? robotMoveTime=null)
    {
        var output=new List<Face3D>(fixedFaces.Length+2048);
        if(!closeUp)output.AddRange(fixedFaces);
        var stage=new Vector3(-(float)snapshot.Motion.StageXUm*1e-6f,0,(float)snapshot.Motion.StageYUm*1e-6f);
        var waferScale=(float)(waferDiameterMm/300);
        var blade=BladePoint(snapshot,robotStep,robotMoveTime??TimeSpan.FromSeconds(2.5));
        var pivot=new Vector3(-.30f,.075f,-.18f);
        var radial=blade-pivot;radial.Y=0;
        radial=radial.LengthSquared()<1e-8f?Vector3.UnitX:Vector3.Normalize(radial);
        var wrist=blade-radial*.09f;
        var planarWrist=new Vector3(wrist.X,pivot.Y,wrist.Z);
        var delta=planarWrist-pivot;var reach=Math.Min(delta.Length(),.7199f);
        var direction=delta.LengthSquared()<1e-8f?Vector3.UnitX:Vector3.Normalize(delta);
        var lateral=Vector3.Normalize(new Vector3(-direction.Z,0,direction.X));
        var elbow=pivot+direction*(reach*.5f)+lateral*MathF.Sqrt(.36f*.36f-reach*reach*.25f);
        foreach(var part in scene.Parts)
        {
            var name=part.Name;
            if(name.StartsWith("STATIC_",StringComparison.Ordinal))continue;
            if(name.StartsWith("LAMP_",StringComparison.Ordinal))
            {
                if(!closeUp)
                {
                    var color=snapshot.State==EquipmentState.Alarm?new Rgb(240,50,35):snapshot.State==EquipmentState.Running?new Rgb(48,220,105):new Rgb(230,170,45);
                    output.AddRange(part.Faces.Select(face=>face with {Color=color,LinearColor=false}));
                }
                continue;
            }
            if(name.StartsWith("COVER_",StringComparison.Ordinal)){if(!cutaway&&!closeUp)output.AddRange(part.Faces);continue;}
            if(name.StartsWith("WAFER_template",StringComparison.Ordinal))
            {
                foreach(var wafer in snapshot.Wafers)
                {
                    Vector3? center=wafer.Location switch
                    {
                        WaferLocation.Chuck=>new Vector3(.35f,.1145f,0)+stage,
                        WaferLocation.LoadPort or WaferLocation.Done when !closeUp=>new Vector3(-.69f,.0255f+(Math.Clamp(wafer.Slot,1,25)-1)*.006f,.28f),
                        WaferLocation.Aligner when !closeUp=>new Vector3(-.12f,.111f,.34f),
                        WaferLocation.RobotArm when !closeUp=>blade+new Vector3(0,.0075f,0),
                        _=>null
                    };
                    if(center is { } target)Copy(part.Faces,Matrix4x4.CreateTranslation(-.35f,-.1145f,0)*Matrix4x4.CreateScale(waferScale)*Matrix4x4.CreateTranslation(target),"wafer:"+wafer.WaferId);
                }
                continue;
            }
            if(name.StartsWith("STAGE_",StringComparison.Ordinal)){Copy(part.Faces,Matrix4x4.CreateTranslation(stage),name);continue;}
            if(name.StartsWith("PROBE_",StringComparison.Ordinal)){Copy(part.Faces,Matrix4x4.CreateTranslation(0,(float)snapshot.Motion.ZUm*.0001f,0),name);continue;}
            if(name.StartsWith("ALIGNER_rotor",StringComparison.Ordinal))
            {
                var angle=(float)(snapshot.Motion.AlignerAngleDeg*Math.PI/180);
                Copy(part.Faces,Matrix4x4.CreateTranslation(.12f,-.101f,-.34f)*Matrix4x4.CreateRotationY(angle)*Matrix4x4.CreateTranslation(-.12f,.101f,.34f),name);continue;
            }
            if(name.StartsWith("ROBOT_",StringComparison.Ordinal))
            {
                if(closeUp)continue;
                var upper=name.StartsWith("ROBOT_upper",StringComparison.Ordinal);
                var lower=name.StartsWith("ROBOT_lower",StringComparison.Ordinal);
                var lowerElbow=new Vector3(elbow.X,.09f,elbow.Z);
                var lowerWrist=new Vector3(wrist.X,.09f,wrist.Z);
                var start=upper?pivot:lower?lowerElbow:wrist;
                var end=upper?elbow:lower?lowerWrist:wrist+radial*.18f;
                var origin=new Vector3(part.World.M41,part.World.M42,part.World.M43);
                var length=(end-start).Length();
                var segmentDirection=length>.00001f?Vector3.Normalize(end-start):Vector3.UnitX;
                var rotation=RotationFromX(segmentDirection);
                var move=Matrix4x4.CreateTranslation(-origin)*rotation*Matrix4x4.CreateTranslation(start);
                Copy(part.Faces,move,name);continue;
            }
            throw new NotSupportedException("분류되지 않은 GLB 부품: "+name);
        }
        return output;
        void Copy(Face3D[] source,Matrix4x4 transform,string name)
        {
            foreach(var face in source)output.Add(face with {Transform=transform,Part=name});
        }
    }
    public static Vector3 BladePoint(EquipmentSnapshot snapshot,StepEvent? step,TimeSpan duration)
    {
        var home=new Vector3(-.30f,.13f,.03f);
        if(step is null||step.Module!=ModuleId.Robot)return home;
        var cassette=new Vector3(-.69f,.13f,.28f);var aligner=new Vector3(-.12f,.13f,.34f);var chuck=new Vector3(.35f,.13f,0);
        var from=step.Phase switch{PhaseKind.PlaceOnChuck=>aligner,PhaseKind.ReturnToSlot=>chuck,PhaseKind.Align when step.Note.Contains("놓기")=>cassette,_=>home};
        var to=step.Phase switch{PhaseKind.PickFromSlot or PhaseKind.ReturnToSlot=>cassette,PhaseKind.Align=>aligner,PhaseKind.PlaceOnChuck or PhaseKind.PickFromChuck=>chuck,_=>from};
        var t=(float)Math.Clamp((snapshot.SimTime-step.Start).TotalSeconds/Math.Max(.001,duration.TotalSeconds),0,1);t=t*t*(3-2*t);
        return Vector3.Lerp(from,to,t);
    }
    private static Matrix4x4 RotationFromX(Vector3 direction)
    {
        var dot=Math.Clamp(Vector3.Dot(Vector3.UnitX,direction),-1,1);
        if(dot>.99999f)return Matrix4x4.Identity;
        if(dot<-.99999f)return Matrix4x4.CreateRotationY(MathF.PI);
        return Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(Vector3.UnitX,direction)),MathF.Acos(dot));
    }
}
