using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using DeathBuffTracker.Models;

namespace DeathBuffTracker.UI;

public sealed class MainWindow : Window {
    private static readonly string[] TableHeaders = {
        "时间",
        "玩家",
        "副本/区域",
        "事件",
        "状态",
        "层数",
        "持续(s)",
        "来源",
        "技能",
        "伤害类型",
    };

    private static readonly string[] EventTypeLabels = {
        "全部",
        "死亡",
        "状态获得",
    };

    private readonly DeathBuffTrackerPlugin plugin;

    private string dutyFilter = string.Empty;
    private string playerFilter = string.Empty;
    private string statusFilter = string.Empty;
    private string sourceFilter = string.Empty;
    private string actionFilter = string.Empty;
    private string damageTypeFilter = string.Empty;
    private string fromFilter = string.Empty;
    private string toFilter = string.Empty;
    private int eventTypeFilterIndex;
    private int pageIndex;
    private int pageSize = 50;

    private bool dateInitialized;
    private int dateYear;
    private int dateMonth;
    private int dateDay;
    private int recentDays = 7;
    private string dateError = string.Empty;

    private string exportMessage = string.Empty;

    public MainWindow(DeathBuffTrackerPlugin plugin) : base("死亡与状态追踪") {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints {
            MinimumSize = new Vector2(500, 300),
        };
    }

    public override void Draw() {
        EnsureDateInitialized();

        var records = plugin.EventStore.GetSnapshot();
        var dutyOptions = BuildDutyOptions(records);
        var playerOptions = BuildPlayerOptions(records);
        var statusOptions = BuildStatusOptions(records);
        var sourceOptions = BuildSourceOptions(records);
        var actionOptions = BuildActionOptions(records);
        var damageTypeOptions = BuildDamageTypeOptions(records);

        ImGui.Text("筛选");
        DrawComboFilter("副本/区域", dutyOptions, ref dutyFilter);
        DrawComboFilter("玩家", playerOptions, ref playerFilter);
        ImGui.Combo("事件类型", ref eventTypeFilterIndex, EventTypeLabels, EventTypeLabels.Length);
        ImGui.SameLine();
        if (ImGui.Button("清空筛选")) {
            ClearAllFilters();
        }

        if (ImGui.CollapsingHeader("高级筛选")) {
            DrawComboFilter("状态", statusOptions, ref statusFilter);
            DrawComboFilter("来源", sourceOptions, ref sourceFilter);
            DrawComboFilter("技能", actionOptions, ref actionFilter);
            DrawComboFilter("伤害类型", damageTypeOptions, ref damageTypeFilter);
        }

        ImGui.Separator();
        DrawDateShortcuts();

        ImGui.InputText("开始时间(本地)", ref fromFilter, 32);
        ImGui.InputText("结束时间(本地)", ref toFilter, 32);

        var parseError = string.Empty;
        var filter = BuildFilter(ref parseError);
        if (!string.IsNullOrWhiteSpace(parseError)) {
            ImGui.Text($"时间解析失败: {parseError}");
        }

        var filtered = records
            .Where(record => MatchesFilter(record, filter))
            .OrderByDescending(record => record.TimestampUtc)
            .ToList();

        var deathCount = filtered.Count(record => record.EventType == TrackerEventType.Death);
        var statusCount = filtered.Count(record => record.EventType == TrackerEventType.StatusGain);

        ImGui.Separator();
        ImGui.Text($"总事件:{records.Count}");
        ImGui.Text($"筛选后:{filtered.Count}");
        ImGui.Text($"死亡:{deathCount}");
        ImGui.Text($"状态获得:{statusCount}");

        ImGui.Separator();
        DrawExportControls(filtered);

        ImGui.Separator();
        DrawPaginationControls(filtered.Count);

        ImGui.Separator();
        DrawEventTable(filtered);
    }

