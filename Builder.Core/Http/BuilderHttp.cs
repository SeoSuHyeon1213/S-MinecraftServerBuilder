using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Builder.Core.Http;

public static class BuilderHttp
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MinecraftHybridBuilder/0.1");
        return client;
    }

    /// <summary>
    /// 임시 파일(.part)로 받은 뒤 검증이 끝나면 대상 경로로 옮긴다.
    /// 중간에 실패해도 불완전한 파일이 대상 경로에 남지 않는다.
    /// </summary>
    public static async Task DownloadFileAsync(
        HttpClient http,
        string url,
        string destinationPath,
        IProgress<double>? progress = null,
        FileHash? expectedHash = null,
        CancellationToken ct = default)
    {
        var fullPath = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var partPath = fullPath + ".part";

        try
        {
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;

                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var target = File.Create(partPath);
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    received += read;
                    if (total > 0)
                        progress?.Report((double)received / total.Value);
                }
            }

            if (expectedHash is not null)
            {
                var actual = await FileHash.ComputeAsync(partPath, expectedHash.Algorithm, ct);
                if (!actual.Equals(expectedHash.Value, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"체크섬 불일치: {url} ({expectedHash.Algorithm} 예상 {expectedHash.Value}, 실제 {actual})");
            }

            File.Move(partPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(partPath))
                File.Delete(partPath);
        }
    }
}

/// <summary>
/// 파일 해시. 다운로드 출처마다 제공하는 방식이 다르다 (Adoptium sha256, Modrinth sha512, CurseForge sha1/md5).
/// </summary>
public sealed record FileHash(string Algorithm, string Value)
{
    public static async Task<string> ComputeAsync(string path, string algorithm, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        var bytes = algorithm.ToLowerInvariant() switch
        {
            "sha512" => await SHA512.HashDataAsync(stream, ct),
            "sha256" => await SHA256.HashDataAsync(stream, ct),
            "sha1" => await SHA1.HashDataAsync(stream, ct),
            "md5" => await MD5.HashDataAsync(stream, ct),
            _ => throw new NotSupportedException($"지원하지 않는 해시 방식: {algorithm}"),
        };
        return Convert.ToHexStringLower(bytes);
    }
}

/// <summary>
/// 사용자가 직접 작성한 JSON에서 숫자·불리언을 문자열 자리에 써도 읽을 수 있게 한다.
/// 예: "server.view-distance": 8
/// </summary>
public sealed class LenientStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => System.Text.Encoding.UTF8.GetString(reader.ValueSpan),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            JsonTokenType.Null => null,
            _ => throw new JsonException($"문자열·숫자·불리언 값이 필요합니다 (실제: {reader.TokenType})."),
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
