using System.ComponentModel;
using KTracker.Models;
using KTracker.Services;

namespace KTracker;

public sealed class MainForm : Form
{
    private readonly ConfigService _configService = new();
    private readonly TaskListStore _store = new();
    private readonly BindingList<TaskItem> _binding = new();

    private string _currentListPath = "";
    private TaskItem? _selectedTask;
    private bool _suppressSelectionChange;
    private bool _loading;
    private FileSystemWatcher? _fileWatcher;
    private FileSystemWatcher? _configWatcher;

    private readonly ToolStrip _toolStrip = new() { GripStyle = ToolStripGripStyle.Hidden };
    private readonly TextBox _nameFilterBox = new() { PlaceholderText = "Filter tasks by title…" };
    private readonly ListView _listView = new()
    {
        Dock = DockStyle.Fill,
        FullRowSelect = true,
        View = View.Details,
        HideSelection = false,
        MultiSelect = false,
    };
    private readonly TextBox _titleBox = new() { Dock = DockStyle.Top, PlaceholderText = "Task title" };
    private readonly TextBox _detailsBox = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = true,
        AcceptsReturn = true,
        AcceptsTab = true,
        Font = new Font("Segoe UI", 10f),
        PlaceholderText = "Large task details…",
    };
    private readonly ComboBox _statusBox = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _priorityBox = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _markedBox = new() { Text = "Marked", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 4, 0, 0) };
    private readonly Label _pathLabel = new() { Dock = DockStyle.Top, AutoSize = false, Height = 35, ForeColor = Color.DimGray };
    private readonly Button _saveTaskButton = new() { Text = "Save Task", AutoSize = true };
    private readonly Button _newTaskButton = new() { Text = "New Task", AutoSize = true };
    private readonly Button _deleteTaskButton = new() { Text = "Delete Task", AutoSize = true };
    private readonly string? _openTaskId;

    public MainForm(string? openTaskId = null)
    {
        _openTaskId = openTaskId;
        Text = "KTracker";
        Width = 1200;
        Height = 800;
        StartPosition = FormStartPosition.CenterScreen;

        BuildLayout();
        WireEvents();
        LoadEffectiveList();
        StartWatchers();
        Shown += (_, _) => TryOpenTaskOnStartup();
    }

    private void BuildLayout()
    {
        var openButton = new ToolStripButton("Open List") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        var saveButton = new ToolStripButton("Save List") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        var setDefaultButton = new ToolStripButton("Set As Default") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        var refreshButton = new ToolStripButton("Reload") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        openButton.Click += (_, _) => OpenList();
        saveButton.Click += (_, _) => SaveCurrentList();
        setDefaultButton.Click += (_, _) => SetAsDefaultList();
        refreshButton.Click += (_, _) => ReloadFromDisk();

        _toolStrip.Items.Add(openButton);
        _toolStrip.Items.Add(saveButton);
        _toolStrip.Items.Add(setDefaultButton);
        _toolStrip.Items.Add(new ToolStripSeparator());
        _toolStrip.Items.Add(refreshButton);
        _toolStrip.Items.Add(new ToolStripSeparator());
        _toolStrip.Items.Add(new ToolStripLabel("Status filter:"));
        var filterCombo = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
        filterCombo.Items.Add("(All)");
        foreach (var status in TaskStatuses.All)
        {
            filterCombo.Items.Add(status);
        }
        filterCombo.SelectedIndex = 0;
        filterCombo.SelectedIndexChanged += (_, _) => RefreshListView();
        _toolStrip.Items.Add(filterCombo);

        var nameFilterPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 34,
            Padding = new Padding(8, 4, 8, 4),
        };
        var nameFilterLabel = new Label
        {
            Text = "Name filter:",
            AutoSize = true,
            Dock = DockStyle.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(0, 6, 8, 0),
        };
        _nameFilterBox.Dock = DockStyle.Fill;
        _nameFilterBox.TextChanged += (_, _) => RefreshListView();
        nameFilterPanel.Controls.Add(_nameFilterBox);
        nameFilterPanel.Controls.Add(nameFilterLabel);

        _listView.Columns.Add("Title", 360);
        _listView.Columns.Add("Priority", 70);
        _listView.Columns.Add("Marked", 55);
        _listView.Columns.Add("Status", 110);
        _listView.Columns.Add("ID", 60);
        _listView.Columns.Add("Details", 80);

        foreach (var status in TaskStatuses.All)
        {
            _statusBox.Items.Add(status);
        }

        foreach (var priority in TaskPriorities.All)
        {
            _priorityBox.Items.Add(priority);
        }

        var editorButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 4, 0, 4),
        };
        _saveTaskButton.Click += (_, _) => SaveSelectedTask();
        _newTaskButton.Click += (_, _) => CreateTask();
        _deleteTaskButton.Click += (_, _) => DeleteSelectedTask();
        editorButtons.Controls.Add(_saveTaskButton);
        editorButtons.Controls.Add(_newTaskButton);
        editorButtons.Controls.Add(_deleteTaskButton);

        var editorPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        editorPanel.Controls.Add(_detailsBox);
        editorPanel.Controls.Add(_markedBox);
        editorPanel.Controls.Add(_priorityBox);
        editorPanel.Controls.Add(_statusBox);
        editorPanel.Controls.Add(_titleBox);
        editorPanel.Controls.Add(editorButtons);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 520,
        };
        split.Panel1.Controls.Add(_listView);
        split.Panel2.Controls.Add(editorPanel);

        Controls.Add(split);
        Controls.Add(_pathLabel);
        Controls.Add(nameFilterPanel);
        Controls.Add(_toolStrip);
    }

    private void WireEvents()
    {
        _listView.SelectedIndexChanged += (_, _) => OnTaskSelected();
        FormClosing += (_, e) =>
        {
            if (!TryCommitEditorToSelectedTask(promptIfDirty: true))
            {
                e.Cancel = true;
            }
        };
    }

    private void LoadEffectiveList()
    {
        var config = _configService.Load();
        var path = config.ResolveListPath();
        LoadList(path, setActive: false);
    }

    private void LoadList(string path, bool setActive)
    {
        _loading = true;
        try
        {
            if (!TryCommitEditorToSelectedTask(promptIfDirty: false))
            {
                return;
            }

            path = Path.GetFullPath(path);
            var doc = _store.Load(path);
            _currentListPath = path;
            _binding.Clear();
            foreach (var task in doc.Tasks.OrderByDescending(t => t.UpdatedUtc))
            {
                _binding.Add(task);
            }

            if (setActive)
            {
                _configService.SetActiveListPath(path);
            }

            RefreshListView();
            UpdatePathLabel();
            RestartFileWatcher();
        }
        finally
        {
            _loading = false;
        }
    }

    private void RefreshListView()
    {
        var statusFilter = _toolStrip.Items.OfType<ToolStripComboBox>().FirstOrDefault()?.SelectedItem?.ToString();
        var nameFilter = _nameFilterBox.Text.Trim();
        _suppressSelectionChange = true;
        _listView.BeginUpdate();
        _listView.Items.Clear();
        foreach (var task in _binding.OrderBy(t => TaskPriorities.SortRank(t.Priority)).ThenByDescending(t => t.UpdatedUtc))
        {
            if (statusFilter != null && statusFilter != "(All)" && !task.Status.Equals(statusFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (nameFilter.Length > 0 &&
                task.Title.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            var item = new ListViewItem(task.Title);
            item.SubItems.Add(task.Priority);
            item.SubItems.Add(task.Marked ? "✓" : "");
            item.SubItems.Add(task.Status);
            item.SubItems.Add(task.Id);
            item.SubItems.Add(task.Details.Length.ToString());
            item.Tag = task;
            _listView.Items.Add(item);
        }
        _listView.EndUpdate();
        _suppressSelectionChange = false;
    }

    private void OnTaskSelected()
    {
        if (_suppressSelectionChange || _loading)
        {
            return;
        }

        if (!TryCommitEditorToSelectedTask(promptIfDirty: true))
        {
            ReselectTask(_selectedTask);
            return;
        }

        if (_listView.SelectedItems.Count == 0)
        {
            _selectedTask = null;
            ClearEditor();
            return;
        }

        _selectedTask = (TaskItem)_listView.SelectedItems[0].Tag!;
        PopulateEditor(_selectedTask);
    }

    private void PopulateEditor(TaskItem task)
    {
        _titleBox.Text = task.Title;
        _detailsBox.Text = task.Details;
        _statusBox.SelectedItem = task.Status;
        _priorityBox.SelectedItem = TaskPriorities.All.Contains(task.Priority, StringComparer.OrdinalIgnoreCase)
            ? task.Priority
            : TaskPriorities.Normal;
        _markedBox.Checked = task.Marked;
    }

    private void ClearEditor()
    {
        _titleBox.Clear();
        _detailsBox.Clear();
        _markedBox.Checked = false;
        _statusBox.SelectedIndex = _statusBox.Items.Count > 0 ? 1 : -1;
        _priorityBox.SelectedItem = TaskPriorities.Normal;
    }

    private bool TryCommitEditorToSelectedTask(bool promptIfDirty)
    {
        if (_selectedTask == null)
        {
            return true;
        }

        var dirty = _titleBox.Text != _selectedTask.Title
            || _detailsBox.Text != _selectedTask.Details
            || (_statusBox.SelectedItem?.ToString() ?? "") != _selectedTask.Status
            || (_priorityBox.SelectedItem?.ToString() ?? TaskPriorities.Normal) != _selectedTask.Priority
            || _markedBox.Checked != _selectedTask.Marked;

        if (!dirty)
        {
            return true;
        }

        if (promptIfDirty)
        {
            var result = MessageBox.Show(this, "Save changes to the selected task?", "Unsaved changes",
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (result == DialogResult.Cancel)
            {
                return false;
            }
            if (result == DialogResult.No)
            {
                return true;
            }
        }

        ApplyEditorToTask(_selectedTask);
        PersistCurrentDocument();
        RefreshListView();
        ReselectTask(_selectedTask);
        return true;
    }

    private void ApplyEditorToTask(TaskItem task)
    {
        task.Title = _titleBox.Text.Trim();
        task.Details = _detailsBox.Text;
        task.Status = _statusBox.SelectedItem?.ToString() ?? TaskStatuses.ToDo;
        task.Priority = _priorityBox.SelectedItem?.ToString() ?? TaskPriorities.Normal;
        task.Marked = _markedBox.Checked;
        task.UpdatedUtc = DateTime.UtcNow;
    }

    private void SaveSelectedTask()
    {
        if (_selectedTask == null)
        {
            MessageBox.Show(this, "Select a task first.", "KTracker", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplyEditorToTask(_selectedTask);
        PersistCurrentDocument();
        RefreshListView();
        ReselectTask(_selectedTask);
    }

    private void CreateTask()
    {
        if (!TryCommitEditorToSelectedTask(promptIfDirty: true))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var task = new TaskItem
        {
            Id = TaskListStore.GenerateId("new"),
            Title = "New task",
            Details = "",
            Status = TaskStatuses.ToDo,
            Priority = TaskPriorities.Normal,
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        _binding.Insert(0, task);
        PersistCurrentDocument();
        RefreshListView();
        SelectTask(task);
    }

    private void DeleteSelectedTask()
    {
        if (_selectedTask == null)
        {
            return;
        }

        if (MessageBox.Show(this, $"Delete task '{_selectedTask.Title}'?", "Confirm delete",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        _binding.Remove(_selectedTask);
        _selectedTask = null;
        ClearEditor();
        PersistCurrentDocument();
        RefreshListView();
    }

    private void PersistCurrentDocument()
    {
        var doc = new TaskListDocument
        {
            Name = Path.GetFileNameWithoutExtension(_currentListPath),
            Tasks = _binding.ToList(),
        };
        _store.Save(_currentListPath, doc);
    }

    private void SaveCurrentList() => PersistCurrentDocument();

    private void OpenList()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "JSON task lists (*.json)|*.json|All files (*.*)|*.*",
            InitialDirectory = AppPaths.ListsDir,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            LoadList(dialog.FileName, setActive: true);
        }
    }

    private void SetAsDefaultList()
    {
        var config = _configService.Load();
        config.DefaultListPath = _currentListPath;
        config.ActiveListPath = _currentListPath;
        _configService.Save(config);
        UpdatePathLabel();
        MessageBox.Show(this, $"Default list set to:\n{_currentListPath}", "KTracker",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ReloadFromDisk()
    {
        if (string.IsNullOrWhiteSpace(_currentListPath))
        {
            return;
        }
        LoadList(_currentListPath, setActive: false);
    }

    private void UpdatePathLabel()
    {
        var config = _configService.Load();
        var active = config.ActiveListPath ?? "(none)";
        _pathLabel.Text = $"List: {_currentListPath}   |   Active: {active}   |   Default: {config.DefaultListPath}";
    }

    private void TryOpenTaskOnStartup()
    {
        if (string.IsNullOrWhiteSpace(_openTaskId))
        {
            return;
        }

        if (!SelectTaskById(_openTaskId))
        {
            MessageBox.Show(this, $"Task id '{_openTaskId}' was not found in the current list.",
                "KTracker", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private bool SelectTaskById(string taskId)
    {
        var task = _binding.FirstOrDefault(t => t.Id.Equals(taskId, StringComparison.OrdinalIgnoreCase));
        if (task == null)
        {
            return false;
        }

        EnsureTaskVisibleInList(task);
        SelectTask(task);
        Text = $"KTracker — {task.Title}";
        Activate();
        return true;
    }

    private void EnsureTaskVisibleInList(TaskItem task)
    {
        var statusFilter = _toolStrip.Items.OfType<ToolStripComboBox>().FirstOrDefault()?.SelectedItem?.ToString();
        var nameFilter = _nameFilterBox.Text.Trim();
        var hiddenByStatus = statusFilter != null && statusFilter != "(All)"
            && !task.Status.Equals(statusFilter, StringComparison.OrdinalIgnoreCase);
        var hiddenByName = nameFilter.Length > 0
            && task.Title.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0;

        if (!hiddenByStatus && !hiddenByName)
        {
            return;
        }

        _nameFilterBox.Text = "";
        var filterCombo = _toolStrip.Items.OfType<ToolStripComboBox>().FirstOrDefault();
        if (filterCombo != null)
        {
            filterCombo.SelectedIndex = 0;
        }
        else
        {
            RefreshListView();
        }
    }

    private void SelectTask(TaskItem task)
    {
        foreach (ListViewItem item in _listView.Items)
        {
            if (ReferenceEquals(item.Tag, task) || ((TaskItem)item.Tag!).Id == task.Id)
            {
                item.Selected = true;
                item.Focused = true;
                item.EnsureVisible();
                break;
            }
        }
    }

    private void ReselectTask(TaskItem? task)
    {
        if (task == null)
        {
            return;
        }
        _suppressSelectionChange = true;
        SelectTask(task);
        _suppressSelectionChange = false;
        _selectedTask = task;
        PopulateEditor(task);
    }

    private void StartWatchers()
    {
        _configWatcher = new FileSystemWatcher(AppPaths.AppRoot, "config.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        _configWatcher.Changed += (_, _) => BeginInvoke(ReloadIfExternalChange);
        _configWatcher.Created += (_, _) => BeginInvoke(ReloadIfExternalChange);
    }

    private void RestartFileWatcher()
    {
        _fileWatcher?.Dispose();
        if (string.IsNullOrWhiteSpace(_currentListPath))
        {
            return;
        }

        var dir = Path.GetDirectoryName(_currentListPath)!;
        var file = Path.GetFileName(_currentListPath);
        _fileWatcher = new FileSystemWatcher(dir, file)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _fileWatcher.Changed += (_, _) => BeginInvoke(ReloadIfExternalChange);
    }

    private void ReloadIfExternalChange()
    {
        if (_loading)
        {
            return;
        }

        var config = _configService.Load();
        var effective = config.ResolveListPath();
        if (!effective.Equals(_currentListPath, StringComparison.OrdinalIgnoreCase))
        {
            LoadList(effective, setActive: false);
            return;
        }

        ReloadFromDisk();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fileWatcher?.Dispose();
            _configWatcher?.Dispose();
        }
        base.Dispose(disposing);
    }
}
