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
- 다음 할 일: Chunky 청크 사전 생성, 자동 백업(low/minimum 강제)
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
