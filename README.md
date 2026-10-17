# S-MinecraftServerBuilder

저사양 PC에서도 Minecraft Java Edition **Fabric** 서버를 쉽게 만들고 운영하도록 돕는 명령줄 도구입니다.
PC 사양에 맞는 경량화 프리셋을 고르고, 필요한 Java·Fabric·최적화 모드를 자동으로 설치합니다.

> 개발 중인 프로젝트입니다. 현재 Windows 11에서 확인했고, Linux는 아직 검증하지 않았습니다.

## 주요 기능

- **사양 측정과 프리셋 추천**: 전체 RAM과 CPU를 기준으로 4단계 프리셋(일반·중간·저·최저) 중 하나를 추천합니다. RAM이 부족하면 얼마를 더 확보해야 하는지 알려 줍니다.
- **Java 자동 설치**: Minecraft 버전에 필요한 Java 버전을 확인하고, 없으면 Eclipse Temurin JRE를 내려받습니다.
- **Fabric 서버 자동 설치**: 최신 안정 버전 또는 지정한 버전으로 서버를 만듭니다.
- **설정 자동 구성**: 프리셋에 맞게 `server.properties`, JVM 메모리·GC 옵션, 시작 스크립트(`start.bat`, `start.sh`)를 만듭니다.
- **최적화 모드 자동 설치**: [Modrinth](https://modrinth.com)에서 서버 버전에 맞는 모드와 필수 의존성을 설치합니다. 맞는 버전이 없는 모드는 건너뛰고 이유를 알려 줍니다. 사용자가 직접 넣은 모드는 건드리지 않습니다.

## 필요 환경

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Java는 따로 설치하지 않아도 됩니다 (필요하면 자동 설치).

## 사용법

저장소 폴더에서 실행합니다.

```bash
# 사양 확인과 프리셋 추천
dotnet run --project Builder.Cli -- hardware

# 서버 만들기 (프리셋을 생략하면 추천 프리셋 사용)
dotnet run --project Builder.Cli -- create servers/my-server --preset 저 --accept-eula

# 서버 실행
dotnet run --project Builder.Cli -- start servers/my-server
```

`--accept-eula`는 [Minecraft EULA](https://aka.ms/MinecraftEULA)에 동의한다는 뜻입니다. 생략하면 실행 중에 동의 여부를 묻습니다.

### 명령 목록

| 명령 | 설명 |
|---|---|
| `hardware` | 사양·Java 확인, 프리셋 추천 |
| `presets` | 프리셋 목록과 설정값 |
| `versions` | 설치 가능한 Minecraft 버전 |
| `create <폴더>` | 새 서버 만들기 (`--mc`, `--preset`, `--set 키=값`, `--no-mods` 등) |
| `configure <폴더>` | 프리셋·설정 변경 (예: `--preset 중간 --set server.view-distance=8`) |
| `mods <폴더>` | 설치된 모드 확인, `--update`로 최신 호환 버전으로 갱신 |
| `start <폴더>` | 서버 실행 |

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

`--set`으로 프리셋 값을 개별적으로 바꿀 수 있습니다 (예: `--set jvm.heapMb=3584`).

## 프로젝트 구조

```
Builder.Core/   핵심 기능 (사양 측정, 프리셋, Java·Fabric 설치, 모드 설치)
Builder.Cli/    명령줄 도구
Presets/        프리셋 정의 (JSON)
Mods/           최적화 모드 목록 (JSON)
Tests/          단위 테스트 (dotnet test)
```

설계 내용은 [설계서](Minecraft_Hybrid_Server_Builder_설계서_v0.2.md)에 있습니다.

## 개발 계획

- [x] 서버 빌더 CLI (Java·Fabric 자동 설치, 설정 자동화)
- [x] 경량화 프리셋, Modrinth 최적화 모드 자동 설치
- [ ] Chunky 청크 사전 생성, 자동 백업
- [ ] 서버 상태 수집 모드(FabricBridge), RCON 연동
- [ ] 웹 대시보드
- [ ] Linux 지원
- [ ] CurseForge 모드 소스 (추후 확장)
