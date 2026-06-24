namespace FH6RB.Assets;

public static class Str
{
    public const string TitleMain = "Forza Horizon Radio Extender";
    public const string TitleSettings = "Settings";
    public const string TitleMarkers = "Markers";
    public const string TitleEditing = "Editing";
    public const string TitleNotice = "Notice";
    
    public const string SecLocalization = "LOCALIZATION";
    public const string SecStation = "STATION";
    public const string SecBank = "BANK";
    public const string SecGamePath = "GAME PATH";
    public const string SecLoudness = "LOUDNESS";
    public const string SecEncoding = "ENCODING";
    public const string SecBackups = "RESTORE ORIGINAL BANKS";
    
    public const string LblSoundName = "SOUND NAME";
    public const string LblTitle = "TITLE";
    public const string LblArtist = "ARTIST";
    public const string LblVolumeCompensation = "VOLUME COMPENSATION";
    public const string LblTargetLoudness = "Target loudness";
    public const string LblEncodingThreads = "Encoding threads";
    
    public const string BtnCancel = "Cancel";
    public const string BtnSave = "Save";
    public const string BtnOk = "OK";
    public const string BtnClose = "Close";
    public const string BtnBrowse = "Browse";
    public const string BtnLoad = "Load";
    public const string BtnDelete = "Delete";
    public const string BtnRestore = "Restore";
    
    public const string BadgeNew = "NEW";
    public const string BadgeReplaced = "REP";
    public const string BtnAddLabel = "ADD";
    public const string BtnBuildLabel = "BUILD";
    
    public const string TipSettings = "Settings";
    public const string TipPlayPause = "Play / Pause";
    public const string TipPlay = "Play";
    public const string TipPause = "Pause";
    public const string TipStop = "Stop";
    public const string TipPlayLoop = "Play current region as loop";
    public const string TipPlayMarkerLoop = "Play current marker as loop";
    public const string TipDelete = "Delete track";
    public const string TipReplaceAudio = "Replace track";
    public const string TipMarkers = "Markers";
    public const string TipEdit = "Edit";
    public const string TipToggle = "Toggle";
    public const string TipDisable = "Disable";
    public const string TipEnable = "Enable";
    public const string TipAddTrack = "Add track";
    public const string TipBuildBank = "Build current bank";
    public const string TipSetPlayhead = "Set this marker to start";
    public const string TipRevertToSaved = "Restore the last saved value";
    public const string TipBackupRestore = "Reverts files to their pre-build state.";
    
    public const string Loading = "Loading…";
    public const string WatermarkOff = "off";
    public const string TimeZero = "0:00 / 0:00";
    public const string NoBackups = "No backups to restore.";
    public const string HintLoudness = "Added tracks are normalized to this level. Originals sit around -23 LUFS.";
    public const string HintEncoding = "Parallel jobs while building. Higher is faster but uses more CPU, RAM and disk.";
    public const string HintRestore = "Reverts banks and RadioInfo to their pre-build state.";
    
    public const string PickAddAudio = "Select audio files to add";
    public const string PickReplaceAudio = "Select replacement audio";
    public const string PickGameFolder = "Select Forza Horizon folder";
    
    public const string StatusLoadingFmt = "Loading {0}…";
    public const string StatusReadingOneFmt = "Reading {0}…";
    public const string StatusReadingManyFmt = "Reading metadata for {0} files…";
    public const string StatusAddedOneFmt = "Added: {0}";
    public const string StatusAddedManyFmt = "Added {0} tracks";
    public const string StatusDecodingFmt = "Decoding {0}…";
    public const string StatusPlayingFmt = "Playing {0}";
    public const string StatusPlaybackErrorFmt = "Playback error: {0}";
    public const string StatusDeletedFmt = "Deleted: {0}";
    public const string StatusReplacingFmt = "Replacing {0}…";
    public const string StatusReplaceStagedFmt = "Replacement staged for {0} — build to apply";
    public const string StatusDecodeFailed = "Couldn't decode this track for marker editing.";
    public const string StatusMarkersUpdatedFmt = "Markers updated: {0}";
    public const string StatusSavedFmt = "Saved: {0}";
    public const string StatusBuildMissingFmt = "Build: missing {0} (put them in app folder)";
    public const string StatusBuildBankNotFoundFmt = "Build: bank file not found ({0})";
    public const string StatusBuildNoRadioInfo = "Build: RadioInfo not available";
    public const string StatusProcessingFmt = "Processing {0}…";
    public const string StatusBuiltFmt = "Built {0}: {1} tracks";
    public const string StatusBuiltCustomFmt = ", +{0} custom";
    public const string StatusBuiltReplacedFmt = ", {0} replaced";
    public const string StatusBuiltXmlFmt = ", {0} xml";
    public const string StatusBuiltCleanedFmt = ", cleaned {0} dead";
    public const string StatusBuildTooLarge = "Build stopped: bank exceeds the 2 GB limit";
    public const string StatusBuildErrorFmt = "Build error: {0}";
    
