using Contracts;

namespace Devices;

public sealed class SimulatedVision(TimeProvider clock, Random random, double failureProbability = 0) : IVision
{
    public void Reseed(int seed) => random = new Random(seed);
    public bool Focused { get; private set; }
    public async Task<DeviceResult> AutoFocusAsync(TimeSpan settle, TimeSpan timeout, CancellationToken ct)
    {
        await Task.Delay(settle < timeout ? settle : timeout, clock, ct);
        if (settle > timeout) return DeviceResult.Fail(AlarmCode.AutoFocusTimeout, "초점 대기 시간 초과");
        Focused = true; return DeviceResult.Success;
    }
    public async Task<DeviceResult<AlignOffset>> InspectAsync(TimeSpan processingTime, CancellationToken ct)
    {
        await Task.Delay(processingTime, clock, ct);
        if (!Focused || random.NextDouble() < failureProbability) return DeviceResult<AlignOffset>.Fail(AlarmCode.VisionAlignFail, "패턴 인식 실패");
        return DeviceResult<AlignOffset>.Success(new((random.NextDouble() * 4) - 2, (random.NextDouble() * 4) - 2, 0, .9 + random.NextDouble() * .1));
    }
}
public sealed class SimulatedLoadPort(IDigitalIO io, IWaitHelper wait, TimeProvider clock, IReadOnlyList<int>? slots = null) : ILoadPort
{
    // 기본은 25슬롯 FOUP가 가득 찬 상태. 로트의 웨이퍼 수는 엔진이 정하고, 로드포트는 슬롯 점유만 안다.
    private readonly HashSet<int> occupied = new(slots ?? Enumerable.Range(1, 25).ToArray());
    public bool HasWafer(int slot) { lock (occupied) return occupied.Contains(slot); }
    public bool CanReturn(int slot) { lock (occupied) return slot > 0 && !occupied.Contains(slot); }
    public void SetOccupied(int slot, bool value) { lock (occupied) { if (value) occupied.Add(slot); else occupied.Remove(slot); } }
    public bool DoorOpen { get; private set; }
    public async Task<DeviceResult> OpenAsync(TimeSpan timeout, CancellationToken ct)
    {
        var write = await io.WriteAsync(IoPoints.LoadPortDoorOpen, true, ct);
        if (!write.Ok) return write;
        var result = await wait.WaitForAsync(async token => (await io.ReadAsync(IoPoints.LoadPortDoorOpened, token)).Value, TimeSpan.FromMilliseconds(20), timeout, AlarmCode.LoadPortDoor, "로드포트 도어 열림", ct);
        if (result.Ok) DoorOpen = true;
        return result;
    }
    public async Task<DeviceResult> CloseAsync(TimeSpan timeout, CancellationToken ct)
    {
        var write = await io.WriteAsync(IoPoints.LoadPortDoorOpen, false, ct);
        if (!write.Ok) return write;
        var result = await wait.WaitForAsync(async token => !(await io.ReadAsync(IoPoints.LoadPortDoorOpened, token)).Value, TimeSpan.FromMilliseconds(20), timeout, AlarmCode.LoadPortDoor, "로드포트 도어 닫힘", ct);
        if (result.Ok) DoorOpen = false;
        return result;
    }
    public async Task<DeviceResult<IReadOnlyList<int>>> MapSlotsAsync(TimeSpan mappingTime, CancellationToken ct)
    { await Task.Delay(mappingTime, clock, ct); lock (occupied) return DeviceResult<IReadOnlyList<int>>.Success(occupied.Order().ToArray()); }
}
/// <summary>로봇. physical(장비 물리 IO)이 있으면 웨이퍼를 척에 놓고 집을 때 DI_CHUCK_PRESENT 센서를 바꾼다(진공 확인의 전제).</summary>
public sealed class SimulatedRobot(IDigitalIO io, TimeProvider clock, InMemoryDigitalIO? physical = null, SimulatedLoadPort? loadPort = null) : IRobot, IRobotTransportSource, IRobotRecovery
{
    private readonly record struct RobotPoint(double X, double Y, double Z);
    private readonly SemaphoreSlim movement = new(1, 1);
    private readonly InMemoryDigitalIO? sensors = physical ?? io as InMemoryDigitalIO;
    private RobotPoint? interruptedContact, interruptedOutside;
    private RobotTransportTelemetry transport = new(-.30, .13, .03, -.30, .13, .03, "대기", null, 0, false, false);
    public RobotTransportTelemetry Transport => Volatile.Read(ref transport);
    public event Action<RobotTransportTelemetry>? TransportChanged;
    public bool HoldingWafer { get; private set; }
    public bool Extended { get; private set; }
    private void Set(RobotTransportTelemetry value) { Volatile.Write(ref transport, value); TransportChanged?.Invoke(value); }
    private async Task DelayExact(TimeSpan duration, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(duration <= TimeSpan.Zero)return;
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation=ct.Register(()=>completion.TrySetCanceled(ct));
        using var timer=clock.CreateTimer(_=>completion.TrySetResult(),null,duration,Timeout.InfiniteTimeSpan);
        await completion.Task;
    }
    private async Task<DeviceResult> Act(TimeSpan moveTime, bool needsWafer, bool resultingHold, CancellationToken ct, bool? chuckPresentAfter = null, int? slot = null, string destination = "")
    {
        await movement.WaitAsync(ct);
        try
        {
            if (needsWafer && !HoldingWafer) return DeviceResult.Fail(AlarmCode.RobotCollisionRisk, "로봇에 웨이퍼 없음");
            if (!needsWafer && HoldingWafer) return DeviceResult.Fail(AlarmCode.RobotCollisionRisk, "로봇이 이미 웨이퍼 보유 중");
            if (slot is { } number && loadPort is not null)
            {
                if (!needsWafer && !loadPort.HasWafer(number)) return DeviceResult.Fail(AlarmCode.RobotCollisionRisk, "슬롯에 웨이퍼 없음");
                if (needsWafer && !loadPort.CanReturn(number)) return DeviceResult.Fail(AlarmCode.RobotCollisionRisk, "반납 슬롯 점유 중");
            }
            var health = await io.ReadAsync(IoPoints.RobotExtended, ct);
            if (!health.Ok) return DeviceResult.Fail(health.Code, health.Message);
            if (destination == "aligner" && Transport.AlignerOccupied != resultingHold)
                return DeviceResult.Fail(AlarmCode.RobotCollisionRisk, resultingHold ? "얼라이너에 웨이퍼 없음" : "얼라이너 점유 중");
            if (destination == "chuck")
            {
                var present = await io.ReadAsync(IoPoints.ChuckWaferPresent, ct);
                if (!present.Ok) return DeviceResult.Fail(present.Code, present.Message);
                if (present.Value != resultingHold) return DeviceResult.Fail(AlarmCode.RobotCollisionRisk, resultingHold ? "척 웨이퍼 재석 미확인" : "척이 이미 점유됨");
            }
            var contact = destination switch
            {
                "slot" => new RobotPoint(-.69, RobotGeometry.SlotBottom(slot!.Value) - RobotGeometry.PadOffset, .28),
                "aligner" => new RobotPoint(-.12, RobotGeometry.AlignerBottom - RobotGeometry.PadOffset, .34),
                _ => new RobotPoint(.35, RobotGeometry.ChuckBottom - RobotGeometry.PadOffset, 0)
            };
            // 슬롯 입구는 -Z 방향. 인계 전후의 수직 구간을 분리해 면 접촉에서만 소유권을 바꾼다.
            var outside = contact with { Z = destination == "slot" ? -.07 : destination == "aligner" ? .08 : -.24 };
            var low = contact with { Y = contact.Y - .001 };
            var high = contact with { Y = contact.Y + .002 };
            var approach = outside with { Y = resultingHold ? low.Y : high.Y };
            var stages = new List<(string Name, RobotPoint Point, double End, bool Handoff)>
            {
                ("안전경로 이송", new(-.30, .22, -.07), .15, false),
                ("높이 접근", approach, .32, false),
                ("블레이드 진입", resultingHold ? low : high, .55, false),
                (resultingHold ? "상승 접촉" : "하강 인계", contact, .65, true),
                (resultingHold ? "보유 확인" : "보유 해제 확인", contact, .72, false),
                (resultingHold ? "상승" : "블레이드 하강", resultingHold ? high : low, .80, false),
                ("후퇴", outside with { Y = resultingHold ? high.Y : low.Y }, 1, false)
            };
            var previous = 0.0;
            long spentTicks = 0;
            Set(Transport with { Zone = destination == "chuck" ? RobotZone.ChuckAccess : RobotZone.IsolatedTransfer });
            interruptedContact = contact; interruptedOutside = outside;
            Extended = true; sensors?.Force(IoPoints.RobotExtended, true);
            try
            {
                foreach (var stage in stages)
                {
                    ct.ThrowIfCancellationRequested();
                    var start = Transport;
                    var target = stage.Point;
                    var waiting = stage.Name.Contains("확인") ? (resultingHold ? "가상 보유 센서" : "가상 재석 센서") : "목표 위치 도달";
                    Set(start with { Subphase = stage.Name, Slot = slot, TargetXMetres = target.X, TargetYMetres = target.Y, TargetZMetres = target.Z, Progress = previous, WaitingFor = waiting, HandoffLocation = null });
                    var endTicks = (long)Math.Round(moveTime.Ticks * stage.End);
                    var ticks = endTicks - spentTicks;
                    var slices = Math.Max(1, (int)Math.Ceiling(TimeSpan.FromTicks(ticks).TotalMilliseconds / 25));
                    long stageSpent = 0;
                    for (var i = 1; i <= slices; i++)
                    {
                        var sliceEnd = ticks * i / slices;
                        await DelayExact(TimeSpan.FromTicks(sliceEnd - stageSpent), ct);
                        ct.ThrowIfCancellationRequested();
                        stageSpent = sliceEnd;
                        var t = (double)i / slices;
                        var eased = t * t * (3 - 2 * t);
                        Set(Transport with { XMetres = start.XMetres + (target.X - start.XMetres) * eased,
                            YMetres = start.YMetres + (target.Y - start.YMetres) * eased,
                            ZMetres = start.ZMetres + (target.Z - start.ZMetres) * eased,
                            Progress = previous + (stage.End - previous) * t });
                    }
                    if (stage.Handoff)
                    {
                        ct.ThrowIfCancellationRequested();
                        HoldingWafer = resultingHold;
                        if (chuckPresentAfter is { } present) sensors?.Force(IoPoints.ChuckWaferPresent, present);
                        if (slot is { } changed && loadPort is not null) loadPort.SetOccupied(changed, needsWafer);
                        Set(Transport with { HoldingWafer = resultingHold,
                            AlignerOccupied = destination == "aligner" ? !resultingHold : Transport.AlignerOccupied,
                            HandoffLocation = resultingHold ? WaferLocation.RobotArm : destination == "slot" ? WaferLocation.LoadPort : destination == "aligner" ? WaferLocation.Aligner : WaferLocation.Chuck });
                    }
                    previous = stage.End; spentTicks = endTicks;
                }
                interruptedContact = null; interruptedOutside = null;
                Extended = false; sensors?.Force(IoPoints.RobotExtended, false);
                Set(Transport with { WaitingFor = null, HandoffLocation = null, Zone = RobotZone.Retracted });
                return DeviceResult.Success;
            }
            catch (OperationCanceledException)
            {
                // 중단은 수납 완료가 아니다. 인터락은 마지막 위치에서 확장 상태를 유지한다.
                Set(Transport with { Subphase = "중단 · 위치 유지", Zone = RobotZone.Unknown, WaitingFor = "복구 및 수납 확인", HandoffLocation = null });
                throw;
            }
        }
        finally { movement.Release(); }
    }
    public async Task<DeviceResult> WithdrawAsync(TimeSpan moveTime, CancellationToken ct)
    {
        await movement.WaitAsync(ct);
        try
        {
            var sense = await io.ReadAsync(IoPoints.RobotExtended, ct);
            if (!sense.Ok || sense.Value != Extended || Transport.HoldingWafer != HoldingWafer) return DeviceResult.Fail(AlarmCode.InterlockBlocked, "수납 센서/보유 불일치");
            if (!Extended) return Transport.Zone == RobotZone.Retracted ? DeviceResult.Success : DeviceResult.Fail(AlarmCode.InterlockBlocked, "수납 영역 미확인");
            if (interruptedContact is not { } contact || interruptedOutside is not { } outside || !double.IsFinite(Transport.XMetres + Transport.YMetres + Transport.ZMetres)) return DeviceResult.Fail(AlarmCode.InterlockBlocked, "중단 경로 미확인 · 수납 거부");
            // 알려진 가상 진입 경로에서만 인계면을 벗어난 뒤 입구로 후퇴한다. 소유권은 바꾸지 않는다.
            var clearY = contact.Y + (HoldingWafer ? .002 : -.001);
            foreach (var target in new[] { new RobotPoint(Transport.XMetres, clearY, Transport.ZMetres), outside with { Y = clearY } })
            {
                var start = Transport;
                Set(start with { Subphase = "안전 회수 · 인계면 이격/후퇴", TargetXMetres = target.X, TargetYMetres = target.Y, TargetZMetres = target.Z, HandoffLocation = null });
                for (var i = 1; i <= 10; i++)
                {
                    await DelayExact(TimeSpan.FromTicks(moveTime.Ticks / 20), ct);
                    var t = i / 10d;
                    Set(Transport with { XMetres = start.XMetres + (target.X - start.XMetres) * t, YMetres = start.YMetres + (target.Y - start.YMetres) * t, ZMetres = start.ZMetres + (target.Z - start.ZMetres) * t });
                }
            }
            ct.ThrowIfCancellationRequested();
            Extended = false; sensors?.Force(IoPoints.RobotExtended, false);
            interruptedContact = null; interruptedOutside = null;
            Set(Transport with { Zone = RobotZone.Retracted, WaitingFor = null, Subphase = "안전 수납 확인" });
            return DeviceResult.Success;
        }
        finally { movement.Release(); }
    }
    public Task<DeviceResult> PickFromSlotAsync(int slot, TimeSpan moveTime, CancellationToken ct) => Act(moveTime, false, true, ct, slot: slot, destination: "slot");
    public Task<DeviceResult> PlaceOnAlignerAsync(TimeSpan moveTime, CancellationToken ct) => Act(moveTime, true, false, ct, destination: "aligner");
    public Task<DeviceResult> PickFromAlignerAsync(TimeSpan moveTime, CancellationToken ct) => Act(moveTime, false, true, ct, destination: "aligner");
    public Task<DeviceResult> PlaceOnChuckAsync(TimeSpan moveTime, CancellationToken ct) => Act(moveTime, true, false, ct, chuckPresentAfter: true, destination: "chuck");
    public Task<DeviceResult> PickFromChuckAsync(TimeSpan moveTime, CancellationToken ct) => Act(moveTime, false, true, ct, chuckPresentAfter: false, destination: "chuck");
    public Task<DeviceResult> ReturnToSlotAsync(int slot, TimeSpan moveTime, CancellationToken ct) => Act(moveTime, true, false, ct, slot: slot, destination: "slot");
}
public sealed class SimulatedAligner(TimeProvider clock) : IAligner
{
    public double AngleDeg { get; private set; }
    public async Task<DeviceResult<double>> AlignAsync(TimeSpan alignTime, CancellationToken ct)
    { await Task.Delay(alignTime, clock, ct); AngleDeg = 0; return DeviceResult<double>.Success(AngleDeg); }
}