    private void DrawExportControls(IReadOnlyList<DeathBuffEventRecord> filtered) {
        ImGui.Text("导出筛选结果");
        if (ImGui.Button("导出 CSV")) {
            ExportRecords(filtered, ExportFormat.Csv);
        }

        ImGui.SameLine();
        if (ImGui.Button("导出 Excel")) {
            ExportRecords(filtered, ExportFormat.Excel);
        }

        if (!string.IsNullOrWhiteSpace(exportMessage)) {
            ImGui.Text(exportMessage);
        }
    }

    private void ExportRecords(IReadOnlyList<DeathBuffEventRecord> records, ExportFormat format) {
        exportMessage = string.Empty;
        try {
            var directory = Service.PluginInterface.GetPluginConfigDirectory();
            Directory.CreateDirectory(directory);
            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var extension = format == ExportFormat.Csv ? "csv" : "xlsx";
            var path = Path.Combine(directory, $"death-buff-tracker-{timestamp}.{extension}");

            if (format == ExportFormat.Csv) {
                ExportCsv(path, records);
            } else {
                ExportXlsx(path, records);
            }

            exportMessage = $"已导出: {path}";
        } catch (Exception ex) {
            exportMessage = $"导出失败: {ex.Message}";
        }
    }

    private static void ExportCsv(string path, IReadOnlyList<DeathBuffEventRecord> records) {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(",", TableHeaders.Select(EscapeCsv)));

        foreach (var record in records) {
            var values = BuildRowValues(record);
            builder.AppendLine(string.Join(",", values.Select(EscapeCsv)));
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
    }

    private static void ExportXlsx(string path, IReadOnlyList<DeathBuffEventRecord> records) {
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        AddZipEntry(archive, "[Content_Types].xml", BuildContentTypesXml());
        AddZipEntry(archive, "_rels/.rels", BuildRootRelsXml());
        AddZipEntry(archive, "xl/workbook.xml", BuildWorkbookXml());
        AddZipEntry(archive, "xl/_rels/workbook.xml.rels", BuildWorkbookRelsXml());
        AddZipEntry(archive, "xl/worksheets/sheet1.xml", BuildWorksheetXml(records));
    }

