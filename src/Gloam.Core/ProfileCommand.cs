using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Gloam.Core
{
    /// <summary>Parsed <c>--profile-set</c> option. <see cref="Present"/> is false when it was not given.</summary>
    public sealed record ProfileCommand(bool Present, DisplayProfileSelection? Selection, string? Error)
    {
        public static ProfileCommand None { get; } = new(false, null, null);
    }

    /// <summary>
    /// Scripting entry point: <c>Gloam.exe --profile-set normal|media|auto</c>. A manual
    /// choice is sticky until <c>auto</c> restores automatic switching.
    /// </summary>
    public static class ProfileCommandLine
    {
        public const string Option = "--profile-set";

        /// <summary>Exit codes of a <c>--profile-set</c> launch, for scripts.</summary>
        public const int ExitApplied = 0;
        public const int ExitNotSaved = 1;
        public const int ExitInvalidArguments = 2;
        public const int ExitInstanceUnreachable = 3;

        public static ProfileCommand Parse(IReadOnlyList<string>? args)
        {
            if (args == null) return ProfileCommand.None;
            ProfileCommand result = ProfileCommand.None;
            for (int i = 0; i < args.Count; i++)
            {
                string arg = args[i] ?? string.Empty;
                string? value;
                if (arg.Equals(Option, StringComparison.OrdinalIgnoreCase))
                {
                    value = i + 1 < args.Count ? args[++i] : null;
                }
                else if (arg.StartsWith(Option + "=", StringComparison.OrdinalIgnoreCase))
                {
                    value = arg[(Option.Length + 1)..];
                }
                else
                {
                    continue;
                }

                if (result.Present)
                    return new ProfileCommand(true, null, $"{Option} was given more than once.");
                result = TryParseValue(value, out DisplayProfileSelection selection)
                    ? new ProfileCommand(true, selection, null)
                    : new ProfileCommand(true, null,
                        $"{Option} expects normal, media, or auto; got '{value ?? string.Empty}'.");
            }
            return result;
        }

        public static bool TryParseValue(string? value, out DisplayProfileSelection selection)
        {
            switch (value?.Trim().ToLowerInvariant())
            {
                case "normal":
                    selection = DisplayProfileSelection.Normal;
                    return true;
                case "media":
                    selection = DisplayProfileSelection.Media;
                    return true;
                case "auto":
                case "automatic":
                    selection = DisplayProfileSelection.Automatic;
                    return true;
                default:
                    selection = DisplayProfileSelection.Automatic;
                    return false;
            }
        }

        /// <summary>The wire and command-line spelling of a selection.</summary>
        public static string ToArgument(DisplayProfileSelection selection) => selection switch
        {
            DisplayProfileSelection.Normal => "normal",
            DisplayProfileSelection.Media => "media",
            _ => "auto"
        };
    }

    public enum ProfileCommandSendResult
    {
        Applied,
        NotSaved,
        Unavailable
    }

    /// <summary>
    /// Local IPC for <c>--profile-set</c>. The single-instance mutex makes a second launch
    /// exit, so the second process forwards its command to the running tray over a named pipe
    /// and reports the outcome through its exit code. The pipe is scoped to the login session
    /// (matching the <c>Local\</c> mutex) and restricted to the current user on both ends.
    /// </summary>
    public static class ProfileCommandChannel
    {
        private const string RequestVerb = "profile-set";
        private const string ReplyApplied = "ok";
        private const string ReplyNotSaved = "not-saved";
        private const int MaximumLineLength = 64;

        public static string DefaultPipeName
        {
            get
            {
                using Process current = Process.GetCurrentProcess();
                return $"Gloam.ProfileCommand.{current.SessionId}";
            }
        }

        /// <summary>
        /// Sends a selection to the running instance. Connect waits for the pipe to appear, which
        /// covers a launch racing the first instance's startup.
        /// </summary>
        public static ProfileCommandSendResult Send(
            DisplayProfileSelection selection,
            TimeSpan timeout,
            string? pipeName = null)
        {
            var clock = Stopwatch.StartNew();
            pipeName ??= DefaultPipeName;
            while (true)
            {
                int remaining = (int)Math.Max(0, (timeout - clock.Elapsed).TotalMilliseconds);
                if (remaining == 0) return ProfileCommandSendResult.Unavailable;
                try
                {
                    using var pipe = new NamedPipeClientStream(
                        ".", pipeName, PipeDirection.InOut,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    pipe.Connect(remaining);
                    using var cts = new CancellationTokenSource(Math.Max(1, remaining));
                    WriteLineAsync(pipe, $"{RequestVerb} {ProfileCommandLine.ToArgument(selection)}", cts.Token)
                        .GetAwaiter().GetResult();
                    string? reply = ReadLineAsync(pipe, cts.Token).GetAwaiter().GetResult();
                    return reply == ReplyApplied
                        ? ProfileCommandSendResult.Applied
                        : reply == ReplyNotSaved
                            ? ProfileCommandSendResult.NotSaved
                            : ProfileCommandSendResult.Unavailable;
                }
                catch (TimeoutException)
                {
                    return ProfileCommandSendResult.Unavailable;
                }
                catch (OperationCanceledException)
                {
                    return ProfileCommandSendResult.Unavailable;
                }
                catch (IOException) when (clock.Elapsed < timeout)
                {
                    // The single server instance is busy with another client; retry shortly.
                    Thread.Sleep(50);
                }
                catch (IOException)
                {
                    return ProfileCommandSendResult.Unavailable;
                }
                catch (UnauthorizedAccessException)
                {
                    // CurrentUserOnly: a pipe owned by another account is never trusted.
                    return ProfileCommandSendResult.Unavailable;
                }
            }
        }

        internal static async Task WriteLineAsync(Stream stream, string line, CancellationToken token)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
            await stream.WriteAsync(bytes, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }

        /// <summary>Reads one bounded ASCII line; null on end of stream or an oversized line.</summary>
        internal static async Task<string?> ReadLineAsync(Stream stream, CancellationToken token)
        {
            var line = new StringBuilder();
            var buffer = new byte[1];
            while (line.Length <= MaximumLineLength)
            {
                int read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read == 0) return null;
                char c = (char)buffer[0];
                if (c == '\n') return line.ToString().TrimEnd('\r');
                line.Append(c);
            }
            return null;
        }

        internal static string HandleRequest(string? request, Func<DisplayProfileSelection, bool> apply)
        {
            string[] parts = (request ?? string.Empty).Split(' ', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !parts[0].Equals(RequestVerb, StringComparison.Ordinal) ||
                !ProfileCommandLine.TryParseValue(parts[1], out DisplayProfileSelection selection))
                return "error invalid request";
            bool saved;
            try { saved = apply(selection); }
            catch (Exception ex)
            {
                Log.Error($"ProfileCommandChannel: applying {selection} failed: {ex.Message}");
                saved = false;
            }
            return saved ? ReplyApplied : ReplyNotSaved;
        }
    }

    /// <summary>Running-instance side of <see cref="ProfileCommandChannel"/>.</summary>
    public sealed class ProfileCommandServer : IDisposable
    {
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
        private readonly Func<DisplayProfileSelection, bool> _apply;
        private readonly string _pipeName;
        private readonly CancellationTokenSource _stop = new();
        private Task? _loop;
        private int _disposed;

        /// <param name="apply">Persists the selection; returns false when it could not be saved.</param>
        public ProfileCommandServer(Func<DisplayProfileSelection, bool> apply, string? pipeName = null)
        {
            _apply = apply ?? throw new ArgumentNullException(nameof(apply));
            _pipeName = pipeName ?? ProfileCommandChannel.DefaultPipeName;
        }

        public void Start()
        {
            if (Volatile.Read(ref _disposed) != 0 || _loop != null) return;
            _loop = Task.Run(() => RunAsync(_stop.Token));
        }

        private async Task RunAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(
                        _pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                    using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
                    request.CancelAfter(RequestTimeout);
                    string? line = await ProfileCommandChannel.ReadLineAsync(pipe, request.Token).ConfigureAwait(false);
                    string reply = ProfileCommandChannel.HandleRequest(line, _apply);
                    Log.Info($"ProfileCommandServer: '{line}' -> {reply}");
                    await ProfileCommandChannel.WriteLineAsync(pipe, reply, request.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // A client that disconnects or stalls must not stop the listener. The delay
                    // keeps a persistent failure (e.g. the name held elsewhere) from spinning.
                    Log.Info($"ProfileCommandServer: request failed: {ex.Message}");
                    try { await Task.Delay(250, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stop.Cancel();
            try { _loop?.Wait(TimeSpan.FromSeconds(2)); }
            catch (AggregateException) { }
            _stop.Dispose();
        }
    }
}
