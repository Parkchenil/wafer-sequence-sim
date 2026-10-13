using Core.Visualization;
using Contracts;
using System.Numerics;
namespace Tests.Integration;
public class PrecisionSceneTests
{
    private static EquipmentSnapshot Snapshot(double x=0,double y=0,double z=0,WaferLocation location=WaferLocation.Chuck)
    {
        var stats=new RunStats(0,1,TimeSpan.Zero,null,null,new Dictionary<PhaseKind,TimeSpan>(),0,0,4);
        return new EquipmentSnapshot(TimeSpan.Zero,EquipmentState.Running,[],[new WaferTrack("W01",1,location,WaferStatus.Processing,0,1)],new MotionSnapshot(x,y,z,false,true,true,true,true,false,false,0),stats,[],"W01",0,ConceptPresets.ThirteenSites);
    }
    [Fact] public void EmbeddedGlb_HasNamedGeometryAndMaterial()
    {
        var scene=PrecisionScene.LoadEmbedded();
        Assert.Contains(scene.Parts,p=>p.Name=="WAFER_template");
        Assert.Contains(scene.Parts,p=>p.Name=="STAGE_chuck");
        Assert.Contains(scene.Parts,p=>p.Name=="ROBOT_blade");
        Assert.Contains(scene.Parts.SelectMany(p=>p.Faces),f=>f.Normals is {Length:3} && f.Roughness<1 && f.Metallic>0);
    }
    [Fact] public void StageAndProbe_MoveInMetres_WithoutMovingCabinet()
    {
        var scene=PrecisionScene.LoadEmbedded();var a=scene.Build(Snapshot(),true,false,300);
        var b=scene.Build(Snapshot(100000,50000,100),true,false,300);
        Vector3 First(IReadOnlyList<Face3D> fs,string name)=>Vector3.Transform(fs.First(f=>f.Part==name).Vertices[0],fs.First(f=>f.Part==name).Transform);
        Assert.Equal(-.1f,First(b,"STAGE_chuck").X-First(a,"STAGE_chuck").X,3);
        Assert.Equal(.05f,First(b,"STAGE_chuck").Z-First(a,"STAGE_chuck").Z,3);
        Assert.Equal(.01f,First(b,"PROBE_holder").Y-First(a,"PROBE_holder").Y,3);
        Assert.Equal(First(a,"STATIC_bed"),First(b,"STATIC_bed"));
    }
    [Fact] public void WaferSizeAndCarrierRemainConsistent()
    {
        var scene=PrecisionScene.LoadEmbedded();
        var chuck=scene.Build(Snapshot(),true,false,200).Where(f=>f.Part=="wafer:W01").ToArray();
        var slot=scene.Build(Snapshot(location:WaferLocation.LoadPort),true,false,200).Where(f=>f.Part=="wafer:W01").ToArray();
        Assert.Equal(chuck.Length,slot.Length);
        var chuckSpan=chuck.SelectMany(f=>f.Vertices.Select(v=>Vector3.Transform(v,f.Transform))).Max(v=>v.X)-chuck.SelectMany(f=>f.Vertices.Select(v=>Vector3.Transform(v,f.Transform))).Min(v=>v.X);
        var slotSpan=slot.SelectMany(f=>f.Vertices.Select(v=>Vector3.Transform(v,f.Transform))).Max(v=>v.X)-slot.SelectMany(f=>f.Vertices.Select(v=>Vector3.Transform(v,f.Transform))).Min(v=>v.X);
        Assert.Equal(chuckSpan,slotSpan,4);
    }
    [Fact] public void RobotSegmentsRemainRigid_AndWaferFollowsBlade()
    {
        var scene=PrecisionScene.LoadEmbedded();
        var step=new StepEvent(1,"W01",null,ModuleId.Robot,PhaseKind.PlaceOnChuck,TimeSpan.Zero,null,false,null,"");
        var sample=Snapshot(location:WaferLocation.RobotArm);
        var a=scene.Build(sample,true,false,300,step,TimeSpan.FromSeconds(2));
        var b=scene.Build(sample with {SimTime=TimeSpan.FromSeconds(2)},true,false,300,step,TimeSpan.FromSeconds(2));
        float Extent(IReadOnlyList<Face3D> fs,string name)
        {
            var points=fs.Where(f=>f.Part==name).SelectMany(f=>f.Vertices.Select(v=>Vector3.Transform(v,f.Transform))).ToArray();
            return Vector3.Distance(points[0],points[1]);
        }
        Assert.Equal(Extent(a,"ROBOT_upper"),Extent(b,"ROBOT_upper"),4);
        var waferA=Vector3.Transform(a.First(f=>f.Part=="wafer:W01").Vertices[0],a.First(f=>f.Part=="wafer:W01").Transform);
        var waferB=Vector3.Transform(b.First(f=>f.Part=="wafer:W01").Vertices[0],b.First(f=>f.Part=="wafer:W01").Transform);
        Assert.True(Vector3.Distance(waferA,waferB)>.1f);
    }
    [Fact] public void TransportWafer_RestsAbovePadsAndJoints_ThroughoutTransfer()
    {
        var scene=PrecisionScene.LoadEmbedded();
        foreach(var phase in new[]{PhaseKind.PickFromSlot,PhaseKind.Align,PhaseKind.PlaceOnChuck,PhaseKind.ReturnToSlot})
        foreach(var seconds in new[]{0,1,2})
        {
            var sample=Snapshot(location:WaferLocation.RobotArm) with {SimTime=TimeSpan.FromSeconds(seconds)};
            var step=new StepEvent(1,"W01",null,ModuleId.Robot,phase,TimeSpan.Zero,null,false,null,"");
            var faces=scene.Build(sample,true,false,300,step,TimeSpan.FromSeconds(2));
            var waferBottom=faces.Where(f=>f.Part=="wafer:W01").SelectMany(f=>f.Vertices.Select(v=>Vector3.Transform(v,f.Transform))).Min(v=>v.Y);
            var carrierTop=faces.Where(f=>f.Part.StartsWith("ROBOT_",StringComparison.Ordinal)).SelectMany(f=>f.Vertices.Select(v=>Vector3.Transform(v,f.Transform))).Max(v=>v.Y);
            Assert.True(waferBottom-carrierTop>=-.000001f && waferBottom-carrierTop<=.00001f, $"{phase} {seconds}: wafer={waferBottom}, " + string.Join(", ",faces.Where(f=>f.Part.StartsWith("ROBOT_",StringComparison.Ordinal)).GroupBy(f=>f.Part).Select(g=>$"{g.Key}={g.SelectMany(f=>f.Vertices.Select(v=>Vector3.Transform(v,f.Transform))).Max(v=>v.Y)}")));
        }
    }
}
