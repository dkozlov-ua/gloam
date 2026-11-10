using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Gloam.Core
{
    /// <summary>
    /// Writes generated LUTs to each display's GPU gamma ramp through
    /// <see cref="NativeGammaRamp"/>, skips writes that would change nothing, and guards the
    /// ramps it wrote against being overwritten.
    /// </summary>
    public class GammaRampWriter
    {
        private readonly IGammaRamp _ramp;

        public GammaRampWriter() : this(null)
        {
        }

        /// <param name="ramp">The displays' ramps; null for the real ones.</param>
        public GammaRampWriter(IGammaRamp? ramp)
        {
            _ramp = ramp ?? new NativeGammaRampDevice();
        }

        // Last LUT successfully applied per display (keyed by \\.\DISPLAYn device name).
        // Re-applying an identical LUT is not a no-op on screen: each write rewrites the GPU
        // gamma ramp, which the user can see as a flicker — so we skip it when nothing
        // changed. The arrays are the shared read-only references handed out by
        // LutGenerator's cache; they are never mutated.
        private readonly ConcurrentDictionary<string, (double[] R, double[] G, double[] B)> _lastApplied = new();

        /// <summary>
        /// Forgets all previously-applied LUTs so the next apply always writes. Call after
        /// events that may have reset the GPU gamma ramp behind our back (display
        /// configuration changes, resume from sleep).
        /// </summary>
        public void InvalidateAppliedState()
        {
            _lastApplied.Clear();
            _unverifiableDisplays.Clear();
        }

        // --- Ramp guard ---------------------------------------------------------
        // Fullscreen exclusive games and some driver events silently reset the VCGT.
        // GetDeviceGammaRamp readback is essentially free, so we can verify each
        // display still holds the ramp we last applied and restore it when stomped.

        // Displays whose drivers transform ramps on write (readback never matches
        // what we set). Guarded once, then excluded — otherwise we'd "restore" on
        // every poll and flash the screen.
        private readonly ConcurrentDictionary<string, bool> _unverifiableDisplays = new();

        // Drivers may quantize ramp entries (e.g. 10-bit hardware); allow ~0.8% of
        // full scale before calling it an external overwrite. Real stomps (identity
        // resets, other apps' night modes) deviate far more than this.
        private const int RampToleranceCounts = 512;

        /// <summary>
        /// Verifies each display still holds the ramp we last applied and restores it
        /// if something overwrote it externally. Returns the number restored.
        /// </summary>
        public int VerifyAndRestoreRamps()
        {
            int restored = 0;
            foreach (var kvp in _lastApplied)
            {
                string device = kvp.Key;
                if (_unverifiableDisplays.ContainsKey(device)) continue;

                var snapshot = kvp.Value;
                try
                {
                    if (RampMatchesApplied(device, snapshot)) continue;

                    Log.Info($"GammaRampWriter: Ramp on {device} was overwritten externally; restoring");
                    if (!_ramp.TryApply(device, snapshot.R, snapshot.G, snapshot.B)) continue;
                    restored++;

                    // TOCTOU guard: the coalescer can swap a newer LUT into _lastApplied (and onto
                    // the hardware) between our read of the snapshot and this write, which would
                    // make us clobber the new ramp with the old one. If the live value no longer
                    // matches our snapshot, the newer apply wins — re-assert it so the screen ends
                    // the tick on the current value rather than self-healing (flickering) next tick.
                    if (_lastApplied.TryGetValue(device, out var current) &&
                        (!LutsEqual(current.R, snapshot.R) ||
                         !LutsEqual(current.G, snapshot.G) ||
                         !LutsEqual(current.B, snapshot.B)))
                    {
                        _ramp.TryApply(device, current.R, current.G, current.B);
                        continue;
                    }

                    if (!RampMatchesApplied(device, snapshot))
                    {
                        _unverifiableDisplays[device] = true;
                        Log.Error($"GammaRampWriter: {device} readback never matches the written ramp; ramp guard disabled for this display");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"GammaRampWriter: Ramp guard error on {device}: {ex.Message}");
                }
            }
            return restored;
        }

        private bool RampMatchesApplied(string device, (double[] R, double[] G, double[] B) luts)
        {
            // Treat an unreadable ramp as a match — never fight a display we can't see.
            if (!_ramp.TryRead(device, out var r, out var g, out var b)) return true;
            return ChannelMatches(r, luts.R) && ChannelMatches(g, luts.G) && ChannelMatches(b, luts.B);
        }

        private static bool ChannelMatches(ushort[] hardware, double[] lut)
        {
            var expected = NativeGammaRamp.BuildRampChannel(lut);
            if (hardware.Length != expected.Length) return false;
            for (int i = 0; i < expected.Length; i++)
            {
                if (Math.Abs(hardware[i] - expected[i]) > RampToleranceCounts) return false;
            }
            return true;
        }

        public void ApplyGamma(MonitorInfo monitor, GammaMode mode, double whiteLevel)
        {
            ApplyGamma(monitor, mode, whiteLevel, DisplayAdjustments.Default);
        }

        /// <summary>
        /// Generates the LUT for <paramref name="mode"/> and <paramref name="adjustments"/> and
        /// writes it to the monitor's gamma ramp, unless the ramp already holds it.
        /// </summary>
        public void ApplyGamma(
            MonitorInfo monitor,
            GammaMode mode,
            double whiteLevel,
            DisplayAdjustments adjustments,
            CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Log.Info($"GammaRampWriter.ApplyGamma: cancelled before LUT generation for {monitor.DeviceName}");
                return;
            }

            Log.Info($"GammaRampWriter.ApplyGamma: monitor={monitor.DeviceName}, mode={mode}, whiteLevel={whiteLevel}, " +
                     $"brightness={adjustments.Brightness}, shadowDetail={adjustments.ShadowDetailStrength:F2}");

            var (lutR, lutG, lutB, _) = LutGenerator.GenerateLut(mode, whiteLevel, adjustments, monitor.IsHdrActive);

            if (cancellationToken.IsCancellationRequested)
            {
                Log.Info($"GammaRampWriter.ApplyGamma: cancelled before hardware write for {monitor.DeviceName}");
                return;
            }

            // Skip the ramp rewrite entirely when this display already has exactly this LUT.
            string applyKey = monitor.DeviceName;
            if (_lastApplied.TryGetValue(applyKey, out var last) &&
                LutsEqual(last.R, lutR) && LutsEqual(last.G, lutG) && LutsEqual(last.B, lutB))
            {
                Log.Info($"GammaRampWriter.ApplyGamma: LUT unchanged for {applyKey}, skipping");
                return;
            }

            if (_ramp.TryApply(monitor.DeviceName, lutR, lutG, lutB))
            {
                Log.Info($"GammaRampWriter.ApplyGamma: Applied gamma ramp for {applyKey}");
                _lastApplied[applyKey] = (lutR, lutG, lutB);
                return;
            }

            // Unknown ramp state — make sure the next apply isn't skipped.
            _lastApplied.TryRemove(applyKey, out _);
            Log.Error($"GammaRampWriter.ApplyGamma: Windows rejected the gamma ramp for {applyKey}");
        }

        public void ClearGamma(MonitorInfo monitor)
        {
            // The ramp is reset (or in an unknown state on failure) either way.
            _lastApplied.TryRemove(monitor.DeviceName, out _);

            if (_ramp.TryClear(monitor.DeviceName))
                Log.Info($"GammaRampWriter.ClearGamma: Cleared gamma ramp for {monitor.DeviceName}");
            else
                Log.Error($"GammaRampWriter.ClearGamma: Could not clear the gamma ramp for {monitor.DeviceName}");
        }

        private static bool LutsEqual(double[] a, double[] b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }
    }
}
