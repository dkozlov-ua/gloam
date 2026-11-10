using System;
using System.Linq;

namespace Gloam.Core
{
    /// <summary>
    /// A named copy of the app with its own data folder, single-instance names and Start with
    /// Windows value, chosen by the <c>GLOAM_PROFILE</c> environment variable. The UI tests run
    /// each app they start as its own profile, so they never meet the user's running Gloam,
    /// settings or registration. Without the variable this is the normal app and every name is
    /// unchanged.
    /// </summary>
    public static class AppProfile
    {
        public const string EnvironmentVariable = "GLOAM_PROFILE";

        /// <summary>"" for the normal app, otherwise "-" and the profile name.</summary>
        public static string Suffix { get; } = FromName(Environment.GetEnvironmentVariable(EnvironmentVariable));

        internal static string FromName(string? name)
        {
            // Only characters that are safe in a folder, mutex, pipe and registry value name.
            string clean = new((name ?? string.Empty)
                .Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
                .Take(40)
                .ToArray());
            return clean.Length == 0 ? string.Empty : "-" + clean;
        }
    }
}
