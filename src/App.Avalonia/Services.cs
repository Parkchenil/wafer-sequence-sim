using Contracts;
using Core.Measurement;
using Core.Sequence;
using Devices;

namespace App.Avalonia;

/// <summary>
/// 조립 루트. 화면은 ISimulator만 보고, 진짜 엔진·장치·저장소는 여기서만 묶는다.
/// 개발 중에는 FakeSimulator 한 줄로 바꿔 화면만 따로 만들 수 있다.
/// </summary>
public static class Services
{
    public static VirtualTimeProvider Clock { get; }
    public static DeviceBundle Devices { get; }
    public static SqliteResultStore Store { get; }
    public static ISimulator Simulator { get; }
    public static string DatabasePath { get; }

    static Services()
    {
        // UI 스레드(동기화 컨텍스트)에서 async 결과를 동기 대기하면 데드락이 나므로 조립은 스레드풀에서 한다
        // 주의: Build 안에서 Services의 정적 멤버를 읽으면 정적 초기화 잠금으로 데드락이 난다. 전부 지역 변수로 만들어 돌려받는다.
        var built = Task.Run(Composition.Build).GetAwaiter().GetResult();
        Clock = built.Clock; Devices = built.Devices; Store = built.Store; DatabasePath = built.Path; Simulator = built.Simulator;
    }

}

/// <summary>조립 본체. Services와 분리한 이유: Services의 정적 초기화 중에 다른 스레드가 Services의 정적 멤버를 부르면 잠금으로 멈춘다.</summary>
internal static class Composition
{
    public const int FieldSeed = 12345;

    public static (VirtualTimeProvider Clock, DeviceBundle Devices, SqliteResultStore Store, string Path, ISimulator Simulator) Build()
    {
        var clock = new VirtualTimeProvider();
        // 장치: 인메모리 전송 위의 SCPI SMU + Modbus TCP IO(실제 프로토콜 프레임을 주고받는다)
        var field = new ResistanceField(FieldSeed);
        var devices = DeviceSetFactory.CreateWithProtocols(clock, field, FieldSeed);

        // 이력: 사용자 로컬 데이터 폴더의 SQLite 파일
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wafer-sequence-sim");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "runs.db");
        var store = new SqliteResultStore(dbPath);
        store.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();

        var engine = new SequenceEngine(devices, new WaitHelper(clock), clock, new RsCalculator(), new SitePatternProvider(), store, new LogDigest());
        // 가상 SMU가 웨이퍼별 분포를 쓰도록 현재 웨이퍼 ID를 넘긴다
        engine.SnapshotChanged += s =>
        {
            if (devices.SmuServer is { } smu && s.CurrentWaferId is { } wafer && smu.CurrentWaferId != wafer)
                smu.CurrentWaferId = wafer;
        };
        return (clock, devices, store, dbPath, engine);
    }
}