    public const string FoundFmt = "Found {0}";
    public const string LocalizationsFmt = "Localizations: {0}";
    public const string RadioBanksFmt = "Radio banks: {0}";
    public const string ErrNoRadioInfo = "No RadioInfo_*.xml found. Check the game folder, or close the program.";
    public const string EditRestoredFmt = "Restored {0} file(s)";
    public const string EditRestoredFailedFmt = "Restored {0}, {1} failed";

    public const string DlgResetMarkersTitle = "Reset custom markers";
    public const string DlgResetMarkersBody = "Reset markers to the current defaults for all custom and replaced tracks? Any manual marker edits on those tracks will be lost.";
    public const string DlgResetMarkersOk = "Reset";
    public const string DlgResetMarkersCancel = "Cancel";
    public const string StatusMarkersResetFmt = "Reset markers for {0} track(s)";
    public const string DlgBankTooLargeTitle = "Bank too large";

    public const string DlgUnsavedTitle = "Unsaved changes";
    public const string DlgUnsavedBody =
        "You have changes that haven't been built into the bank yet " +
        "(added, edited, replaced or toggled tracks). Build to keep them. Quit without building?";
    public const string DlgUnsavedOk = "Quit anyway";
    public const string DlgUnsavedCancel = "Keep editing";

    public const string DlgBuildProgressTitle = "Build in progress";
    public const string DlgBuildProgressBody =
        "A build is still running. Quitting now may leave the bank half-written " +
        "(a .bak backup exists to restore from). Quit anyway?";
    public const string DlgBuildProgressOk = "Quit";
    public const string DlgBuildProgressCancel = "Keep building";


    
    public const string GrpCore = "Core";
    public const string GrpDjDrops = "DJ / Drops";
    public const string GrpTrackLoops = "Track loops";
    public const string GrpExtraLoops = "Extra loops";
    public const string GrpSections = "Sections";
    public const string GrpOther = "Other";
    public const string StatusFilesInUse = "Files are in use — is the game running?";
    public const string DlgFilesInUseTitle = "Files in use";
    public const string DlgFilesInUseBody = "Some game files are in use. Forza Horizon is most likely running. Close the game before building, otherwise saving and building will fail.";
    public const string DlgGameRunningTitle = "Game is running";
    public const string MkTrackStart = "Start of the track.";
    public const string MkEnd = "End of the track.";
    public const string MkDjStart = "Free-roam: where the DJ comes in at the end and the music fades out. Best at the very end on the fade, 3-5 sec before the end.";
    public const string MkDjDrop = "Free-roam: where the DJ stops talking over the intro and the music comes to the front. Best after the intro or on an early drop, no later than 30 sec.";
    public const string MkDjSegment = "Free-roam: a DJ line over the middle of the track, layered on top of the music. Best on a calmer mid-track section.";
    public const string MkTrackDrop = "Events: where the track starts when an event begins, right after the countdown. Best on a drop, no later than 30% in.";
    public const string MkPostDrop = "Events: the point reached after crossing the finish line. Best on a final drop. Must not exceed PostRaceLoopStart (ideally equal or nearly equal).";
    public const string MkTrackLoopStart = "Events: where the track loops back to during an event. Pick a section that loops cleanly with TrackLoopEnd.";
    public const string MkTrackLoopEnd = "Events: where the track stops during an event; after it, playback loops between TrackLoopStart and TrackLoopEnd.";
    public const string MkPostRaceLoopStart = "Events: start of the section that loops while results are tallied. Best on an outro.";
    public const string MkPostRaceLoopEnd = "Events: end of the section that loops while results are tallied.";
    
    public const string LangAll = "All languages";
    
