using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using DeathBuffTracker.Models;
using StatusSheet = Lumina.Excel.Sheets.Status;

namespace DeathBuffTracker.UI;

public sealed class ConfigWindow : Window {
    private const int MaxStatusSearchResults = 200;

    private readonly DeathBuffTrackerPlugin plugin;
    private string statusInput = string.Empty;
    private string statusMessage = string.Empty;

    private string statusSearch = string.Empty;
    private string statusSearchMessage = string.Empty;
    private bool statusSearchIncludeName = true;
    private readonly List<StatusSearchEntry> statusSearchResults = new();

    public ConfigWindow(DeathBuffTrackerPlugin plugin) : base("死亡与状态追踪 - 设置") {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints {
            MinimumSize = new Vector2(450, 300),
        };

        statusInput = FormatStatuses(plugin.Configuration.TrackedStatuses);
    }

    public override void Draw() {
        ImGui.Text("采集范围");

        var trackSelf = plugin.Configuration.TrackSelf;
        if (ImGui.Checkbox("追踪自己", ref trackSelf)) {
            plugin.Configuration.TrackSelf = trackSelf;
            plugin.Configuration.Save();
        }

        var trackPartyMembers = plugin.Configuration.TrackPartyMembers;
        if (ImGui.Checkbox("追踪小队成员", ref trackPartyMembers)) {
            plugin.Configuration.TrackPartyMembers = trackPartyMembers;
            plugin.Configuration.Save();
        }

        var trackOtherPlayers = plugin.Configuration.TrackOtherPlayers;
        if (ImGui.Checkbox("追踪全部可见玩家（含自己/小队）", ref trackOtherPlayers)) {
            plugin.Configuration.TrackOtherPlayers = trackOtherPlayers;
            plugin.Configuration.Save();
        }

        var trackDeaths = plugin.Configuration.TrackDeaths;
        if (ImGui.Checkbox("追踪死亡", ref trackDeaths)) {
            plugin.Configuration.TrackDeaths = trackDeaths;
            plugin.Configuration.Save();
        }

        var trackStatusGains = plugin.Configuration.TrackStatusGains;
        if (ImGui.Checkbox("追踪状态获得", ref trackStatusGains)) {
            plugin.Configuration.TrackStatusGains = trackStatusGains;
            plugin.Configuration.Save();
        }

        var trackOnlyInInstances = plugin.Configuration.TrackOnlyInInstances;
        if (ImGui.Checkbox("仅副本内采集", ref trackOnlyInInstances)) {
            plugin.Configuration.TrackOnlyInInstances = trackOnlyInInstances;
            plugin.Configuration.Save();
        }

        ImGui.Separator();
        ImGui.Text("追踪状态 ID（逗号或换行分隔），可选名称：id:名称");
        ImGui.InputTextMultiline("##status-input", ref statusInput, 2048, new Vector2(0, 120));

        ImGui.Spacing();
        if (ImGui.Button("保存")) {
            statusMessage = string.Empty;
            var parsed = ParseStatuses(statusInput, out var ignoredCount);

            if (parsed.Count == 0 && !string.IsNullOrWhiteSpace(statusInput)) {
                statusMessage = "未识别到有效状态 ID，请使用数字 ID（支持中文逗号/冒号）。";
            } else {
                plugin.Configuration.TrackedStatuses = parsed;
                plugin.Configuration.Save();
                plugin.StatusStore.Save(parsed);
                statusInput = FormatStatuses(parsed);

                if (string.IsNullOrWhiteSpace(statusInput)) {
                    statusMessage = "已清空追踪状态。";
                } else if (ignoredCount > 0) {
                    statusMessage = $"已保存 {parsed.Count} 条，忽略 {ignoredCount} 条无效输入。";
                } else {
                    statusMessage = $"已保存 {parsed.Count} 条。";
                }
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("从 JSON 读取")) {
            var loaded = plugin.StatusStore.Load();
            if (loaded != null) {
                plugin.Configuration.TrackedStatuses = loaded;
                plugin.Configuration.Save();
                statusInput = FormatStatuses(loaded);
                statusMessage = $"已加载 {loaded.Count} 条。";
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("重载")) {
            statusInput = FormatStatuses(plugin.Configuration.TrackedStatuses);
            statusMessage = string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(statusMessage)) {
            ImGui.Text(statusMessage);
        }

        ImGui.Separator();
        DrawStatusSearch();
    }

    private void DrawStatusSearch() {
        ImGui.Text("状态搜索");
        var searchChanged = ImGui.InputText("名称/ID", ref statusSearch, 64);
        ImGui.SameLine();
        var runSearch = ImGui.Button("搜索");
        ImGui.SameLine();
        ImGui.Checkbox("附带名称", ref statusSearchIncludeName);

        if (searchChanged || runSearch) {
            UpdateStatusSearchResults();
        }

        if (!string.IsNullOrWhiteSpace(statusSearchMessage)) {
            ImGui.Text(statusSearchMessage);
        }

        var existingIds = BuildStatusIdSetFromInput();
        ImGui.BeginChild("status-search-results", new Vector2(0, 160), true);
        if (statusSearchResults.Count == 0) {
            ImGui.Text(string.IsNullOrWhiteSpace(statusSearch) ? "输入关键词开始搜索" : "没有结果");
        } else {
            foreach (var entry in statusSearchResults) {
                ImGui.PushID((int)entry.Id);
                if (existingIds.Contains(entry.Id)) {
                    ImGui.TextDisabled("已添加");
                } else if (ImGui.SmallButton("添加")) {
                    AppendStatusToInput(entry);
                }
                ImGui.SameLine();
                ImGui.Text($"{entry.Id} | {entry.Name}");
                ImGui.PopID();
            }
        }
        ImGui.EndChild();
    }

    private void UpdateStatusSearchResults() {
        statusSearchResults.Clear();
        statusSearchMessage = string.Empty;

        var query = statusSearch.Trim();
        if (string.IsNullOrWhiteSpace(query)) {
            return;
        }

        var sheet = Service.DataManager.GetExcelSheet<StatusSheet>();
        if (sheet == null) {
            statusSearchMessage = "无法读取状态数据表。";
            return;
        }

        if (uint.TryParse(query, out var statusId)) {
            var row = sheet.GetRow(statusId);
            if (row.RowId == 0 && statusId != 0) {
                statusSearchMessage = "未找到该状态 ID。";
                return;
            }

            var name = row.Name.ToString();
            if (string.IsNullOrWhiteSpace(name)) {
                name = "（无名称）";
            }

            statusSearchResults.Add(new StatusSearchEntry(statusId, name));
            return;
        }

        foreach (var row in sheet) {
            var name = row.Name.ToString();
            if (string.IsNullOrWhiteSpace(name)) {
                continue;
            }

            if (!name.Contains(query, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            statusSearchResults.Add(new StatusSearchEntry(row.RowId, name));
            if (statusSearchResults.Count >= MaxStatusSearchResults) {
                statusSearchMessage = $"匹配过多，仅显示前 {MaxStatusSearchResults} 条。";
                break;
            }
        }

        if (statusSearchResults.Count == 0) {
            statusSearchMessage = "未找到匹配的状态名。";
        }
    }

    private void AppendStatusToInput(StatusSearchEntry entry) {
        var value = statusSearchIncludeName && !string.IsNullOrWhiteSpace(entry.Name)
            ? $"{entry.Id}:{entry.Name}"
            : entry.Id.ToString();

        statusInput = string.IsNullOrWhiteSpace(statusInput)
            ? value
            : $"{statusInput}{Environment.NewLine}{value}";

        statusMessage = $"已添加 {entry.Id}";
    }

    private HashSet<uint> BuildStatusIdSetFromInput() {
        if (string.IsNullOrWhiteSpace(statusInput)) {
            return new HashSet<uint>();
        }

        var parsed = ParseStatuses(statusInput, out _);
        return parsed.Select(status => status.Id).ToHashSet();
    }

    private static List<TrackedStatus> ParseStatuses(string input, out int ignoredCount) {
        ignoredCount = 0;
        var results = new List<TrackedStatus>();
        var indexById = new Dictionary<uint, int>();
        var tokens = input.Split(new[] { ',', '，', '\n', '\r', ';', '；', '、' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var token in tokens) {
            var trimmed = token.Trim();
            if (trimmed.Length == 0) {
                continue;
            }

            string idPart;
            string namePart = string.Empty;
            var colonIndex = trimmed.IndexOf(':');
            if (colonIndex < 0) {
                colonIndex = trimmed.IndexOf('：');
            }

            if (colonIndex >= 0) {
                idPart = trimmed[..colonIndex].Trim();
                namePart = trimmed[(colonIndex + 1)..].Trim();
            } else {
                var parts = trimmed.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) {
                    continue;
                }

                idPart = parts[0].Trim();
                if (parts.Length > 1) {
                    namePart = parts[1].Trim();
                }
            }

            if (!uint.TryParse(idPart, out var id)) {
                ignoredCount++;
                continue;
            }

            if (indexById.TryGetValue(id, out var index)) {
                if (!string.IsNullOrWhiteSpace(namePart)) {
                    results[index].Name = namePart;
                }
                continue;
            }

            results.Add(new TrackedStatus {
                Id = id,
                Name = namePart,
            });
            indexById[id] = results.Count - 1;
        }

        return results;
    }

    private static string FormatStatuses(IEnumerable<TrackedStatus> statuses) {
        return string.Join(Environment.NewLine, statuses.Select(status =>
            string.IsNullOrWhiteSpace(status.Name) ? status.Id.ToString() : $"{status.Id}:{status.Name}"));
    }

    private sealed record StatusSearchEntry(uint Id, string Name);
}
