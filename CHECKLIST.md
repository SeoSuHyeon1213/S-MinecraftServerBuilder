# 작업 체크리스트 — 1차 단계: 서버 빌더 CLI

## 관리 규칙
- 실제 수행과 필요한 검증이 끝난 항목만 체크한다.
- 실패·차단·미실행을 완료로 표시하지 않는다.
- 불필요한 항목은 삭제하거나 체크하지 않은 채 `해당 없음 — 이유`로 표시한다.
- 주요 단계가 끝나거나 작업 상태가 달라질 때 갱신한다.
- 검증 기록에는 실행 명령 또는 확인 방법과 실제 결과를 남긴다.
- 여러 작업을 병행하면 작업별 섹션이나 별도 파일로 기록을 구분한다.
- 지속적으로 적용할 규칙은 AGENTS.md에 기록한다.

## 목표와 범위
- 목표: 설계서 v0.2의 1차 단계(Java 감지·설치, Fabric 설치·실행, EULA·설정 자동화)와 MVP 2~3번(프리셋·오버라이드 JSON, server.properties·JVM 인자 자동 구성)을 C# CLI로 구현한다.
- 작업 대상: `Builder.Core`, `Builder.Cli`, `Presets/`, `Tests/Builder.Core.Tests`
- 완료 조건: `create`로 만든 서버가 필요한 Java 버전으로 Fabric을 정상 로드하고, 프리셋 값이 server.properties와 시작 스크립트에 반영된다.

## 현재 상태
- 진행 단계: 1차 단계 완료, 작업 2(Modrinth 모드 자동 설치) 완료 — 아래 "작업 2" 섹션 참조
- 다음 할 일: 작업 3(NeoForge 지원) Windows 빌드·테스트·실서버 검증 → Chunky 청크 사전 생성, 자동 백업(low/minimum 강제)
- 병행 작업: 작업 4(SQLite 지표 저장, `feature/sqlite-metrics` 브랜치) 1~3단계 완료, 원격 push 완료(2026-10-23). main 병합(PR)은 사용자 결정 대기 — 아래 "작업 4" 섹션 참조
- 차단 원인: 없음

## 1. 사전 확인
- [x] 적용되는 AGENTS.md와 관련 문서를 확인했다. (상위 AGENTS.md 없음 → 새로 작성, CLAUDE.md·설계서 확인)
- [x] 관련 코드와 설정, 기존 변경 상태를 확인했다. (문서 4개만 있는 빈 프로젝트, Git 저장소 아님)
- [x] 요청한 결과와 수정 범위를 파악했다.
- [x] 필요한 실행·검증 방법을 확인했다. (.NET SDK 10.0.301, 시스템 Java 8만 설치됨)

## 2. 실행
- [x] 솔루션 구조 (`MinecraftHybridBuilder.slnx`, `Directory.Build.props`, `.gitignore`)
- [x] 하드웨어 감지 (Windows `GlobalMemoryStatusEx`, Linux `/proc/meminfo`) 및 프리셋 추천
- [x] 경량화 프리셋 4종 JSON + 오버라이드 병합 (`server.<키>`, `jvm.heapMb`)
- [x] Java 탐색(빌더 런타임 → JAVA_HOME → PATH)과 Adoptium JRE 자동 설치 (SHA-256 검증)
- [x] Mojang 메타데이터로 필요한 Java 버전 결정, Fabric Meta API로 로더·인스톨러·런처 jar 다운로드
- [x] EULA 명시 동의, server.properties 줄 단위 병합, start.bat / start.sh 생성, builder.json
- [x] CLI 명령: `hardware`, `presets`, `versions`, `create`, `configure`, `start`
- [x] 단위 테스트 34개
- [x] 추천 기준을 전체 RAM 기반으로 변경, 부족분 안내와 start 시 여유 RAM 경고 추가

## 3. 검증
- [x] 변경한 동작이 완료 조건을 만족하는지 확인했다.
- [x] 영향받는 기존 동작을 필요한 범위에서 확인했다. (신규 프로젝트, 해당 범위는 전체)
- [x] 발견된 문제를 해결하고 필요한 검증을 다시 수행했다. (builder.json의 `+`가 `+`로 기록되는 문제 → 인코더 변경 후 재확인)

