namespace Contracts;

// ─────────────────────────────────────────────────────────────
// 장치 계층 계약. 실제 장비 SW처럼 "이름 붙은 IO 포인트" 위에 장치를 구성한다.
//  - 모든 메서드는 async + CancellationToken. 지연은 TimeProvider로만 기다린다(Task.Delay(ts, timeProvider)).
//  - 실패는 DeviceResult. 타임아웃은 각 메서드 인자 또는 장치 설정으로 받는다.
//  - 인터락은 시퀀스 if문이 아니라 명령 계층(IInterlock)에서 막는다.
// ─────────────────────────────────────────────────────────────

/// <summary>IO 포인트 이름. 설정 파일(iomap.json)의 키와 같다.</summary>
public static class IoPoints
{
    // 출력(DO)
    public const string VacChuckOn = "DO_VAC_CHUCK_ON";
    public const string LampOn = "DO_LAMP_ON";
    public const string LoadPortDoorOpen = "DO_LP_DOOR_OPEN";
    public const string LoadPortClamp = "DO_LP_CLAMP";
    // 입력(DI)
    public const string VacChuckSense = "DI_VAC_CHUCK_SENSE";   // 진공 확인 센서
    public const string ProbeContact = "DI_PROBE_CONTACT";      // 접촉 감지
    public const string LoadPortWaferPresent = "DI_LP_PRESENT";
    public const string LoadPortDoorOpened = "DI_LP_DOOR_OPENED";
    public const string RobotExtended = "DI_ROBOT_EXTENDED";
    public const string ChuckWaferPresent = "DI_CHUCK_PRESENT";
    public const string Emo = "DI_EMO";
}

public interface IDigitalIO
{
    IReadOnlyList<string> Points { get; }
    Task<DeviceResult<bool>> ReadAsync(string point, CancellationToken ct);
    Task<DeviceResult> WriteAsync(string point, bool value, CancellationToken ct);
}

public sealed record AxisStatus(double PositionUm, bool ServoOn, bool Homed, bool InPosition, bool LimitPlus, bool LimitMinus, bool Moving);

/// <summary>단일 축. 이름은 "X","Y","Z".</summary>
public interface IAxis
{
    string Name { get; }
    Task<DeviceResult> ServoOnAsync(CancellationToken ct);
    Task<DeviceResult> HomeAsync(TimeSpan timeout, CancellationToken ct);
    /// <summary>절대 이동. 사다리꼴 속도 프로파일로 시간을 계산한다(가속·정속·감속).</summary>
    Task<DeviceResult> MoveAbsoluteAsync(double targetUm, double speedMmPerS, double accelMmPerS2, TimeSpan timeout, CancellationToken ct);
    /// <summary>상대 이동(Z 스텝 하강 등).</summary>
    Task<DeviceResult> MoveRelativeAsync(double deltaUm, double speedMmPerS, double accelMmPerS2, TimeSpan timeout, CancellationToken ct);
    Task<DeviceResult> StopAsync(CancellationToken ct);
    Task<DeviceResult<AxisStatus>> GetStatusAsync(CancellationToken ct);
    /// <summary>현재 위치(폴링 없이 즉시). 스냅샷·애니메이션용.</summary>
    double PositionUm { get; }
}

/// <summary>전류원·전압계. 실제 장비는 SCPI로 말한다(":SOUR:CURR 1e-3", ":MEAS:VOLT?").</summary>
public interface ISourceMeasureUnit
{
    Task<DeviceResult> ConfigureAsync(double sourceCurrentA, CancellationToken ct);
    /// <summary>1회 읽기. V를 돌려준다. 읽는 데 readingTime만큼 걸린다.</summary>
    Task<DeviceResult<double>> MeasureVoltageAsync(TimeSpan readingTime, CancellationToken ct);
    Task<DeviceResult> OutputAsync(bool on, CancellationToken ct);
}

public sealed record AlignOffset(double DxUm, double DyUm, double DThetaDeg, double Score);

public interface IVision
{
    Task<DeviceResult> AutoFocusAsync(TimeSpan settle, TimeSpan timeout, CancellationToken ct);
    /// <summary>현재 위치에서 패턴을 찾아 오프셋을 돌려준다.</summary>
    Task<DeviceResult<AlignOffset>> InspectAsync(TimeSpan processingTime, CancellationToken ct);
    bool Focused { get; }
}

