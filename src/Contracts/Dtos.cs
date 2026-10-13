namespace Contracts;

// ─────────────────────────────────────────────────────────────
// 공통 규약
//  - 길이 µm, 속도 mm/s, 가속 mm/s², 시간은 TimeSpan 하나로 통일한다.
//  - 좌표계: 웨이퍼 중심이 원점, +X 오른쪽, +Y 위(노치는 -Y 방향, 6시).
//  - 모든 시간은 TimeProvider 하나(VirtualTimeProvider)에서만 읽는다. DateTime.Now 금지.
//  - 난수는 Recipe.Seed에서 파생한 Random만 쓴다. 같은 레시피+seed = 같은 로그.
//  - 실패는 예외가 아니라 DeviceResult + AlarmCode로 표현한다.
// ─────────────────────────────────────────────────────────────

/// <summary>장비를 구성하는 모듈. 각 모듈은 자기 상태기계를 갖고 동시에 움직인다.</summary>
public enum ModuleId
{
    LoadPort,
    Robot,
    Aligner,
    Stage,      // XY 스테이지
    ZHead,      // 프로브 Z축 + 접촉 감지
    Smu,        // 전류원·전압계
    Vision,     // 정렬 카메라 + 오토포커스 + 조명
    Vacuum,     // 척 진공
    Storage,    // 결과 저장 큐·DB
    Host,       // 상위 시스템 보고(SECS/GEM 자리)
}

/// <summary>모듈 상태기계의 공통 상태. 세부 동작은 Detail 문자열로.</summary>
public enum ModuleState { Uninitialized, Homing, Idle, Busy, Waiting, Alarm, Paused }

public sealed record ModuleStatus(ModuleId Module, ModuleState State, string Detail, TimeSpan Since);

/// <summary>장비 전체 상태. 모듈 상태에서 계산되는 값이며 별도 상태기계가 아니다.</summary>
public enum EquipmentState { Uninitialized, Initializing, Idle, Running, Paused, Alarm, Aborting, Completed }

/// <summary>시퀀스 단계 이름. 로그·간트·통계의 공통 키.</summary>
public enum PhaseKind
{
    Home,
    MapSlots,       // 로드포트 슬롯 매핑
    PickFromSlot,   // 로봇이 FOUP에서 꺼냄
    Align,          // 얼라이너 노치 정렬
    PlaceOnChuck,
    VacuumCheck,    // 진공 확인(고정 대기가 끼는 자리)
    Illumination,   // 조명 안정 대기
    AutoFocus,
    MapInspect,     // 비전으로 측정 위치 확인
    MoveXY,
    DescendZ,
    ContactDetect,
    Measure,
    LiftZ,
    PickFromChuck,
    ReturnToSlot,
    Save,           // 결과 저장(동기면 시퀀스를 막는다)
    HostReport,
    Wait,           // 위 어느 것도 아닌 순수 대기(큐 역압·인터락 대기 등)
    AlarmRecovery,
}

/// <summary>단계 로그 한 줄. End가 null이면 진행 중.</summary>
public sealed record StepEvent(
    long Seq,
    string WaferId,
    int? SiteIndex,
    ModuleId Module,
    PhaseKind Phase,
    TimeSpan Start,
    TimeSpan? End,
    bool IsWait,
    string? WaitReason,
    string Note)
{
    public TimeSpan? Duration => End is { } e ? e - Start : null;
    public bool IsDetail { get; init; }
}

public enum WaferLocation { LoadPort, RobotArm, Aligner, Chuck, Done }
public enum WaferStatus { Pending, Processing, Measured, Saved, Failed, Journaled }

/// <summary>웨이퍼 추적. 슬롯·현재 위치·처리 상태.</summary>
public sealed record WaferTrack(string WaferId, int Slot, WaferLocation Location, WaferStatus Status, int SitesDone, int SitesTotal);

public enum AlarmSeverity { Info, Warning, Error }

/// <summary>알람 코드. 장치·시퀀스가 공통으로 쓴다. 숫자는 README의 ALID 매핑표와 같다.</summary>
public enum AlarmCode
{
    None = 0,
    AxisTimeout = 1001,
    AxisLimit = 1002,
    AxisNotHomed = 1003,
    ServoOff = 1004,
    AxisStopped = 1005,          // 운영자/안전 상태의 정지로 끊긴 이동(타임아웃이 아님)
    ConfigError = 1006,          // 알 수 없는 IO 포인트 등 설정 오류
    ContactTimeout = 2001,
    ContactRetryExceeded = 2002,
    QualityRejected = 2003,
    VacuumNotConfirmed = 3001,
    InterlockBlocked = 3002,
    VisionAlignFail = 4001,
    AutoFocusTimeout = 4002,
    SmuTimeout = 5001,
    SmuOverRange = 5002,
    SaveQueueFull = 6001,
    SaveWriteFail = 6002,
    LoadPortDoor = 7001,
    RobotCollisionRisk = 7002,
    CommTimeout = 8001,
    Emo = 9001,
}

