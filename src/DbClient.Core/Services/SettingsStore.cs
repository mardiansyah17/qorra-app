using System.Text.Json;

namespace DbClient.Core.Services;

public enum AppTheme { System, Light, Dark }

public sealed class AppSettings
{
    public static readonly double[] EditorFontSizes = [11, 12, 13, 14, 15, 16, 18, 20];
    public static readonly int[] PageSizes = [100, 200, 500, 1000, 2000, 5000];
    public static readonly double[] GridFontSizes = [11, 12, 12.5, 13, 14, 15];
    public static readonly int[] TabSizes = [2, 4, 8];
    public static readonly int[] QueryTimeouts = [0, 30, 60, 300, 1800];
    public static readonly string[] FontWeights = ["Light", "Normal", "Medium", "SemiBold"];
    public static readonly string[] RowDensities = ["Compact", "Default", "Comfortable"];
    public static readonly string[] LaunchChoices = ["Connections", "LastSession", "LastConnection"];
    public static readonly string[] AutosaveChoices = ["Never", "1s", "5s", "FocusLost"];
    public static readonly string[] RunTargets = ["Statement", "Script"];
    public static readonly string[] IsolationLevels = ["Default", "ReadCommitted", "RepeatableRead", "Serializable"];
    public static readonly int[] HistoryDaysChoices = [7, 30, 90, 0];
    public static readonly string[] DangerScopes = ["Never", "ProductionAndStaging", "ProductionOnly", "All"];
    public static readonly string[] DateFormats = ["yyyy-MM-dd HH:mm:ss", "dd/MM/yyyy HH:mm:ss", "MM/dd/yyyy hh:mm:ss tt", "System"];
    public static readonly string[] NumberFormats = ["Plain", "Comma", "Dot", "System"];
    public static readonly string[] BinaryFormats = ["Hex", "Base64", "Text"];
    public static readonly int[] TruncateChoices = [100, 300, 1000, 0];
    public static readonly string[] CopyFormats = ["Tsv", "Csv", "Json", "Insert", "Markdown"];
    public static readonly int[] ConnectTimeouts = [5, 10, 30, 60];
    public static readonly int[] KeepAliveChoices = [0, 30, 60, 300];
    public static readonly int[] IdleChoices = [0, 15, 60, 240];
    public static readonly string[] SslDefaults = ["Prefer", "Require", "VerifyCA", "VerifyFull"];
    public static readonly string[] ExportFormats = ["Csv", "Json", "Insert", "Markdown"];
    public static readonly string[] Delimiters = [",", ";", "\t", "|"];
    public static readonly string[] Encodings = ["UTF-8", "UTF-8 BOM", "UTF-16"];
    public static readonly string[] LineEndingChoices = ["CRLF", "LF"];
    public static readonly int[] LockChoices = [0, 5, 15, 60];
    public static readonly string[] KeywordCases = ["Upper", "Lower", "AsTyped"];
    public static readonly int[] SuggestDelays = [0, 100, 250, 500];
    public static readonly string[] CommaPositions = ["Trailing", "Leading"];

    public AppTheme Theme { get; set; }
    public double EditorFontSize { get; set; } = 13;
    public string EditorTheme { get; set; } = "Default";
    public int PageSize { get; set; } = 1000;

    public string LaunchOpen { get; set; } = "Connections";
    public bool RestoreTabs { get; set; } = true;
    public bool NewWindowPerConnection { get; set; }
    public bool ConfirmQuit { get; set; } = true;
    public string Autosave { get; set; } = "1s";
    public List<Guid> LastSession { get; set; } = [];
    public bool CheckForUpdates { get; set; } = true;

