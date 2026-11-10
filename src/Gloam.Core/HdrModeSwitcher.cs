using System;
using System.Collections.Generic;
using Gloam.Interop;

namespace Gloam.Core
{
    /// <summary>Turns a display's HDR ("Use HDR" in Settings > Display) on or off.</summary>
    public interface IHdrSwitch
    {
        /// <summary>True when the display is now in the asked mode.</summary>
        bool TrySetHdr(MonitorInfo monitor, bool enabled);
    }

    /// <summary>The real switch: <see cref="DisplayConfig.SetHdrState"/>.</summary>
    public sealed class NativeHdrSwitch : IHdrSwitch
    {
        public bool TrySetHdr(MonitorInfo monitor, bool enabled)
        {
            ArgumentNullException.ThrowIfNull(monitor);
            // MonitorManager infers capability from DXGI luminance metadata, which some SDR
            // panels report too; Windows' own Advanced Color flags decide here.
            if (DisplayConfig.TryGetAdvancedColorInfo(monitor.DeviceName, out bool supported, out _, out bool forceDisabled) &&
                (!supported || forceDisabled))
            {
                Log.Info($"HdrSwitch: {monitor.DeviceName} does not support HDR (supported={supported}, forceDisabled={forceDisabled}).");
                return false;
            }
            int error = DisplayConfig.SetHdrState(monitor.DeviceName, enabled);
            if (error != 0)
            {
                Log.Error($"HdrSwitch: could not turn HDR {(enabled ? "on" : "off")} on {monitor.DeviceName}: Win32 error {error}.");
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Puts every HDR-capable display in the mode the selected profile asks for: HDR for
    /// Normal and Media, SDR for SDR. The app runs it at startup and when the profile changes,
    /// never after a display change, so HDR turned on or off by hand (Win+Alt+B, Settings)
    /// holds until the next profile switch, and the mode change Gloam itself causes cannot
    /// feed back into another one.
    /// </summary>
    public sealed class HdrModeSwitcher
    {
        private readonly IHdrSwitch _switch;
        private readonly object _lock = new();

        public HdrModeSwitcher(IHdrSwitch hdrSwitch)
        {
            _switch = hdrSwitch ?? throw new ArgumentNullException(nameof(hdrSwitch));
        }

        /// <summary>Whether a profile wants the displays in HDR.</summary>
        public static bool WantsHdr(DisplayProfileSelection selection) => selection != DisplayProfileSelection.Sdr;

        /// <summary>
        /// Lists the displays and switches those not in the profile's mode; returns how many
        /// changed. Serialized, and both the displays' modes and the profile are read inside
        /// the lock, so two quick profile switches cannot interleave or act on a mode the other
        /// is changing. A display Windows refuses (no HDR support, a driver error) is logged
        /// and left alone.
        /// </summary>
        public int Apply(IMonitorSource monitors, Func<DisplayProfileSelection> selection)
        {
            ArgumentNullException.ThrowIfNull(monitors);
            ArgumentNullException.ThrowIfNull(selection);
            lock (_lock) return Switch(monitors.EnumerateMonitors(), selection());
        }

        /// <summary>Switches these displays, as listed, to <paramref name="selection"/>'s mode.</summary>
        public int Apply(IEnumerable<MonitorInfo> monitors, DisplayProfileSelection selection)
        {
            ArgumentNullException.ThrowIfNull(monitors);
            lock (_lock) return Switch(monitors, selection);
        }

        private int Switch(IEnumerable<MonitorInfo> monitors, DisplayProfileSelection selection)
        {
            bool hdr = WantsHdr(selection);
            int switched = 0;
            foreach (MonitorInfo monitor in monitors)
            {
                if (!monitor.IsHdrCapable || monitor.IsHdrActive == hdr) continue;
                if (_switch.TrySetHdr(monitor, hdr))
                {
                    switched++;
                    Log.Info($"HdrModeSwitcher: HDR {(hdr ? "on" : "off")} on {monitor.DeviceName} for {selection}.");
                }
            }
            return switched;
        }
    }
}
