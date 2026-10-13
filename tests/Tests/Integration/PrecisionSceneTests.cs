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
    [Theory] [InlineData(WaferLocation.LoadPort,.022,-.69,.28)] [InlineData(WaferLocation.Aligner,.118,-.12,.34)]
    public void 인계순간_웨이퍼_메시는_위치와_두께가_연속이다(WaferLocation station,double y,double x,double z)
    {
        var scene=PrecisionScene.LoadEmbedded();
        var transport=new RobotTransportTelemetry(x,y,z,x,y,z,"접촉",1,.65,true,station==WaferLocation.Aligner);
        var a=scene.Build(Snapshot(location:station) with {RobotTransport=transport},true,false,200);
        var b=scene.Build(Snapshot(location:WaferLocation.RobotArm) with {RobotTransport=transport},true,false,200);
        Vector3[] Points(IReadOnlyList<Face3D> fs)=>fs.Where(f=>f.Part=="wafer:W01").SelectMany(f=>f.Vertices.Select(v=>Vector3.Transform(v,f.Transform))).ToArray();
        var before=Points(a);var after=Points(b);
        for(var i=0;i<before.Length;i++)Assert.True(Vector3.Distance(before[i],after[i])<.000001f);
        Assert.Equal(.001f,after.Max(v=>v.Y)-after.Min(v=>v.Y),6);
        var padTop=b.Where(f=>f.Part=="ROBOT_blade").SelectMany(f=>f.Vertices.Select(v=>Vector3.Transform(v,f.Transform))).Max(v=>v.Y);
        Assert.Equal(after.Min(v=>v.Y),padTop,6);
    }
    [Fact] public void 반송_스냅샷_재생은_시간변경에도_자세를_보존한다()
    {
        var replay=new RunReplay();var scene=PrecisionScene.LoadEmbedded();
        var state=new RobotTransportTelemetry(-.69,.022,.28,-.69,.022,.28,"상승 접촉",1,.65,true,false);
        var snapshot=Snapshot(location:WaferLocation.RobotArm) with {SimTime=TimeSpan.FromSeconds(1),RobotTransport=state};
        replay.Record(snapshot);replay.Record(snapshot with {SimTime=TimeSpan.FromSeconds(2),State=EquipmentState.Completed});
        var original=scene.Build(snapshot,true,false,300);var viewed=scene.Build(replay.ViewAt(TimeSpan.FromSeconds(1.5))!,true,false,300);
        Assert.Equal(original.Select(f=>f.Transform),viewed.Select(f=>f.Transform));
        Assert.Same(replay.LastFrame,replay.ViewAt(TimeSpan.FromSeconds(3)));
        Assert.Equal(TimeSpan.FromSeconds(2),replay.LastFrame!.SimTime);
    }
    [Theory] [InlineData(.022)] [InlineData(.094)] [InlineData(.166)]
    public void 승강시_캐리지와_관절은_같은거리만큼_움직인다(double y)
    {
        var scene=PrecisionScene.LoadEmbedded();
        EquipmentSnapshot At(double height)=>Snapshot(location:WaferLocation.RobotArm) with {RobotTransport=new(-.69,height,.28,-.69,height,.28,"접촉",1,.65,true,false)};
        var a=scene.Build(At(.13),true,false,300);var b=scene.Build(At(y),true,false,300);
        foreach(var name in new[]{"ROBOT_upper","ROBOT_lower","ROBOT_blade","CARRIAGE_robot_slide","JOINT_wrist_riser"})
        {
            var f=a.First(f=>f.Part==name);var g=b.First(f=>f.Part==name);
            Assert.Equal(y-.13,Vector3.Transform(g.Vertices[0],g.Transform).Y-Vector3.Transform(f.Vertices[0],f.Transform).Y,5);
        }
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
        var sample=Snapshot(location:WaferLocation.RobotArm) with {RobotTransport=new(-.3,.13,.03,-.3,.13,.03,"대기",1,0,true,false)};
        var a=scene.Build(sample,true,false,300,step,TimeSpan.FromSeconds(2));
        var b=scene.Build(sample with {SimTime=TimeSpan.FromSeconds(2),RobotTransport=sample.RobotTransport! with {XMetres=-.69,YMetres=.022,ZMetres=.28}},true,false,300,step,TimeSpan.FromSeconds(2));
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
