using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gloam.Core
{
    /// <summary>
    /// Per-monitor profile data stored in settings.
    /// </summary>
    public class MonitorProfileData
    {
        public GammaMode GammaMode { get; set; } = GammaMode.Gamma22;
        public double Brightness { get; set; } = 100.0;

        /// <summary>
        /// True dims linearly; false (the default) dims with the shadow-preserving power curve.
        /// See <see cref="ToneAdjustments.ApplyDimming"/>.
        /// </summary>
        public bool UseLinearBrightness { get; set; }

        public DisplayAdjustments ToAdjustments() => new()
        {
            Brightness = Brightness,
            UseLinearBrightness = UseLinearBrightness,
        };

        public MonitorProfileData Clone() => new()
        {
            GammaMode = GammaMode,
            Brightness = Brightness,
            UseLinearBrightness = UseLinearBrightness,
        };
    }

    /// <summary>
    /// Enum converter that never throws on unknown values: an unrecognized enum string in
    /// settings.json (a value retired by this build, or added by a newer one) deserializes to
    /// the enum's default value instead of failing the whole settings load. Writing matches
    /// JsonStringEnumConverter (member name for defined values).
    /// </summary>
    internal sealed class TolerantJsonStringEnumConverter : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
            => (JsonConverter)Activator.CreateInstance(
                typeof(TolerantEnumConverter<>).MakeGenericType(typeToConvert))!;

        private sealed class TolerantEnumConverter<T> : JsonConverter<T> where T : struct, Enum
        {
            public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType == JsonTokenType.String)
                {
                    string? text = reader.GetString();
                    if (Enum.TryParse<T>(text, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
                        return parsed;

                    Log.Info($"SettingsManager: Unknown {typeof(T).Name} value '{text}'; using default '{default(T)}'.");
                    return default;
                }

                if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int numeric))
                    return (T)Enum.ToObject(typeof(T), numeric);

                Log.Info($"SettingsManager: Unexpected {reader.TokenType} token for {typeof(T).Name}; using default '{default(T)}'.");
                return default;
            }

            public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
                => writer.WriteStringValue(value.ToString());
        }
    }

    public class SettingsManager
    {
        /// <summary>
        /// Version of the on-disk settings schema this binary understands. Absent/0 in the
        /// file means a v1 (pre-versioning) file. A file with a GREATER version was written
        /// by a newer build: it is loaded best-effort but this manager refuses to Save so an
        /// old binary can never clobber a newer file.
        /// </summary>
        /// <remarks>
        /// Schema 6 dropped calibration, Game Lab's game library, night mode and color
        /// balance. Their fields in an older file are ignored, and an older build refuses to
        /// overwrite a schema 6 file.
        /// </remarks>
        public const int CurrentSchemaVersion = 6;
        internal const long MaxSettingsFileBytes = 16L * 1024 * 1024;

        // Use LocalApplicationData to avoid Resilio Sync corruption
        private static string AppDataPath => AppPaths.DataDir;

        private static string SettingsFilePath => Path.Combine(AppDataPath, "settings.json");

        // SettingsManager is a DI singleton read from the UI thread and the apply service's
        // coalescer threads. All access to _data and every Save() file-write must take this lock:
        //   - it closes the temp-file + File.Move write race under concurrent saves,
        //   - it stops enumeration of MonitorProfiles while a Set* mutator is mid-insert.
        // Reads that hand out mutable objects (GetMonitorProfile, MediaProfile) return COPIES so
        // callers can't mutate shared state out from under us after the lock releases.
        private readonly object _dataLock = new();
        private readonly object _saveLock = new();
        private readonly object _profileWriteLock = new();
        private long _dataVersion;

        private SettingsData _data = new SettingsData();
        public bool LoadedExistingSettingsFile { get; private set; }

        /// <summary>
        /// True when the settings file was written by a newer schema than this binary knows.
        /// The file was loaded best-effort, but every Save() is refused so this (older)
        /// binary can never clobber the newer file.
        /// </summary>
        public bool SettingsFileFromNewerVersion { get; private set; }

        /// <summary>
        /// True when the settings file existed but could not be parsed. The file is left
        /// untouched on disk and in-memory defaults are used; Save() is suppressed until
        /// something actually changes (_dataVersion advances), so the unreadable file is
        /// never silently overwritten with defaults.
        /// </summary>
        public bool LoadFailedPreservingFile { get; private set; }

        // _dataVersion at the moment Load() completed; used to detect "no change yet" for
        // the post-parse-failure save suppression above.
        private long _dataVersionAtLoad;

        public bool StartupDefaultApplied
        {
            get { lock (_dataLock) { return _data.StartupDefaultApplied; } }
        }

        public void MarkStartupDefaultApplied()
        {
            lock (_dataLock)
            {
                if (_data.StartupDefaultApplied) return;
                _data.StartupDefaultApplied = true;
                _dataVersion++;
            }
            Save();
        }

        /// <summary>True once the user has been told about a lingering legacy (pre-Velopack) install.</summary>
        public bool LegacyInstallWarningShown
        {
            get { lock (_dataLock) { return _data.LegacyInstallWarningShown; } }
        }

        public void MarkLegacyInstallWarningShown()
        {
            lock (_dataLock)
            {
                if (_data.LegacyInstallWarningShown) return;
                _data.LegacyInstallWarningShown = true;
                _dataVersion++;
            }
            Save();
        }

        public SettingsManager()
        {
            Load();
        }

        public void Load()
        {
            var loaded = SettingsPersistence.Load(
                SettingsFilePath,
                MaxSettingsFileBytes,
                CurrentSchemaVersion);

            lock (_dataLock)
            {
                _data = loaded.Data;
                LoadedExistingSettingsFile = loaded.FileExists;
                SettingsFileFromNewerVersion = loaded.NewerSchema;
                LoadFailedPreservingFile = loaded.ParseFailed;
                _dataVersion++;
                _dataVersionAtLoad = _dataVersion;
            }

            Log.Info($"SettingsManager: Loaded {_data.MonitorProfiles.Count} monitor profiles.");
            if (loaded.NewerSchema)
            {
                Log.Error(
                    $"SettingsManager: settings.json uses schema {_data.SchemaVersion}, newer than supported " +
                    $"schema {CurrentSchemaVersion}; saves are disabled to preserve the newer file.");
            }
        }

        public bool Save()
        {
            string json;
            int profileCount;
            long snapshotVersion;
            lock (_dataLock)
            {
                if (SettingsFileFromNewerVersion)
                {
                    // A newer build wrote this file; an older binary must never clobber it.
                    Log.Info($"SettingsManager: Save refused; settings.json schema is newer than this build supports (v{CurrentSchemaVersion}).");
                    return false;
                }

                if (LoadFailedPreservingFile && _dataVersion == _dataVersionAtLoad)
                {
                    // The file on disk could not be parsed and nothing has changed in memory
                    // since: a save now would just overwrite the user's file with defaults.
                    Log.Info("SettingsManager: Save suppressed; settings.json failed to parse at load and no setting has changed since.");
                    return false;
                }

                // Stamp the schema version we are about to write.
                _data.SchemaVersion = CurrentSchemaVersion;

                json = SettingsPersistence.Serialize(_data);
                profileCount = _data.MonitorProfiles.Count;
                snapshotVersion = _dataVersion;
            }

            // File I/O happens outside _dataLock so a slow disk write cannot block UI/settings
            // reads. _saveLock serializes writers, and the version check below prevents an older
            // snapshot from overwriting a newer settings state when two Save() calls overlap.
            try
            {
                lock (_saveLock)
                {
                    lock (_dataLock)
                    {
                        if (snapshotVersion != _dataVersion)
                        {
                            json = SettingsPersistence.Serialize(_data);
                            profileCount = _data.MonitorProfiles.Count;
                            snapshotVersion = _dataVersion;
                        }
                    }

                    SettingsPersistence.WriteAtomic(SettingsFilePath, json);
                    Log.Info($"SettingsManager: Saved {profileCount} monitor profiles (v{snapshotVersion}).");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Info($"SettingsManager: Failed to save settings: {ex.Message}");
                return false;
            }
        }

        public GammaMode? GetProfileForMonitor(string monitorDevicePath)
        {
            if (string.IsNullOrEmpty(monitorDevicePath)) return null;

            lock (_dataLock)
            {
                if (_data.MonitorProfiles.TryGetValue(monitorDevicePath, out var profile))
                {
                    return profile.GammaMode;
                }
            }
            return null;
        }

        public void SetProfileForMonitor(string monitorDevicePath, GammaMode mode)
        {
            if (string.IsNullOrEmpty(monitorDevicePath)) return;

            lock (_dataLock)
            {
                if (!_data.MonitorProfiles.TryGetValue(monitorDevicePath, out var profile))
                {
                    profile = new MonitorProfileData();
                    _data.MonitorProfiles[monitorDevicePath] = profile;
                }
                else if (profile.GammaMode == mode)
                {
                    // Skip the serialize-and-write when nothing actually changed.
                    return;
                }
                profile.GammaMode = mode;
                _dataVersion++;
            }
            Save();
            NotifyMonitorProfileChanged(monitorDevicePath);
        }

        public MonitorProfileData? GetMonitorProfile(string monitorDevicePath)
        {
            if (string.IsNullOrEmpty(monitorDevicePath)) return null;
            // Return a clone so a caller can't mutate shared in-memory state after the lock
            // releases (callers read+modify+write these fields).
            lock (_dataLock)
            {
                _data.MonitorProfiles.TryGetValue(monitorDevicePath, out var profile);
                return profile?.Clone();
            }
        }

        public void SetMonitorProfile(string monitorDevicePath, MonitorProfileData profile)
        {
            if (string.IsNullOrEmpty(monitorDevicePath)) return;
            if (profile == null)
            {
                Log.Info($"SettingsManager.SetMonitorProfile: WARNING - Null profile for {monitorDevicePath}, skipping save");
                return;
            }
            Log.Info($"SettingsManager.SetMonitorProfile: Saving {monitorDevicePath} - Brightness={profile.Brightness}, Gamma={profile.GammaMode}");
            lock (_dataLock)
            {
                _data.MonitorProfiles[monitorDevicePath] = SettingsNormalization.MonitorProfile(profile.Clone());
                _dataVersion++;
            }
            Save();
            NotifyMonitorProfileChanged(monitorDevicePath);
        }

        /// <summary>A copy of the Media profile.</summary>
        public MediaProfile MediaProfile
        {
            get { lock (_dataLock) { return _data.MediaProfile.Clone(); } }
        }

        /// <summary>
        /// Replaces the Media profile only when it can be durably written. A failed settings
        /// write restores the previous in-memory value and raises no event, so the UI and the
        /// displays cannot show a state that would disappear on restart.
        /// </summary>
        public bool TrySetMediaProfile(MediaProfile? profile)
        {
            lock (_profileWriteLock)
            {
                MediaProfile incoming = (profile ?? new MediaProfile()).Sanitized();
                MediaProfile previous;
                lock (_dataLock)
                {
                    if (_data.MediaProfile.SemanticallyEquals(incoming)) return true;
                    previous = _data.MediaProfile.Clone();
                    _data.MediaProfile = incoming;
                    _dataVersion++;
                }

                if (!Save())
                {
                    lock (_dataLock)
                    {
                        if (_data.MediaProfile.SemanticallyEquals(incoming))
                        {
                            _data.MediaProfile = previous;
                            _dataVersion++;
                        }
                    }
                    return false;
                }
            }

            NotifyDisplayProfileChanged();
            return true;
        }

        /// <summary>
        /// The selected display profile. It is sticky: it survives restarts until another
        /// profile is chosen, from the window or with <c>--profile-set</c>.
        /// </summary>
        public DisplayProfileSelection ProfileSelection
        {
            get { lock (_dataLock) { return _data.ProfileSelection; } }
        }

        /// <summary>Changes the profile selection only when it can be durably written.</summary>
        public bool TrySetProfileSelection(DisplayProfileSelection selection)
        {
            if (!Enum.IsDefined(selection)) return false;
            lock (_profileWriteLock)
            {
                DisplayProfileSelection previous;
                lock (_dataLock)
                {
                    if (_data.ProfileSelection == selection) return true;
                    previous = _data.ProfileSelection;
                    _data.ProfileSelection = selection;
                    _dataVersion++;
                }

                if (!Save())
                {
                    lock (_dataLock)
                    {
                        if (_data.ProfileSelection == selection)
                        {
                            _data.ProfileSelection = previous;
                            _dataVersion++;
                        }
                    }
                    return false;
                }
            }

            Log.Info($"SettingsManager: profile selection changed to {selection}.");
            NotifyDisplayProfileChanged();
            return true;
        }

        /// <summary>Raised after the profile selection or the Media profile changed.</summary>
        public event Action? DisplayProfileChanged;

        /// <summary>Raised after a monitor's own settings changed.</summary>
        public event Action<string>? MonitorProfileChanged;

        private void NotifyDisplayProfileChanged()
        {
            try { DisplayProfileChanged?.Invoke(); }
            catch (Exception ex) { Log.Info($"SettingsManager: display-profile subscriber failed: {ex.Message}"); }
        }

        private void NotifyMonitorProfileChanged(string monitorDevicePath)
        {
            try { MonitorProfileChanged?.Invoke(monitorDevicePath); }
            catch (Exception ex) { Log.Info($"SettingsManager: monitor-profile subscriber failed: {ex.Message}"); }
        }

        internal class SettingsData
        {
            /// <summary>
            /// On-disk schema version (see <see cref="CurrentSchemaVersion"/>). Absent/0 in
            /// older files means v1. Stamped to the current version on every save.
            /// </summary>
            public int SchemaVersion { get; set; }

            public Dictionary<string, MonitorProfileData> MonitorProfiles { get; set; } = new Dictionary<string, MonitorProfileData>();
            public MediaProfile MediaProfile { get; set; } = new MediaProfile();
            /// <summary>The sticky Normal/Media/SDR choice.</summary>
            public DisplayProfileSelection ProfileSelection { get; set; }
            public bool StartupDefaultApplied { get; set; }
            public bool LegacyInstallWarningShown { get; set; }
        }
    }
}