    private static void AddZipEntry(ZipArchive archive, string entryName, string content) {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        using var writer = new StreamWriter(entryStream, new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string BuildContentTypesXml() {
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
               "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
               "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
               "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
               "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
               "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
               "</Types>";
    }

    private static string BuildRootRelsXml() {
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
               "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
               "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
               "</Relationships>";
    }

    private static string BuildWorkbookXml() {
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
               "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
               "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
               "<sheets>" +
               "<sheet name=\"Events\" sheetId=\"1\" r:id=\"rId1\"/>" +
               "</sheets>" +
               "</workbook>";
    }

    private static string BuildWorkbookRelsXml() {
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
               "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
               "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
               "</Relationships>";
    }

    private static string BuildWorksheetXml(IReadOnlyList<DeathBuffEventRecord> records) {
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        builder.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        builder.Append("<sheetData>");

        AppendWorksheetRow(builder, 1, TableHeaders);
        var rowIndex = 2;
        foreach (var record in records) {
            AppendWorksheetRow(builder, rowIndex, BuildRowValues(record));
            rowIndex++;
        }

        builder.Append("</sheetData>");
        builder.Append("</worksheet>");
        return builder.ToString();
    }

    private static void AppendWorksheetRow(StringBuilder builder, int rowIndex, string[] values) {
        builder.Append("<row r=\"")
            .Append(rowIndex.ToString(CultureInfo.InvariantCulture))
            .Append("\">");

        for (var i = 0; i < values.Length; i++) {
            var cellRef = GetColumnName(i) + rowIndex.ToString(CultureInfo.InvariantCulture);
            var value = EscapeXml(values[i] ?? string.Empty);
            builder.Append("<c r=\"")
                .Append(cellRef)
                .Append("\" t=\"inlineStr\"><is><t>")
                .Append(value)
                .Append("</t></is></c>");
        }

        builder.Append("</row>");
    }

    private static string GetColumnName(int index) {
        var dividend = index + 1;
        var name = string.Empty;
        while (dividend > 0) {
            var modulo = (dividend - 1) % 26;
            name = (char)('A' + modulo) + name;
            dividend = (dividend - modulo - 1) / 26;
        }
        return name;
    }

    private static string EscapeCsv(string value) {
        if (value.Contains('"')) {
            value = value.Replace("\"", "\"\"");
        }

        if (value.Contains(',') || value.Contains('\n') || value.Contains('\r') || value.Contains('"')) {
            return $"\"{value}\"";
        }

        return value;
    }

    private static string EscapeXml(string value) {
        if (string.IsNullOrEmpty(value)) {
            return string.Empty;
        }

        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }

    private static string[] BuildRowValues(DeathBuffEventRecord record) {
        var values = new string[TableHeaders.Length];
        for (var i = 0; i < values.Length; i++) {
            values[i] = GetColumnValue(record, i);
        }
        return values;
    }

    private static string GetColumnValue(DeathBuffEventRecord record, int columnIndex) {
        return columnIndex switch {
            0 => record.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            1 => FormatPlayerName(record),
            2 => GetDutyName(record),
            3 => GetEventTypeLabel(record.EventType),
            4 => FormatStatus(record),
            5 => record.StatusStackCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            6 => record.StatusDurationSeconds.HasValue
                ? record.StatusDurationSeconds.Value.ToString("0.0", CultureInfo.InvariantCulture)
                : string.Empty,
            7 => record.DamageSourceName ?? string.Empty,
            8 => FormatAction(record),
            9 => record.DamageType ?? string.Empty,
            _ => string.Empty,
        };
    }

    private static string GetEventTypeLabel(TrackerEventType eventType) {
        return eventType == TrackerEventType.Death ? "死亡" : "状态获得";
    }

    private static string GetDutyName(DeathBuffEventRecord record) {
        return string.IsNullOrWhiteSpace(record.ContentName) ? record.TerritoryName : record.ContentName;
    }

    private static string FormatPlayerName(DeathBuffEventRecord record) {
        var world = record.PlayerHomeWorldName ?? record.PlayerCurrentWorldName;
        if (!string.IsNullOrWhiteSpace(world)) {
            return $"{record.PlayerName}@{world}";
        }

        return record.PlayerName;
    }

    private static string FormatStatus(DeathBuffEventRecord record) {
        if (record.EventType == TrackerEventType.Death) {
            return "死亡";
        }

        var name = record.StatusName;
        var id = record.StatusId?.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id)) {
            return $"{name}({id})";
        }

        if (!string.IsNullOrWhiteSpace(name)) {
            return name;
        }

        return id ?? string.Empty;
    }

