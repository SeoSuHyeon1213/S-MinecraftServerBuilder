using System.Text.Json;
using Builder.Core;

namespace Builder.Gui;

/// <summary>관리 창 개인 설정. 빌더 데이터 폴더의 gui.json에 둔다 (서버 폴더에는 쓰지 않는다).</summary>
public sealed class GuiSettings
{
    public string? LastServerDir { get; set; }

    private static string PathFor(BuilderPaths paths) => Path.Combine(paths.DataRoot, "gui.json");

    public static GuiSettings Load(BuilderPaths paths)
    {
        try
        {
            var path = PathFor(paths);
            return File.Exists(path) ? JsonSerializer.Deserialize<GuiSettings>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    /// <summary>저장 실패는 무시한다. 마지막 폴더를 기억하지 못할 뿐 서버 관리에는 영향이 없다.</summary>
    public void Save(BuilderPaths paths)
    {
        try
        {
            Directory.CreateDirectory(paths.DataRoot);
            File.WriteAllText(PathFor(paths), JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
