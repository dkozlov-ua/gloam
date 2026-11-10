using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Gloam.Core;

namespace Gloam.Tests
{
    /// <summary>
    /// A fresh data folder for one test: its own settings.json and log, removed afterwards.
    /// The process-wide redirect in AssemblyInfo keeps tests off the real data; this keeps them
    /// off each other's.
    /// </summary>
    internal sealed class TempDataScope : IDisposable
    {
        private readonly string _previousData = AppPaths.DataDir;
        private readonly string _previousRoaming = AppPaths.RoamingDataDir;

        public TempDataScope()
        {
            Root = Path.Combine(Path.GetTempPath(), "GloamTestData", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            AppPaths.UseDataDirectoriesForCurrentProcess(Root, Path.Combine(Root, "roaming"));
        }

        public string Root { get; }

        public string SettingsFile => Path.Combine(Root, "settings.json");

        public void Dispose()
        {
            AppPaths.UseDataDirectoriesForCurrentProcess(_previousData, _previousRoaming);
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    internal static class Eventually
    {
        /// <summary>Waits until <paramref name="condition"/> holds; fails with <paramref name="what"/> after a timeout.</summary>
        public static void True(Func<bool> condition, string what, int timeoutMs = 5000)
        {
            var clock = Stopwatch.StartNew();
            while (!condition())
            {
                if (clock.ElapsedMilliseconds > timeoutMs)
                    throw new Xunit.Sdk.XunitException($"Timed out waiting for {what}.");
                Thread.Sleep(20);
            }
        }
    }
}
