# Minecraft Hybrid Server Builder — 간이 설계서 v0.2

> v0.1 대비 변경: 비트 패킹 제외(모드 버전에서 검토), 경량화 프리셋(일반/중간/저/최저) 도입, 최적화 프로필을 개발 초기 단계로 이동, IPC 직렬화 방식을 MessagePack으로 변경, RCON 활용 추가.

## 1. 프로젝트 개요

**목표:** Minecraft Java Edition Fabric 서버의 모드 호환성을 유지하면서, 하드웨어에 맞춘 경량화 프리셋과 검증된 최적화 모드를 자동 구성해 저사양 PC에서도 안정적으로 서버를 운영할 수 있는 빌드·관리 프로그램을 개발한다.

| 항목 | 설계 기준 |
|---|---|
| 서버 기반 | Minecraft Java Edition / Fabric |
| 개발 언어 | C# (.NET), Java |
| 웹 프레임워크 | ASP.NET Core |
| 개발 OS | Windows 11 |
| 추후 지원 OS | Linux |
| 목표 하드웨어 | CPU 6코어 / RAM 12GB (최저 단계는 그 이하 고려) |
| 서버 JVM 힙 | 프리셋별 3~6GB, 하드웨어 측정 후 자동 결정 |
| 실시간 처리 목표 | 20 TPS / 틱당 50ms 이하 |
| 확장성 | Fabric 모드 추가 및 관리 |
| 핵심 전략 | 경량화 프리셋, 최적화 모드 자동 구성, 청크 사전 생성, 비동기 작업 분리 |

## 2. 핵심 아키텍처

```
Minecraft Client
      │  기존 Minecraft 프로토콜 / TCP
      ▼
Fabric Game Server (Java)
  게임 틱 · 이동 · 전투 · 몹 AI · 블록 · 모드 처리
  + 최적화 모드 묶음 (Lithium, FerriteCore, ModernFix, Krypton, spark, Chunky, [C2ME])
      │
      ├─ FabricBridge (자체 Fabric 모드)
      │    TPS/MSPT·이벤트 수집 · 비동기 큐 · 메시지 배치
      │        │  로컬 소켓 / MessagePack
      │        ▼
      └─ RCON ──────────► ASP.NET Core 관리 서비스
                            웹 API · 모니터링 · 프리셋 관리 · 작업 스케줄링
                                 ├─ Storage Worker (로그 · 백업 · 통계 저장)
                                 └─ Web Dashboard (서버 상태 · 설정 · 모드 관리)
```

**설계 원칙**
- 게임 상태의 최종 변경 권한은 Fabric 서버가 갖는다.
- 웹 서비스는 게임 상태를 직접 수정하지 않으며, 관리 명령은 RCON 또는 FabricBridge를 통해 서버에 요청한다.
- 관리 서비스가 중단되어도 게임은 계속 진행된다.

## 3. 통신 및 데이터 처리 설계

| 구분 | 처리 기술 | 담당 기능 |
|---|---|---|
| 실시간 통신 | Minecraft TCP 프로토콜 | 플레이어·월드 동기화 |
| 내부 통신 | 로컬 소켓 + MessagePack | FabricBridge → C# 서비스 (지표·이벤트) |
| 콘솔 명령 | RCON | C# 서비스 → 서버 (명령 실행, 설정 즉시 반영) |
| 웹 통신 | HTTP API | 관리·통계·외부 연동 |
| 비동기 처리 | Queue / Worker | 로그·통계·백업 |

### 메시지 규격

- 길이 접두사(length-prefixed) 프레임 + MessagePack 본문.
- 모든 메시지에 `version`, `type`, `timestamp` 필드를 포함한다.
- 알 수 없는 필드는 무시하여 하위 호환성을 유지한다.
- 이벤트는 일정 주기로 묶어 배치 전송한다.

### 비트 패킹에 대하여

로컬 IPC에서는 대역폭이 병목이 아니므로 서버 빌더 버전에서는 비트 패킹을 적용하지 않는다. 향후 **모드 버전**(게임 데이터 경로를 직접 다루는 경우)이나 장기 통계 저장 포맷에서 재검토한다.

## 4. 경량화 프리셋

사용자는 **일반 / 중간 / 저 / 최저** 4단계 중 하나를 선택한다. 첫 실행 시 하드웨어를 측정해 단계를 추천한다.

