using Nexus.Core.Apps;
using Nexus.Core.Protocol;

namespace Nexus.Tray;

/// <summary>
/// The list of apps a phone may start (only editable here, on the PC) and the profiles built from them.
/// </summary>
internal sealed class AppsForm : Form
{
    private static readonly (string Id, string Name)[] Presets =
    [
        ("gaming", "Gaming"), ("developer", "Developer"), ("creator", "Creator"), ("night", "Night"),
    ];

    private readonly UserAppStore _store;
    private readonly ListView _apps = new() { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, HideSelection = false };
    private readonly CheckBox _allowClose = new() { Text = "Разрешать закрывать с телефона", AutoSize = true, Checked = false };
    private readonly ListBox _profiles = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly TextBox _profileName = new() { Width = 300 };
    private readonly CheckedListBox _profileApps = new() { Width = 300, Height = 260, CheckOnClick = true };
    private readonly Label _profileStatus = new() { AutoSize = true, ForeColor = Theme.Muted };
    private string? _editingProfileId;

    public AppsForm(UserAppStore store)
    {
        _store = store;
        Text = "Nexus PC — приложения и профили";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(760, 480);
        MinimumSize = new Size(700, 420);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        Theme.StyleTabs(tabs);
        tabs.TabPages.Add(BuildAppsPage());
        tabs.TabPages.Add(BuildProfilesPage());
        Controls.Add(tabs);
        Theme.Apply(this);

        _store.Changed += OnStoreChanged;
        FormClosed += (_, _) => _store.Changed -= OnStoreChanged;
        RefreshAll();
    }

    private void OnStoreChanged(object? sender, EventArgs e) => BeginInvoke(RefreshAll);

    private TabPage BuildAppsPage()
    {
        var page = new TabPage("Приложения") { BackColor = Theme.Background };
        _apps.Columns.Add("Название", 300);
        _apps.Columns.Add("Тип", 140);
        _apps.Columns.Add("Закрытие с телефона", 180);

        var note = new Label
        {
            Text = "Телефон может запускать только приложения из этого списка. Добавлять их можно только здесь, на ПК.",
            Dock = DockStyle.Top, Height = 40, ForeColor = Theme.Muted, Padding = new Padding(4),
        };
        var fromStart = new Button { Text = "Из меню «Пуск»…", Width = 170, Height = 34 };
        var fromExe = new Button { Text = "Выбрать .exe…", Width = 150, Height = 34 };
        var remove = new Button { Text = "Удалить", Width = 110, Height = 34 };
        fromStart.Click += (_, _) => AddFromStartMenu();
        fromExe.Click += (_, _) => AddExecutable();
        remove.Click += (_, _) =>
        {
            foreach (ListViewItem item in _apps.SelectedItems)
            {
                _store.RemoveApp((string)item.Tag!);
            }
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(4) };
        buttons.Controls.AddRange([fromStart, fromExe, remove, _allowClose]);
        page.Controls.Add(_apps);
        page.Controls.Add(note);
        page.Controls.Add(buttons);
        return page;
    }

    private TabPage BuildProfilesPage()
    {
        var page = new TabPage("Профили") { BackColor = Theme.Background };
        var left = new Panel { Dock = DockStyle.Left, Width = 230, Padding = new Padding(4) };
        var presets = new Button { Text = "Создать стандартные", Dock = DockStyle.Bottom, Height = 34 };
        var create = new Button { Text = "Новый профиль", Dock = DockStyle.Bottom, Height = 34 };
        left.Controls.Add(_profiles);
        left.Controls.Add(create);
        left.Controls.Add(presets);

        var right = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(12), WrapContents = false };
        var save = new Button { Text = "Сохранить", Width = 140, Height = 34 };
        Theme.MakePrimary(save);
        var delete = new Button { Text = "Удалить профиль", Width = 160, Height = 34 };
        var actions = new FlowLayoutPanel { AutoSize = true };
        actions.Controls.AddRange([save, delete]);
        right.Controls.AddRange([new Label { Text = "Название", AutoSize = true }, _profileName,
            new Label { Text = "Приложения профиля", AutoSize = true }, _profileApps, actions, _profileStatus]);

        page.Controls.Add(right);
        page.Controls.Add(left);