public sealed record Alarm(
    string Id,
    AlarmCode Code,
    AlarmSeverity Severity,
    ModuleId Module,
    string Message,
    TimeSpan SetAt,
    TimeSpan? ClearedAt,
    bool Acked);

/// <summary>장치 호출 결과. 실패해도 예외를 던지지 않는다.</summary>
public sealed record DeviceResult(bool Ok, AlarmCode Code, string Message)
{
    public static readonly DeviceResult Success = new(true, AlarmCode.None, "");
    public static DeviceResult Fail(AlarmCode code, string message) => new(false, code, message);
}

public sealed record DeviceResult<T>(bool Ok, AlarmCode Code, string Message, T? Value)
{
    public static DeviceResult<T> Success(T value) => new(true, AlarmCode.None, "", value);
    public static DeviceResult<T> Fail(AlarmCode code, string message) => new(false, code, message, default);
}

// ───────────── 레시피 ─────────────

public enum SitePattern { Center1, Standard5, Standard9, Standard49, Custom }
public enum ZApproachMode { Slow, Step }
public enum MapInspectMode { AllSites, FirstSiteOnly }
public enum SaveMode { Sync, Async }
public enum ClockMode { Realtime, Instant }

public sealed record SitePoint(double XUm, double YUm);

/// <summary>레시피. 모든 수치는 가상이다. 특정 제조사 장비 값이 아니다.</summary>
public sealed record RecipeDto
{
    public int SchemaVersion { get; init; } = 1;
    public string Name { get; init; } = "기본";
    public int Seed { get; init; } = 12345;

    // 측정 위치
    public SitePattern Pattern { get; init; } = SitePattern.Standard9;
    public IReadOnlyList<SitePoint> CustomSites { get; init; } = Array.Empty<SitePoint>();
    public double WaferDiameterMm { get; init; } = 300;
    public double EdgeExclusionMm { get; init; } = 3;

    // 전기 측정
    public int ReadingsPerSite { get; init; } = 4;          // 사이트당 반복 측정 수
    public TimeSpan ReadingTime { get; init; } = TimeSpan.FromMilliseconds(250);
    public double SourceCurrentA { get; init; } = 1e-3;

    // 모션
    public double TravelSpeedMmPerS { get; init; } = 100;
    public double AccelMmPerS2 { get; init; } = 500;
    public ZApproachMode ZMode { get; init; } = ZApproachMode.Slow;
    public double ZApproachSpeedMmPerS { get; init; } = 0.5;
    public double ZStepUm { get; init; } = 5;
    public double ZSafeHeightUm { get; init; } = 300;        // 측정 후 이 높이까지 들어 올린다
    public double ZLiftAfterMeasureUm { get; init; } = 300;   // 사이트 사이에 드는 높이(작을수록 빠르다)
    public TimeSpan ContactDetectTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public double ContactRetryProbability { get; init; } = 0.05; // seed 기반. 재시도 발생 비율
    public int ContactMaxRetry { get; init; } = 2;

    public bool QualityEnabled { get; init; } = false;
    public double QualityTolerancePct { get; init; } = 1;
    public int QualityMinSamples { get; init; } = 3;
    public int QualityMaxRetry { get; init; } = 1;
    public TimeSpan ProbeSettleTime { get; init; } = TimeSpan.FromMilliseconds(100);

    // 고정 대기(장비 설정에서 흔히 보이는 상수 대기)
    public TimeSpan VacuumCheckWait { get; init; } = TimeSpan.FromMilliseconds(800);
    public int VacuumCheckCount { get; init; } = 3;
    public TimeSpan IlluminationWait { get; init; } = TimeSpan.FromMilliseconds(300);
    public TimeSpan AutoFocusWait { get; init; } = TimeSpan.FromMilliseconds(120);
    public MapInspectMode MapInspect { get; init; } = MapInspectMode.AllSites;
    public TimeSpan MapInspectTime { get; init; } = TimeSpan.FromMilliseconds(350);