    public string EditorFontFamily { get; set; } = "Cascadia Code";
    public string EditorFontWeight { get; set; } = "Normal";
    public bool LineNumbers { get; set; } = true;
    public bool HighlightStatement { get; set; } = true;
    public bool WordWrap { get; set; }
    public bool ShowWhitespace { get; set; }
    public bool MatchBrackets { get; set; } = true;
    public int TabSize { get; set; } = 4;
    public bool InsertSpaces { get; set; } = true;
    public bool AutoClosePairs { get; set; } = true;
    public string KeywordCase { get; set; } = "Upper";
    public bool SuggestWhileTyping { get; set; } = true;
    public int SuggestDelay { get; set; } = 100;
    public bool FuzzyMatching { get; set; } = true;
    public bool JoinSuggestions { get; set; } = true;
    public bool AliasColumns { get; set; } = true;
    public bool CheckOnServer { get; set; } = true;
    public bool WarnUnknownTables { get; set; } = true;
    public string CommaPosition { get; set; } = "Trailing";
    public bool AlignClauses { get; set; } = true;
    public bool FormatOnSave { get; set; }

    public string GridFontFamily { get; set; } = "Cascadia Code";
    public double GridFontSize { get; set; } = 12.5;
    public string RowDensity { get; set; } = "Default";
    public bool AlternatingRows { get; set; }
    public bool GridLines { get; set; } = true;
    public string NullText { get; set; } = "NULL";
    public bool EstimatedRowCount { get; set; } = true;
    public bool OrderByKey { get; set; }
    public string DateFormat { get; set; } = "yyyy-MM-dd HH:mm:ss";
    public string NumberFormat { get; set; } = "Plain";
    public string BinaryFormat { get; set; } = "Hex";
    public int TruncateAt { get; set; } = 300;
    public bool RowNumbers { get; set; } = true;
    public bool FitColumns { get; set; } = true;
    public bool ReviewBeforeCommit { get; set; }
    public bool ConfirmRowDelete { get; set; }
    public bool NewRowsAtTop { get; set; } = true;
    public string CopyFormat { get; set; } = "Tsv";

    public int QueryTimeout { get; set; } = 30;
    public string RunTarget { get; set; } = "Statement";
    public bool StopOnError { get; set; } = true;
    public bool KeepResults { get; set; }
    public bool ManualCommit { get; set; }
    public string IsolationLevel { get; set; } = "Default";
    public bool WarnOpenTransaction { get; set; } = true;
    public bool SaveHistory { get; set; } = true;
    public int HistoryDays { get; set; } = 90;

    public int ConnectTimeout { get; set; } = 10;
    public int KeepAlive { get; set; } = 30;
    public bool Reconnect { get; set; } = true;
    public int IdleDisconnect { get; set; }
    public string DefaultSslMode { get; set; } = "Require";
    public string? DefaultTag { get; set; }
    public bool ProductionReadOnly { get; set; } = true;
    public bool UseSshAgent { get; set; }
    public string DefaultSshKey { get; set; } = "";
    public bool ShareTunnels { get; set; }

    public string ConfirmDangerOn { get; set; } = "ProductionAndStaging";
    public bool BlockUnfilteredWrites { get; set; } = true;
    public bool TypeNameToDrop { get; set; } = true;
    public bool TintWindow { get; set; } = true;
    public bool RequireUnlock { get; set; }
    public int LockAfter { get; set; }
    public bool AskBeforeReveal { get; set; } = true;
    public bool ClearCopiedPasswords { get; set; } = true;

    public string ExportFormat { get; set; } = "Csv";
    public string CsvDelimiter { get; set; } = ",";
    public bool ExportHeader { get; set; } = true;
    public string ExportEncoding { get; set; } = "UTF-8";
    public string LineEndings { get; set; } = "CRLF";
    public string ExportFolder { get; set; } = "";
    public bool ExportPasswords { get; set; }

    public Dictionary<string, string> Shortcuts { get; set; } = [];
    public string KeymapPreset { get; set; } = "Qorra";

    public void CopyFrom(AppSettings source)
    {
        foreach (var p in CoreJsonContext.Default.AppSettings.Properties) p.Set!(this, p.Get!(source));
    }

