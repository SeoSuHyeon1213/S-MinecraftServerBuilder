# AGENTS.md — Minecraft Hybrid Server Builder

## 프로젝트 개요
- 목적: 저사양 PC에서 Minecraft Java Edition **Fabric / NeoForge** 서버를 하드웨어에 맞는 경량화 프리셋으로 자동 생성·실행하는 빌더.
- 설계 기준 문서: `Minecraft_Hybrid_Server_Builder_설계서_v0.2.md` (단계별 범위, 프리셋 값의 출처).
- 기술: C# / .NET 10 (`net10.0`, 공통 설정은 `Directory.Build.props`). 외부 NuGet 의존성은 테스트 프로젝트에만 있다.
- 일반 작업 규칙은 `CLAUDE.md`, 현재 작업 상태는 `CHECKLIST.md`를 따른다.

## 디렉터리
| 경로 | 역할 |
|---|---|
| `Builder.Core/` | 하드웨어 감지, 프리셋, Java 탐색·설치(Adoptium), Fabric/Mojang 메타 API, 모드 로더 설치(`Loaders/`), 서버 생성·설정·전환·실행, 모드 설치(`Mods/`) |
| `Builder.Cli/` | 콘솔 실행 도구 (실행 파일 이름 `mchb`) |
| `Presets/` | 경량화 프리셋 JSON (normal / medium / low / minimum). 각 프리셋의 `mods`가 설치할 모드 목록. 빌드 시 출력 폴더로 복사됨 |
| `Mods/catalog.json` | 최적화 모드 카탈로그 (모드 ID, 이름, 소스별 프로젝트 ID). 빌드 시 출력 폴더로 복사됨 |
| `Tests/Builder.Core.Tests/` | xUnit 단위 테스트 (네트워크 사용 안 함) |
| `MinecraftHybridBuilder.slnx` | 솔루션 |

설계서의 `Builder.Web`, `Builder.Worker`, `FabricBridge`, `Protocol`은 아직 없다 (4~5차 단계).

## 명령 (프로젝트 루트에서 실행)
- 빌드: `dotnet build MinecraftHybridBuilder.slnx`
- 테스트: `dotnet test MinecraftHybridBuilder.slnx`
- CLI 실행: `dotnet run --project Builder.Cli -- <명령>` (`help`로 명령 목록 확인)

## 런타임 데이터 위치
- 빌더가 내려받은 Java 런타임: `%LOCALAPPDATA%\MinecraftHybridBuilder\runtimes\java-<버전>` (Linux: `~/.local/share/...`). 환경 변수 `MCHB_HOME`으로 변경 가능.
- 각 서버 폴더의 `builder.json`이 해당 서버 구성(MC·로더 버전, 프리셋, 오버라이드, Java 경로, 빌더가 설치한 모드 기록)의 원본이다.

## 프로젝트 규칙
- 필요한 Java 버전은 하드코딩하지 않고 Mojang 버전 메타데이터의 `javaVersion.majorVersion`에서 읽는다 (예: 26.3은 Java 25).
- EULA는 사용자의 명시적 동의(`--accept-eula` 또는 대화형 `y`) 없이 `eula=true`로 쓰지 않는다.
- 비어 있지 않은 폴더에 서버를 생성하지 않는다 (기존 월드 보호).
- `server.properties`는 줄 단위로 병합해 기존 주석과 프리셋에 없는 키를 보존한다.
- 프리셋 값을 바꿀 때는 설계서 4장 표와 일치하는지 확인하고, 다르면 설계서도 함께 갱신한다.
- 외부 API 요청에는 사용자 개인정보(이메일 등)를 넣지 않는다.
- 모드 다운로드 사이트 연동은 `IModSource`(Builder.Core/Mods/ModSources.cs)를 구현해 추가한다. 설치 로직(`ModInstaller`)에 사이트별 분기를 넣지 않는다. 현재 소스는 Modrinth뿐이며 CurseForge는 추후 확장(설계서 6장).
- 모드 로더 연동은 `IServerLoader`(Builder.Core/Loaders/ServerLoaders.cs)를 구현해 `ServerService`의 로더 목록에 등록한다. 서버 생성·전환·실행 로직에 로더별 분기를 넣지 않는다. 현재: Fabric(메타 API 런처 jar), NeoForge(maven 인스톨러 `--install-server` → `@libraries/.../win_args.txt|unix_args.txt`).
- `builder.json`의 `loader`가 없으면(스키마 1) Fabric으로 읽는다. 저장 시 스키마 2로 기록한다.
- 로더 전환 시 사용자 jar는 지우지 않고 다른 로더 전용만 `mods-disabled/<이전 로더>/`로 옮긴다. 월드 폴더가 있으면 `--confirm` 없이는 전환하지 않는다.
- 카탈로그 모드가 로더별로 다른 프로젝트면 `"modrinth@neoforge"` 키를, 특정 로더 전용이면 `loaders` 목록을 쓴다.
- 모드 설치는 사용자가 직접 넣은 jar를 덮어쓰거나 지우지 않는다. `builder.json`의 `mods` 기록에 있는 파일만 빌더가 관리한다.
- API 키 등 비밀 값은 저장소·builder.json에 쓰지 않는다 (CurseForge 연동 시 사용자 환경 변수·개인 설정 사용).

## 직접 수정 주의
- 서버 폴더의 `start.bat` / `start.sh`는 `configure` 실행 시 다시 생성된다. 수정은 프리셋이나 오버라이드로 한다.
- `bin/`, `obj/`는 빌드 산출물이다.
