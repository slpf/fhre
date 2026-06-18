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
    public const string BtnAddLabel = "ADD";
    public const string BtnBuildLabel = "BUILD";
    
    public const string TipSettings = "Settings";
    public const string TipPlayPause = "Play / Pause";
    public const string TipPlay = "Play";
    public const string TipPause = "Pause";
    public const string TipDelete = "Delete track";
    public const string TipReplaceAudio = "Replace track";
    public const string TipMarkers = "Markers";
    public const string TipEdit = "Edit";
    public const string TipToggle = "Toggle";
    public const string TipDisable = "Disable";
    public const string TipEnable = "Enable";
    public const string TipAddTrack = "Add track";
    public const string TipBuildBank = "Build current bank";
    public const string TipSetPlayhead = "Set to current playback time";
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
    
    public const string LangAll = "All languages";
    
    public const string StatusLoadedFmt = "Loaded {0}";
    public const string StatusBankErrorFmt = "{0}: {1}";
    public const string StatusEnabledFmt = "Enabled: {0}";
    public const string StatusDisabledFmt = "Disabled: {0}";
    
    public const string FilterAudioFiles = "Audio files";

    public const string TipMarkerDefaults = "Edit default custom-track marker values";
    public const string TipWaveHotkeys = "SPACE - play/pause\nClick, drag — move playhead\nArrows on keyboard - also move playhead\nDrag a marker — move this marker\nSHIFT + hover a label — focus that marker\nSHIFT + wheel — raise/lower its label row\nSHIFT + drag — move the focused marker\nCTRL + LMB click — set play region start\nCTRL + RMB click — set play region end\nCTRL + LMB/RMB click a label — region edge to it";
    public const string TipResetMarkers = "Replace markers to custom-track defaults";
    public const string TitleMarkerDefaults = "Custom-track marker values";
    public const string HintMarkerDefaults = "Default positions for custom-track markers.";
    public const string BtnRestoreDefaults = "Restore defaults";

    public const string TipSaveBackup = "Save station backup";
    public const string TipBackups = "Backups";
    public const string TitleBackups = "Backups";
    public const string BackupNameTitle = "Backup";
    public const string BackupNameWatermark = "Enter a name for this backup";
    public const string BackupEmpty = "No backups yet.";
    public const string BackupInfoFmt = "{0} · R{1} {2}";
    public const string BackupMetaFmt = "{0} tracks · {1} custom · {2}";
    public const string BackupRestoreTitle = "Restore backup";
    public const string BackupRestoreBodyFmt = "Restore this backup? The current station data will be overwritten.\n\nName: {0}\nGame: {1}\nStation: R{2} {3}\nCreated: {4}\nTracks: {5} (custom {6})";
    public const string BackupDeleteTitle = "Delete backup";
    public const string BackupDeleteBodyFmt = "Delete backup \"{0}\"?";
    public const string StatusBackupNoStation = "Select a station first.";
    public const string StatusBackupSavedFmt = "Backup saved: {0}";
    public const string StatusBackupFailedFmt = "Backup failed: {0}";
    public const string StatusBackupSaving = "Saving backup…";
    public const string StatusBackupRestoring = "Restoring backup…";
    public const string StatusBackupRestoredFmt = "Backup restored: {0}";
}
