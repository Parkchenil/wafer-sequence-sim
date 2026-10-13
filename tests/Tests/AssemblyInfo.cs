using Xunit;

// 즉시 모드(VirtualTimeProvider.Instant)는 "스레드풀이 조용해지면 다음 타이머"라는 휴리스틱으로 가상 시간을 넘긴다.
// 테스트 클래스를 병렬로 돌리면 다른 테스트(특히 3D 래스터라이저)의 CPU 작업이 그 판단을 흔들어 통신 타임아웃이 거짓으로 난다.
// 그래서 이 어셈블리의 테스트는 직렬로 돈다(CI 러너에서 재현된 간헐 실패의 원인).
[assembly: CollectionBehavior(DisableTestParallelization = true)]
