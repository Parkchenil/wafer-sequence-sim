using Contracts;
using Core.Measurement;
using Core.Sequence;
using Devices;

namespace App.WinForms;

public static class Services
{
    private const int FieldSeed = 12345;
    private static bool _fake;
    private static readonly Lazy<(ISimulator Simulator, IResultStore? Store)> Built = new(() => _fake ? (new FakeSimulator(), null) : Task.Run(Build).GetAwaiter().GetResult());
    public static ISimulator Simulator => Built.Value.Simulator;
    public static IResultStore? Store => Built.Value.Store;
    public static void Configure(bool fake)
    {
        if (Built.IsValueCreated) throw new InvalidOperationException("시뮬레이터 생성 후 모드를 변경할 수 없음");
        _fake = fake;
    }

    private static (ISimulator Simulator, IResultStore? Store) Build()
    {
        // 장치와 저장소를 스레드풀에서 조립해 UI 동기 대기를 피한다.
        var clock = new VirtualTimeProvider();
        var field = new ResistanceField(FieldSeed);
        var devices = DeviceSetFactory.CreateWithProtocols(clock, field, FieldSeed);
        var directory = Environment.GetEnvironmentVariable("WAFER_SIM_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wafer-sequence-sim");
        Directory.CreateDirectory(directory);
        var store = new SqliteResultStore(Path.Combine(directory, "runs.db"));
        store.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        var engine = new SequenceEngine(devices, new WaitHelper(clock), clock, new RsCalculator(), new SitePatternProvider(), new RecoverableResultStore(store, Path.Combine(directory, "recovery.jsonl")), new LogDigest());
        // 레시피 seed와 웨이퍼 ID는 엔진이 IDeviceContext로 직접 넘긴다(스냅샷 중계 없음)
        return (engine, store);
    }
}