### 검증 기록
| 확인 항목 | 명령 또는 방법 | 결과 | 미실행·실패 이유 |
|---|---|---|---|
| 빌드 | `dotnet build MinecraftHybridBuilder.slnx` | 통과 (경고 0, 오류 0) | |
| 단위 테스트 | `dotnet test MinecraftHybridBuilder.slnx` | 통과 (34/34, 추천 기준 변경 후) | |
| 정보 명령 | `mchb hardware`, `presets`, `versions` | 통과 (12 논리 프로세서, 여유 RAM 1.8GB → 최저 추천·경고 출력) | |
| 입력 오류 처리 | EULA 미동의 create, 잘못된 오버라이드 키 | 통과 (종료 코드 1, 폴더 미생성) | |
| 실제 생성 | `mchb create servers/e2e --preset minimum --set jvm.heapMb=1536 --set server.motd=e2e --accept-eula` | 통과 (MC 26.3 / 로더 0.19.5 / Temurin JRE 25.0.4.1 자동 설치) | |
| EULA 실행 차단 | eula=false로 바꾼 뒤 `mchb start` | 통과 (실행 거부, 종료 코드 1) | |
| Fabric 기동 | eula=false 상태에서 start.bat과 같은 Java·인자로 직접 실행 | 통과 (Fabric Loader 0.19.5가 MC 26.3 로드, JVM 플래그 오류 없음, EULA 안내 후 정상 종료) | EULA 대리 동의를 피하기 위해 월드 생성·접속은 미실행 |
| configure | Minecraft가 다시 쓴 server.properties에 `configure --preset 저 --set server.view-distance=7 --unset server.motd` | 통과 (프리셋 값 반영, 다른 키 71줄 보존) | |
| 전체 RAM 기반 추천 | `mchb hardware` (전체 11.8GB, 여유 2.2GB) | 통과 (일반 추천, "약 5.8GB 더 확보" 안내) | |
| Java 탐지 | 사용자가 설치한 Oracle JDK 27 | 통과 (PATH의 javapath로 탐지, MC 26.3에는 요구 버전과 같은 관리 JRE 25 선택) | |
| start 시 RAM 경고 | 여유 RAM 부족 상태에서 `mchb start` | 미실행 | EULA 동의한 서버가 필요함 |
| Ctrl+C 종료 대기 | `mchb start` 중 Ctrl+C | 미실행 | 대화형 콘솔이 필요함 |
| Linux | start.sh, tar.gz JRE 설치, /proc/meminfo | 미실행 | Linux 환경 없음 (6차 단계) |

시험 서버 폴더 `servers/e2e`는 검증 후 삭제했다. 내려받은 JRE 25는 `%LOCALAPPDATA%\MinecraftHybridBuilder\runtimes\java-25`에 남아 있어 재사용된다.

## 4. 완료
- [x] 필요한 문서를 갱신했다. (AGENTS.md 작성, CLAUDE.md에 AGENTS.md 안내 추가, 설계서의 Java 21 문구 수정)
- [x] 변경 내용을 최종 검토했다.
- [x] 미완료 항목과 남은 한계를 기록했다.
- [x] 사용자에게 변경 내용과 검증 결과를 설명했다.

## 주요 결정
- 결정: 필요한 Java 버전을 Mojang 메타데이터에서 읽는다.
  - 이유: 설계서는 Java 21이라 했지만 실제 최신 26.3은 Java 25를 요구함 (2026-10-17 확인).
- 결정: low/minimum 프리셋에서 `-XX:+AlwaysPreTouch`를 뺀다.
  - 이유: 시작 시 힙 전체를 물리 메모리에 올려 여유 RAM이 적은 PC에서 스와핑을 유발할 수 있음.
- 결정 (2026-10-17 변경, 사용자 선택): 프리셋 추천은 **서버용 RAM = 전체 RAM - OS 몫**(Windows 3GB, Linux 1.5GB) 기준. 필요 RAM = 힙 + JVM 오버헤드 (일반 ≥8GB·6코어, 중간 ≥6.5GB·4코어, 저 ≥5.5GB, 그 외 최저). 코어 수는 논리 프로세서 기준.
  - 이유: 측정 순간의 여유 RAM은 브라우저·CurseForge·VS Code 등 잠시 열린 프로그램에 좌우되어 실제보다 낮은 단계를 추천함. 현재 여유 RAM은 "더 확보해야 할 RAM" 안내(hardware·create 출력, start 시 경고)에만 쓴다.
