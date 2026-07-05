using System.ComponentModel;
using System.Text.Json;
using KTracker.Models;
using KTracker.Services;
using ModelContextProtocol.Server;

namespace KTracker.Mcp;

[McpServerToolType]
public static class KTrackerTools
{
    private static readonly TaskListStore Store = new();
    private static readonly ConfigService Config = new();

    [McpServerTool, Description("ktracker_set_list — Open or create a task list JSON file and set it as the active list for MCP and GUI.")]
    public static string SetList(
        [Description("Absolute or workspace-relative path to a .json task list file")] string list_path,
        [Description("Optional workspace directory for resolving relative paths")] string? workspace = null)
    {
        var path = ResolvePath(list_path, workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Store.Load(path);
        Config.SetActiveListPath(path);
        return FormatSummary(path, Store.Load(path));
    }

    [McpServerTool, Description("ktracker_summary — Task counts and in-progress items for the active or default list.")]
    public static string Summary()
    {
        var path = Store.GetEffectiveListPath();
        return FormatSummary(path, Store.LoadEffective());
    }

    [McpServerTool, Description("ktracker_search — Search tasks by status, text, or id in the active or default list.")]
    public static string Search(
        [Description("Optional comma-separated statuses to filter")] string? statuses = null,
        [Description("Optional comma-separated search terms (title/details, OR logic)")] string? terms = null,
        [Description("Optional comma-separated task ids")] string? ids = null,
        [Description("Filter by marked flag when set")] bool? marked = null,
        [Description("Optional comma-separated priorities to filter")] string? priorities = null,
        [Description("Include full details field in results")] bool include_details = false)
    {
        var doc = Store.LoadEffective();
        IEnumerable<TaskItem> tasks = doc.Tasks;

        if (!string.IsNullOrWhiteSpace(statuses))
        {
            var wanted = SplitCsv(statuses);
            tasks = tasks.Where(t => wanted.Contains(t.Status, StringComparer.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(ids))
        {
            var wantedIds = SplitCsv(ids);
            tasks = tasks.Where(t => wantedIds.Contains(t.Id, StringComparer.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(terms))
        {
            var wantedTerms = SplitCsv(terms);
            tasks = tasks.Where(t =>
                wantedTerms.Any(term =>
                    TaskListStore.FuzzyMatch(t.Title, term)
                    || TaskListStore.FuzzyMatch(t.Details, term)
                    || TaskListStore.FuzzyMatch(t.Status, term)
                    || TaskListStore.FuzzyMatch(t.Priority, term)));
        }

        if (marked.HasValue)
        {
            tasks = tasks.Where(t => t.Marked == marked.Value);
        }

        if (!string.IsNullOrWhiteSpace(priorities))
        {
            var wantedPriorities = SplitCsv(priorities);
            tasks = tasks.Where(t => wantedPriorities.Contains(t.Priority, StringComparer.OrdinalIgnoreCase));
        }

        var result = tasks
            .OrderBy(t => TaskPriorities.SortRank(t.Priority))
            .ThenByDescending(t => t.UpdatedUtc)
            .Select(t => ToDto(t, include_details))
            .ToList();
        return JsonSerializer.Serialize(new
        {
            source = SourceInfo(Store.GetEffectiveListPath()),
            count = result.Count,
            tasks = result,
        });
    }

    [McpServerTool, Description("ktracker_add — Add one or more tasks with optional large details text.")]
    public static string Add(
        [Description("Task titles; each entry becomes one task")] string[] titles,
        [Description("Status for all new tasks")] string status = TaskStatuses.ToDo,
        [Description("Priority for all new tasks")] string priority = TaskPriorities.Normal,
        [Description("Marked flag for all new tasks")] bool marked = false,
        [Description("Optional details body applied to every new task when a single title is given")] string? details = null,
        [Description("Optional parallel details array matching titles")] string[]? details_list = null)
    {
        if (titles.Length == 0)
        {
            throw new ArgumentException("At least one title is required.");
        }

        ValidateStatus(status);
        ValidatePriority(priority);
        var path = Store.GetEffectiveListPath();
        var doc = Store.Load(path);
        var added = new List<object>();

        for (var i = 0; i < titles.Length; i++)
        {
            var title = titles[i].Trim();
            if (title.Length == 0)
            {
                continue;
            }

            var body = details_list != null && i < details_list.Length
                ? details_list[i]
                : titles.Length == 1 ? details ?? "" : "";

            var now = DateTime.UtcNow;
            var task = new TaskItem
            {
                Id = TaskListStore.GenerateId(title),
                Title = title,
                Details = body ?? "",
                Status = status,
                Priority = priority,
                Marked = marked,
                CreatedUtc = now,
                UpdatedUtc = now,
            };
            doc.Tasks.Add(task);
            added.Add(ToDto(task, includeDetails: true));
        }

        Store.Save(path, doc);
        return JsonSerializer.Serialize(new
        {
            source = SourceInfo(path),
            added,
            summary = BuildCounts(doc),
        });
    }

    [McpServerTool, Description("ktracker_update — Update task title, large details, status, and/or priority by id.")]
    public static string Update(
        [Description("Task ids to update")] string[] ids,
        [Description("New status")] string? status = null,
        [Description("New priority")] string? priority = null,
        [Description("Replace title")] string? title = null,
        [Description("Replace entire details field")] string? details = null,
        [Description("Append to existing details")] string? append_details = null,
        [Description("Set marked flag")] bool? marked = null)
    {
        if (ids.Length == 0)
        {
            throw new ArgumentException("At least one id is required.");
        }

        if (status != null)
        {
            ValidateStatus(status);
        }

        if (priority != null)
        {
            ValidatePriority(priority);
        }

        var path = Store.GetEffectiveListPath();
        var doc = Store.Load(path);
        var updated = new List<object>();

        foreach (var id in ids)
        {
            var task = doc.Tasks.FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Task id '{id}' not found.");

            if (!string.IsNullOrWhiteSpace(title))
            {
                task.Title = title.Trim();
            }

            if (details != null)
            {
                task.Details = details;
            }
            else if (!string.IsNullOrEmpty(append_details))
            {
                task.Details = string.IsNullOrEmpty(task.Details)
                    ? append_details
                    : task.Details + Environment.NewLine + append_details;
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                task.Status = status;
            }

            if (!string.IsNullOrWhiteSpace(priority))
            {
                task.Priority = priority;
            }

            if (marked.HasValue)
            {
                task.Marked = marked.Value;
            }

            task.UpdatedUtc = DateTime.UtcNow;
            updated.Add(ToDto(task, includeDetails: true));
        }

        Store.Save(path, doc);
        return JsonSerializer.Serialize(new
        {
            source = SourceInfo(path),
            updated,
            summary = BuildCounts(doc),
        });
    }

    [McpServerTool, Description("ktracker_get — Fetch a single task including full details.")]
    public static string GetTask([Description("Task id")] string id)
    {
        var doc = Store.LoadEffective();
        var task = doc.Tasks.FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Task id '{id}' not found.");
        return JsonSerializer.Serialize(new
        {
            source = SourceInfo(Store.GetEffectiveListPath()),
            task = ToDto(task, includeDetails: true),
        });
    }

    private static string FormatSummary(string path, TaskListDocument doc)
    {
        var counts = BuildCounts(doc);
        var wip = doc.Tasks.Where(t => t.Status == TaskStatuses.InProgress)
            .Select(t => ToDto(t, includeDetails: false))
            .ToList();
        return JsonSerializer.Serialize(new
        {
            source = SourceInfo(path),
            counts,
            total = doc.Tasks.Count,
            inProgress = wip,
            instructions = "KTracker MCP updates the active list (GUI-open file) or default list from config.json.",
        });
    }

    private static object SourceInfo(string path) => new
    {
        path,
        id = TaskListStore.GenerateStableId(path),
        name = Path.GetFileNameWithoutExtension(path),
    };

    private static Dictionary<string, int> BuildCounts(TaskListDocument doc)
    {
        var counts = TaskStatuses.All.ToDictionary(s => s, _ => 0, StringComparer.OrdinalIgnoreCase);
        foreach (var task in doc.Tasks)
        {
            if (!counts.ContainsKey(task.Status))
            {
                counts[task.Status] = 0;
            }
            counts[task.Status]++;
        }
        return counts;
    }

    private static object ToDto(TaskItem task, bool includeDetails) => includeDetails
        ? new
        {
            task.Id,
            task.Title,
            task.Details,
            task.Status,
            task.Priority,
            task.Marked,
            task.CreatedUtc,
            task.UpdatedUtc,
        }
        : new
        {
            task.Id,
            task.Title,
            task.Status,
            task.Priority,
            task.Marked,
            detailsLength = task.Details.Length,
            task.UpdatedUtc,
        };

    private static string ResolvePath(string listPath, string? workspace)
    {
        if (Path.IsPathRooted(listPath))
        {
            return Path.GetFullPath(listPath);
        }

        var root = string.IsNullOrWhiteSpace(workspace) ? AppPaths.AppRoot : workspace;
        return Path.GetFullPath(Path.Combine(root, listPath));
    }

    private static string[] SplitCsv(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void ValidateStatus(string status)
    {
        if (!TaskStatuses.All.Contains(status, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Invalid status '{status}'. Use: {string.Join(", ", TaskStatuses.All)}");
        }
    }

    private static void ValidatePriority(string priority)
    {
        if (!TaskPriorities.All.Contains(priority, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Invalid priority '{priority}'. Use: {string.Join(", ", TaskPriorities.All)}");
        }
    }
}