    public void Normalize()
    {
        var d = new AppSettings();
        static T Pick<T>(T value, T[] allowed, T fallback) => allowed.Contains(value) ? value : fallback;
        if (!Enum.IsDefined(Theme)) Theme = d.Theme;
        EditorFontSize = Pick(EditorFontSize, EditorFontSizes, d.EditorFontSize);
        PageSize = Pick(PageSize, PageSizes, d.PageSize);
        GridFontSize = Pick(GridFontSize, GridFontSizes, d.GridFontSize);
        TabSize = Pick(TabSize, TabSizes, d.TabSize);
        QueryTimeout = Pick(QueryTimeout, QueryTimeouts, d.QueryTimeout);
        EditorFontWeight = Pick(EditorFontWeight, FontWeights, d.EditorFontWeight);
        RowDensity = Pick(RowDensity, RowDensities, d.RowDensity);
        LaunchOpen = Pick(LaunchOpen, LaunchChoices, d.LaunchOpen);
        Autosave = Pick(Autosave, AutosaveChoices, d.Autosave);
        RunTarget = Pick(RunTarget, RunTargets, d.RunTarget);
        IsolationLevel = Pick(IsolationLevel, IsolationLevels, d.IsolationLevel);
        HistoryDays = Pick(HistoryDays, HistoryDaysChoices, d.HistoryDays);
        ConfirmDangerOn = Pick(ConfirmDangerOn, DangerScopes, d.ConfirmDangerOn);
        DateFormat = Pick(DateFormat, DateFormats, d.DateFormat);
        NumberFormat = Pick(NumberFormat, NumberFormats, d.NumberFormat);
        BinaryFormat = Pick(BinaryFormat, BinaryFormats, d.BinaryFormat);
        TruncateAt = Pick(TruncateAt, TruncateChoices, d.TruncateAt);
        CopyFormat = Pick(CopyFormat, CopyFormats, d.CopyFormat);
        ConnectTimeout = Pick(ConnectTimeout, ConnectTimeouts, d.ConnectTimeout);
        KeepAlive = Pick(KeepAlive, KeepAliveChoices, d.KeepAlive);
        IdleDisconnect = Pick(IdleDisconnect, IdleChoices, d.IdleDisconnect);
        DefaultSslMode = Pick(DefaultSslMode, SslDefaults, d.DefaultSslMode);
        ExportFormat = Pick(ExportFormat, ExportFormats, d.ExportFormat);
        CsvDelimiter = Pick(CsvDelimiter, Delimiters, d.CsvDelimiter);
        ExportEncoding = Pick(ExportEncoding, Encodings, d.ExportEncoding);
        LineEndings = Pick(LineEndings, LineEndingChoices, d.LineEndings);
        LockAfter = Pick(LockAfter, LockChoices, d.LockAfter);
        KeywordCase = Pick(KeywordCase, KeywordCases, d.KeywordCase);
        SuggestDelay = Pick(SuggestDelay, SuggestDelays, d.SuggestDelay);
        CommaPosition = Pick(CommaPosition, CommaPositions, d.CommaPosition);
        if (string.IsNullOrWhiteSpace(EditorFontFamily)) EditorFontFamily = d.EditorFontFamily;
        if (string.IsNullOrWhiteSpace(GridFontFamily)) GridFontFamily = d.GridFontFamily;
        NullText ??= d.NullText;
        DefaultSshKey ??= "";
        ExportFolder ??= "";
        LastSession ??= [];
        Shortcuts ??= [];
        if (string.IsNullOrWhiteSpace(KeymapPreset)) KeymapPreset = d.KeymapPreset;
    }
}

public sealed class SettingsStore(string filePath)
{
    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "settings.json");

    public string FilePath { get; } = filePath;

    public AppSettings Load()
    {
        var s = AppPaths.Locked(() =>
        {
            if (!File.Exists(FilePath)) return null;
            try { return JsonSerializer.Deserialize(File.ReadAllText(FilePath), CoreJsonContext.Default.AppSettings); }
            catch (JsonException) { return null; }
        }) ?? new AppSettings();
        s.Normalize();
        return s;
    }

    public void Save(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, CoreJsonContext.Default.AppSettings);
        AppPaths.Locked(() => AppPaths.WriteAtomic(FilePath, json));
    }
}
