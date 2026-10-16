## 1. 지적 목록

### A-01 / `CountJob.Execute()` / 병렬 쓰기 제한 위반과 비원자적 증가
- **문제:** **코드상 사실:** `cellCounts[cell]++`는 두 가지 문제가 있습니다.
  1. `IJobParallelFor`의 기본 쓰기 제한은 실행 인덱스에 대응하는 요소를 대상으로 합니다. 여기서는 `i`와 무관한 `cell`에 씁니다.
  2. 여러 실행이 같은 셀의 읽기→증가→쓰기를 동시에 수행하므로 데이터 경쟁이 발생합니다. `++`는 원자 연산이 아닙니다.
- **발생 조건:** 안전성 검사가 활성화된 환경에서는 허용 범위 밖 쓰기 때 오류가 발생할 수 있습니다. 검사가 없는 환경에서는 같은 셀로 분류되는 개체들이 병렬 실행될 때 갱신이 유실됩니다.
- **영향:** 에디터 오류와 빌드의 잘못된 집계 결과를 설명합니다. 다만 **자료 부족: 실제 예외 전문과 스택 트레이스**. 이 접근 위반이 보고된 `InvalidOperationException`과 정확히 같은 오류라고 단정할 수는 없습니다.
- **심각도:** 높음
- **확신도:** 결함은 높음, 관찰된 예외와의 정확한 대응은 미확인
- **검증 방법:** 모든 위치를 동일한 셀에 고정하고 집계만 반복합니다. 수정 후 매번 해당 셀이 정확히 `count`, 전체 셀 합도 `count`여야 합니다. 안전성 검사 활성화 여부에 관계없이 확인합니다.
- **대응 방법:** 아래의 청크별 독립 히스토그램과 병렬 합산을 사용합니다. 쓰기 제한 속성만 해제하면 데이터 경쟁은 남습니다.

### A-02 / `_cellCounts` 갱신 경로 / 프레임마다 초기화하지 않음
- **문제:** **코드상 사실:** 배열은 생성 시 기본적으로 0으로 초기화되지만, 이후 프레임에는 초기화되지 않습니다.
- **발생 조건:** 두 번째 `Update()`부터.
- **영향:** 프레임별 개체 수를 의도했다면 결과가 틀립니다. 경쟁을 제거해도 현재 방식은 누적 집계가 됩니다. 장시간 누적하면 정수 범위 초과도 가능합니다.
- **심각도:** 중간
- **확신도:** 초기화 누락은 높음, 버그 여부는 집계 사양에 종속
- **검증 방법:** 같은 입력으로 두 프레임 집계합니다. 프레임별 집계라면 두 프레임 모두 전체 합이 `count`여야 합니다.
- **대응 방법:** 매 집계에서 출력 셀을 새 값으로 덮어씁니다. 별도 초기화 작업 없이 아래 합산 Job으로 해결할 수 있습니다.

### A-03 / `Start()`의 위치 초기화 / 실행마다 입력 자체가 달라짐
- **문제:** **코드상 사실:** `UnityEngine.Random`의 전역 상태에서 위치를 생성하며, 이 코드에는 고정 시드 설정이 없습니다.
- **발생 조건:** 실행 사이의 전역 난수 상태가 다르거나 다른 코드가 난수를 소비할 때.
- **영향:** 데이터 경쟁을 제거해도 실행마다 `cell0`가 달라질 수 있습니다. 출력 차이 전체를 경쟁 탓으로 돌리면 검증이 잘못됩니다.
- **심각도:** 중간
- **확신도:** 높음
- **검증 방법:** 동일한 위치 배열을 재사용하거나 로컬 고정 시드로 초기화한 뒤 같은 프레임의 결과를 비교합니다.
- **대응 방법:** 독립적인 `Unity.Mathematics.Random`을 고정된 0이 아닌 시드로 생성합니다. 실제 이동까지 비교하려면 초기 속도와 시간 입력도 같아야 합니다.

### A-04 / `Start()`의 `_positions`, `_velocities` 할당 / 장기 보관 배열에 `TempJob` 사용
- **문제:** **코드상 사실:** 두 배열을 여러 프레임 보관하지만 `Allocator.TempJob`을 사용합니다. 이 할당자는 4프레임 이내 해제를 전제로 합니다.
- **발생 조건:** 컴포넌트가 그보다 오래 살아 있을 때.
- **영향:** 할당자 수명 위반 진단의 원인이 됩니다. 이를 종료 시 오류나 특정 예외의 직접 원인으로 확정하려면 로그가 필요합니다.
- **심각도:** 높음
- **확신도:** 수명 위반은 높음, 관찰된 종료 오류와의 대응은 미확인
- **검증 방법:** 변경 전후로 수십 프레임 이상 실행하고 할당자 관련 경고·스택을 비교합니다.
- **대응 방법:** 컴포넌트가 소유하는 장기 배열은 `Allocator.Persistent`로 할당하고 명시적으로 해제합니다.

