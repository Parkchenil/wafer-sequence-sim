using Contracts;
namespace Core.Visualization;

// 한 실행의 최근 30,000 스냅샷과 5,000 이벤트를 보관한다.
public sealed class RunReplay
{
    public const int MaxFrames = 30000, MaxEvents = 5000;
    private readonly object gate = new();
    private readonly List<EquipmentSnapshot> frames = [];
    private readonly SortedDictionary<long, StepEvent> events = [];
    public int FrameCount { get { lock(gate) return frames.Count; } }
    public bool FramesTruncated { get; private set; }
    public bool EventsTruncated { get; private set; }
    public IReadOnlyList<EquipmentSnapshot> Frames { get { lock(gate) return frames.ToArray(); } }
    public EquipmentSnapshot? LastFrame { get { lock(gate) return frames.Count==0?null:frames[^1]; } }
    public TimeSpan StartTime { get { lock(gate) return frames.Count==0?TimeSpan.Zero:frames[0].SimTime; } }
    public TimeSpan Duration { get { lock(gate) return frames.Count==0?TimeSpan.Zero:frames[^1].SimTime; } }
    public void Clear(){ lock(gate){frames.Clear();events.Clear();FramesTruncated=false;EventsTruncated=false;} }
    public void Record(EquipmentSnapshot snapshot)
    {
        var copy=snapshot with { Interlock=snapshot.Interlock is { } interlock ? interlock with { Conditions=Array.AsReadOnly(interlock.Conditions.ToArray()) } : null, Modules=snapshot.Modules.ToArray(),Wafers=snapshot.Wafers.ToArray(),Sites=snapshot.Sites.ToArray(),ActiveAlarms=snapshot.ActiveAlarms.ToArray(),Stats=snapshot.Stats with { PhaseTotals=new Dictionary<PhaseKind,TimeSpan>(snapshot.Stats.PhaseTotals) } };
        lock(gate)
        {
            if(frames.Count>0)
            {
                var comparison=copy.SimTime.CompareTo(frames[^1].SimTime);
                if(comparison<0)return;
                if(comparison==0){
                    var previous=frames[^1].State;
                    var previousTerminal=previous is EquipmentState.Completed or EquipmentState.Idle or EquipmentState.Alarm;
                    var incomingTerminal=copy.State is EquipmentState.Completed or EquipmentState.Idle or EquipmentState.Alarm;
                    if(previousTerminal&&!incomingTerminal)return;
                    frames[^1]=copy;return;
                }
            }
            if(frames.Count==MaxFrames){frames.RemoveAt(0);FramesTruncated=true;}
            frames.Add(copy);
        }
    }
    public void Record(StepEvent step){ lock(gate){events[step.Seq]=step;if(events.Count>MaxEvents){events.Remove(events.Keys.First());EventsTruncated=true;}} }
    public EquipmentSnapshot? Seek(TimeSpan time)
    {
        lock(gate)
        {
            if(frames.Count==0||time<frames[0].SimTime)return null;
            var lo=0;var hi=frames.Count-1;
            while(lo<hi){var mid=(lo+hi+1)/2;if(frames[mid].SimTime<=time)lo=mid;else hi=mid-1;}
            return frames[lo];
        }
    }
    // 상태는 직전 샘플을 유지하고 시계는 요청 시각을 따른다. 미래 상태를 가져오지 않는다.
    public EquipmentSnapshot? ViewAt(TimeSpan time)
    {
        lock(gate)
        {
            if(frames.Count>0 && time>=Duration)return frames[^1];
            var frame=Seek(time);
            return frame is null?null:frame with { SimTime=time>Duration?Duration:time };
        }
    }
    public IReadOnlyList<StepEvent> VisibleEvents(TimeSpan time){lock(gate)return events.Values.Where(e=>e.Start<=time).Select(e=>e.End is { } end&&end>time?e with { End=null }:e).ToArray();}
    public StepEvent? ActiveEvent(TimeSpan time, ModuleId? module = null){lock(gate)return events.Values.LastOrDefault(e=>(module is null || e.Module==module)&&e.Start<=time&&(e.End is null||e.End>time));}
    public TimeSpan PreviousEvent(TimeSpan time){lock(gate)return events.Values.Where(e=>e.Start<time).Select(e=>e.Start).DefaultIfEmpty(frames.Count==0?TimeSpan.Zero:frames[0].SimTime).Max();}
    public TimeSpan NextEvent(TimeSpan time){lock(gate)return events.Values.Where(e=>e.Start>time).Select(e=>e.Start).DefaultIfEmpty(frames.Count==0?TimeSpan.Zero:frames[^1].SimTime).Min();}
}
