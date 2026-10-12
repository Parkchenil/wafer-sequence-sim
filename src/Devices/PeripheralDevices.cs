using Contracts;

namespace Devices;

public sealed class SimulatedVision(TimeProvider clock, Random random, double failureProbability = 0) : IVision
{
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
    { await Task.Delay(mappingTime, clock, ct); return DeviceResult<IReadOnlyList<int>>.Success(slots ?? [1]); }
}
/// <summary>로봇. physical(장비 물리 IO)이 있으면 웨이퍼를 척에 놓고 집을 때 DI_CHUCK_PRESENT 센서를 바꾼다(진공 확인의 전제).</summary>
public sealed class SimulatedRobot(IDigitalIO io, TimeProvider clock, InMemoryDigitalIO? physical = null) : IRobot
{
    public bool HoldingWafer { get; private set; }
    public bool Extended { get; private set; }
    private async Task<DeviceResult> Act(TimeSpan moveTime, bool needsWafer, bool resultingHold, CancellationToken ct, bool? chuckPresentAfter = null)
    {
        if (needsWafer && !HoldingWafer) return DeviceResult.Fail(AlarmCode.RobotCollisionRisk, "로봇에 웨이퍼 없음");
        if (!needsWafer && HoldingWafer) return DeviceResult.Fail(AlarmCode.RobotCollisionRisk, "로봇이 이미 웨이퍼 보유 중");
        Extended = true; await io.WriteAsync(IoPoints.RobotExtended, true, ct);
        try
        {
            await Task.Delay(moveTime, clock, ct); HoldingWafer = resultingHold;
            if (chuckPresentAfter is { } present) physical?.Force(IoPoints.ChuckWaferPresent, present);
            return DeviceResult.Success;
        }
        finally { Extended = false; await io.WriteAsync(IoPoints.RobotExtended, false, CancellationToken.None); }
    }
    public Task<DeviceResult> PickFromSlotAsync(int slot, TimeSpan moveTime, CancellationToken ct) => Act(moveTime, false, true, ct);
    public Task<DeviceResult> PlaceOnAlignerAsync(TimeSpan moveTime, CancellationToken ct) => Act(moveTime, true, false, ct);
    public Task<DeviceResult> PickFromAlignerAsync(TimeSpan moveTime, CancellationToken ct) => Act(moveTime, false, true, ct);
    public Task<DeviceResult> PlaceOnChuckAsync(TimeSpan moveTime, CancellationToken ct) => Act(moveTime, true, false, ct, chuckPresentAfter: true);
    public Task<DeviceResult> PickFromChuckAsync(TimeSpan moveTime, CancellationToken ct) => Act(moveTime, false, true, ct, chuckPresentAfter: false);
    public Task<DeviceResult> ReturnToSlotAsync(int slot, TimeSpan moveTime, CancellationToken ct) => Act(moveTime, true, false, ct);
}
public sealed class SimulatedAligner(TimeProvider clock) : IAligner
{
    public double AngleDeg { get; private set; }
    public async Task<DeviceResult<double>> AlignAsync(TimeSpan alignTime, CancellationToken ct)
    { await Task.Delay(alignTime, clock, ct); AngleDeg = 0; return DeviceResult<double>.Success(AngleDeg); }
}
