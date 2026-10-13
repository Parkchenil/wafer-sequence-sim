using Core.Visualization;
using Contracts;
using Core.Measurement;
using System.Numerics;
namespace Tests.Integration;
public class Concept3DReplayTests
{
    private static EquipmentSnapshot Snapshot(int second,int stage=0)
    {
        var stats=new RunStats(0,1,TimeSpan.FromSeconds(second),null,null,new Dictionary<PhaseKind,TimeSpan>(),0,0,4);
        return new EquipmentSnapshot(TimeSpan.FromSeconds(second),EquipmentState.Running,[],[new WaferTrack("W01",1,WaferLocation.Chuck,WaferStatus.Processing,0,13)],new MotionSnapshot(stage,0,300,false,true,true,true,true,false,false,0),stats,[],"W01",0,ConceptPresets.ThirteenSites);
    }
    [Fact] public void Projection_IsFinite_AtCameraLimits()
    {
        var faces=Concept3D.Build(Snapshot(0));Assert.Contains(faces,f=>f.Part=="probe pin");Assert.Contains(faces,f=>f.Part=="robot arm");
        foreach(var camera in new[]{new Camera3D(-99,-99,-99),new Camera3D(99,99,99),new Camera3D(float.NaN,float.PositiveInfinity,float.NegativeInfinity),Camera3D.Default})
        {
            var projected=Concept3D.Project(faces,camera,800,500);
            Assert.NotEmpty(projected);Assert.All(projected.SelectMany(f=>f.Vertices),p=>{Assert.True(float.IsFinite(p.X));Assert.True(float.IsFinite(p.Y));});
        }
    }
    [Fact] public void Replay_Seek_CopiesAndClipsEvents()
    {
        var replay=new RunReplay();var source=Snapshot(1);replay.Record(source);replay.Record(Snapshot(3,50000));
        replay.Record(new StepEvent(1,"W01",0,ModuleId.Stage,PhaseKind.MoveXY,TimeSpan.FromSeconds(1),TimeSpan.FromSeconds(3),false,null,""));
        Assert.Equal(TimeSpan.FromSeconds(1),replay.Seek(TimeSpan.FromSeconds(2))!.SimTime);
        Assert.NotSame(source.Wafers,replay.Seek(TimeSpan.FromSeconds(1))!.Wafers);
        Assert.NotSame(source.Sites,replay.Seek(TimeSpan.FromSeconds(1))!.Sites);
        Assert.NotSame(source.Stats.PhaseTotals,replay.Seek(TimeSpan.FromSeconds(1))!.Stats.PhaseTotals);
        Assert.Null(replay.VisibleEvents(TimeSpan.Zero).FirstOrDefault());
        Assert.Null(replay.VisibleEvents(TimeSpan.FromSeconds(2))[0].End);
        Assert.Equal(TimeSpan.FromSeconds(3),replay.VisibleEvents(TimeSpan.FromSeconds(3))[0].End);
        Assert.Equal(replay.Seek(TimeSpan.FromSeconds(2)),replay.Seek(TimeSpan.FromSeconds(3-1)));
        replay.Clear();Assert.Null(replay.Seek(TimeSpan.Zero));Assert.Empty(replay.VisibleEvents(TimeSpan.FromSeconds(9)));
    }
    [Fact] public void Presets_StayWithinWafer_AndImproveSupportedSettings()
    {
        var a=ConceptPresets.WaitingExample();var b=ConceptPresets.ImprovementExample();
        Assert.Equal(13,new SitePatternProvider().Build(a).Count);Assert.All(a.CustomSites,p=>Assert.True(Math.Sqrt(p.XUm*p.XUm+p.YUm*p.YUm)<147000));
        Assert.Equal(a.VacuumCheckCount,b.VacuumCheckCount);Assert.True(b.VacuumCheckWait<a.VacuumCheckWait);Assert.True(b.ZLiftAfterMeasureUm<a.ZLiftAfterMeasureUm);Assert.Equal(300,a.ZSafeHeightUm);Assert.Equal(a.ZSafeHeightUm,b.ZSafeHeightUm);Assert.True(a.ZLiftAfterMeasureUm>=a.ZSafeHeightUm);Assert.True(b.ZLiftAfterMeasureUm>=b.ZSafeHeightUm);Assert.Equal(MapInspectMode.FirstSiteOnly,b.MapInspect);Assert.False(b.UnloadOverlap);Assert.Equal(SaveMode.Async,b.Save);
    }
    [Fact] public void Stage_TargetSite_AndProbeContact_AreAligned()
    {
        const double diameter=200;
        var point=new SitePoint(50000,-25000);
        var motion=new MotionSnapshot(point.XUm,point.YUm,0,true,true,true,true,true,false,false,0);
        var site=Concept3D.SiteWorld(motion,point,diameter);
        Assert.Equal(Concept3D.ChuckHome.X,site.X,4);
        Assert.Equal(Concept3D.ChuckHome.Z,site.Z,4);
        Assert.Equal(Concept3D.WaferSurfaceY,Concept3D.ProbeTipY(0,diameter),5);
        Assert.Equal(Concept3D.WaferSurfaceY,Concept3D.ProbeHeadY(0,diameter)+Concept3D.ProbePinOffsetY-Concept3D.ProbePinLength/2,5);
        var rise=Concept3D.ProbeTipY(1,diameter)-Concept3D.ProbeTipY(0,diameter);
        Assert.Equal(Concept3D.WorldPerUm(diameter)*Concept3D.ZDisplayMagnification,rise,4);
    }
    [Fact] public void Scene_PreservesWaferOwnership_AndCutawayOpensRoof()
    {
        var locations=Enum.GetValues<WaferLocation>();
        var wafers=locations.Select((location,i)=>new WaferTrack($"W{i}",i+1,location,WaferStatus.Processing,0,1)).ToArray();
        var snapshot=Snapshot(1) with { Wafers=wafers };
        var faces=Concept3D.Build(snapshot);
        Assert.DoesNotContain(faces,f=>f.Part=="roof");
        Assert.Equal(wafers.Length,faces.Where(f=>f.Part.StartsWith("wafer:")).Select(f=>f.Part).Distinct().Count());
        Assert.Equal(25,faces.Count(f=>f.Part=="cassette slot")/6);
        Assert.Contains(Concept3D.Build(snapshot,cutaway:false),f=>f.Part=="roof");
        Assert.Equal(wafers.Length,Concept3D.Build(snapshot,closeUp:true).Where(f=>f.Part.StartsWith("wafer:")).Select(f=>f.Part).Distinct().Count());
    }
    [Fact] public void RobotRoute_UsesActiveStepTimeAndDestination()
    {
        var step=new StepEvent(1,"W01",null,ModuleId.Robot,PhaseKind.PlaceOnChuck,TimeSpan.Zero,null,false,null,"");
        var start=Concept3D.RobotBladePoint(Snapshot(0),step,TimeSpan.FromSeconds(2));
        var mid=Concept3D.RobotBladePoint(Snapshot(1),step,TimeSpan.FromSeconds(2));
        var end=Concept3D.RobotBladePoint(Snapshot(2),step,TimeSpan.FromSeconds(2));
        Assert.Equal(Concept3D.RobotSource(step),start);Assert.Equal(Concept3D.RobotTarget(step),end);
        Assert.True(Vector3.Distance(mid,Vector3.Lerp(Concept3D.RobotSource(step),Concept3D.RobotTarget(step),.5f))<.0001f);
    }
    [Fact] public async Task Replay_IsThreadSafe_AndNeverShowsFutureFrame()
    {
        var replay=new RunReplay();replay.Record(Snapshot(5));
        Assert.Null(replay.Seek(TimeSpan.Zero));
        await Task.WhenAll(Enumerable.Range(0,50).Select(i=>Task.Run(()=>{replay.Record(new StepEvent(i,$"W{i}",null,ModuleId.Stage,PhaseKind.MoveXY,TimeSpan.FromSeconds(i),null,false,null,""));_ = replay.VisibleEvents(TimeSpan.FromSeconds(20));_ = replay.Seek(TimeSpan.FromSeconds(5));})));
        replay.Record(Snapshot(5) with { State=EquipmentState.Completed });
        replay.Record(Snapshot(5));
        Assert.Equal(EquipmentState.Completed,replay.Seek(TimeSpan.FromSeconds(5))!.State);
        replay.Clear();Assert.Equal(0,replay.FrameCount);Assert.Null(replay.Seek(TimeSpan.FromSeconds(10)));
    }
    [Fact] public void TwentyFiveSlots_AndDepthShading_ArePresent()
    {
        var wafers=Enumerable.Range(1,25).Select(i=>new WaferTrack($"W{i:000}",i,i%2==0?WaferLocation.Done:WaferLocation.LoadPort,WaferStatus.Pending,0,1)).ToArray();
        var faces=Concept3D.Build(Snapshot(0) with { Wafers=wafers });
        Assert.Equal(25,faces.Where(f=>f.Part.StartsWith("wafer:")).Select(f=>f.Part).Distinct().Count());
        var projected=Concept3D.Project(faces,Camera3D.Default,900,500);
        Assert.True(projected.Where(f=>f.Part=="XY stage").Select(f=>f.Color).Distinct().Count()>1);
        Assert.DoesNotContain(projected.SelectMany(f=>f.Vertices),p=>!float.IsFinite(p.X)||!float.IsFinite(p.Y));
    }
    [Fact] public void Replay_BoundsMemoryAndExposesRetainedStart()
    {
        var replay=new RunReplay();
        var template=Snapshot(0);
        for(var i=0;i<=RunReplay.MaxFrames;i++)replay.Record(template with { SimTime=TimeSpan.FromMilliseconds(i) });
        Assert.Equal(RunReplay.MaxFrames,replay.FrameCount);
        Assert.True(replay.FramesTruncated);
        Assert.Equal(TimeSpan.FromMilliseconds(1),replay.StartTime);
        Assert.Null(replay.Seek(TimeSpan.Zero));
        for(var i=0;i<=RunReplay.MaxEvents;i++)replay.Record(new StepEvent(i,"W",null,ModuleId.Stage,PhaseKind.MoveXY,TimeSpan.FromMilliseconds(i),null,false,null,""));
        Assert.True(replay.EventsTruncated);
    }
    [Fact] public void Replay_EventNavigation_UsesCursorBetweenSamples()
    {
        var replay=new RunReplay();replay.Record(Snapshot(1));replay.Record(Snapshot(3));
        replay.Record(new StepEvent(1,"W01",null,ModuleId.Robot,PhaseKind.PlaceOnChuck,TimeSpan.FromSeconds(1.2),TimeSpan.FromSeconds(1.8),false,null,""));
        replay.Record(new StepEvent(2,"W01",null,ModuleId.Stage,PhaseKind.MoveXY,TimeSpan.FromSeconds(2.2),TimeSpan.FromSeconds(2.8),false,null,""));
        var cursor=replay.NextEvent(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(1),replay.Seek(cursor)!.SimTime);
        cursor=replay.NextEvent(cursor);
        Assert.Equal(TimeSpan.FromSeconds(2.2),cursor);
        Assert.Equal(TimeSpan.FromSeconds(1.2),replay.PreviousEvent(cursor));
    }
    [Fact] public void Replay_RobotEvent_IgnoresConcurrentModulesAndCompletedStarts()
    {
        var replay=new RunReplay();
        var robot=new StepEvent(1,"W01",null,ModuleId.Robot,PhaseKind.PlaceOnChuck,TimeSpan.FromSeconds(1),null,false,null,"");
        replay.Record(robot);
        replay.Record(new StepEvent(2,"W02",null,ModuleId.Stage,PhaseKind.MoveXY,TimeSpan.FromSeconds(1.5),null,false,null,""));
        Assert.Equal(robot,replay.ActiveEvent(TimeSpan.FromSeconds(2),ModuleId.Robot));
        replay.Record(robot with { End=TimeSpan.FromSeconds(3) });
        Assert.Null(replay.ActiveEvent(TimeSpan.FromSeconds(3),ModuleId.Robot));
        Assert.Equal(robot.Seq,replay.ActiveEvent(TimeSpan.FromSeconds(2),ModuleId.Robot)!.Seq);
    }
    [Fact] public void Replay_EventJump_ShowsBoundaryWithoutFutureState()
    {
        var replay=new RunReplay();
        replay.Record(Snapshot(0,100) with { SimTime=TimeSpan.FromSeconds(.99) });
        replay.Record(Snapshot(0,200) with { SimTime=TimeSpan.FromSeconds(1.0004) });
        var step=new StepEvent(1,"W01",null,ModuleId.Robot,PhaseKind.PlaceOnChuck,TimeSpan.FromSeconds(1),null,false,null,"");
        replay.Record(step);
        var cursor=replay.NextEvent(TimeSpan.FromSeconds(.99));
        var view=replay.ViewAt(cursor)!;
        Assert.Equal(TimeSpan.FromSeconds(1),view.SimTime);
        Assert.Equal(100,view.Motion.StageXUm);
        Assert.Contains(step,replay.VisibleEvents(view.SimTime));
        Assert.Equal(step,replay.ActiveEvent(view.SimTime,ModuleId.Robot));
        Assert.Equal(TimeSpan.FromSeconds(1.0004),replay.ViewAt(TimeSpan.FromSeconds(2))!.SimTime);
    }
}
