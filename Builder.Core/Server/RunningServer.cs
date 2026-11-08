using System.Diagnostics;

namespace Builder.Core.Server;

/// <summary>
/// 빌더가 표준 입출력을 맡아 실행 중인 서버 (<see cref="ServerService.StartManagedAsync"/>).
/// 종료는 <see cref="StopAsync"/>로 <c>stop</c> 명령을 보내 서버가 월드를 저장하고 스스로 끝나게 한다.
/// <see cref="Kill"/>은 저장하지 않으므로 서버가 응답하지 않을 때만 쓴다.
/// </summary>
public sealed class RunningServer : IAsyncDisposable
{
    private readonly Process process;
    private readonly Action<string> output;
    private readonly SemaphoreSlim inputLock = new(1, 1);
    private readonly CancellationTokenSource monitorStop = new();
    private readonly Task monitorTask;
    private int stopRequested;

    private RunningServer(ServerRunContext context, Action<string> output, Func<ServerRunContext, CancellationToken, Task>? monitor)
    {
        Context = context;
        process = context.Process;
        this.output = output;
        process.OutputDataReceived += (_, e) => Forward(e.Data);
        process.ErrorDataReceived += (_, e) => Forward(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        monitorTask = monitor is null ? Task.CompletedTask : Task.Run(() => monitor(context, monitorStop.Token));
        Completion = WaitForExitAsync();
    }

    /// <summary>표준 입출력이 리다이렉트된 채 시작된 프로세스를 맡는다. 모니터는 서버가 끝나면 취소된다.</summary>
    public static RunningServer Attach(ServerRunContext context, Action<string> output,
        Func<ServerRunContext, CancellationToken, Task>? monitor = null) => new(context, output, monitor);

    public ServerRunContext Context { get; }

    /// <summary>서버 프로세스와 모니터가 모두 끝나면 종료 코드로 완료된다. 남은 출력도 모두 전달된 뒤다.</summary>
    public Task<int> Completion { get; }

    public bool HasExited => Completion.IsCompleted;

    public bool StopRequested => Volatile.Read(ref stopRequested) == 1;

    /// <summary>서버 콘솔에 명령 한 줄을 보낸다 (<see cref="ServerConsole.NormalizeCommand"/>).</summary>
    public async Task SendCommandAsync(string command, CancellationToken ct = default)
    {
        var line = ServerConsole.NormalizeCommand(command);
        await inputLock.WaitAsync(ct);
        try
        {
            if (process.HasExited)
                throw new InvalidOperationException("서버가 이미 종료되었습니다.");
            await process.StandardInput.WriteLineAsync(line.AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("서버가 이미 종료되었습니다.", ex);
        }
        finally
        {
            inputLock.Release();
        }
    }

    /// <summary>
    /// <c>stop</c>을 한 번만 보내고 <paramref name="timeout"/>까지 종료를 기다린다. 끝났으면 true.
    /// false면 서버가 아직 저장 중이거나 멈춘 것이므로, 더 기다리거나 <see cref="Kill"/>을 고른다.
    /// </summary>
    public async Task<bool> StopAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (!HasExited && Interlocked.Exchange(ref stopRequested, 1) == 0)
        {
            try
            {
                await SendCommandAsync(ServerConsole.Stop, ct);
            }
            catch (InvalidOperationException)
            {
                // 보내는 사이에 이미 끝난 경우
            }
        }
        await Task.WhenAny(Completion, Task.Delay(timeout, ct));
        return HasExited;
    }

    /// <summary>저장 없이 프로세스를 끝낸다. 마지막 자동 저장 이후 변경이 사라질 수 있다.</summary>
    public void Kill()
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// 핸들만 정리한다. 실행 중이면 서버를 끝내지 않는다 (저장 없이 끝내는 것보다 계속 실행되는 편이 월드에 안전하다).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (!HasExited)
            return;
        await Completion;
        monitorStop.Dispose();
        inputLock.Dispose();
        process.Dispose();
    }

    private async Task<int> WaitForExitAsync()
    {
        // 비동기 읽기를 쓰면 WaitForExitAsync는 남은 출력이 모두 전달될 때까지 기다린다.
        await process.WaitForExitAsync(CancellationToken.None);
        await monitorStop.CancelAsync();
        await monitorTask;
        return process.ExitCode;
    }

    private void Forward(string? line)
    {
        if (line is null)
            return;
        try
        {
            output(line);
        }
        catch (Exception)
        {
            // 출력 처리 실패가 읽기 스레드를 멈춰 서버 출력이 막히지 않게 한다.
        }
    }
}