        _profiles.SelectedIndexChanged += (_, _) => LoadProfile(_profiles.SelectedItem as ProfileItem);
        create.Click += (_, _) =>
        {
            _profiles.ClearSelected();
            _editingProfileId = null;
            _profileName.Text = string.Empty;
            for (var i = 0; i < _profileApps.Items.Count; i++)
            {
                _profileApps.SetItemChecked(i, false);
            }
            _profileName.Focus();
        };
        presets.Click += (_, _) =>
        {
            foreach (var (id, name) in Presets)
            {
                if (_store.FindProfile(id) is null)
                {
                    _store.UpsertProfile(new ProfileDefinition(id, name, []));
                }
            }
            _profileStatus.Text = "Созданы Gaming, Developer, Creator, Night. Отметьте в каждом нужные приложения.";
        };
        save.Click += (_, _) => SaveProfile();
        delete.Click += (_, _) =>
        {
            if (_editingProfileId is { } id)
            {
                _store.DeleteProfile(id);
                _editingProfileId = null;
                _profileStatus.Text = "Профиль удалён.";
            }
        };
        return page;
    }

    private void RefreshAll()
    {
        _apps.BeginUpdate();
        _apps.Items.Clear();
        foreach (var app in _store.Apps)
        {
            var kind = app.Kind switch
            {
                AppLaunchKind.Shortcut => "Ярлык",
                AppLaunchKind.PackagedApp => "Приложение Store",
                _ => "Программа",
            };
            var close = app.CloseAllowed && app.ProcessPath is not null ? "Разрешено" : "Нет";
            _apps.Items.Add(new ListViewItem([app.DisplayName, kind, close]) { Tag = app.Id });
        }
        _apps.EndUpdate();

        var selected = _editingProfileId;
        _profiles.Items.Clear();
        foreach (var profile in _store.Profiles)
        {
            _profiles.Items.Add(new ProfileItem(profile));
        }
        _profileApps.Items.Clear();
        foreach (var app in _store.Apps)
        {
            _profileApps.Items.Add(new AppItem(app));
        }
        var index = _store.Profiles.ToList().FindIndex(p => p.Id == selected);
        if (index >= 0)
        {
            _profiles.SelectedIndex = index;
        }
    }

    private void LoadProfile(ProfileItem? item)
    {
        if (item is null)
        {
            return;
        }
        _editingProfileId = item.Profile.Id;
        _profileName.Text = item.Profile.Name;
        for (var i = 0; i < _profileApps.Items.Count; i++)
        {
            _profileApps.SetItemChecked(i, item.Profile.AppIds.Contains(((AppItem)_profileApps.Items[i]).App.Id));
        }
        _profileStatus.Text = string.Empty;
    }

    private void SaveProfile()
    {
        var name = _profileName.Text.Trim();
        if (!ProfileRules.IsValidName(name))
        {
            _profileStatus.Text = "Название: от 1 до 64 символов.";
            return;
        }
        var appIds = _profileApps.CheckedItems.Cast<AppItem>().Select(a => a.App.Id).ToList();
        if (appIds.Count > ProfileRules.MaxApps)
        {
            _profileStatus.Text = $"Не больше {ProfileRules.MaxApps} приложений в профиле.";
            return;
        }
        var id = _editingProfileId ?? Identifiers.Slugify(name, _store.Profiles.Select(p => p.Id));
        try
        {
            _store.UpsertProfile(new ProfileDefinition(id, name, appIds));
            _editingProfileId = id;
            _profileStatus.Text = "Сохранено.";
        }
        catch (InvalidOperationException ex)
        {
            _profileStatus.Text = ex.Message;
        }
    }

    private void AddFromStartMenu()
    {
        using var picker = new StartMenuPicker();
        if (picker.ShowDialog(this) != DialogResult.OK || picker.Selected is not { } item)
        {
            return;
        }
        var processPath = ShellLinks.ResolveExecutable(item.Path);
        Add(item.Name, AppLaunchKind.Shortcut, item.Path, processPath);
    }

    private void AddExecutable()
    {
        using var dialog = new OpenFileDialog { Filter = "Программы (*.exe)|*.exe", Title = "Выберите программу" };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        Add(Path.GetFileNameWithoutExtension(dialog.FileName), AppLaunchKind.Executable, dialog.FileName, dialog.FileName);
    }

    private void Add(string name, AppLaunchKind kind, string target, string? processPath)
    {
        try
        {
            var displayName = name.Length > ProfileRules.MaxNameLength ? name[..ProfileRules.MaxNameLength] : name;
            _store.AddApp(displayName, kind, target, _allowClose.Checked, processPath);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            MessageBox.Show(this, ex.Message, "Nexus PC", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private sealed record ProfileItem(ProfileDefinition Profile)
    {
        public override string ToString() => $"{Profile.Name}  ({Profile.AppIds.Count})";
    }

    private sealed record AppItem(AppEntry App)
    {
        public override string ToString() => App.DisplayName;
    }
}

/// <summary>Searchable list of Start menu shortcuts.</summary>
internal sealed class StartMenuPicker : Form
{
    private readonly IReadOnlyList<ShellLinks.StartMenuItem> _all = ShellLinks.StartMenu();
    private readonly TextBox _search = new() { Dock = DockStyle.Top, PlaceholderText = "Поиск…" };
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false };

    public StartMenuPicker()
    {
        Text = "Меню «Пуск»";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(420, 520);
        var ok = new Button { Text = "Добавить", Dock = DockStyle.Bottom, Height = 38, DialogResult = DialogResult.OK };
        Theme.MakePrimary(ok);
        Controls.Add(_list);
        Controls.Add(_search);
        Controls.Add(ok);
        AcceptButton = ok;
        Theme.Apply(this);
        _search.TextChanged += (_, _) => Fill();
        _list.DoubleClick += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        Fill();
    }

    public ShellLinks.StartMenuItem? Selected => _list.SelectedItem as ShellLinks.StartMenuItem;

    private void Fill()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        _list.DisplayMember = nameof(ShellLinks.StartMenuItem.Name);
        foreach (var item in _all.Where(i => i.Name.Contains(_search.Text, StringComparison.CurrentCultureIgnoreCase)))
        {
            _list.Items.Add(item);
        }
        _list.EndUpdate();
    }
}
