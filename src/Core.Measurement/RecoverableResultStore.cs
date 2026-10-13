using System.Text.Json;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using Contracts;
namespace Core.Measurement;

// 측정·실행·로그를 먼저 flush(true)로 보존한다. DB 반영 뒤 ACK도 동일 저널에 남긴다.
// 재시작 시 미확인 항목만 재생한다. DB의 기본키 upsert가 중복 반영을 방지한다.
public sealed class RecoverableResultStore : IResultStore, IStorageRecovery, IRunFinalizer
{
    private sealed record Entry(string Id, string Kind, string RunId, string Payload, bool Ack = false);
    private readonly IResultStore inner;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ConcurrentDictionary<string, Entry> pending = new();
    private readonly List<Entry> replayOrder = [];
    private int count;
    public bool InjectWriteFailure { get; set; }
    public int PendingCount => Volatile.Read(ref count);
    public string JournalPath { get; }
    public string JournalStatus { get; private set; } = "저널 정상";
    public bool JournalBlocked { get; private set; }
    private readonly long rotateBytes, maximumPendingBytes;
    public bool IsWaferPending(string runId, string waferId) => pending.Values.Any(e => e.Kind == "wafer" && e.RunId == runId && JsonSerializer.Deserialize<WaferSummary>(e.Payload)!.WaferId == waferId);
    private void EnsureWritable() { if (JournalBlocked) throw new IOException(JournalStatus); }
    private string ArchiveName(string reason) => JournalPath + ".archive-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfffffff") + "-" + Guid.NewGuid().ToString("N") + "." + reason;
    private void Rotate()
    {
        if (pending.Count != 0 || !File.Exists(JournalPath) || new FileInfo(JournalPath).Length < rotateBytes) return;
        // ACK가 모두 영속화된 파일만 이름을 바꾼다. 중단 뒤 active가 없어도 미확인 자료는 없다.
        File.Move(JournalPath, ArchiveName("acked"));
        using var fresh = new FileStream(JournalPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        fresh.Flush(true);
    }
    public RecoverableResultStore(IResultStore inner, string path, long rotateBytes = 8 * 1024 * 1024, long maximumPendingBytes = 128 * 1024 * 1024)
    {
        this.inner = inner; JournalPath = path;
        this.rotateBytes = Math.Max(1, rotateBytes); this.maximumPendingBytes = Math.Max(1, maximumPendingBytes);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (Directory.EnumerateFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".archive-*.tail-original").Any()) JournalStatus = "과거 마지막 줄 손상 원본 보관됨 · 미완성 기록 확인 필요";
            var ready = path + ".repair-ready";
            // 교체 파일을 먼저 flush한 뒤 원본을 보관하므로 두 rename 사이의 중단도 복구 가능하다.
            var commit = ready + ".commit";
            if (File.Exists(ready))
            {
                var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ready)));
                if (File.Exists(commit) && File.ReadAllText(commit) == hash)
                {
                    if (File.Exists(path)) File.Move(path, ArchiveName("tail-original"));
                    File.Move(ready, path);
                    JournalStatus = "마지막 줄 손상 복구 재개 · 원본 archive 보존, 마지막 미완성 기록 확인 필요";
                }
                else
                {
                    File.Move(ready, ArchiveName("uncommitted-repair"));
                    if (!File.Exists(path)) throw new IOException("교체 원본/완료 표식 미확인 · 자동 복구 차단");
                }
            }
            if (File.Exists(commit)) File.Move(commit, ArchiveName("repair-commit"));
            if (!File.Exists(path)) return;
            var bytes = File.ReadAllBytes(path);
            var lines = System.Text.Encoding.UTF8.GetString(bytes).Split('\n');
            var nonemptyLast = lines.Length - 1;
            while (nonemptyLast >= 0 && string.IsNullOrWhiteSpace(lines[nonemptyLast])) nonemptyLast--;
            var valid = new List<string>();
            for (var i = 0; i <= nonemptyLast; i++)
            {
                try
                {
                    var e = JsonSerializer.Deserialize<Entry>(lines[i]) ?? throw new JsonException("빈 기록");
                    if (string.IsNullOrEmpty(e.Id) || string.IsNullOrEmpty(e.RunId) || e.Kind is not ("run" or "wafer" or "log")) throw new JsonException("기록 식별자 오류");
                    if (e.Ack)
                    {
                        if (!pending.TryGetValue(e.Id, out var original) || original.Kind != e.Kind || original.RunId != e.RunId || e.Payload != "") throw new JsonException("ACK 대조 실패");
                        pending.TryRemove(e.Id, out _); replayOrder.RemoveAll(x => x.Id == e.Id);
                    }
                    else
                    {
                        using var payload = JsonDocument.Parse(e.Payload);
                        if (!pending.TryAdd(e.Id, e)) throw new JsonException("중복 미확인 식별자");
                        replayOrder.Add(e);
                    }
                    if (i == nonemptyLast && bytes.Length > 0 && bytes[^1] != (byte)'\n')
                    {
                        valid.Add(lines[i]);
                        throw new JsonException("마지막 줄 종료 표식 없음 · 유효 기록은 보존");
                    }
                    valid.Add(lines[i]);
                }
                catch (Exception error) when (error is JsonException or ArgumentException)
                {
                    if (i != nonemptyLast) throw new IOException($"저널 중간 손상 · {i + 1}행 · 원본 보존 / 저장 차단", error);
                    using (var replacement = new FileStream(ready, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    {
                        var prefix = System.Text.Encoding.UTF8.GetBytes(string.Join("\n", valid) + (valid.Count > 0 ? "\n" : ""));
                        replacement.Write(prefix); replacement.Flush(true);
                    }
                    using (var marker = new FileStream(commit, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    {
                        marker.Write(System.Text.Encoding.UTF8.GetBytes(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ready))))); marker.Flush(true);
                    }
                    File.Move(path, ArchiveName("tail-original"));
                    File.Move(ready, path);
                    File.Move(commit, ArchiveName("repair-commit"));
                    JournalStatus = $"마지막 줄 손상 · {i + 1}행 원본 archive 보존 · 유효 prefix 복구 / 미완성 기록 확인 필요";
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            JournalBlocked = true; JournalStatus = "저널 복구 차단: " + error.Message;
        }
        count = pending.Count;
    }
    private void Append(Entry entry)
    {
        EnsureWritable();
        using var stream = new FileStream(JournalPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry) + "\n");
        stream.Write(bytes); stream.Flush(true);
    }
    private async Task Apply(Entry e, CancellationToken ct)
    {
        // 실제 저장소 경로 안에서 주입된 IO 실패가 발생한다.
        if (inner is SqliteResultStore sqlite) sqlite.InjectWriteFailure = InjectWriteFailure;
        else if (InjectWriteFailure) throw new IOException("가상 DB 쓰기 실패");
        switch (e.Kind)
        {
            case "wafer": await inner.SaveWaferAsync(e.RunId, JsonSerializer.Deserialize<WaferSummary>(e.Payload)!, ct); break;
            case "run":
                var run = JsonSerializer.Deserialize<RunRecord>(e.Payload)!;
                if (inner is IRunFinalizer finalizer) await finalizer.FinalizeRunAsync(run, ct); else await inner.SaveRunAsync(run, ct);
                break;
            case "log": await inner.SaveLogAsync(e.RunId, JsonSerializer.Deserialize<StepEvent[]>(e.Payload)!, ct); break;
        }
    }
    private async Task Save(string kind, string run, object value, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            EnsureWritable();
            Rotate();
            if (pending.Count >= 256) throw new IOException("복구 대기 256건 한도 · 안전 정지");
            var entry = new Entry(Guid.NewGuid().ToString("N"), kind, run, JsonSerializer.Serialize(value));
            if (pending.Values.Sum(e => (long)System.Text.Encoding.UTF8.GetByteCount(e.Payload)) + System.Text.Encoding.UTF8.GetByteCount(entry.Payload) > maximumPendingBytes) throw new IOException("미확인 저널 용량 한도 · 원본 보존 후 안전 정지");
            Append(entry); pending.TryAdd(entry.Id, entry); replayOrder.Add(entry); Volatile.Write(ref count, pending.Count);
            // 이전 장애의 같은 대상 갱신을 먼저 반영해야 최신 ACK 뒤 복구가 과거 상태를 덮지 않는다.
            foreach (var queued in replayOrder.Where(e => e.Kind == kind && e.RunId == run &&
                (kind != "wafer" || JsonSerializer.Deserialize<WaferSummary>(e.Payload)!.WaferId == ((WaferSummary)value).WaferId)).ToArray())
            {
                try { await Apply(queued, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) when (error is IOException or Microsoft.Data.Sqlite.SqliteException) { return; }
                Append(queued with { Ack = true, Payload = "" }); pending.TryRemove(queued.Id, out _); replayOrder.RemoveAll(x => x.Id == queued.Id); Volatile.Write(ref count, pending.Count);
            }
        }
        finally { gate.Release(); }
    }
    public async Task RecoverAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            EnsureWritable();
            // 동일 키의 시작/종료 기록은 저널에 쓰인 순서대로 재생한다. 해시 열거 순서는 사용하지 않는다.
            foreach (var entry in replayOrder.ToArray())
            {
                await Apply(entry, ct);
                Append(entry with { Ack = true, Payload = "" }); pending.TryRemove(entry.Id, out _); replayOrder.RemoveAll(x => x.Id == entry.Id); Volatile.Write(ref count, pending.Count);
            }
            Rotate();
        }
        finally { gate.Release(); }
    }
    public Task InitializeAsync(CancellationToken ct) => inner.InitializeAsync(ct);
    public Task SaveRunAsync(RunRecord run, CancellationToken ct) => Save("run", run.RunId, run, ct);
    public Task FinalizeRunAsync(RunRecord run, CancellationToken ct) => SaveRunAsync(run, ct);
    public Task SaveWaferAsync(string runId, WaferSummary wafer, CancellationToken ct) => Save("wafer", runId, wafer, ct);
    public Task SaveLogAsync(string runId, IReadOnlyList<StepEvent> log, CancellationToken ct) => Save("log", runId, log, ct);
    public Task<IReadOnlyList<RunRecord>> ListRunsAsync(CancellationToken ct) => inner.ListRunsAsync(ct);
    public Task<IReadOnlyList<WaferSummary>> GetWafersAsync(string id, CancellationToken ct) => inner.GetWafersAsync(id, ct);
    public Task<IReadOnlyList<StepEvent>> GetLogAsync(string id, CancellationToken ct) => inner.GetLogAsync(id, ct);
}