### A-05 / `OnDestroy()` / Job 완료 및 생성 여부 확인 없이 해제
- **문제:** **코드상 사실:** 진행 중인 Job을 완료하지 않고 배열을 해제하며, `IsCreated`도 확인하지 않습니다.
- **발생 조건:** 예약 후 `LateUpdate()` 전에 파괴되는 경우, 초기화 전 파괴되는 경우, 초기화가 일부만 성공한 경우 등.
- **영향:** 안전성 검사가 활성화되면 사용 중이거나 생성되지 않은 컨테이너 해제 오류가 발생할 수 있습니다. 검사 없는 환경에서 실행 중인 Job의 메모리를 해제하면 잘못된 메모리 접근 위험이 있습니다.
- **심각도:** 높음
- **확신도:** 방어 누락은 높음, 실제 종료 시 발생 경로는 미확인
- **검증 방법:** 초기화 전 파괴, 실행 중 씬 전환, 예약 후 `LateUpdate()` 전 파괴를 각각 시험하고 종료 스택을 확인합니다.
- **대응 방법:** 마지막으로 예약된 모든 작업을 대표하는 핸들을 완료한 다음, 생성된 배열만 해제합니다.

### A-06 / `Update()`의 핸들 저장 / 첫 작업 예약 직후 소유 핸들을 잃을 수 있음
- **문제:** **코드상 사실:** `h1`은 지역 변수이고, `_handle`은 두 번째 예약이 성공한 후에만 갱신됩니다.
- **발생 조건:** `CountJob` 예약은 성공했지만 `MoveJob` 예약이 예외로 실패할 때.
- **영향:** `_handle.Complete()`가 이번 프레임의 `CountJob`을 기다리지 못합니다. A-05의 종료 처리만 수정해도 이 경로는 해결되지 않습니다.
- **심각도:** 높음
- **확신도:** 높음
- **검증 방법:** 첫 예약 직후 테스트용 예외를 삽입하고 종료합니다. `_handle`이 첫 작업을 추적하는지 확인합니다.
- **대응 방법:** 예약이 성공할 때마다 즉시 `_handle`에 저장하고 다음 작업을 그 핸들에 연결합니다.

### A-07 / `LateUpdate()`의 `_positions[0]` / 빈 입력 처리 누락
- **문제:** **코드상 사실:** `count == 0`이면 첫 요소가 없습니다. 음수 값도 직렬화 필드에서 코드상 배제하지 않습니다.
- **발생 조건:** `count <= 0`.
- **영향:** 초기화 또는 요소 접근 오류가 발생합니다.
- **심각도:** 낮음
- **확신도:** 높음
- **검증 방법:** `count`를 0과 음수로 각각 설정합니다.
- **대응 방법:** 약 20,000개가 필수라면 초기화 전에 `count > 0`을 검증합니다. 빈 입력을 지원하려면 예약 및 첫 요소 접근 경로를 별도로 처리합니다.

## 2. 사양 확인이 필요한 항목

- **결정성의 범위:** 동일한 초기 상태·시간 입력에서 스케줄링과 무관한 결과를 요구하는지, 서로 다른 CPU와 Burst 설정에서도 이동 결과까지 비트 단위로 같아야 하는지 확인이 필요합니다. 아래 정수 집계는 전자를 해결하지만, 부동소수점 이동까지 포함한 플랫폼 간 결정성을 보장하지는 않습니다.
- **집계 시점:** 현재 의존성은 이동 **전** 위치 집계입니다. 아래 수정도 유지합니다. 이동 후 집계가 필요하면 순서를 바꿔야 합니다.
- **집계 의미:** 프레임별 개체 수인지 누적인지 확인이 필요합니다. 아래 수정은 프레임별 집계를 가정합니다.
- **이동 의도:** 기본 생성 옵션으로 `_velocities`는 모두 0입니다. 제공 코드만으로는 위치가 이동하지 않습니다. **자료 부족: 실제 속도 초기화·갱신 코드**.
- **셀 정의:** 현재 초기 위치의 x 범위에서는 셀 0~50만 사용하며 음수·양수 x가 합쳐집니다. 의도된 분류라면 오류가 아닙니다. 공간 격자 설계 변경은 **범위 밖**입니다.
- **실제 예외 식별:** **자료 부족: 에디터 및 종료 오류 전문, 스택 트레이스, Collections/Burst 패키지 버전, 빌드 안전성 검사 설정**. 아래 수정은 확인된 결함을 제거하지만, 제공되지 않은 예외까지 모두 같은 원인이라고 확정할 수는 없습니다.

## 3. 수정 방향

### 3-1. 장기 배열과 독립적인 초기 입력

모든 소유 배열을 `Persistent`로 생성합니다. 다음 필드를 추가하고 `Start()`의 할당·초기화 부분에 반영합니다.

```csharp
const int CellCount = 1024;
const int ChunkSize = 256;

NativeArray<int> _partialCounts;
int _chunkCount;
```

