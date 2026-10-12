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
public sealed class SimulatedRobot(IDigitalIO io, TimeProvider clock, InMemoryDigitalIO? physical = null, SimulatedLoadPort? loadPort = null) : IRobot
{
    private readonly SemaphoreSlim movement = new(1, 1);
    private readonly InMemoryDigitalIO? sensors = physical ?? io as InMemoryDigitalIO;
    public bool HoldingWafer { get; private set; }
    public bool Extended { get; private set; }
    private async Task<DeviceResult> Act(TimeSpan moveTime, bool needsWafer, bool resultingHold, CancellationToken ct, bool? chuckPresentAfter = null, int? slot = null)
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
            Extended = true; sensors?.Force(IoPoints.RobotExtended, true);
            try
            {
                await Task.Delay(moveTime, clock, ct);
                HoldingWafer = resultingHold;
                if (chuckPresentAfter is { } present) sensors?.Force(IoPoints.ChuckWaferPresent, present);
                if (slot is { } changed && loadPort is not null) loadPort.SetOccupied(changed, needsWafer);
                return DeviceResult.Success;
            }
            finally { Extended = false; sensors?.Force(IoPoints.RobotExtended, false); }
        }
        finally { movement.Release(); }
    }
    public Task<DeviceResult> PickFromSlotAsync(int slot, TimeSpan moveTime, CancellationToken ct) => Act(moveTime, false, true, ct, slot: slot);
    public Task<DeviceResult> PlaceOnAlignerAsync(TimeSpan moveTime, CancellationToken ct) => Act(moveTime, true, false, ct);
    public Task<DeviceResult> PickFromAlignerAsync(TimeSpan moveTime, CancellationToken ct) => Act(moveTime, false, true, ct);
    public Task<DeviceResult> PlaceOnChuckAsync(TimeSpan moveTime, CancellationToken ct) => Act(moveTime, true, false, ct, chuckPresentAfter: true);
    public Task<DeviceResult> PickFromChuckAsync(TimeSpan moveTime, CancellationToken ct) => Act(moveTime, false, true, ct, chuckPresentAfter: false);
    public Task<DeviceResult> ReturnToSlotAsync(int slot, TimeSpan moveTime, CancellationToken ct) => Act(moveTime, true, false, ct, slot: slot);
}
public sealed class SimulatedAligner(TimeProvider clock) : IAligner
{
    public double AngleDeg { get; private set; }
    public async Task<DeviceResult<double>> AlignAsync(TimeSpan alignTime, CancellationToken ct)
    { await Task.Delay(alignTime, clock, ct); AngleDeg = 0; return DeviceResult<double>.Success(AngleDeg); }
}
