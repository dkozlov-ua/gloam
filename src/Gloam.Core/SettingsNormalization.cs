using System;
using System.Collections.Generic;
using System.Linq;

namespace Gloam.Core
{
    /// <summary>
    /// Canonicalizes untrusted or hand-edited settings into the runtime domain. This has no
    /// file-system side effects, so loading, imports, and tests all use one policy.
    /// </summary>
    internal static class SettingsNormalization
    {
        internal static MonitorProfileData MonitorProfile(MonitorProfileData profile)
        {
            if (!Enum.IsDefined(profile.GammaMode))
                profile.GammaMode = GammaMode.Gamma22;
            profile.Brightness = ClampFinite(profile.Brightness, 10.0, 100.0, 100.0);
            return profile;
        }

        internal static void Validate(SettingsManager.SettingsData data)
        {
            ArgumentNullException.ThrowIfNull(data);
            data.MonitorProfiles ??= new Dictionary<string, MonitorProfileData>();
            data.MediaProfile = (data.MediaProfile ?? new MediaProfile()).Sanitized();
            if (!Enum.IsDefined(data.ProfileSelection))
                data.ProfileSelection = DisplayProfileSelection.Normal;

            foreach (var key in data.MonitorProfiles.Keys.ToList())
            {
                var profile = data.MonitorProfiles[key];
                if (profile == null)
                {
                    data.MonitorProfiles.Remove(key);
                    continue;
                }
                MonitorProfile(profile);
            }
        }

        private static double ClampFinite(double value, double min, double max, double fallback) =>
            double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
    }
}