```csharp
if (count <= 0)
    throw new System.ArgumentOutOfRangeException(nameof(count));

_chunkCount = (count + ChunkSize - 1) / ChunkSize;

_positions = new NativeArray<float3>(count, Allocator.Persistent);
_velocities = new NativeArray<float3>(count, Allocator.Persistent);
_cellCounts = new NativeArray<int>(CellCount, Allocator.Persistent);
_partialCounts = new NativeArray<int>(
    checked(_chunkCount * CellCount), Allocator.Persistent);

var rng = new Unity.Mathematics.Random(12345u);
for (int i = 0; i < count; i++)
    _positions[i] = rng.NextFloat3Direction() * 50f;
```

これは初期化だけのメインスレッド処理であり、毎フレームの集計・移動はJobのままです。位置の分布を元の球内一様分布に近づけるなら、最後の式を次にします。

```csharp
_positions[i] =
    rng.NextFloat3Direction()
    * (50f * math.pow(rng.NextFloat(), 1f / 3f));
```

### 3-2. 固定チャンクごとのヒストグラムに置換

```csharp
using Unity.Collections.LowLevel.Unsafe;
```

```csharp
[BurstCompile]
struct CountChunksJob : IJobParallelFor
{
    [ReadOnly] public NativeArray<float3> positions;

    [NativeDisableParallelForRestriction]
    public NativeArray<int> partialCounts;

    public void Execute(int chunk)
    {
        int row = chunk * CellCount;

        for (int cell = 0; cell < CellCount; cell++)
            partialCounts[row + cell] = 0;

        int begin = chunk * ChunkSize;
        int end = math.min(begin + ChunkSize, positions.Length);

        for (int i = begin; i < end; i++)
        {
            int cell = (int)(math.abs(positions[i].x) % CellCount);
            partialCounts[row + cell]++;
        }
    }
}

[BurstCompile]
struct ReduceCountsJob : IJobParallelFor
{
    [ReadOnly] public NativeArray<int> partialCounts;
    public NativeArray<int> cellCounts;
    public int chunkCount;

    public void Execute(int cell)
    {
        int sum = 0;
        for (int chunk = 0; chunk < chunkCount; chunk++)
            sum += partialCounts[chunk * CellCount + cell];

        cellCounts[cell] = sum;
    }
}
```

- 制限解除の根拠は、各 `chunk` が専有する区間  
  `[chunk * CellCount, (chunk + 1) * CellCount)`  
  が重ならないことです。**元の共有セルへの `++` に属性を付けるだけの対処ではありません。**
- 各行をJob内で初期化し、最終出力は代入するため、前フレームの値は残りません。
- 約20,000個なら79チャンク、追加バッファは約316 KiBです。
- 個数の整数加算なので、この規模では加算順による丸め差もありません。

### 3-3. 予約成功ごとにハンドルを保存

既存の `MoveJob` はそのまま使用できます。

```csharp
_handle = new CountChunksJob
{
    positions = _positions,
    partialCounts = _partialCounts
}.Schedule(_chunkCount, 1, _handle);

_handle = new ReduceCountsJob
{
    partialCounts = _partialCounts,
    cellCounts = _cellCounts,
    chunkCount = _chunkCount
}.Schedule(CellCount, 64, _handle);

_handle = new MoveJob
{
    positions = _positions,
    velocities = _velocities,
    dt = Time.deltaTime
}.Schedule(_positions.Length, 64, _handle);
```

この構成は移動前の集計を保ち、各Job内部の並列処理も維持します。元コードの `h1` による読み書き依存関係自体は正しく、通常の `LateUpdate().Complete()` が成功する経路に、必ずフレーム間競合があるわけではありません。

### 3-4. 完了後に、生成済み配列だけ解放

```csharp
void OnDestroy()
{
    _handle.Complete();

    if (_positions.IsCreated) _positions.Dispose();
    if (_velocities.IsCreated) _velocities.Dispose();
    if (_cellCounts.IsCreated) _cellCounts.Dispose();
    if (_partialCounts.IsCreated) _partialCounts.Dispose();
}
```

これは3-3と併用してください。Job自体が失敗して `Complete()` が例外になる場合の復旧方針は、**資料不足: 実際の例外と継続・停止方針**。例外を握りつぶして無条件に解放する対処は避けます。

### 3-5. 修正後の検証

コードは実行未確認です。Unity上で次を確認します。

1. **競合検証:** 全位置を0に固定し、毎フレーム `cellCounts[0] == count`、他セルは0。
2. **初期化検証:** 固定位置で複数フレーム実行し、全セル合が毎回 `count`。
3. **決定性検証:** 同じ初期位置・速度・時間入力で全1024セルを比較。`cell0`だけでは不十分です。
4. **環境差検証:** エディター、安全性検査有効の開発ビルド、通常ビルドで同じ入力の集計を比較。
5. **寿命検証:** 長時間実行後のシーン終了、初期化前の破棄、予約直後の破棄、予約間のテスト用例外を確認。