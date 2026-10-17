namespace Builder.Core;

/// <summary>빌더가 서버 폴더 밖에 공유해서 쓰는 데이터 위치.</summary>
public sealed class BuilderPaths
{
    public BuilderPaths(string dataRoot)
    {
        DataRoot = Path.GetFullPath(dataRoot);
    }

    public string DataRoot { get; }

    public string RuntimesDir => Path.Combine(DataRoot, "runtimes");

    /// <summary>MCHB_HOME 환경 변수가 있으면 사용하고, 없으면 OS별 로컬 앱 데이터 폴더를 쓴다.</summary>
    public static BuilderPaths Default()
    {
        var overrideRoot = Environment.GetEnvironmentVariable("MCHB_HOME");
        if (!string.IsNullOrWhiteSpace(overrideRoot))
            return new BuilderPaths(overrideRoot);

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new BuilderPaths(Path.Combine(local, "MinecraftHybridBuilder"));
    }
}