| 항목 | 일반 | 중간 | 저 | 최저 |
|---|---|---|---|---|
| `view-distance` | 10 | 8 | 6 | 4 |
| `simulation-distance` | 8 | 6 | 5 | 3 |
| `entity-broadcast-range-percentage` | 100 | 85 | 70 | 50 |
| 권장 최대 인원 | 10~20 | 8~12 | 4~8 | 2~4 |
| JVM 힙 (`-Xms` = `-Xmx`) | 6GB | 5GB | 4GB | 3GB |
| GC | G1GC 튜닝 | G1GC 튜닝 | G1GC 튜닝 | G1GC + 짧은 일시정지 목표 |
| `sync-chunk-writes` | true | true | false | false |
| `network-compression-threshold` | 256 | 256 | 512 | 512 (LAN이면 -1) |
| 청크 사전 생성 | 선택 | 권장 | 필수 | 필수 + 월드 경계 고정 |
| 최적화 모드 | 기본 묶음 + C2ME | 기본 묶음 + C2ME(스레드 제한) | 기본 묶음 | 기본 묶음 |
| 백그라운드 작업 | 실시간 통계 | 5초 단위 통계 | 30초 단위, 백업은 접속자 0명일 때 | 통계 최소화, 대시보드 수동 갱신 |

**기본 묶음:** Lithium, FerriteCore, ModernFix, Krypton, spark, Chunky

### 프리셋 운용 규칙

1. **자동 추천:** 코어 수와 여유 RAM을 기준으로 단계를 추천한다. (예: 여유 RAM 8GB 이상·6코어 이상 → 일반, 여유 RAM 4GB 미만 → 최저)
2. **개별 덮어쓰기:** `프리셋 기본값 → 사용자 오버라이드` 순서로 병합한다.
3. **재시작 필요 여부 표시:** 각 설정 항목에 `requiresRestart` 플래그를 둔다. JVM 힙·모드 구성은 재시작 필요, 시야·시뮬레이션 거리는 즉시 반영 가능.
4. **안전 장치:** `sync-chunk-writes=false` 단계에서는 자동 백업을 강제 활성화한다.
5. **적응형 조정(5차):** 95백분위 MSPT가 일정 시간 50ms를 넘으면 한 단계 낮추기를 제안한다.

```json
{
  "preset": "low",
  "overrides": {
    "server.view-distance": 8
  }
}
```

## 5. 비동기 처리 우선순위

| 구분 | 대상 |
|---|---|
| 우선 분리 | 로그, 통계, 백업, 외부 API 호출, 웹 대시보드 데이터 |
| 검증 후 분리 | 청크 생성 보조 작업, 대량 데이터 압축, 복잡한 모드 계산 |
| 게임 코어에 유지 | 이동, 충돌, 공격 판정, 블록 상태 변경, 인벤토리·거래 확정 |

백그라운드 큐에는 용량 제한을 두고, 가득 차면 중요도가 낮은 이벤트를 병합하거나 폐기한다. 반드시 저장해야 하는 데이터는 별도 경로로 처리해 유실을 막는다.

## 6. 프로그램 구성

```
MinecraftHybridBuilder/
├── Builder.Core/        # 설정·프리셋·서버·모드 관리, 하드웨어 감지
├── Builder.Cli/         # Windows/Linux 실행 도구
├── Builder.Web/         # ASP.NET Core API + 대시보드
├── Builder.Worker/      # 비동기 작업 처리 (초기엔 Web 내부 BackgroundService)
├── FabricBridge/        # Java 기반 Fabric 모드
├── Protocol/            # MessagePack 메시지 규격
├── Presets/             # 프리셋 정의 JSON
├── Mods/                # 최적화 모드 카탈로그 (모드별 소스 ID)
└── Tests/               # 기능 및 성능 테스트
```

### 외부 연동

- **Fabric Meta API:** 게임·로더·인스톨러 버전 조회 및 서버 런처 jar 다운로드
- **NeoForge Maven (maven.neoforged.net):** `maven-metadata.xml`로 버전 조회(1.X.Y → X.Y.빌드, 26.N[.P] → 26.N.P.빌드), 인스톨러 `--install-server`로 설치, `@libraries/net/neoforged/neoforge/<버전>/win_args.txt|unix_args.txt`로 실행. 로더는 `IServerLoader`로 추상화하며 `switch-loader`로 같은 Minecraft 버전 안에서 Fabric ↔ NeoForge 전환을 지원한다 (2026-10-19 추가)
- **Modrinth API (기본 모드 소스):** 모드 검색, 버전 호환성, 의존성 확인. API 키 불필요, SHA-512 해시 제공
- **CurseForge API (추후 확장):** 아래 "모드 소스 구조"에 맞춰 필요할 때 추가한다.
- **Adoptium API:** Java 런타임 미설치 시 다운로드. 필요한 Java 버전은 Mojang 버전 메타데이터(`javaVersion.majorVersion`)에서 읽는다 (2026-10 기준 최신 26.3은 Java 25 필요)

### 모드 소스 구조

모드 설치기는 사이트별 구현을 직접 부르지 않고 공통 규격 `IModSource`만 사용한다.

```
ModInstaller (버전 선택 · 의존성 · 해시 검사 · 설치 기록)
  └─ IModSource
       ├─ ModrinthSource    (구현됨)
       └─ CurseForgeSource  (추후 확장)
```

