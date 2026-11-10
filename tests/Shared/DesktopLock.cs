using System;
using System.Threading;

namespace Gloam.TestSupport
{
    /// <summary>
    /// One test at a time on the desktop, across test processes. <c>dotnet test src/Gloam.sln</c>
    /// runs the unit and the UI test assemblies at the same time; a WPF window a unit test shows
    /// takes the foreground, and Gloam's flyout, which hides when it loses activation, vanished
    /// under a UI test that was waiting for it ("Timed out waiting for the Gloam window").
    /// A named semaphore rather than a mutex: a test's setup and teardown need not run on the
    /// same thread, and a mutex must be released by the thread that took it.
    /// </summary>
    internal sealed class DesktopLock : IDisposable
    {
        private const string Name = @"Local\Gloam.Tests.Desktop";
        private readonly Semaphore _semaphore;
        private int _released;

        private DesktopLock(Semaphore semaphore) => _semaphore = semaphore;

        /// <summary>Waits for the desktop; a test that holds it longer than this is stuck.</summary>
        public static DesktopLock Take()
        {
            var semaphore = new Semaphore(1, 1, Name);
            try
            {
                if (!semaphore.WaitOne(TimeSpan.FromMinutes(3)))
                    throw new TimeoutException($"Another test process has held {Name} for 3 minutes.");
                return new DesktopLock(semaphore);
            }
            catch
            {
                semaphore.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            _semaphore.Release();
            _semaphore.Dispose();
        }
    }
}
