# S-MinecraftServerBuilder

저사양 PC에서도 Minecraft Java Edition **Fabric** 또는 **NeoForge** 서버를 쉽게 만들고 운영하도록 돕는 명령줄 도구입니다.
PC 사양에 맞는 경량화 프리셋을 고르고, 필요한 Java·Fabric·최적화 모드를 자동으로 설치합니다.

> 개발 중인 프로젝트입니다. 현재 Windows 11에서 확인했고, Linux는 아직 검증하지 않았습니다.

## 주요 기능

- **사양 측정과 프리셋 추천**: 전체 RAM과 CPU를 기준으로 4단계 프리셋(일반·중간·저·최저) 중 하나를 추천합니다. RAM이 부족하면 얼마를 더 확보해야 하는지 알려 줍니다.
- **Java 자동 설치**: Minecraft 버전에 필요한 Java 버전을 확인하고, 없으면 Eclipse Temurin JRE를 내려받습니다.
- **Fabric / NeoForge 서버 자동 설치**: 서버를 만들 때 로더를 고르고(`--modloader`), 최신 안정 버전 또는 지정한 버전으로 설치합니다.
- **로더 전환**: 이미 만든 서버를 `switch-loader`로 Fabric ↔ NeoForge 전환할 수 있습니다. 빌더 최적화 모드는 새 로더용으로 다시 설치되고, 사용자가 넣은 이전 로더 전용 jar는 `mods-disabled/<로더>/`로 옮겨 두었다가 다시 돌아오면 복원됩니다.
- **모드 수에 따른 메모리 경고**: `start`·`mods` 실행 시 mods 폴더의 jar 수로 대략적인 권장 힙을 계산해, 현재 힙이 작으면 알려 줍니다.
- **설정 자동 구성**: 프리셋에 맞게 `server.properties`, JVM 메모리·GC 옵션, 시작 스크립트(`start.bat`, `start.sh`)를 만듭니다.
- **서버 지표 기록**: `start`로 실행하는 동안 RCON으로 TPS·MSPT·접속자를, Java 프로세스에서 메모리·CPU를 읽어 서버 폴더의 `builder-data/metrics.db`(SQLite)에 기록합니다. 기록 주기는 프리셋마다 다릅니다(일반 1초 · 중간 5초 · 저 30초 · 최저 60초). `stats`로 요약과 성능 판정을 볼 수 있습니다.
- **최적화 모드 자동 설치**: [Modrinth](https://modrinth.com)에서 서버 버전에 맞는 모드와 필수 의존성을 설치합니다. 맞는 버전이 없는 모드는 건너뛰고 이유를 알려 줍니다. 사용자가 직접 넣은 모드는 건드리지 않습니다.

## 필요 환경

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Java는 따로 설치하지 않아도 됩니다 (필요하면 자동 설치).

## 사용법

저장소 폴더에서 실행합니다.

```bash
# 사양 확인과 프리셋 추천
dotnet run --project Builder.Cli -- hardware

# 서버 만들기 (프리셋을 생략하면 추천 프리셋 사용, 로더를 생략하면 실행 중에 물어봄)
dotnet run --project Builder.Cli -- create servers/my-server --preset 저 --accept-eula

# NeoForge 서버 만들기
dotnet run --project Builder.Cli -- create servers/neo --modloader neoforge --preset 저 --accept-eula

# 기존 Fabric 서버를 NeoForge로 전환 (월드가 있으면 백업 후 --confirm)
dotnet run --project Builder.Cli -- switch-loader servers/my-server neoforge --confirm

# 서버 실행
dotnet run --project Builder.Cli -- start servers/my-server

# 최근 1시간 지표 요약 (다른 터미널에서 실행 중에도 가능)
dotnet run --project Builder.Cli -- stats servers/my-server --since 1h
```

`--accept-eula`는 [Minecraft EULA](https://aka.ms/MinecraftEULA)에 동의한다는 뜻입니다. 생략하면 실행 중에 동의 여부를 묻습니다.

### 명령 목록

| 명령 | 설명 |
|---|---|
| `hardware` | 사양·Java 확인, 프리셋 추천 |
| `presets` | 프리셋 목록과 설정값 |
| `versions` | 설치 가능한 Minecraft 버전 (`--modloader neoforge`로 NeoForge 목록) |
| `create <폴더>` | 새 서버 만들기 (`--modloader`, `--mc`, `--preset`, `--set 키=값`, `--no-mods` 등) |
| `switch-loader <폴더> <로더>` | Fabric ↔ NeoForge 전환 (`--loader <버전>`, `--confirm`) |
| `configure <폴더>` | 프리셋·설정 변경 (예: `--preset 중간 --set server.view-distance=8`) |
| `mods <폴더>` | 설치된 모드 확인, `--update`로 최신 호환 버전으로 갱신 |
| `start <폴더>` | 서버 실행, 실행 중 지표 기록 (`--no-metrics`로 이번 실행만 끔) |
| `stats <폴더>` | 지표 요약과 성능 판정 (`--since 30m`/`6h`/`2d`, `--recent`, `--events`) |

자세한 옵션은 `dotnet run --project Builder.Cli -- help`로 확인할 수 있습니다.

## 경량화 프리셋

| 항목 | 일반 | 중간 | 저 | 최저 |
|---|---|---|---|---|
| 시야 거리 (view-distance) | 10 | 8 | 6 | 4 |
| 시뮬레이션 거리 | 8 | 6 | 5 | 3 |
| JVM 힙 메모리 | 6GB | 5GB | 4GB | 3GB |
| 권장 인원 | 10~20 | 8~12 | 4~8 | 2~4 |
| 최적화 모드 | 기본 묶음 + C2ME | 기본 묶음 + C2ME | 기본 묶음 | 기본 묶음 |

기본 묶음: Lithium, FerriteCore, ModernFix, Krypton, spark, Chunky

NeoForge에서는 Krypton(Fabric 전용)을 건너뛰고, C2ME는 NeoForge 포트(`c2me-neoforge`)를 사용합니다. 모드 50개 안팎을 넣을 계획이면 `jvm.heapMb`를 권장 힙(대략 Fabric 2GB / NeoForge 2.5GB + 모드당 64MB) 이상으로 올리세요.

`--set`으로 프리셋 값을 개별적으로 바꿀 수 있습니다 (예: `--set jvm.heapMb=3584`, `--set metrics.intervalSeconds=10`. 기록 주기를 0으로 하면 지표를 기록하지 않습니다).

## 지표 기록과 RCON

지표를 기록하려고 빌더가 `server.properties`에서 RCON을 켭니다 (`enable-rcon=true`, 비밀번호가 없으면 무작위 생성, `broadcast-rcon-to-ops=false`). 이미 정한 포트·비밀번호는 바꾸지 않습니다.

- RCON 포트는 기본적으로 게임 포트 + 10입니다 (25565 → 25575).
- `server-ip`를 비워 두면 RCON도 모든 네트워크 주소에서 열립니다. **공유기 포트 포워딩이나 방화벽 허용은 게임 포트만 하고 RCON 포트는 열지 마세요.**
- RCON 비밀번호는 `server.properties`에만 있습니다. 이 파일을 다른 사람과 공유하지 마세요.
- `stats` 판정은 설계 목표(20 TPS, 틱당 50ms)를 기준으로 하며, MSPT 95백분위가 50ms를 넘으면 한 단계 낮은 프리셋을 제안합니다. 설정을 자동으로 바꾸지는 않습니다.
- 원본 샘플은 48시간, 1분 집계와 이벤트는 30일 동안 보관합니다.

## 프로젝트 구조

```
Builder.Core/   핵심 기능 (사양 측정, 프리셋, Java 설치, 로더(Fabric·NeoForge) 설치, 모드 설치)
Builder.Cli/    명령줄 도구
Builder.Storage/ 서버 지표 저장소 (SQLite, 개발 중)
Presets/        프리셋 정의 (JSON)
Mods/           최적화 모드 목록 (JSON)
Tests/          단위 테스트 (dotnet test)
```

설계 내용은 [설계서](Minecraft_Hybrid_Server_Builder_설계서_v0.2.md)에 있습니다.

## 개발 계획

- [x] 서버 빌더 CLI (Java·Fabric 자동 설치, 설정 자동화)
- [x] 경량화 프리셋, Modrinth 최적화 모드 자동 설치
- [x] NeoForge 지원, Fabric ↔ NeoForge 전환
- [ ] Chunky 청크 사전 생성, 자동 백업
- [x] RCON 연동, 서버 지표 기록(SQLite)과 `stats` 요약
- [ ] 서버 상태 수집 모드(FabricBridge) — 힙 사용량 등 정밀 지표
- [ ] 웹 대시보드
- [ ] Linux 지원
- [ ] CurseForge 모드 소스 (추후 확장)
