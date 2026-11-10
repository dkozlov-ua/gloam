using System;
using System.Collections.Generic;
using System.Threading;

namespace Gloam.Core
{
    /// <summary>
    /// Owns the policy for applying gamma to monitors: resolves what each monitor should
    /// render (its saved settings, with the Media profile layered on while Media is
    /// selected), coalesces rapid requests per monitor, and keeps the written ramps in place.
    /// Has no UI dependencies; the tray reacts to settings events by calling
    /// <see cref="ApplyAll"/>.
    /// </summary>
    public class GammaApplyService : IDisposable
    {
        private readonly GammaRampWriter _rampWriter;
        private readonly SettingsManager _settingsManager;

        // Ramp guard: periodically verify the hardware still holds what we applied
        // and restore it when a fullscreen game or driver event stomps the VCGT.
        // Readback is ~free, so a 10s cadence costs nothing measurable.
        private readonly System.Timers.Timer _rampGuard;

        // Per-monitor coalescer: rapid slider drags collapse to one ramp write per
        // completed invocation instead of queueing a backlog. See LatestValueCoalescer
        // for the concurrency contract and its tests for the proof.
        private readonly LatestValueCoalescer<IntPtr, (MonitorInfo Monitor, GammaMode Mode, DisplayAdjustments Adjustments, double WhiteLevel)> _coalescer;

        private int _disposed;

        public GammaApplyService(GammaRampWriter rampWriter, SettingsManager settingsManager)
        {
            _rampWriter = rampWriter ?? throw new ArgumentNullException(nameof(rampWriter));
            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));

            _coalescer = new LatestValueCoalescer<IntPtr, (MonitorInfo, GammaMode, DisplayAdjustments, double)>(
                (_, item, cancellationToken) =>
                {
                    try
                    {
                        if (cancellationToken.IsCancellationRequested)
                            return;
                        _rampWriter.ApplyGamma(item.Item1, item.Item2, item.Item4, item.Item3, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"GammaApplyService: apply failed ({item.Item1.FriendlyName}): {ex.Message}");
                    }
                },
                // Hard floor on hardware gamma-write rate per monitor. This caps any runaway
                // trigger (e.g. a display-change feedback loop) so SetDeviceGammaRamp can never
                // be hammered fast enough to stall the compositor and hang the machine.
                minIntervalMs: 250);

            _rampGuard = new System.Timers.Timer(10000) { AutoReset = true };
            _rampGuard.Elapsed += (_, _) =>
            {
                try { _rampWriter.VerifyAndRestoreRamps(); }
                catch (Exception ex) { Log.Error($"GammaApplyService: ramp guard tick failed: {ex.Message}"); }
            };
            _rampGuard.Start();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            _rampGuard.Stop();
            _rampGuard.Dispose();
            _coalescer.Dispose();
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// What <paramref name="monitor"/> should render now. Resolved synchronously on the
        /// caller's thread so the coalesced hardware work, which may run later, uses the
        /// settings as they were at the request.
        /// </summary>
        public (GammaMode Mode, DisplayAdjustments Adjustments) Resolve(MonitorInfo monitor)
        {
            ArgumentNullException.ThrowIfNull(monitor);
            return Resolve(
                _settingsManager.GetMonitorProfile(monitor.MonitorDevicePath),
                monitor.CurrentGamma,
                monitor.MonitorDevicePath,
                _settingsManager.ProfileSelection,
                _settingsManager.MediaProfile);
        }

        /// <summary>
        /// The resolution policy: the monitor's saved profile (or <paramref name="fallbackMode"/>
        /// and neutral adjustments for a monitor with none), with the Media profile layered on
        /// while Media is selected and covers this monitor.
        /// </summary>
        internal static (GammaMode Mode, DisplayAdjustments Adjustments) Resolve(
            MonitorProfileData? profile,
            GammaMode fallbackMode,
            string? monitorDevicePath,
            DisplayProfileSelection selection,
            MediaProfile media)
        {
            GammaMode mode = profile?.GammaMode ?? fallbackMode;
            DisplayAdjustments adjustments = profile?.ToAdjustments() ?? DisplayAdjustments.Default;

            if (selection == DisplayProfileSelection.Media && media.AppliesTo(monitorDevicePath))
                return media.ComposeWith(mode, adjustments);

            return (mode, adjustments);
        }

        /// <summary>Resolves and writes one monitor's ramp.</summary>
        public void RequestApply(MonitorInfo monitor)
        {
            var (mode, adjustments) = Resolve(monitor);
            if (_settingsManager.GetMonitorProfile(monitor.MonitorDevicePath) is { } saved)
                monitor.CurrentGamma = saved.GammaMode;

            _coalescer.Submit(monitor.HMonitor,
                (monitor, mode, adjustments, MonitorInfo.SanitizeSdrWhiteLevel(monitor.SdrWhiteLevel)));
        }

        public void ApplyAll(IEnumerable<MonitorInfo> monitors)
        {
            foreach (var monitor in monitors)
            {
                RequestApply(monitor);
            }
        }

        /// <summary>Resets all gamma tables. Failures are logged, never thrown.</summary>
        public void ClearAll(IEnumerable<MonitorInfo> monitors)
        {
            foreach (var monitor in monitors)
            {
                try { _rampWriter.ClearGamma(monitor); }
                catch (Exception ex)
                {
                    Log.Error($"GammaApplyService: clear failed ({monitor.FriendlyName}): {ex.Message}");
                }
            }
        }

        /// <summary>See <see cref="GammaRampWriter.InvalidateAppliedState"/>.</summary>
        public void InvalidateAppliedState() => _rampWriter.InvalidateAppliedState();
    }
}