    public const string StatusLoadedFmt = "Loaded {0}";
    public const string StatusBankErrorFmt = "{0}: {1}";
    public const string StatusEnabledFmt = "Enabled: {0}";
    public const string StatusDisabledFmt = "Disabled: {0}";
    
    public const string FilterAudioFiles = "Audio files";

    public const string TipMarkerDefaults = "Edit default custom-track marker values";
    public const string TipWaveHotkeys = "Space: play / stop\nP: pause / resume\nLeft / Right: move the playback head\nClick or drag: set the start position\nClick a label: set the start to that marker\nCtrl + wheel: zoom at the cursor\nMiddle-drag: pan the waveform\nCtrl + Left-click: set play region start\nCtrl + Right-click: set play region end\nCtrl + click a label: snap a region edge to it\nShift + hover a label: focus that marker\nShift + drag: move the focused marker\nShift + wheel: raise / lower its label row";
    public const string MenuCut = "Cut";
    public const string MenuCopy = "Copy";
    public const string MenuPaste = "Paste";
    public const string MenuRevert = "Revert";
    public const string MenuReset = "Reset";
    public const string TipResetMarkers = "Replace markers to custom-track defaults";
    public const string TipResetAllMarkers = "Reset markers for all custom and replaced tracks";
    public const string TitleMarkerDefaults = "Custom-track marker values";
    public const string HintMarkerDefaults = "Default positions for custom-track markers.";
    public const string BtnRestoreDefaults = "Restore defaults";

    public const string TipSaveBackup = "Save station backup";
    public const string TipBackups = "Station backups";
    public const string TipRestoreStation = "Restore station to original";
    public const string TitleBackups = "Station backups";
    public const string BackupNameTitle = "Station backup";
    public const string BackupNameWatermark = "Enter a name for this backup";
    public const string BackupEmpty = "No station backups yet.";
    public const string BackupInfoFmt = "{0} · R{1} {2}";
    public const string BackupMetaFmt = "{0} tracks · {1} custom · {2}";
    public const string BackupRestoreTitle = "Restore backup";
    public const string BackupRestoreBodyFmt = "Restore this backup? The current station data will be overwritten.\n\nName: {0}\nGame: {1}\nStation: R{2} {3}\nCreated: {4}\nTracks: {5} (custom {6})";
    public const string BackupDeleteTitle = "Delete backup";
    public const string BackupDeleteBodyFmt = "Delete backup \"{0}\"?";
    public const string StatusBackupNoStation = "Select a station first.";
    public const string BtnResetSettings = "Reset";
    public const string TipResetSettings = "Reset all settings to defaults (keeps the game folder)";
    public const string DlgResetSettingsTitle = "Reset settings";
    public const string DlgResetSettingsBody = "Reset all settings to their defaults? This also restores the default marker positions. Your game folder is kept, and tracks you have added are not affected.";
    public const string DlgResetSettingsOk = "Reset";
    public const string DlgResetSettingsCancel = "Cancel";
    public const string DlgBackupUnbuiltTitle = "Unbuilt changes";
    public const string DlgBackupUnbuiltBody = "You have added or replaced tracks that haven’t been built into the game yet. A backup only captures the current game files, so these pending tracks won’t be included — they will show as 0 custom. Build first if you want them in the backup.\n\nCreate the backup anyway?";
    public const string DlgBackupUnbuiltOk = "Back up anyway";
    public const string DlgBackupUnbuiltCancel = "Cancel";
    public const string StatusBackupSavedFmt = "Backup saved: {0}";
    public const string StatusBackupFailedFmt = "Backup failed: {0}";
    public const string StatusBackupSaving = "Saving backup…";
    public const string StatusBackupRestoring = "Restoring backup…";
    public const string StatusRestoreNoOriginal = "No original backup found. Build at least once first.";
    public const string StatusRestoreStationFmt = "Restored {0}: {1} banks, {2} languages.";
    public const string DlgRestoreStationTitle = "Restore station to original";
    public const string DlgRestoreStationBody = "Replace \"{0}\" with the original game banks and metadata? Any custom tracks and edits on this station will be lost. Other stations are not affected.";
    public const string DlgRestoreStationOk = "Restore";
    public const string DlgRestoreStationCancel = "Cancel";
    public const string StatusBackupRestoredFmt = "Backup restored: {0}";
}