    // 핸들링 중첩
    public bool UnloadOverlap { get; init; } = false;        // 언로딩과 다음 웨이퍼 픽업을 겹친다
    public TimeSpan RobotMoveTime { get; init; } = TimeSpan.FromSeconds(2.5);
    public TimeSpan AlignTime { get; init; } = TimeSpan.FromSeconds(3);

    // 결과 저장
    public SaveMode Save { get; init; } = SaveMode.Sync;
    public int SaveQueueCapacity { get; init; } = 4;
    public TimeSpan SaveWriteTime { get; init; } = TimeSpan.FromMilliseconds(600);

    // 시계
    public ClockMode Clock { get; init; } = ClockMode.Realtime;
    public double TimeScale { get; init; } = 1.0;             // Realtime에서 배속
}

// ───────────── 측정 ─────────────

public sealed record MeasurementResult(
    string WaferId,
    int SiteIndex,
    double XUm,
    double YUm,
    IReadOnlyList<double> VoltageV,
    double CurrentA,
    double RsOhmPerSq,          // 반복 측정 평균에서 계산
    double RsSigma,             // 같은 사이트 반복의 표준편차(재현성)
    TimeSpan MeasuredAt);

public sealed record WaferSummary(
    string WaferId,
    int SiteCount,
    double MeanRs,
    double SigmaRs,
    double MinRs,
    double MaxRs,
    double UniformitySigmaPct,     // σ/평균 × 100
    double UniformityRangePct,     // (최대-최소)/(2·평균) × 100
    double RepeatabilityPct,       // 사이트별 반복 σ 평균 / 평균 × 100
    TimeSpan Tact,
    IReadOnlyList<MeasurementResult> Sites);

public sealed record RunStats(
    int WafersDone,
    int WafersTotal,
    TimeSpan Elapsed,
    TimeSpan? AverageTact,
    double? Uph,
    IReadOnlyDictionary<PhaseKind, TimeSpan> PhaseTotals,
    double WaitRatio,              // 대기로 분류된 시간 / 전체
    int SaveQueueDepth,
    int SaveQueueCapacity);

/// <summary>화면이 그리는 모든 것. SnapshotChanged는 30 Hz 이하로만 발행된다.</summary>
public sealed record EquipmentSnapshot(
    TimeSpan SimTime,
    EquipmentState State,
    IReadOnlyList<ModuleStatus> Modules,
    IReadOnlyList<WaferTrack> Wafers,
    MotionSnapshot Motion,
    RunStats Stats,
    IReadOnlyList<Alarm> ActiveAlarms,
    string? CurrentWaferId,
    int? CurrentSiteIndex,
    IReadOnlyList<SitePoint> Sites)
{
    public RobotTransportTelemetry? RobotTransport { get; init; }
    public InterlockSnapshot? Interlock { get; init; }
    public ProbeSnapshot? ProbeDetail { get; init; }
    public FaultSnapshot? Faults { get; init; }
}

public sealed record RobotTransportTelemetry(
    double XMetres, double YMetres, double ZMetres,
    double TargetXMetres, double TargetYMetres, double TargetZMetres,
    string Subphase, int? Slot, double Progress, bool HoldingWafer, bool AlignerOccupied,
    string? WaitingFor = null)
{
    public WaferLocation? HandoffLocation { get; init; }
    public RobotZone Zone { get; init; } = RobotZone.Unknown;
}

/// <summary>2D 도식 애니메이션용 위치·상태.</summary>
public sealed record MotionSnapshot(
    double StageXUm,
    double StageYUm,
    double ZUm,                // 0 = 접촉면, 양수 = 위로 떨어진 높이
    bool ProbeInContact,
    bool VacuumOn,
    bool VacuumConfirmed,
    bool LampOn,
    bool CameraFocused,
    bool RobotExtended,
    bool LoadPortDoorOpen,
    double AlignerAngleDeg);

// ───────────── 실행 이력·비교 ─────────────

public sealed record RunRecord(
    string RunId,
    DateTimeOffset StartedAtWall,
    string RecipeName,
    string RecipeJson,
    int Seed,
    int WaferCount,
    TimeSpan? AverageTact,
    double? Uph,
    IReadOnlyDictionary<PhaseKind, TimeSpan> PhaseTotals,
    string LogDigest);

public sealed record PhaseDelta(PhaseKind Phase, TimeSpan A, TimeSpan B, TimeSpan Delta);

public sealed record RunComparison(RunRecord A, RunRecord B, IReadOnlyList<PhaseDelta> Phases, TimeSpan TactDelta, double UphDelta);