- 결정: 외부 패키지 없이 간단한 인자 파서를 사용한다 (Core·CLI 의존성 0).

## 남은 작업
- [x] 2차: 최적화 모드 묶음 설치 — 작업 2에서 완료
- [ ] 2차: Chunky 청크 사전 생성, 최저 단계 월드 경계 고정
- [ ] 2차: `requiresAutoBackup` 프리셋의 자동 백업 강제
- [ ] 2차: 설정 항목별 `requiresRestart` 메타데이터
- [ ] Ctrl+C 시 서버 저장 후 종료 대화형 확인

---

# 작업 2 — Modrinth 최적화 모드 자동 설치 (2026-10-17)

## 목표와 범위
- 목표: 프리셋별 최적화 모드를 Modrinth에서 자동 설치·업데이트·정리한다. CurseForge는 구조만 열어 두고 설계서에 추후 확장으로 기록한다 (사용자 결정).
- 작업 대상: `Builder.Core/Mods/`, `Builder.Core/Http/BuilderHttp.cs`(해시 일반화), `ServerService`, CLI, `Presets/*.json`, `Mods/catalog.json`, 설계서 6·7·8장, AGENTS.md
- 완료 조건: `create` 시 Minecraft 버전에 맞는 모드와 필수 의존성이 설치되고, 맞는 버전이 없는 모드는 건너뛴 이유가 표시되며, Fabric이 설치된 모드를 오류 없이 로드한다.

## 실행
- [x] 공통 규격 `IModSource` / `ModRelease` / `ModLookupResult`, `ModrinthSource` (정식 버전 우선, 알파·베타는 허용 시에만)
- [x] `FileHash`로 해시 방식 일반화 (sha512/sha256/sha1/md5) — Adoptium 호출부도 변경
- [x] `ModInstaller`: 필수 의존성 재귀 설치, 해시 검사, 임시 폴더 다운로드 후 이동, 설치 기록, 프리셋 제외 모드 제거(의존성은 유지), 사용자 jar 보호(fabric.mod.json 모드 ID 비교), 업데이트 실패 시 기존 버전 유지
- [x] `Mods/catalog.json`(7종), 프리셋 `mods` 목록 (C2ME는 일반·중간만)
- [x] `builder.json`: `manageMods`, `allowUnstableMods`, `mods` 기록
- [x] CLI: `create --no-mods --allow-unstable-mods`, `mods <폴더> [--update] [--allow-unstable-mods[=false]]`, `configure` 시 모드 동기화, `presets`에 모드 표시
- [x] create 실패 시 정리 로직이 하위 폴더(mods)도 지우도록 수정
- [x] 설계서 6장 "모드 소스 구조"·CurseForge 고려 사항, 7장 RAM 기준, 8장 추후 단계 갱신 / AGENTS.md 규칙 추가

## 검증 기록
| 확인 항목 | 명령 또는 방법 | 결과 | 미실행·실패 이유 |
|---|---|---|---|
| 빌드 | `dotnet build MinecraftHybridBuilder.slnx` | 통과 (경고 0, 오류 0) | |
| 단위 테스트 | `dotnet test MinecraftHybridBuilder.slnx` | 통과 (45/45, 모드 관련 11개 추가) | |
| 실제 생성 | `mchb create servers/e2e --preset 중간 --set jvm.heapMb=1536 --accept-eula` | 통과: Lithium·FerriteCore·Krypton·spark·Chunky + 의존성 fabric-api 설치. ModernFix(26.3용 없음)·C2ME(알파만 있음) 건너뜀과 이유 표시 | |
| Fabric 모드 로드 | eula=false 상태에서 start.bat과 같은 인자로 직접 실행 | 통과: 6개 모드 로드, 오류·의존성 충돌 없음 | 월드 로드 시 동작은 EULA 동의 실행이 필요해 미확인 |
| 업데이트·알파 허용 | `mchb mods servers/e2e --update --allow-unstable-mods` | 통과: C2ME 0.4.2-alpha 설치, 나머지 유지 | |
| 프리셋 변경 | `mchb configure servers/e2e --preset 저` | 통과: C2ME 제거, fabric-api 등 유지, 네트워크 조회 없이 유지 | |
| 사용자 jar 보호·해시 불일치·업데이트 실패 | 단위 테스트 (가짜 소스·가짜 CDN) | 통과 | |