    private static string GetStatusFilterValue(DeathBuffEventRecord record) {
        if (record.EventType != TrackerEventType.StatusGain) {
            return string.Empty;
        }

        var name = record.StatusName;
        var id = record.StatusId?.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id)) {
            return $"{name}({id})";
        }

        if (!string.IsNullOrWhiteSpace(name)) {
            return name;
        }

        return id ?? string.Empty;
    }

    private static string FormatAction(DeathBuffEventRecord record) {
        var actionIdText = record.DamageActionId?.ToString(CultureInfo.InvariantCulture);
        var actionNameText = record.DamageActionName;
        if (!string.IsNullOrWhiteSpace(actionNameText) && !string.IsNullOrWhiteSpace(actionIdText)) {
            return $"{actionNameText}({actionIdText})";
        }

        if (!string.IsNullOrWhiteSpace(actionNameText)) {
            return actionNameText;
        }

        if (!string.IsNullOrWhiteSpace(actionIdText)) {
            return actionIdText;
        }

        return string.Empty;
    }

    private void DrawEventTable(IReadOnlyList<DeathBuffEventRecord> filtered) {
        ImGui.BeginChild("events", new Vector2(0, 0), true);

        if (filtered.Count == 0) {
            ImGui.Text("没有事件");
            ImGui.EndChild();
            return;
        }

        var startIndex = pageIndex * pageSize;
        var endIndex = Math.Min(startIndex + pageSize, filtered.Count);
        var rowCount = Math.Max(0, endIndex - startIndex);

        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.Reorderable | ImGuiTableFlags.Hideable;
        if (ImGui.BeginTable("events-table", TableHeaders.Length, flags)) {
            foreach (var header in TableHeaders) {
                ImGui.TableSetupColumn(header);
            }
            ImGui.TableHeadersRow();

            var clipper = new ImGuiListClipper();
            clipper.Begin(rowCount);
            while (clipper.Step()) {
                for (var row = clipper.DisplayStart; row < clipper.DisplayEnd; row++) {
                    var record = filtered[startIndex + row];
                    ImGui.TableNextRow();
                    for (var col = 0; col < TableHeaders.Length; col++) {
                        ImGui.TableSetColumnIndex(col);
                        DrawCopyableCell(GetColumnValue(record, col));
                    }
                }
            }
            clipper.End();

            ImGui.EndTable();
        }

        ImGui.EndChild();
    }

    private static void DrawCopyableCell(string value) {
        var text = value ?? string.Empty;
        ImGui.TextUnformatted(text);
        if (ImGui.IsItemHovered()) {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted("点击复制");
            ImGui.EndTooltip();
        }
        if (ImGui.IsItemClicked()) {
            ImGui.SetClipboardText(text);
        }
    }

    private void DrawDateShortcuts() {
        ImGui.Text("日期快捷");
        ImGui.SetNextItemWidth(80);
        ImGui.InputInt("年##date-year", ref dateYear);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(60);
        ImGui.InputInt("月##date-month", ref dateMonth);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(60);
        ImGui.InputInt("日##date-day", ref dateDay);
        ImGui.SameLine();
        if (ImGui.Button("应用日期")) {
            ApplyDateFields();
        }

        ImGui.SameLine();
        if (ImGui.Button("今天")) {
            ApplyDateFilter(DateTime.Today);
        }

        ImGui.SameLine();
        if (ImGui.Button("昨天")) {
            ApplyDateFilter(DateTime.Today.AddDays(-1));
        }

        ImGui.Text("最近 X 天");
        ImGui.SetNextItemWidth(80);
        ImGui.InputInt("天数##recent-days", ref recentDays);
        ImGui.SameLine();
        if (ImGui.Button("应用##recent")) {
            ApplyRecentDays(recentDays);
        }

        ImGui.SameLine();
        if (ImGui.Button("近1天")) {
            ApplyRecentDays(1);
        }

        ImGui.SameLine();
        if (ImGui.Button("近3天")) {
            ApplyRecentDays(3);
        }

        ImGui.SameLine();
        if (ImGui.Button("近7天")) {
            ApplyRecentDays(7);
        }

        ImGui.SameLine();
        if (ImGui.Button("近14天")) {
            ApplyRecentDays(14);
        }

        ImGui.SameLine();
        if (ImGui.Button("近30天")) {
            ApplyRecentDays(30);
        }

        if (ImGui.Button("清空时间筛选")) {
            ClearTimeFilters();
        }

        if (!string.IsNullOrWhiteSpace(dateError)) {
            ImGui.Text(dateError);
        }
    }

    private void EnsureDateInitialized() {
        if (dateInitialized) {
            return;
        }

        var today = DateTime.Today;
        dateYear = today.Year;
        dateMonth = today.Month;
        dateDay = today.Day;
        dateInitialized = true;
    }

    private void ApplyDateFields() {
        dateError = string.Empty;
        try {
            var date = new DateTime(dateYear, dateMonth, dateDay);
            ApplyDateFilter(date);
        } catch (ArgumentOutOfRangeException) {
            dateError = "日期无效，请检查年月日";
        }
    }

    private void ApplyDateFilter(DateTime date) {
        dateError = string.Empty;
        SetDateFields(date);
        var start = date.Date;
        var end = date.Date.AddDays(1).AddSeconds(-1);
        fromFilter = start.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        toFilter = end.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private void ApplyRecentDays(int days) {
        if (days < 1) {
            dateError = "最近天数必须 >= 1";
            return;
        }

        dateError = string.Empty;
        var now = DateTime.Now;
        var from = now.AddDays(-days);
        fromFilter = from.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        toFilter = now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        SetDateFields(now.Date);
    }

    private void SetDateFields(DateTime date) {
        dateYear = date.Year;
        dateMonth = date.Month;
        dateDay = date.Day;
    }

    private void ClearTimeFilters() {
        fromFilter = string.Empty;
        toFilter = string.Empty;
        dateError = string.Empty;
    }

    private void ClearAllFilters() {
        dutyFilter = string.Empty;
        playerFilter = string.Empty;
        statusFilter = string.Empty;
        sourceFilter = string.Empty;
        actionFilter = string.Empty;
        damageTypeFilter = string.Empty;
        eventTypeFilterIndex = 0;
        pageIndex = 0;
        exportMessage = string.Empty;
        ClearTimeFilters();
    }

    private void DrawPaginationControls(int totalCount) {
        ImGui.Text("分页");
        ImGui.SetNextItemWidth(120);
        pageSize = Math.Clamp(pageSize, 10, 1000);
        if (ImGui.InputInt("每页数量", ref pageSize)) {
            pageSize = Math.Clamp(pageSize, 10, 1000);
        }

        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        pageIndex = Math.Clamp(pageIndex, 0, totalPages - 1);

        ImGui.SameLine();
        if (ImGui.Button("<")) {
            pageIndex = Math.Max(0, pageIndex - 1);
        }

        ImGui.SameLine();
        ImGui.Text($"第 {pageIndex + 1} / {totalPages} 页");

        ImGui.SameLine();
        if (ImGui.Button(">")) {
            pageIndex = Math.Min(totalPages - 1, pageIndex + 1);
        }
    }

    private FilterCriteria BuildFilter(ref string parseError) {
        var from = ParseTimestamp(fromFilter, ref parseError);
        var to = ParseTimestamp(toFilter, ref parseError);

        return new FilterCriteria {
            DutyFilter = dutyFilter,
            PlayerFilter = playerFilter,
            StatusFilter = statusFilter,
            SourceFilter = sourceFilter,
            ActionFilter = actionFilter,
            DamageTypeFilter = damageTypeFilter,
            EventType = eventTypeFilterIndex switch {
                1 => TrackerEventType.Death,
                2 => TrackerEventType.StatusGain,
                _ => null,
            },
            FromUtc = from,
            ToUtc = to,
        };
    }

    private static bool MatchesFilter(DeathBuffEventRecord record, FilterCriteria filter) {
        if (!string.IsNullOrWhiteSpace(filter.DutyFilter)) {
            var dutyName = GetDutyName(record);
            if (!dutyName.Contains(filter.DutyFilter, StringComparison.OrdinalIgnoreCase)) {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(filter.PlayerFilter)) {
            var playerName = FormatPlayerName(record);
            if (!playerName.Contains(filter.PlayerFilter, StringComparison.OrdinalIgnoreCase)) {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(filter.StatusFilter)) {
            var statusLabel = GetStatusFilterValue(record);
            if (string.IsNullOrWhiteSpace(statusLabel) ||
                !statusLabel.Contains(filter.StatusFilter, StringComparison.OrdinalIgnoreCase)) {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(filter.SourceFilter)) {
            var sourceName = record.DamageSourceName ?? string.Empty;
            if (!sourceName.Contains(filter.SourceFilter, StringComparison.OrdinalIgnoreCase)) {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(filter.ActionFilter)) {
            var actionName = FormatAction(record);
            if (!actionName.Contains(filter.ActionFilter, StringComparison.OrdinalIgnoreCase)) {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(filter.DamageTypeFilter)) {
            var damageType = record.DamageType ?? string.Empty;
            if (!damageType.Contains(filter.DamageTypeFilter, StringComparison.OrdinalIgnoreCase)) {
                return false;
            }
        }

        if (filter.EventType.HasValue && record.EventType != filter.EventType.Value) {
            return false;
        }

        if (filter.FromUtc.HasValue && record.TimestampUtc < filter.FromUtc.Value) {
            return false;
        }

        if (filter.ToUtc.HasValue && record.TimestampUtc > filter.ToUtc.Value) {
            return false;
        }

        return true;
    }

    private static DateTimeOffset? ParseTimestamp(string input, ref string parseError) {
        if (string.IsNullOrWhiteSpace(input)) {
            return null;
        }

        if (DateTimeOffset.TryParse(input, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var value)) {
            return value.ToUniversalTime();
        }

        parseError = "时间格式应为 'YYYY-MM-DD HH:MM'";
        return null;
    }

    private static void DrawComboFilter(string label, string[] options, ref string filter) {
        if (options.Length == 0) {
            return;
        }

        var currentIndex = 0;
        var filterValue = filter;
        if (!string.IsNullOrWhiteSpace(filterValue)) {
            var matchIndex = Array.FindIndex(options, option =>
                string.Equals(option, filterValue, StringComparison.OrdinalIgnoreCase));
            if (matchIndex >= 0) {
                currentIndex = matchIndex;
            }
        }

        ImGui.SetNextItemWidth(240);
        if (ImGui.Combo(label, ref currentIndex, options, options.Length)) {
            filter = currentIndex <= 0 ? string.Empty : options[currentIndex];
        }
    }

    private static string[] BuildDutyOptions(IReadOnlyList<DeathBuffEventRecord> records) {
        var names = records
            .Select(GetDutyName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        names.Insert(0, "全部");
        return names.ToArray();
    }

    private static string[] BuildPlayerOptions(IReadOnlyList<DeathBuffEventRecord> records) {
        var names = records
            .Select(FormatPlayerName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        names.Insert(0, "全部");
        return names.ToArray();
    }

    private static string[] BuildStatusOptions(IReadOnlyList<DeathBuffEventRecord> records) {
        var names = records
            .Select(GetStatusFilterValue)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        names.Insert(0, "全部");
        return names.ToArray();
    }

    private static string[] BuildSourceOptions(IReadOnlyList<DeathBuffEventRecord> records) {
        var names = records
            .Select(record => record.DamageSourceName ?? string.Empty)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        names.Insert(0, "全部");
        return names.ToArray();
    }

    private static string[] BuildActionOptions(IReadOnlyList<DeathBuffEventRecord> records) {
        var names = records
            .Select(FormatAction)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        names.Insert(0, "全部");
        return names.ToArray();
    }

    private static string[] BuildDamageTypeOptions(IReadOnlyList<DeathBuffEventRecord> records) {
        var names = records
            .Select(record => record.DamageType ?? string.Empty)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        names.Insert(0, "全部");
        return names.ToArray();
    }

    private sealed class FilterCriteria {
        public string DutyFilter { get; init; } = string.Empty;
        public string PlayerFilter { get; init; } = string.Empty;
        public string StatusFilter { get; init; } = string.Empty;
        public string SourceFilter { get; init; } = string.Empty;
        public string ActionFilter { get; init; } = string.Empty;
        public string DamageTypeFilter { get; init; } = string.Empty;
        public TrackerEventType? EventType { get; init; }
        public DateTimeOffset? FromUtc { get; init; }
        public DateTimeOffset? ToUtc { get; init; }
    }

    private enum ExportFormat {
        Csv,
        Excel,
    }
}