public interface ILoadPort
{
    Task<DeviceResult> OpenAsync(TimeSpan timeout, CancellationToken ct);
    Task<DeviceResult> CloseAsync(TimeSpan timeout, CancellationToken ct);
    /// <summary>웨이퍼가 있는 슬롯 번호 목록(1부터).</summary>
    Task<DeviceResult<IReadOnlyList<int>>> MapSlotsAsync(TimeSpan mappingTime, CancellationToken ct);
    bool DoorOpen { get; }
}

public interface IRobot
{
    Task<DeviceResult> PickFromSlotAsync(int slot, TimeSpan moveTime, CancellationToken ct);
    Task<DeviceResult> PlaceOnAlignerAsync(TimeSpan moveTime, CancellationToken ct);
    Task<DeviceResult> PickFromAlignerAsync(TimeSpan moveTime, CancellationToken ct);
    Task<DeviceResult> PlaceOnChuckAsync(TimeSpan moveTime, CancellationToken ct);
    Task<DeviceResult> PickFromChuckAsync(TimeSpan moveTime, CancellationToken ct);
    Task<DeviceResult> ReturnToSlotAsync(int slot, TimeSpan moveTime, CancellationToken ct);
    bool HoldingWafer { get; }
    bool Extended { get; }
}

public interface IAligner
{
    /// <summary>노치 정렬. 보정한 각도를 돌려준다.</summary>
    Task<DeviceResult<double>> AlignAsync(TimeSpan alignTime, CancellationToken ct);
    double AngleDeg { get; }
}

/// <summary>프로브 위치 공급원. 가상 SMU가 "지금 어디를 찍고 있는지" 알기 위해 쓴다.</summary>
public interface IProbePosition
{
    double XUm { get; }
    double YUm { get; }
    bool InContact { get; }
}

/// <summary>면저항 분포 모델. 가상 SMU가 V를 만들 때 Rs(x,y)를 여기서 읽는다. Core.Measurement가 구현한다.</summary>
public interface IResistanceField
{
    /// <summary>웨이퍼·좌표의 참값 Rs(Ω/sq). 잡음 없음.</summary>
    double TrueRsAt(string waferId, double xUm, double yUm);
    /// <summary>측정 잡음을 포함한 1회 전압(V). random은 seed에서 파생된 것만 넘긴다.</summary>
    double VoltageSample(string waferId, double xUm, double yUm, double currentA, Random random);
}

/// <summary>인터락. 명령 계층에서 호출된다. 허용되지 않으면 사유를 돌려준다.</summary>
public interface IInterlock
{
    /// <summary>XY 이동 허용 조건: Z가 안전 높이 이상, 진공 확인됨, 로봇 수축.</summary>
    DeviceResult CheckMoveXY();
    /// <summary>Z 하강 허용: 진공 확인됨, XY 정지, 비상정지 아님.</summary>
    DeviceResult CheckDescendZ();
    /// <summary>로봇 진입 허용: Z 안전 높이, 스테이지 홈 위치.</summary>
    DeviceResult CheckRobotAccess();
    /// <summary>진공 해제 허용: Z 안전 높이.</summary>
    DeviceResult CheckVacuumOff();
}

/// <summary>장치 묶음. 시퀀스 엔진은 이것 하나를 받는다.</summary>
public interface IDeviceSet
{
    IDigitalIO Io { get; }
    IAxis StageX { get; }
    IAxis StageY { get; }
    IAxis ZAxis { get; }
    ISourceMeasureUnit Smu { get; }
    IVision Vision { get; }
    ILoadPort LoadPort { get; }
    IRobot Robot { get; }
    IAligner Aligner { get; }
    IProbePosition Probe { get; }
}

/// <summary>"조건이 될 때까지 폴링하다 타임아웃이면 알람" 헬퍼. 장치 계층이 구현한다.</summary>
public interface IWaitHelper
{
    Task<DeviceResult> WaitForAsync(Func<CancellationToken, Task<bool>> condition, TimeSpan pollInterval, TimeSpan timeout, AlarmCode onTimeout, string what, CancellationToken ct);
}