시험 서버 폴더 `servers/e2e`는 검증 후 삭제했다.

## 주요 결정
- 결정: Modrinth만 구현하고 CurseForge는 `IModSource` 구조와 설계서 기록으로 대비한다.
  - 이유: Modrinth는 API 키가 없고 SHA-512·의존성 정보를 제공. CurseForge는 API 키와 배포 제한 문제가 있음 (사용자 결정).
- 결정: 맞는 버전이 없는 모드는 서버 생성을 막지 않고 건너뛰며, `mods --update`로 나중에 다시 찾는다.
  - 이유: 최신 Minecraft(26.3)에서 ModernFix는 미지원, C2ME는 알파만 있음을 실제로 확인.
- 결정: 의존성으로 설치한 모드(fabric-api 등)는 프리셋이 바뀌어도 지우지 않는다.
  - 이유: 사용자가 직접 넣은 모드가 의존할 수 있음.

## 남은 작업
- [ ] C2ME 스레드 수 제한 (설계서 4장: 중간 단계) — C2ME 설정 파일 연동
- [ ] 클라이언트 전용 모드 차단 (Modrinth `server_side` 확인) — 3차 모드 관리자에서 사용자 모드 검색 시 필요
- [ ] CurseForge 소스 (추후 확장, 설계서 6장)

---

# 작업 3 — NeoForge 지원과 로더 전환 (2026-10-19)

## 목표와 범위
- 목표: 서버 생성 시 Fabric/NeoForge를 고를 수 있고, 기존 서버를 Fabric ↔ NeoForge로 전환할 수 있게 한다. 모드 50개 안팎 운용을 위해 모드 수 기반 힙 경고를 추가한다.
- 작업 대상: `Builder.Core/Loaders/`(신규), `Server/`, `Mods/`, `Presets/Presets.cs`, `Builder.Cli`, `Mods/catalog.json`, 테스트, 문서

## 실행
- [x] `IServerLoader` 규격 + `FabricServerLoader`(기존 동작 이동) + `NeoForgeServerLoader`(maven 메타데이터, 인스톨러 `--install-server`, OS별 인자 파일 실행)
- [x] NeoForge 버전 규칙 `NeoForgeVersions` (1.X.Y → X.Y.빌드, 26.N[.P] → 26.N.P.빌드, `-beta` 불안정)
- [x] `builder.json` 스키마 2: `loader` 추가, `installerVersion` 선택 항목 (스키마 1은 Fabric으로 읽음)
- [x] start.bat / start.sh를 OS별 인자로 따로 생성 (NeoForge win_args / unix_args)
- [x] 모드 메타데이터: fabric.mod.json + neoforge.mods.toml/mods.toml의 `[[mods]]` modId (의존성 modId 제외)
- [x] 카탈로그 `loaders`(Krypton=Fabric 전용), `modrinth@neoforge`(C2ME → c2me-neoforge)
- [x] `switch-loader` 명령: 설치 → 빌더 모드 삭제 → 사용자 jar 보관/복원(`mods-disabled/<로더>/`) → 스크립트 재생성 → 모드 재설치. 월드가 있으면 `--confirm` 필수
- [x] `create --modloader`, 대화형 로더 선택, `versions --modloader neoforge`
- [x] 모드 수 기반 권장 힙 경고 (`ModdedHeapAdvisor`, start·mods 출력)
- [x] 단위 테스트 추가 (`LoaderTests.cs`, CoreTests 3개)

## 검증 기록
| 확인 항목 | 명령 또는 방법 | 결과 | 미실행·실패 이유 |
|---|---|---|---|
| NeoForge 버전 체계 | maven-metadata.xml 확인 (최신 26.3.0.90, 21.1.262, 26.1.2.122) | 확인 | |
| 빌드 | `dotnet build MinecraftHybridBuilder.slnx` | 미실행 | 작업 환경(Linux VM·클라우드)에 .NET SDK가 없고 설치 차단됨 → Windows에서 실행 필요 |
| 단위 테스트 | `dotnet test MinecraftHybridBuilder.slnx` | 미실행 | 위와 같음 |
| NeoForge 실제 생성·기동 | `mchb create servers/neo --modloader neoforge --preset minimum --accept-eula` 후 start | 미실행 | 위와 같음 |
| 로더 전환 | Fabric 서버에 `switch-loader ... neoforge --confirm` → 다시 fabric | 미실행 | 위와 같음 |

