using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Gloam.Core;

namespace Gloam.Cli
{
    /// <summary>
    /// Headless diagnostics for the gamma pipeline. It can dump LUTs, run CI-friendly LUT
    /// invariants, and compare brightness / dark-area visibility scenarios without starting
    /// the tray app or touching display state.
    /// </summary>
    static class Program
    {
        static int Main(string[] args)
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                PrintHelp();
                return 0;
            }

            try
            {
                return args[0].ToLowerInvariant() switch
                {
                    "lut" => RunLut(args),
                    "sweep" => RunSweep(args),
                    "compare" => RunCompare(args),
                    "check-lut" => RunCheckLut(args),
                    // Back-compat: old `Cli 2.2 200 [out.csv]` form still works
                    _ => RunLegacy(args)
                };
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                if (Environment.GetEnvironmentVariable("HDRCLI_TRACE") == "1")
                    Console.Error.WriteLine(ex);
                return 1;
            }
        }

        private static void PrintHelp()
        {
            Console.WriteLine("Gloam CLI - headless gamma pipeline diagnostics");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  cli lut      <gamma> <white> [options]   Dump one LUT");
            Console.WriteLine("  cli sweep    <gamma> <white>             Matrix of brightness/dark-area scenarios");
            Console.WriteLine("  cli compare  <gamma> <white> <A> <B>     Side-by-side diff of two scenarios");
            Console.WriteLine("  cli check-lut <gamma> <white> [options]  Validate LUT invariants, exit non-zero on failure");
            Console.WriteLine("  cli <gamma> <white> [out.csv]            Legacy form: dump one LUT, optionally to CSV");
            Console.WriteLine();
            Console.WriteLine("Gamma:  2.2 | 2.4 | default");
            Console.WriteLine("White:  nits (e.g. 80, 200, 480)");
            Console.WriteLine();
            Console.WriteLine("Options (lut/check-lut):");
            Console.WriteLine("  --brightness N      10..100 (default 100)");
            Console.WriteLine("  --shadow N          dark-area visibility 0..100 % (default 0)");
            Console.WriteLine("  --linear-dim        Use linear instead of perceptual dimming");
            Console.WriteLine("  --sdr               Generate the SDR path (decode-gamma-adjust-encode)");
            Console.WriteLine("  --csv PATH          Also write a CSV file");
            Console.WriteLine("  --json PATH|-       Write machine-readable output, or '-' for stdout");
            Console.WriteLine("  --no-chart          Skip the ASCII sparkline");
            Console.WriteLine();
            Console.WriteLine("Scenario shorthand (compare): 'b=50,s=30,lin' means brightness 50,");
            Console.WriteLine("  dark-area visibility 30 %, linear dimming");
            Console.WriteLine();
            Console.WriteLine("Exit codes: 0 ok, 1 error, 2 check-lut invariant failure");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  cli sweep 2.2 200");
            Console.WriteLine("  cli lut 2.2 200 --brightness 50 --csv dim50.csv");
            Console.WriteLine("  cli lut 2.4 240 --shadow 40 --sdr");
            Console.WriteLine("  cli check-lut 2.2 200 --brightness 50 --json -");
            Console.WriteLine("  cli compare 2.2 200 b=100 b=50,s=30");
        }

        // ---------- Legacy path (backward compat with the tiny original CLI) ----------

        private static int RunLegacy(string[] args)
        {
            if (args.Length < 2)
            {
                PrintHelp();
                return 1;
            }
            var opts = new LutOptions
            {
                Mode = ParseGamma(args[0]),
                WhiteLevel = ParseDouble(args[1]),
                CsvPath = args.Length >= 3 ? args[2] : null,
                ShowChart = false
            };
            DumpLut(opts);
            return 0;
        }

        // ---------- Subcommands ----------

        private static int RunLut(string[] args)
        {
            if (args.Length < 3) throw new ArgumentException("lut requires <gamma> <white>");
            var opts = new LutOptions
            {
                Mode = ParseGamma(args[1]),
                WhiteLevel = ParseDouble(args[2])
            };
            ParseLutOptions(args, 3, opts);
            DumpLut(opts);
            return 0;
        }

        private static int RunSweep(string[] args)
        {
            if (args.Length < 3) throw new ArgumentException("sweep requires <gamma> <white>");
            var mode = ParseGamma(args[1]);
            var white = ParseDouble(args[2]);

            Console.WriteLine($"Sweep: gamma {GammaLabel(mode)}, SDR white {white} nits\n");

            Console.WriteLine("-- Brightness (perceptual) --");
            foreach (var b in new[] { 100.0, 75.0, 50.0, 25.0, 10.0 })
            {
                var opts = new LutOptions { Mode = mode, WhiteLevel = white };
                opts.Adjustments.Brightness = b;
                PrintScenarioRow($"brightness={b,3:0}", opts);
            }

            Console.WriteLine("\n-- Brightness (linear) --");
            foreach (var b in new[] { 100.0, 75.0, 50.0, 25.0, 10.0 })
            {
                var opts = new LutOptions { Mode = mode, WhiteLevel = white };
                opts.Adjustments.Brightness = b;
                opts.Adjustments.UseLinearBrightness = true;
                PrintScenarioRow($"brightness={b,3:0} lin", opts);
            }

            Console.WriteLine("\n-- Dark-area visibility --");
            foreach (var s in new[] { 0.0, 25.0, 50.0, 75.0, 100.0 })
            {
                var opts = new LutOptions { Mode = mode, WhiteLevel = white };
                opts.Adjustments.ShadowDetailStrength = s / 100.0;
                PrintScenarioRow($"shadow={s,3:0}%", opts);
            }

            Console.WriteLine("\n-- Combined (dimmed Media-like) --");
            foreach (var (b, s) in new[] { (100.0, 0.0), (75.0, 25.0), (50.0, 50.0), (25.0, 75.0), (10.0, 100.0) })
            {
                var opts = new LutOptions { Mode = mode, WhiteLevel = white };
                opts.Adjustments.Brightness = b;
                opts.Adjustments.ShadowDetailStrength = s / 100.0;
                PrintScenarioRow($"b={b,3:0} s={s,3:0}%", opts);
            }

            // The visibility toe only moves values below its pivot (10 % of SDR white), so the
            // L/M/H columns alone would show no change for it. S sits inside the toe.
            Console.WriteLine($"\nColumns after chart: L=lowest (index 0), S=shadow (index {ShadowSampleIndex}), " +
                              "M=mid (index 512), H=peak (1023)");
            return 0;
        }

        private static int RunCompare(string[] args)
        {
            if (args.Length < 5) throw new ArgumentException("compare requires <gamma> <white> <A> <B>");
            var mode = ParseGamma(args[1]);
            var white = ParseDouble(args[2]);
            var optsA = new LutOptions { Mode = mode, WhiteLevel = white };
            var optsB = new LutOptions { Mode = mode, WhiteLevel = white };
            ApplyScenarioShorthand(optsA.Adjustments, args[3]);
            ApplyScenarioShorthand(optsB.Adjustments, args[4]);

            var lutA = GenerateGreyLut(optsA);
            var lutB = GenerateGreyLut(optsB);

            Console.WriteLine($"Compare: gamma {GammaLabel(mode)}, SDR white {white} nits");
            Console.WriteLine($"  A: {args[3]}   B: {args[4]}\n");

            Console.WriteLine("A " + Sparkline(lutA));
            Console.WriteLine("B " + Sparkline(lutB));
            Console.WriteLine("Δ " + SparklineSigned(lutA, lutB));

            double maxDelta = 0, peakDelta = lutB[1023] - lutA[1023];
            for (int i = 0; i < lutA.Length; i++)
            {
                double d = Math.Abs(lutA[i] - lutB[i]);
                if (d > maxDelta) maxDelta = d;
            }
            Console.WriteLine();
            Console.WriteLine($"Max |Δ|       : {maxDelta:F4}");
            Console.WriteLine($"Peak A / B    : {lutA[1023]:F4} / {lutB[1023]:F4}  (delta {peakDelta:+0.0000;-0.0000})");
            Console.WriteLine($"Midpoint A / B: {lutA[512]:F4} / {lutB[512]:F4}");
            Console.WriteLine($"Shadow A / B  : {lutA[ShadowSampleIndex]:F4} / {lutB[ShadowSampleIndex]:F4}  (index {ShadowSampleIndex})");
            return 0;
        }

        private static int RunCheckLut(string[] args)
        {
            if (args.Length < 3) throw new ArgumentException("check-lut requires <gamma> <white>");
            var opts = new LutOptions
            {
                Mode = ParseGamma(args[1]),
                WhiteLevel = ParseDouble(args[2]),
                ShowChart = false
            };
            ParseLutOptions(args, 3, opts);

            var lut = GenerateLut(opts);

            var stats = new[]
            {
                AnalyzeChannel("R", lut.R),
                AnalyzeChannel("G", lut.G),
                AnalyzeChannel("B", lut.B),
                AnalyzeChannel("Grey", lut.Grey)
            };
            // Grey is a reference only; the hardware ramp takes R, G and B.
            var ramps = new[]
            {
                AnalyzeRamp("R", lut.R),
                AnalyzeRamp("G", lut.G),
                AnalyzeRamp("B", lut.B)
            };

            var failures = stats.SelectMany(s => s.Failures.Select(f => $"{s.Name}: {f}")).ToList();
            failures.AddRange(ramps.SelectMany(r => r.Failures.Select(f => $"{r.Name} ramp: {f}")));
            failures.AddRange(CheckCurveInvariants(opts, lut));

            var warnings = new List<string>();
            foreach (var ramp in ramps.Where(r => r.EntriesOutsideEnvelope > 0))
            {
                string message =
                    $"{ramp.Name} ramp: {ramp.EntriesOutsideEnvelope} of 256 entries fall outside Windows' gamma-range " +
                    $"envelope (max bend {ramp.MaxBendCounts} counts); unless GdiIcmGammaRange is unlocked the " +
                    "display gets the closest accepted ramp, not this curve";

                // The HDR path keeps its PQ LUT near identity at the top (headroom passthrough),
                // so it never needs the envelope; an overrun there is a pipeline regression.
                // The SDR path legitimately leaves the envelope at low brightness (white below
                // ~0.496 of full scale at entry 255), and NativeGammaRamp clamps that by design.
                if (opts.IsSdr) warnings.Add(message);
                else failures.Add(message);
            }

            var report = new
            {
                command = "check-lut",
                gamma = GammaLabel(opts.Mode),
                sdrWhiteNits = opts.WhiteLevel,
                path = opts.IsSdr ? "sdr" : "hdr",
                adjustments = DescribeAdjustments(opts.Adjustments),
                passed = failures.Count == 0,
                failures,
                warnings,
                channels = stats,
                ramps
            };

            if (opts.JsonPath == "-")
            {
                WriteJson(opts.JsonPath, report);
            }
            else
            {
                Console.WriteLine(failures.Count == 0 ? "PASS: LUT invariants hold." : "FAIL: LUT invariants broke.");
                foreach (var failure in failures)
                    Console.WriteLine("  " + failure);
                foreach (var warning in warnings)
                    Console.WriteLine("  warning: " + warning);
                foreach (var s in stats)
                {
                    Console.WriteLine(
                        $"  {s.Name,-4} min={s.Min:F6} max={s.Max:F6} max_step={s.MaxPositiveStep:F6} " +
                        $"max_drop={s.MaxNegativeStep:F6}");
                }
                foreach (var r in ramps)
                {
                    Console.WriteLine(
                        $"  {r.Name,-4} ramp[0]={r.First} ramp[255]={r.Last} outside_envelope={r.EntriesOutsideEnvelope} " +
                        $"max_bend={r.MaxBendCounts}");
                }
                if (opts.JsonPath != null) WriteJson(opts.JsonPath, report);
            }

            return failures.Count == 0 ? 0 : 2;
        }

        // ---------- Core dump ----------

        private static void DumpLut(LutOptions opts)
        {
            var lut = GenerateLut(opts);

            var report = new
            {
                command = "lut",
                gamma = GammaLabel(opts.Mode),
                sdrWhiteNits = opts.WhiteLevel,
                path = opts.IsSdr ? "sdr" : "hdr",
                adjustments = DescribeAdjustments(opts.Adjustments),
                samples = SamplePoints(lut.Grey.Length).Select(i => new
                {
                    index = i,
                    inputSignal = i / 1023.0,
                    inputNits = TransferFunctions.PqEotf(i / 1023.0),
                    outputSignal = lut.Grey[i],
                    outputNits = TransferFunctions.PqEotf(lut.Grey[i])
                }).ToArray(),
                channels = new[]
                {
                    AnalyzeChannel("R", lut.R),
                    AnalyzeChannel("G", lut.G),
                    AnalyzeChannel("B", lut.B),
                    AnalyzeChannel("Grey", lut.Grey)
                }
            };

            if (opts.JsonPath == "-")
            {
                WriteJson(opts.JsonPath, report);
                return;
            }

            Console.WriteLine($"LUT: gamma {GammaLabel(opts.Mode)}, SDR white {opts.WhiteLevel} nits, " +
                              $"brightness {opts.Adjustments.Brightness:0}" +
                              (opts.Adjustments.UseLinearBrightness ? " (linear)" : string.Empty) +
                              $", shadow {opts.Adjustments.ShadowDetailStrength * 100.0:0}%" +
                              (opts.IsSdr ? " [SDR path]" : string.Empty));

            if (opts.ShowChart)
            {
                // Every adjustment is neutral, so R, G and B are the same curve; one line says it all.
                Console.WriteLine("Y " + Sparkline(lut.Grey));
            }

            Console.WriteLine();
            Console.WriteLine($"{"idx",4} {"in_sig",8} {"in_nits",10} {"out_sig(grey)",14} {"out_nits(grey)",16}");
            foreach (int i in SamplePoints(lut.Grey.Length))
            {
                double inSig = i / 1023.0;
                double inNits = TransferFunctions.PqEotf(inSig);
                double outSig = lut.Grey[i];
                double outNits = TransferFunctions.PqEotf(outSig);
                Console.WriteLine($"{i,4} {inSig,8:F4} {inNits,10:F2} {outSig,14:F4} {outNits,16:F2}");
            }

            if (opts.CsvPath != null)
            {
                WriteCsv(opts.CsvPath, lut);
                Console.WriteLine($"\nWrote {opts.CsvPath}");
            }

            if (opts.JsonPath != null)
            {
                WriteJson(opts.JsonPath, report);
            }
        }

        private static void PrintScenarioRow(string label, LutOptions opts)
        {
            var lut = GenerateGreyLut(opts);
            double L = lut[0], S = lut[ShadowSampleIndex], M = lut[512], H = lut[1023];
            Console.WriteLine($"  {label,-22} {Sparkline(lut)}  L={L:F2} S={S:F3} M={M:F2} H={H:F2}");
        }

        // The cache hands out shared arrays: everything below only reads them.
        private static (double[] R, double[] G, double[] B, double[] Grey) GenerateLut(LutOptions opts) =>
            LutGenerator.GenerateLut(opts.Mode, opts.WhiteLevel, opts.Adjustments, isHdr: !opts.IsSdr);

        private static double[] GenerateGreyLut(LutOptions opts) => GenerateLut(opts).Grey;

        // ---------- Invariants ----------

        /// <summary>
        /// Whole-curve promises the generator makes beyond per-channel range and monotonicity.
        /// The unit tests pin them on a fixed grid of whites and settings; checking them here
        /// covers whatever gamma/white/adjustment combination a user is actually running.
        /// </summary>
        private static List<string> CheckCurveInvariants(
            LutOptions opts, (double[] R, double[] G, double[] B, double[] Grey) lut)
        {
            var failures = new List<string>();
            // Judge against what the generator evaluated, not the raw options.
            var adjustments = opts.Adjustments.Sanitized();
            double[] grey = lut.Grey;

            // Every remaining adjustment is neutral, so the three uploaded channels must be the
            // same curve. A divergence means a per-channel stage crept back in unchecked.
            foreach (var (name, channel) in new[] { ("R", lut.R), ("G", lut.G), ("B", lut.B) })
            {
                if (channel.Length != grey.Length)
                {
                    failures.Add($"{name}: length {channel.Length} differs from Grey's {grey.Length}");
                    continue;
                }
                for (int i = 0; i < grey.Length; i++)
                {
                    if (channel[i].Equals(grey[i])) continue;
                    failures.Add($"{name}: diverges from Grey at index {i} ({channel[i]:G17} vs {grey[i]:G17})");
                    break;
                }
            }

            if (grey.Length != 1024)
            {
                // Everything below indexes the 1024-point grid the ramp resampler assumes.
                failures.Add($"Grey: expected 1024 entries, got {grey.Length}");
                return failures;
            }

            // Both paths, gamma and the visibility toe are black-anchored. A lifted black greys
            // out letterboxing and every dark UI surface.
            if (Math.Abs(grey[0]) > 1e-9)
                failures.Add($"black is not anchored: index 0 maps to {grey[0]:G17}");

            if (opts.Mode == GammaMode.WindowsDefault && !adjustments.HasAdjustments)
            {
                // Default gamma with nothing to adjust is the bypass: an exact identity on
                // both paths, so a stale ramp guard restore can never look like a change.
                CheckIdentity(grey, 0, 1e-12, "bypass", failures);
            }
            else if (opts.IsSdr)
            {
                // Gamma and the toe never move SDR white; only dimming may.
                if (adjustments.Brightness >= 100.0 && Math.Abs(grey[1023] - 1.0) > 1e-9)
                    failures.Add($"SDR white moved at full brightness: index 1023 maps to {grey[1023]:G17}");

                // Gamma 2.2 and Default decode and re-encode with the same 2.2 exponent, so with
                // nothing to adjust the SDR path nets to a passthrough (pow round-trip only).
                if (opts.Mode != GammaMode.Gamma24 && !adjustments.HasAdjustments)
                    CheckIdentity(grey, 0, 1e-9, "SDR 2.2 passthrough", failures);
            }
            else
            {
                CheckHdrHeadroom(grey, opts.WhiteLevel, adjustments, failures);
            }

            return failures;
        }

        /// <summary>
        /// Above SDR white the HDR path must preserve creative intent: at full brightness every
        /// highlight is an exact passthrough (gamma and the toe have zero residual at white);
        /// when dimmed, every highlight lands exactly on the requested dimming curve with no
        /// second shoulder. Either way no highlight step may collapse.
        /// </summary>
        private static void CheckHdrHeadroom(
            double[] grey, double white, DisplayAdjustments adjustments, List<string> failures)
        {
            bool fullBrightness = adjustments.Brightness >= 100.0;
            for (int i = 1; i < grey.Length; i++)
            {
                double input = i / 1023.0;
                double nits = TransferFunctions.PqEotf(input);
                if (nits < white) continue;

                if (fullBrightness)
                {
                    if (Math.Abs(grey[i] - input) >= 1e-12)
                    {
                        failures.Add(
                            $"HDR highlight not passed through at index {i}: {nits:F3} nits became " +
                            $"{TransferFunctions.PqEotf(grey[i]):F3}");
                        return;
                    }
                }
                else
                {
                    double expected = ToneAdjustments.ApplyDimmingNits(
                        nits, adjustments.Brightness, white, adjustments.UseLinearBrightness);
                    double actual = TransferFunctions.PqEotf(grey[i]);
                    if (Math.Abs(actual - expected) > Math.Max(1e-8, expected * 1e-9))
                    {
                        failures.Add(
                            $"HDR highlight off the dimming curve at index {i}: {nits:F3} nits became " +
                            $"{actual:F3}, expected {expected:F3}");
                        return;
                    }
                }

                if (!(grey[i] > grey[i - 1]))
                {
                    failures.Add($"HDR highlight detail lost at index {i - 1}->{i}");
                    return;
                }
            }
        }

        private static void CheckIdentity(double[] lut, int start, double tolerance, string label, List<string> failures)
        {
            for (int i = start; i < lut.Length; i++)
            {
                double expected = i / 1023.0;
                if (Math.Abs(lut[i] - expected) > tolerance)
                {
                    failures.Add($"{label} is not identity at index {i}: {lut[i]:G17} vs {expected:G17}");
                    return;
                }
            }
        }

        // Mirrors NativeGammaRamp's Windows validation envelope: each 16-bit entry's high byte
        // must stay within 128 of its own index, and one violating entry makes
        // SetDeviceGammaRamp reject the whole ramp. The bounds live in internals Gloam.Core
        // does not expose to the CLI, so they are restated here; the cross-check in
        // AnalyzeRamp fails loudly if the two ever disagree.
        private const int EnvelopeHalfRangeHighBytes = 128;

        private static ushort EnvelopeLower(int i) =>
            (ushort)(i <= EnvelopeHalfRangeHighBytes ? 0 : (i - EnvelopeHalfRangeHighBytes) * 256);

        private static ushort EnvelopeUpper(int i) =>
            (ushort)Math.Min(65535, (i + EnvelopeHalfRangeHighBytes + 1) * 256 - 1);

        /// <summary>
        /// Resamples the LUT onto the 256-entry hardware ramp without the envelope (the curve
        /// the app asks for), measures how far Windows' envelope would bend it, and checks
        /// the ramp <see cref="NativeGammaRamp.BuildRampChannel(double[])"/> actually builds.
        /// </summary>
        private static RampStats AnalyzeRamp(string name, double[] lut)
        {
            var failures = new List<string>();
            ushort[] applied = NativeGammaRamp.BuildRampChannel(lut);
            if (applied.Length != 256)
            {
                failures.Add($"expected 256 entries, got {applied.Length}");
                return new RampStats(name, 0, 0, 0, 0, failures);
            }

            int outside = 0;
            int maxBend = 0;
            for (int i = 0; i < 256; i++)
            {
                // Same linear interpolation as NativeGammaRamp (and dispwin loading a .cal).
                double pos = i / 255.0 * (lut.Length - 1);
                int lo = (int)pos;
                int hi = Math.Min(lo + 1, lut.Length - 1);
                double value = lut[lo] + (lut[hi] - lut[lo]) * (pos - lo);
                value = double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : 0.0;
                ushort requested = (ushort)Math.Clamp(Math.Round(value * 65535.0), 0, 65535);

                ushort bounded = Math.Clamp(requested, EnvelopeLower(i), EnvelopeUpper(i));
                if (bounded != requested)
                {
                    outside++;
                    maxBend = Math.Max(maxBend, Math.Abs(bounded - requested));
                }

                // The real ramp is the requested one (range unlocked in the registry) or its
                // envelope clamp (the default). Anything else means this mirror has drifted from
                // NativeGammaRamp and the envelope numbers above can't be trusted.
                if (applied[i] != requested && applied[i] != bounded)
                {
                    failures.Add(
                        $"NativeGammaRamp built {applied[i]} at entry {i}; expected {requested} " +
                        $"(or {bounded} clamped). The CLI's envelope mirror is stale.");
                    break;
                }

                // Hardware ramps are uploaded as-is; a backwards step there is visible banding
                // even when every LUT sample was monotonic.
                if (i > 0 && applied[i] < applied[i - 1])
                {
                    failures.Add($"non-monotonic ramp {applied[i - 1]}->{applied[i]} at entry {i - 1}->{i}");
                    break;
                }
            }

            return new RampStats(name, applied[0], applied[255], outside, maxBend, failures);
        }

        // ---------- ASCII rendering ----------

        // Braille-block bar chars with 8 vertical steps. Unicode range 0x2581..0x2588.
        private static readonly char[] Blocks = { ' ', '▁', '▂', '▃', '▄', '▅', '▆', '▇', '█' };

        private static string Sparkline(double[] lut, int width = 64)
        {
            // Map the 1024-point LUT onto `width` columns, taking the mean of each slice.
            var sb = new StringBuilder(width);
            double step = lut.Length / (double)width;
            for (int c = 0; c < width; c++)
            {
                int start = (int)(c * step);
                int end = Math.Min(lut.Length, (int)((c + 1) * step));
                double sum = 0;
                int n = 0;
                for (int i = start; i < end; i++) { sum += lut[i]; n++; }
                double v = n > 0 ? sum / n : 0;
                int idx = Math.Clamp((int)Math.Round(v * (Blocks.Length - 1)), 0, Blocks.Length - 1);
                sb.Append(Blocks[idx]);
            }
            return sb.ToString();
        }

        private static string SparklineSigned(double[] a, double[] b, int width = 64)
        {
            // Signed delta: '.' near zero, '-' for B<A, '+' for B>A, uppercase for larger diffs.
            var sb = new StringBuilder(width);
            double step = a.Length / (double)width;
            for (int c = 0; c < width; c++)
            {
                int start = (int)(c * step);
                int end = Math.Min(a.Length, (int)((c + 1) * step));
                double sum = 0;
                int n = 0;
                for (int i = start; i < end; i++) { sum += (b[i] - a[i]); n++; }
                double d = n > 0 ? sum / n : 0;
                double mag = Math.Abs(d);
                char ch = mag < 0.005 ? '·'
                        : mag < 0.02 ? (d < 0 ? '-' : '+')
                        : mag < 0.08 ? (d < 0 ? '=' : '#')
                        : (d < 0 ? '▼' : '▲');
                sb.Append(ch);
            }
            return sb.ToString();
        }

        // ---------- Helpers ----------

        // Index 256 is PQ 0.25, about 5 nits: deep inside the visibility toe at every usual SDR
        // white (the toe's pivot is 10 % of white, 8 nits even at 80-nit white), so it shows
        // the toe's lift where the L/M/H columns cannot.
        private const int ShadowSampleIndex = 256;

        private static IEnumerable<int> SamplePoints(int len)
        {
            // Spread that hits the SDR region densely, the SDR/HDR boundary (around index 520
            // at 200 nits) specifically, and a couple headroom samples.
            yield return 0;
            yield return 64;
            yield return 128;
            yield return 256;
            yield return 384;
            yield return 448;
            yield return 512;
            yield return 576;
            yield return 640;
            yield return 768;
            yield return 896;
            yield return len - 1;
        }

        private static void WriteCsv(string path, (double[] R, double[] G, double[] B, double[] Grey) lut)
        {
            using var w = new StreamWriter(path);
            w.WriteLine("index,input_signal,input_nits,out_r,out_g,out_b,out_grey");
            for (int i = 0; i < lut.Grey.Length; i++)
            {
                double inSig = i / 1023.0;
                double inNits = TransferFunctions.PqEotf(inSig);
                w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0},{1:G8},{2:G8},{3:G8},{4:G8},{5:G8},{6:G8}",
                    i, inSig, inNits, lut.R[i], lut.G[i], lut.B[i], lut.Grey[i]));
            }
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        private static void WriteJson(string path, object payload)
        {
            string json = JsonSerializer.Serialize(payload, JsonOptions);
            if (path == "-")
            {
                Console.WriteLine(json);
                return;
            }

            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(path, json + Environment.NewLine);
            Console.WriteLine($"Wrote {path}");
        }

        private static object DescribeAdjustments(DisplayAdjustments adjustments) => new
        {
            adjustments.Brightness,
            adjustments.UseLinearBrightness,
            shadowPercent = adjustments.ShadowDetailStrength * 100.0,
            adjustments.ShadowDetailStrength,
            adjustments.ShadowDetailPivot
        };

        private static ChannelStats AnalyzeChannel(string name, double[] values)
        {
            var failures = new List<string>();
            double min = double.PositiveInfinity;
            double max = double.NegativeInfinity;
            double maxPositiveStep = 0.0;
            double maxNegativeStep = 0.0;
            double previous = double.NaN;

            for (int i = 0; i < values.Length; i++)
            {
                double value = values[i];
                if (!double.IsFinite(value))
                {
                    failures.Add($"non-finite value at index {i}");
                    continue;
                }
                if (value < -1e-9 || value > 1.0 + 1e-9)
                    failures.Add($"out-of-range value {value:G17} at index {i}");

                min = Math.Min(min, value);
                max = Math.Max(max, value);

                if (double.IsFinite(previous))
                {
                    double step = value - previous;
                    if (step >= 0)
                    {
                        maxPositiveStep = Math.Max(maxPositiveStep, step);
                    }
                    else
                    {
                        maxNegativeStep = Math.Max(maxNegativeStep, -step);
                        if (step < -1e-7)
                            failures.Add($"non-monotonic drop {step:G17} at index {i - 1}->{i}");
                    }
                }
                previous = value;
            }

            if (values.Length == 0)
                failures.Add("empty channel");

            return new ChannelStats(
                name,
                double.IsPositiveInfinity(min) ? double.NaN : min,
                double.IsNegativeInfinity(max) ? double.NaN : max,
                maxPositiveStep,
                maxNegativeStep,
                failures);
        }

        private static GammaMode ParseGamma(string s) => s.ToLowerInvariant() switch
        {
            "2.2" => GammaMode.Gamma22,
            "2.4" => GammaMode.Gamma24,
            "default" or "srgb" or "windows" => GammaMode.WindowsDefault,
            _ => throw new ArgumentException($"Unknown gamma mode: {s}")
        };

        private static string GammaLabel(GammaMode m) => m switch
        {
            GammaMode.Gamma22 => "2.2",
            GammaMode.Gamma24 => "2.4",
            _ => "default"
        };

        private static double ParseDouble(string s)
        {
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                throw new ArgumentException($"Invalid number: {s}");
            return v;
        }

        // The generator silently clamps out-of-range adjustments. A diagnostic tool must not:
        // a typo like --shadow 400 would otherwise report on a curve nobody asked for.
        private static double ParseRange(string s, double min, double max, string option)
        {
            double v = ParseDouble(s);
            if (v < min || v > max)
                throw new ArgumentException($"{option} must be within {min}..{max}, got {s}");
            return v;
        }

        // Same range the app's settings normalization allows for a monitor's brightness.
        private static double ParseBrightness(string s, string option) => ParseRange(s, 10.0, 100.0, option);

        // Dark-area visibility is a percentage in the UI and a 0..1 toe strength in Core.
        private static double ParseShadowStrength(string s, string option) => ParseRange(s, 0.0, 100.0, option) / 100.0;

        private static string RequireValue(string[] args, ref int index, string option)
        {
            if (index + 1 >= args.Length)
                throw new ArgumentException($"{option} requires a value");
            index++;
            return args[index];
        }

        private static void ParseLutOptions(string[] args, int start, LutOptions opts)
        {
            for (int i = start; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--brightness":
                        opts.Adjustments.Brightness = ParseBrightness(RequireValue(args, ref i, args[i]), "--brightness");
                        break;
                    case "--shadow":
                        opts.Adjustments.ShadowDetailStrength = ParseShadowStrength(RequireValue(args, ref i, args[i]), "--shadow");
                        break;
                    case "--linear-dim":
                        opts.Adjustments.UseLinearBrightness = true;
                        break;
                    case "--sdr":
                        opts.IsSdr = true;
                        break;
                    case "--csv":
                        opts.CsvPath = RequireValue(args, ref i, args[i]);
                        break;
                    case "--json":
                        opts.JsonPath = RequireValue(args, ref i, args[i]);
                        break;
                    case "--no-chart":
                        opts.ShowChart = false;
                        break;
                    default: throw new ArgumentException($"Unknown option: {args[i]}");
                }
            }
        }

        /// <summary>Parses `b=50,s=30,lin` style shorthand into DisplayAdjustments.</summary>
        private static void ApplyScenarioShorthand(DisplayAdjustments adjustments, string spec)
        {
            foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string piece = part.Trim();
                if (piece.ToLowerInvariant() is "lin" or "linear")
                {
                    adjustments.UseLinearBrightness = true;
                    continue;
                }

                var kv = piece.Split('=');
                if (kv.Length != 2) throw new ArgumentException($"Bad scenario piece: {part}");
                switch (kv[0].Trim().ToLowerInvariant())
                {
                    case "b": case "brightness": adjustments.Brightness = ParseBrightness(kv[1], kv[0]); break;
                    case "s": case "shadow":     adjustments.ShadowDetailStrength = ParseShadowStrength(kv[1], kv[0]); break;
                    default: throw new ArgumentException($"Unknown scenario key: {kv[0]}");
                }
            }
        }

        private sealed class LutOptions
        {
            public GammaMode Mode { get; set; } = GammaMode.Gamma22;
            public double WhiteLevel { get; set; } = 200;
            public DisplayAdjustments Adjustments { get; } = new DisplayAdjustments();
            public bool IsSdr { get; set; }
            public string? CsvPath { get; set; }
            public string? JsonPath { get; set; }
            public bool ShowChart { get; set; } = true;
        }

        private sealed record ChannelStats(
            string Name,
            double Min,
            double Max,
            double MaxPositiveStep,
            double MaxNegativeStep,
            IReadOnlyList<string> Failures);

        private sealed record RampStats(
            string Name,
            ushort First,
            ushort Last,
            int EntriesOutsideEnvelope,
            int MaxBendCounts,
            IReadOnlyList<string> Failures);
    }
}
