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
    private readonly CheckBox _allowClose = new() { Text = TrayText.Get("AppsAllowClose"), AutoSize = true, Checked = false };
    private readonly ListBox _profiles = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly TextBox _profileName = new() { Width = 300 };
    private readonly CheckedListBox _profileApps = new() { Width = 300, Height = 260, CheckOnClick = true };
    private readonly Label _profileStatus = new() { AutoSize = true, ForeColor = Theme.Muted };
    private string? _editingProfileId;

    public AppsForm(UserAppStore store)
    {
        _store = store;
        Text = TrayText.Get("AppsTitle");
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(900, 480);
        MinimumSize = new Size(880, 420);

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
        var page = new TabPage(TrayText.Get("AppsTab")) { BackColor = Theme.Background };
        _apps.Columns.Add(TrayText.Get("AppsColName"), 300);
        _apps.Columns.Add(TrayText.Get("AppsColType"), 140);
        _apps.Columns.Add(TrayText.Get("AppsColClose"), 180);

        var note = new Label
        {
            Text = TrayText.Get("AppsHint"),
            Dock = DockStyle.Top, Height = 40, ForeColor = Theme.Muted, Padding = new Padding(4),
        };
        var fromStart = new Button { Text = TrayText.Get("AppsFromStart"), AutoSize = true, MinimumSize = new Size(170, 34), Height = 34 };
        var fromStore = new Button { Text = TrayText.Get("AppsFromStore"), AutoSize = true, MinimumSize = new Size(180, 34), Height = 34 };
        var fromExe = new Button { Text = TrayText.Get("AppsFromExe"), AutoSize = true, MinimumSize = new Size(150, 34), Height = 34 };
        var remove = new Button { Text = TrayText.Get("AppsRemove"), AutoSize = true, MinimumSize = new Size(110, 34), Height = 34 };
        fromStart.Click += (_, _) => AddFromStartMenu();
        fromStore.Click += (_, _) => AddPackagedApp();
        fromExe.Click += (_, _) => AddExecutable();
        remove.Click += (_, _) =>
        {
            foreach (ListViewItem item in _apps.SelectedItems)
            {
                _store.RemoveApp((string)item.Tag!);
            }
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(4) };
        buttons.Controls.AddRange([fromStart, fromStore, fromExe, remove, _allowClose]);
        page.Controls.Add(_apps);
        page.Controls.Add(note);
        page.Controls.Add(buttons);
        return page;
    }

    private TabPage BuildProfilesPage()
    {
        var page = new TabPage(TrayText.Get("ProfilesTab")) { BackColor = Theme.Background };
        var left = new Panel { Dock = DockStyle.Left, Width = 230, Padding = new Padding(4) };
        var presets = new Button { Text = TrayText.Get("ProfilesPresets"), Dock = DockStyle.Bottom, Height = 34 };
        var create = new Button { Text = TrayText.Get("ProfilesNew"), Dock = DockStyle.Bottom, Height = 34 };
        left.Controls.Add(_profiles);
        left.Controls.Add(create);
        left.Controls.Add(presets);

        var right = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(12), WrapContents = false };
        var save = new Button { Text = TrayText.Get("ProfilesSave"), AutoSize = true, MinimumSize = new Size(140, 34), Height = 34 };
        Theme.MakePrimary(save);
        var delete = new Button { Text = TrayText.Get("ProfilesDelete"), AutoSize = true, MinimumSize = new Size(160, 34), Height = 34 };
        var actions = new FlowLayoutPanel { AutoSize = true };
        actions.Controls.AddRange([save, delete]);
        right.Controls.AddRange([new Label { Text = TrayText.Get("ProfilesName"), AutoSize = true }, _profileName,
            new Label { Text = TrayText.Get("ProfilesApps"), AutoSize = true }, _profileApps, actions, _profileStatus]);

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
            _profileStatus.Text = TrayText.Get("ProfilesPresetsDone");
        };
        save.Click += (_, _) => SaveProfile();
        delete.Click += (_, _) =>
        {
            if (_editingProfileId is { } id)
            {
                _store.DeleteProfile(id);
                _editingProfileId = null;
                _profileStatus.Text = TrayText.Get("ProfilesDeleted");
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
                AppLaunchKind.Shortcut => TrayText.Get("AppsKindShortcut"),
                AppLaunchKind.PackagedApp => TrayText.Get("AppsKindStore"),
                _ => TrayText.Get("AppsKindProgram"),
            };
            var close = app.CloseAllowed && app.ProcessPath is not null ? TrayText.Get("AppsCloseAllowed") : TrayText.Get("AppsCloseNo");
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
            _profileStatus.Text = TrayText.Get("ProfilesNameInvalid");
            return;
        }
        var appIds = _profileApps.CheckedItems.Cast<AppItem>().Select(a => a.App.Id).ToList();
        if (appIds.Count > ProfileRules.MaxApps)
        {
            _profileStatus.Text = TrayText.Format("ProfilesTooManyApps", ProfileRules.MaxApps);
            return;
        }
        var id = _editingProfileId ?? Identifiers.Slugify(name, _store.Profiles.Select(p => p.Id));
        try
        {
            _store.UpsertProfile(new ProfileDefinition(id, name, appIds));
            _editingProfileId = id;
            _profileStatus.Text = TrayText.Get("ProfilesSaved");
        }
        catch (InvalidOperationException ex)
        {
            _profileStatus.Text = ex.Message;
        }
    }

    private void AddFromStartMenu()
    {
        using var picker = new StartMenuPicker(TrayText.Get("AppsPickerStart"), ShellLinks.StartMenu());
        if (picker.ShowDialog(this) != DialogResult.OK || picker.Selected is not { } item)
        {
            return;
        }
        var processPath = ShellLinks.ResolveExecutable(item.Path);
        Add(item.Name, AppLaunchKind.Shortcut, item.Path, processPath);
    }

    private void AddPackagedApp()
    {
        IReadOnlyList<ShellLinks.StartMenuItem> apps;
        Cursor = Cursors.WaitCursor;
        try
        {
            apps = ShellLinks.PackagedApps();
        }
        finally
        {
            Cursor = Cursors.Default;
        }
        if (apps.Count == 0)
        {
            MessageBox.Show(this, TrayText.Get("AppsNoStoreApps"), "Nexus PC", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var picker = new StartMenuPicker(TrayText.Get("AppsPickerStore"), apps);
        if (picker.ShowDialog(this) != DialogResult.OK || picker.Selected is not { } item)
        {
            return;
        }
        // Started by AppUserModelID through the shell; no .exe is known, so closing from the phone is not offered.
        Add(item.Name, AppLaunchKind.PackagedApp, item.Path, processPath: null);
    }

    private void AddExecutable()
    {
        using var dialog = new OpenFileDialog { Filter = TrayText.Get("AppsExeFilter"), Title = TrayText.Get("AppsExeTitle") };
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

/// <summary>Searchable list of Start menu shortcuts or Store apps.</summary>
internal sealed class StartMenuPicker : Form
{
    private readonly IReadOnlyList<ShellLinks.StartMenuItem> _all;
    private readonly TextBox _search = new() { Dock = DockStyle.Top, PlaceholderText = TrayText.Get("AppsSearch") };
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false };

    public StartMenuPicker(string title, IReadOnlyList<ShellLinks.StartMenuItem> items)
    {
        _all = items;
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(420, 520);
        var ok = new Button { Text = TrayText.Get("AppsAdd"), Dock = DockStyle.Bottom, Height = 38, DialogResult = DialogResult.OK };
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