## 주요 결정
- 결정: 로더 전환은 같은 Minecraft 버전 안에서만 한다. 이유: 버전까지 바꾸면 월드 업그레이드가 겹쳐 되돌리기 어려움.
- 결정: 사용자 jar는 삭제하지 않고 `mods-disabled/<로더>/`로 옮긴다. 이유: AGENTS.md "사용자 jar 보호" 규칙, 전환을 되돌릴 수 있게 함.
- 결정: 권장 힙은 어림값(로더 기본 2048/2560MB + 모드당 64MB)으로 경고만 한다. 이유: 실측 근거가 없으므로 설정을 자동으로 바꾸지 않음. spark로 실측 후 조정 예정.
- 결정: 프리셋 힙 값(설계서 4장)은 바꾸지 않았다.

## 남은 작업
- [x] Windows에서 빌드·테스트 실행, 실패 시 수정 — 2026-10-23 작업 4 브랜치 분기 직후(Core 미변경) `dotnet build`·`dotnet test` 통과 (경고 0, Core 79/79). Fabric 26.3 서버 생성·기동도 작업 4 실서버 검증에서 정상. NeoForge 실서버는 여전히 미확인
- [ ] NeoForge 서버 실제 생성·기동, 인스톨러가 `libraries/net/neoforged/neoforge/<버전>/win_args.txt`를 만드는지 확인
- [ ] 모드 50개 구성으로 spark 실측 후 권장 힙 어림값 보정
- [ ] (선택) CurseForge 소스 — 50개 모드 중 Modrinth에 없는 모드 대응

---

# 작업 4 — SQLite 지표 저장 (2026-10-23, 브랜치 `feature/sqlite-metrics`)

## 목표와 범위
- 목표: 설계서 2장 Storage Worker의 저장소로 SQLite를 도입해 TPS·MSPT·메모리·접속자 지표와 이벤트를 서버 폴더별로 기록·조회한다. 적응형 단계 조정(설계서 4장 5번)과 프리셋 단계별 성능 비교(9장)의 기반으로 쓴다.
- 작업 대상: `Builder.Storage/`(신규), `Tests/Builder.Storage.Tests/`(신규), 솔루션, AGENTS.md, 설계서 3·6장, README
- 완료 조건: (1단계) 저장소가 스키마 생성·배치 기록·조회·1분 집계·보존 정리를 하고 단위 테스트로 확인된다. (2~3단계) 실서버에서 RCON으로 수집한 지표가 DB에 쌓이고 `mchb stats`로 조회된다.