- `Mods/catalog.json`의 각 모드는 소스별 프로젝트 ID를 가진다: `"sources": { "modrinth": "lithium", "curseforge": "<ID>" }`. 소스 우선순위 목록 순서대로 찾고, 없으면 다음 소스로 넘어간다.
- 설치 기록(`builder.json`의 `mods`)에 소스·프로젝트·버전 ID·해시를 남겨, 업데이트도 같은 소스에서 한다.
- 같은 모드 판별은 jar 안 `fabric.mod.json`의 모드 ID로 한다. 출처와 관계없이 중복 설치를 막고, 사용자가 직접 넣은 모드는 건드리지 않는다.
- 정식(release) 버전을 우선하며, 알파·베타는 사용자가 허용한 경우에만 설치한다. 맞는 버전이 없으면 건너뛰고 보고한다.

**CurseForge 추가 시 고려 사항 (추후 확장)**
1. **API 키:** 필수. 저장소·배포본에 넣지 않고 사용자가 환경 변수 또는 개인 설정으로 입력한다. 키가 없으면 CurseForge 소스는 비활성화한다.
2. **해시:** SHA-1/MD5를 제공하므로 공통 해시 형식(`FileHash`의 알고리즘 지정)으로 처리한다.
3. **배포 제한 모드:** 제작자가 외부 배포를 막은 파일은 다운로드 주소가 없다. 실패로 끝내지 않고 페이지 링크를 안내해 사용자가 직접 `mods` 폴더에 넣게 한다 (`ModRelease.DownloadUrl = null` 처리 경로가 이미 있음).
4. **이용 약관:** CurseForge API 이용 약관과 모드별 라이선스를 확인한 뒤 연동한다.

## 7. 최적화 전략

| 항목 | 전략 |
|---|---|
| CPU | 시뮬레이션 거리 우선 조정, 메인 틱 우선, 백그라운드 작업량 제한 |
| RAM | 프리셋별 힙 설정. 추천은 전체 RAM - OS 몫(Windows 약 3GB) 기준, JVM 오버헤드(1.5~2GB) 포함. 현재 여유 RAM은 부족분 안내에만 사용 |
| 디스크 | 청크 사전 생성, 비동기 I/O, 배치 저장, 백업 분리 |
| 네트워크 | 압축 임계값 조정, 엔티티 전송 범위 축소 |
| 모드 | 검증된 최적화 모드 자동 구성, 버전·의존성 검사 |
| 안정성 | 관리 서비스 장애 시 게임 진행 유지, 위험 설정 시 백업 강제 |
| OS | 플랫폼 독립적인 경로 및 프로세스 관리 |

## 8. 개발 단계

| 단계 | 개발 내용 | 결과물 |
|---|---|---|
| 1차 | Java 감지·설치, Fabric 설치·실행, EULA·설정 자동화 | 서버 빌더 CLI |
| 2차 | 하드웨어 감지, 경량화 프리셋, 최적화 모드 묶음, 청크 사전 생성 | 최적화 프로필 |
| 3차 | Modrinth 기반 모드 설치·의존성 관리 | 모드 관리자 |
| 4차 | FabricBridge, MessagePack IPC, RCON 연동 | 내부 통신 시스템 |
| 5차 | 웹 API·대시보드·비동기 작업, 적응형 단계 조정 | 관리 웹 서비스 |
| 6차 | Linux 지원·배포 자동화 | 크로스플랫폼 빌더 |
| 추후 | CurseForge 모드 소스 (6장 "모드 소스 구조" 참조) | 모드 소스 확장 |
| 추후 | 모드 버전, 비트 패킹 기반 데이터 최적화 | 자체 최적화 모드 |

## 9. 성능 검증 기준

동일한 월드, 모드, 플레이어 부하 조건에서 **프리셋 단계별로** 다음 지표를 비교한다.

- **TPS / MSPT:** 목표 20 TPS, 평균과 95백분위 MSPT
- **CPU:** 메인 틱과 백그라운드 작업 사용량
- **RAM:** JVM 힙, 전체 프로세스 메모리, GC 일시정지 시간
- **디스크:** 청크 저장 지연, 백업 소요 시간
- **모드 호환성:** 추가·삭제·업데이트 이후 정상 동작 여부

측정에는 spark 프로파일러와 FabricBridge 지표를 함께 사용한다.

## 10. MVP 개발 범위

1. Java 감지·설치 후 Fabric 서버를 자동 설치·실행하는 C# CLI
2. 일반/중간/저/최저 프리셋과 오버라이드를 JSON으로 관리
3. 프리셋에 따른 `server.properties`, JVM 인자, 최적화 모드 자동 구성
4. 서버 상태(TPS, MSPT, RAM, CPU) 확인 — 초기엔 spark/RCON 기반, 이후 FabricBridge로 확장

## 11. 최종 설계 방향

기존 Minecraft 게임 코어는 그대로 유지하고, C# 빌더가 하드웨어에 맞는 경량화 프리셋과 최적화 모드를 자동 구성한다. 부가 기능은 관리 서비스로 분리하며, 실측 성능과 모드 호환성을 기준으로 점진적으로 확장한다. 비트 단위 최적화는 향후 모드 버전에서 재검토한다.