## 실행
- [x] 브랜치 `feature/sqlite-metrics` 생성 (main에서 분기, 2026-10-23 `origin/feature/sqlite-metrics`로 push)
- [x] `Builder.Storage` 프로젝트 (Microsoft.Data.Sqlite 10.0.13), 솔루션 등록
- [x] `MetricsStore`: `<서버>/builder-data/metrics.db`, WAL + `synchronous=NORMAL`, `PRAGMA user_version` 마이그레이션(더 새 스키마는 열기 거부), `samples`·`samples_1m`·`events` 테이블
- [x] 배치 기록(한 트랜잭션, 준비된 명령), 조회(최근 샘플, 1분 집계, 이벤트 종류 필터), 구간 요약(MSPT 95백분위는 최근접 순위 방식)
- [x] `Compact`: 끝난 분만 1분 집계(집계 지연 1분), 이미 집계된 분 이후만 처리, 보존 기간 정리(원본 48시간, 집계·이벤트 30일 기본값). 집계 안 된 원본은 지우지 않음
- [x] `MetricsWriter`: 샘플은 용량 제한 큐(가득 차면 오래된 것부터 버림, 버린 수 집계), 이벤트는 버리지 않는 큐, 주기 기록·주기 정리, 기록 실패 시 로그만 남기고 계속 동작, Dispose 시 남은 데이터 기록
- [x] 단위 테스트 20개 (`Tests/Builder.Storage.Tests`)
- [x] AGENTS.md(디렉터리·의존성·DB 위치·스키마 규칙), 설계서 3·6장, README 갱신
- [x] 2단계: RCON 클라이언트(`Builder.Core/Rcon/`), `enable-rcon` 자동 구성(비밀번호는 server.properties에만, `broadcast-rcon-to-ops=false`), 프리셋별 수집 주기(`metricsIntervalSeconds` 1/5/30/60초, 오버라이드 `metrics.intervalSeconds`, 0이면 끔), Java 프로세스 메모리·CPU 수집
  - `ServerService.RunAsync`에 모니터 훅 추가 (서버 종료 시 취소, 예외는 로그만), `mchb start --no-metrics`
  - `MetricsCollector`: `/tick query` → 실패 시 `/spark tps`, `/list`로 접속자·접속/퇴장 이벤트, `server-start`/`server-stop` 이벤트, RCON 대기·재접속·비밀번호 오류 시 프로세스 지표만 기록
  - 실서버에서 찾은 버그 수정: 명령과 확인용 패킷을 연달아 보내면 Minecraft가 연결을 끊음 → 패킷을 하나씩 보내고, 응답 조각이 4096바이트로 꽉 찬 경우에만 확인용 패킷 사용. 가짜 RCON 서버도 같은 제약을 흉내 내도록 변경
- [x] 3단계: `mchb stats <폴더> [--since] [--recent] [--events]` — 구간 요약(TPS 평균·최저, MSPT 평균·95백분위, RSS 최대, CPU 평균, 접속자 최대), 실행 상태(실행 중/종료/기록 끊김), 성능 판정(설계서 1장 목표, 4장 5번: p95 MSPT > 50ms면 한 단계 낮은 프리셋 제안만), 최근 샘플·이벤트
  - 계산은 `Builder.Storage/MetricsReport.cs`(테스트 가능), 출력은 CLI. DB가 없으면 만들지 않고 안내만 함
  - README에 지표 기록·RCON 포트 노출 주의 안내 추가

## 검증 기록
| 확인 항목 | 명령 또는 방법 | 결과 | 미실행·실패 이유 |
|---|---|---|---|
| 빌드 | `dotnet build MinecraftHybridBuilder.slnx` (Windows 11, .NET SDK 10.0.401) | 통과 (경고 0, 오류 0) | |
| 단위 테스트 | `dotnet test MinecraftHybridBuilder.slnx` | 통과 (Core 79/79, Storage 20/20) | |
| 타이머 테스트 안정성 | `dotnet test Tests/Builder.Storage.Tests --no-build` 5회 반복 | 5회 모두 통과 | |
| 빌드·테스트 (2단계) | `dotnet build` / `dotnet test MinecraftHybridBuilder.slnx` 3회 반복 | 통과 (경고 0, Core 98/98, Storage 35/35) | |
| 실서버 1차 | `mchb create servers/e2e --preset minimum --set jvm.heapMb=1536 --set metrics.intervalSeconds=5 --accept-eula`(사용자 EULA 동의) 후 `start` 150초 | 실패: RCON 연결이 명령마다 끊김 (샘플 31개 중 TPS 13개). `/tick query`·`/list` 파싱과 프로세스 지표는 정상 | 원인: 패킷 연달아 전송 → 위 수정 |
| 실서버 2차 | 수정 후 같은 서버 `start` 100초 | 통과: RCON 연결 1개 유지, 샘플 21개 중 TPS·접속자 18개(시작 중 3개 제외), 평균 MSPT 0.75ms, RSS 최대 1133MB, server-start/stop 이벤트 기록, 서버 콘솔에 수집 명령 출력 없음 | |
| 빌드·테스트 (3단계) | `dotnet build` / `dotnet test MinecraftHybridBuilder.slnx` 2회 | 통과 (경고 0, Core 98/98, Storage 49/49) | |
| `stats` 출력 | 임시 폴더에 합성 DB(30초 간격 80개, MSPT 30→75ms) 후 `mchb stats`, `--since 5d`, `--since abc`, DB 없는 폴더 | 통과: 요약·판정(저 → 최저 제안)·최근 샘플·이벤트 표시, 48시간 초과 안내, 잘못된 기간은 종료 코드 1, DB 없으면 만들지 않고 안내 | 실서버 DB로는 미확인 (새 서버 생성에 EULA 동의 필요) |
| 4096바이트 넘는 응답 | 실서버 | 미실행 | 수집 명령 응답이 짧아 해당 없음. 가짜 서버로만 확인 |
| 접속·퇴장 이벤트 | 실제 클라이언트 접속 | 미실행 | 클라이언트 없음. 단위 테스트로만 확인 |
| spark 대체 경로 | `/tick query` 없는 구버전 서버 | 미실행 | 26.3에서는 `/tick query`가 동작해 사용되지 않음. 단위 테스트로만 확인 |
| Linux | e_sqlite3 네이티브 라이브러리 로드 | 미실행 | Linux 환경 없음 |

## 주요 결정
- 결정: DB는 서버 폴더별(`<서버>/builder-data/metrics.db`)에 둔다. 이유: 서버와 함께 이동·백업되고, 여러 서버의 지표가 섞이지 않음 (사용자 승인).
- 결정: SQLite는 별도 프로젝트 `Builder.Storage`에만 넣는다. 이유: Core 외부 의존성 0 규칙 유지. (2단계부터 CLI가 지표 수집·조회를 위해 `Builder.Storage`를 참조하므로 CLI에는 SQLite가 간접 의존성으로 들어감 — AGENTS.md 반영)
- 결정: 실시간 표시는 메모리에서, DB 기록은 주기 배치로 한다. 이유: 샘플마다 커밋하면 저사양 디스크에 부담이 큼.
- 결정: TPS·MSPT는 바닐라 `/tick query`를 먼저 쓰고 spark는 대체 경로로 둔다. 이유: 바닐라 명령은 모드 설치와 무관하고 출력이 단순함. spark는 MSPT 평균 대신 중앙값을 줌.
- 결정: `mchb.exe` 대신 `dotnet Builder.Cli/bin/Debug/net10.0/mchb.dll`로 실서버 검증. 이유: 작업 환경에서 `dotnet run`의 mchb.exe 실행이 "액세스 거부"됨 (코드 문제 아님).
- 결정: 호출마다 연결을 새로 연다 (연결 풀 사용). 이유: SqliteConnection은 스레드 안전하지 않고, WAL에서는 연결별 동시 조회·기록이 가능함.

## 커밋 기록 (`feature/sqlite-metrics`, `origin`에 push됨)
| 커밋 | 내용 |
|---|---|
| `42660d0` | 1단계: `Builder.Storage` (MetricsStore·MetricsWriter), 테스트 20개, 문서 |
| `24e6431` | 2단계: RCON 클라이언트·설정, 프리셋 수집 주기, 실행 중 지표 수집, 실서버 검증 |
| `c0afafc` | 3단계: `mchb stats`, README 지표·RCON 안내 |
| `dd64798` | CHECKLIST 커밋 기록·상태 보완 |

## 완료
- [x] 필요한 문서를 갱신했다. (AGENTS.md, 설계서 3·4·6장, README, CHECKLIST)
- [x] 변경 내용을 최종 검토했다.
- [x] 미완료 항목과 남은 한계를 기록했다. (검증 기록의 미실행 항목, 아래 남은 작업)
- [x] 사용자에게 변경 내용과 검증 결과를 설명했다.

## 남은 작업
- [x] 원격 push (2026-10-23, `origin/feature/sqlite-metrics`)
- [ ] main 병합 — PR 생성 여부는 사용자 결정 대기
- [ ] 서버 실행 중 `stats`를 다른 터미널에서 실행해 실서버 DB로 확인
- [ ] 1분 집계(`samples_1m`)를 쓰는 장기 추이 표시 (48시간 넘는 구간) — 웹 대시보드 단계에서 검토
- [ ] 힙 사용량: RCON으로는 얻을 수 없음 → FabricBridge 단계
- [ ] 서버 종료 직전 RCON이 먼저 닫혀 "연결이 끊겼습니다" 로그가 한 줄 나옴 (무해, 필요하면 정리)
- [ ] 서버 백업 기능을 만들 때 `builder-data/`의 포함 여부 결정 (WAL 파일이 있으면 백업 API 또는 체크포인트 후 복사)
