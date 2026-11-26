using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Globalization;
using System.Windows.Forms;
using System.Drawing;

class Entry {
    public string Old, New;
    public DateTime Created, Modified;
    public long Size;
}

static class Renamer {
    static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;
    static readonly string[] Formats = { ".png", ".jpg", ".jpeg", ".mp4", ".gif" };
    public static string Journal(string folder) { return Path.Combine(folder, ".rename-recovery"); }
    public static Entry[] Plan(string folder, bool all) {
        return Directory.GetFiles(folder).Select(p => new FileInfo(p))
            .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0 &&
                (all || Formats.Contains(f.Extension, Names)))
            .OrderBy(f => f.CreationTimeUtc).ThenBy(f => f.Name, Names)
            .Select((f, i) => new Entry { Old = f.Name, New = (i + 1).ToString(CultureInfo.InvariantCulture) + f.Extension,
                 Created = f.CreationTimeUtc, Modified = f.LastWriteTimeUtc, Size = f.Length }).ToArray();
    }
    static bool Exists(string p) { return File.Exists(p) || Directory.Exists(p); }
    static string Slot(string journal, int i) { return Path.Combine(journal, i.ToString(CultureInfo.InvariantCulture)); }
    static string Manifest(string journal) { return Path.Combine(journal, "names.txt"); }
    static void Mark(string journal, string name) {
        using (var stream = new FileStream(Path.Combine(journal, name), FileMode.CreateNew)) stream.Flush(true);
    }
    public static Entry[] RandomNames(IEnumerable<string> names, Entry[] entries) {
        var occupied = new HashSet<string>(names, Names);
        const string letters = "abcdefghijklmnopqrstuvwxyz";
        const string symbols = "_-~!";
        using (var random = System.Security.Cryptography.RandomNumberGenerator.Create()) {
            var bytes = new byte[14];
            foreach (var entry in entries) {
                string name;
                do {
                    random.GetBytes(bytes);
                    var stem = new char[bytes.Length];
                    for (int i = 0; i < stem.Length; i++)
                        stem[i] = i == 4 || i == 9 ? symbols[bytes[i] % symbols.Length] : letters[bytes[i] % letters.Length];
                    name = new string(stem) + Path.GetExtension(entry.Old);
                } while (!occupied.Add(name));
                entry.New = name;
            }
        }
        return entries;
    }
    public static Entry[] Number(IEnumerable<Entry> entries) {
        return entries.Select((e, i) => new Entry { Old = e.Old,
            New = (i + 1).ToString(CultureInfo.InvariantCulture) + Path.GetExtension(e.Old),
            Created = e.Created, Modified = e.Modified, Size = e.Size }).ToArray();
    }
    public static void Apply(string folder, bool all, Entry[] expected, bool subset, bool random, Action moved = null) {
        string journal = Journal(folder);
        if (Exists(journal)) throw new IOException("Сначала восстановите предыдущую операцию.");
        var actual = Plan(folder, all);
        if (subset) {
            var selected = new HashSet<string>(expected.Select(e => e.Old), Names);
            actual = Number(actual.Where(e => selected.Contains(e.Old)));
        }
        if (actual.Length != expected.Length || actual.Where((e, i) => e.Old != expected[i].Old ||
             (!random && e.New != expected[i].New) || e.Created != expected[i].Created ||
             e.Modified != expected[i].Modified || e.Size != expected[i].Size).Any())
            throw new IOException("Содержимое папки изменилось. Обновите предпросмотр.");
        var changes = expected.Where(e => e.Old != e.New).ToArray();
        var sources = new HashSet<string>(changes.Select(e => e.Old), Names);
        foreach (var e in changes)
            if (Exists(Path.Combine(folder, e.New)) && !sources.Contains(e.New))
                throw new IOException("Имя уже занято: " + e.New);
        if (changes.Length == 0) return;
        Directory.CreateDirectory(journal);
        try {
            using (var stream = new FileStream(Manifest(journal), FileMode.CreateNew))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false, true))) {
                foreach (var e in changes) { writer.WriteLine(e.Old); writer.WriteLine(e.New); }
                writer.Flush();
                stream.Flush(true);
            }
            Mark(journal, "ready");
            for (int i = 0; i < changes.Length; i++)
                { File.Move(Path.Combine(folder, changes[i].Old), Slot(journal, i)); if (moved != null) moved(); }
            Mark(journal, "placing");
            for (int i = 0; i < changes.Length; i++)
                { File.Move(Slot(journal, i), Path.Combine(folder, changes[i].New)); if (moved != null) moved(); }
            Mark(journal, "done");
        } catch (Exception error) {
            try { Recover(folder); }
            catch (Exception recovery) {
                throw new IOException(error.Message + "\nВосстановление остановилось: " + recovery.Message);
            }
            throw;
        }
        Recover(folder);
    }
    public static void Recover(string folder) {
        string journal = Journal(folder);
        if (!Directory.Exists(journal)) return;
        if ((File.GetAttributes(journal) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Папка восстановления является ссылкой.");
        if (!File.Exists(Manifest(journal)))
            throw new IOException("Журнал восстановления отсутствует: " + journal);
        if (File.Exists(Path.Combine(journal, "ready")) && !File.Exists(Path.Combine(journal, "done"))) {
            var names = File.ReadAllLines(Manifest(journal));
            if (names.Length == 0 || names.Length % 2 != 0 ||
                names.Any(n => n.Length == 0 || n == "." || n == ".." ||
                    n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                throw new IOException("Повреждён журнал восстановления.");
            if (File.Exists(Path.Combine(journal, "placing")) && !File.Exists(Path.Combine(journal, "restoring"))) {
                for (int i = 0; i < names.Length / 2; i++)
                    if (!File.Exists(Slot(journal, i)))
                        File.Move(Path.Combine(folder, names[2*i+1]), Slot(journal, i));
                Mark(journal, "restoring");
            }
            for (int i = 0; i < names.Length / 2; i++)
                if (File.Exists(Slot(journal, i)))
                    File.Move(Slot(journal, i), Path.Combine(folder, names[2*i]));
            Mark(journal, "done");
        }
        // Rename the finished journal before deleting metadata so interrupted cleanup cannot block a new run.
        if (Directory.GetFiles(journal).Any(p => !new[] { "names.txt", "ready", "placing", "restoring", "done" }.Contains(Path.GetFileName(p))) ||
            Directory.GetDirectories(journal).Length != 0)
            throw new IOException("В папке восстановления остались файлы: " + journal);
        string finished = journal + "-" + Guid.NewGuid().ToString("N");
        Directory.Move(journal, finished);
        try {
            foreach (string p in Directory.GetFiles(finished)) File.Delete(p);
            Directory.Delete(finished);
        } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    public static string Backup(string folder, string destination, Entry[] selected) {
        var source = new DirectoryInfo(folder);
        if ((source.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Выберите исходную папку без перенаправления.");
        string root = source.FullName.TrimEnd('\\') + "\\";
        var parent = new DirectoryInfo(destination);
        for (var p = parent; p != null; p = p.Parent) {
            if (Names.Equals(p.FullName.TrimEnd('\\') + "\\", root))
                throw new IOException("Выберите место вне исходной папки.");
            if ((p.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Выберите место без перенаправления.");
        }
        if (Exists(Journal(folder))) throw new IOException("Сначала восстановите предыдущую операцию.");
        string name = (source.Parent == null ? "Диск" : source.Name) + " - копия " +
            DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + " " + Guid.NewGuid().ToString("N").Substring(0, 8);
        string target = Path.Combine(parent.FullName, name);
        string pending = target + " (не завершена)";
        Directory.CreateDirectory(pending);
        try {
            if (selected == null) CopyTree(source, pending);
            else foreach (var e in selected) CopyFile(new FileInfo(Path.Combine(folder, e.Old)), pending);
            Directory.Move(pending, target);
        } catch (Exception ex) { throw new IOException(ex.Message + "\nНезавершённая копия: " + pending, ex); }
        return target;
    }
    static void CopyTree(DirectoryInfo source, string destination) {
        if ((source.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Обнаружена ссылка: " + source.FullName);
        Directory.CreateDirectory(destination);
        foreach (var file in source.GetFiles()) CopyFile(file, destination);
        foreach (var child in source.GetDirectories()) CopyTree(child, Path.Combine(destination, child.Name));
        Directory.SetCreationTimeUtc(destination, source.CreationTimeUtc);
        Directory.SetLastWriteTimeUtc(destination, source.LastWriteTimeUtc);
    }
    static void CopyFile(FileInfo file, string destination) {
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Обнаружена ссылка: " + file.FullName);
        string output = Path.Combine(destination, file.Name);
        using (var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var copy = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
            input.CopyTo(copy);
            copy.Flush(true);
        }
        File.SetCreationTimeUtc(output, file.CreationTimeUtc);
        File.SetLastWriteTimeUtc(output, file.LastWriteTimeUtc);
    }
}

class MainWindow : Form {
    readonly Button choose = new Button { Text = "Выбрать папку" };
    readonly Button selectAll = new Button { Text = "Выбрать все файлы" };
    readonly Button refresh = new Button { Text = "Обновить" };
    readonly Button apply = new Button { Text = "Переименовать" };
    readonly Button backup = new Button { Text = "Сделать копию" };
    readonly Button recover = new Button { Text = "Восстановить имена" };
    readonly ComboBox filter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 255 };
    readonly ComboBox mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
    readonly ComboBox scope = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
    readonly Label path = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, Text = "Выберите папку с файлами" };
    readonly Timer progressTimer = new Timer { Interval = 90 };
    readonly Panel spinner = new Panel { Dock = DockStyle.Fill, Visible = false };
    readonly System.Diagnostics.Stopwatch renameClock = new System.Diagnostics.Stopwatch();
    int movesDone, movesTotal, spinnerAngle;
    readonly Label renameStatus = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.DarkGreen };
    readonly Label status = new Label { Dock = DockStyle.Fill, AutoEllipsis = true };
    readonly ListView list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
        VirtualMode = true, MultiSelect = true, HideSelection = false };
    readonly ConverterPanel converter = new ConverterPanel();
    string sharedFolder;
    bool renamePreviewChanged;
    string folder;
    Entry[] entries = new Entry[0];
    bool busy, valid, closeRequested;
    Entry[] randomEntries;
    string[] occupiedNames = new string[0];
    Dictionary<string, string> selectedNames;
    bool selectingAll;
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct ListItemState {
        public uint Mask;
        public int Item, SubItem;
        public uint State, StateMask;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr window, int message, IntPtr item, ref ListItemState state);
    Entry[] ActiveEntries { get {
        if (!RandomMode) return entries;
        if (randomEntries == null) randomEntries = Renamer.RandomNames(occupiedNames, Renamer.Number(entries));
        return randomEntries;
    } }
    bool selectionUpdateQueued;
    Entry[] SelectedPlan() {
        var active = ActiveEntries;
        var selected = list.SelectedIndices.Cast<int>().OrderBy(i => i).Select(i => active[i]);
        return RandomMode ? selected.ToArray() : Renamer.Number(selected);
    }
    void UpdateSelection() {
        if (!valid || busy || selectionUpdateQueued) return;
        selectionUpdateQueued = true;
        BeginInvoke((Action)(() => {
            selectionUpdateQueued = false;
            if (!valid || busy) return;
            var selected = SelectedPlan();
            selectedNames = selected.Length == 0 ? null : selected.ToDictionary(e => e.Old, e => e.New);
            if (entries.Length > 0) list.RedrawItems(0, entries.Length - 1, true);
            status.Text = selected.Length == 0 ? "Файлов: " + entries.Length + " · Shift выделяет диапазон" :
                "Выделено файлов: " + selected.Length + " из " + entries.Length + (RandomMode ? " · Уникальные имена" : " · Нумерация с 1");
            EnableControls();
        }));
    }
    bool RandomMode { get { return mode.SelectedIndex == 1; } }
    bool All { get { return filter.SelectedIndex == 1; } }
    public MainWindow() {
        Text = "";
        using (var icon = typeof(MainWindow).Assembly.GetManifestResourceStream("Rename.ico"))
            Icon = new Icon(icon, SystemInformation.SmallIconSize);
        Font = new Font("Segoe UI", 10);
        AutoScaleMode = AutoScaleMode.Font;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(730, 440);
        ClientSize = new Size(900, 570);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(16) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var renameTab = new TabPage("Переименование");
        var convertTab = new TabPage("Конвертация");
        renameTab.Controls.Add(layout);
        convertTab.Controls.Add(converter);
        tabs.TabPages.AddRange(new[] { renameTab, convertTab });
        Controls.Add(tabs);
        tabs.Selecting += (s, e) => { if (busy || converter.Busy) e.Cancel = true; };
        tabs.Selected += async (s, e) => {
            if (e.TabPage == renameTab && renamePreviewChanged) {
                renamePreviewChanged = false;
                await LoadPlan(null);
                return;
            }
            if (e.TabPage != convertTab || folder == null || folder == sharedFolder) return;
            sharedFolder = folder;
            await converter.LoadFolder(folder);
        };
        converter.FilesChanged += changedFolder => {
            if (string.Equals(changedFolder, folder, StringComparison.OrdinalIgnoreCase)) renamePreviewChanged = true;
        };
        converter.Finished += () => { if (closeRequested) Close(); };
        filter.Items.AddRange(new object[] { "PNG, JPG, JPEG, MP4, GIF", "Все типы файлов" });
        scope.Items.AddRange(new object[] { "Папка целиком", "Выделение или список" });
        mode.Items.AddRange(new object[] { "По дате создания (с 1)", "Уникальные буквы и символы" });
        filter.SelectedIndex = scope.SelectedIndex = mode.SelectedIndex = 0;
        foreach (var button in new[] { choose, refresh, selectAll, apply, backup, recover }) {
            button.AutoSize = true;
            button.Padding = new Padding(8, 3, 8, 3);
        }
        var top = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        top.Controls.AddRange(new Control[] { choose, filter, selectAll, refresh });
        var bottom = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
        bottom.Controls.AddRange(new Control[] { apply, scope, backup, recover });
        layout.RowCount = 6;
        layout.RowStyles.Insert(1, new RowStyle(SizeType.AutoSize));
        var modeRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        modeRow.Controls.Add(new Label { Text = "Режим имён", AutoSize = true, Margin = new Padding(3, 7, 8, 3) });
        modeRow.Controls.Add(mode);
        layout.Controls.Add(top); layout.Controls.Add(modeRow); layout.Controls.Add(path); layout.Controls.Add(list);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 26));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(status, 0, 0);
        renameStatus.AutoSize = true;
        footer.Controls.Add(spinner, 1, 0);
        footer.Controls.Add(renameStatus, 2, 0);
        spinner.Paint += (s, e) => {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var pen = new Pen(Color.DarkGreen, 2)) {
                pen.StartCap = pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                e.Graphics.DrawArc(pen, 3, (spinner.Height - 14) / 2, 14, 14, spinnerAngle, 265);
            }
        };
        progressTimer.Tick += (s, e) => {
            spinnerAngle = (spinnerAngle + 36) % 360;
            spinner.Invalidate();
            int done = System.Threading.Volatile.Read(ref movesDone);
            if (done == 0 || renameClock.ElapsedMilliseconds < 500) return;
            if (done >= movesTotal) {
                renameStatus.Text = "Выполняется… · Завершение";
                return;
            }
            double seconds = Math.Ceiling(renameClock.Elapsed.TotalSeconds * (movesTotal - done) / done);
            string remaining = seconds < 60 ? seconds + " сек." : Math.Ceiling(seconds / 60) + " мин.";
            renameStatus.Text = "Выполняется… · Осталось ≈ " + remaining;
        };
        Disposed += (s, e) => progressTimer.Dispose();
        layout.Controls.Add(bottom); layout.Controls.Add(footer);
        list.Columns.Add("Текущее имя", 340); list.Columns.Add("Новое имя", 250); list.Columns.Add("Дата создания", 200);
        list.ItemSelectionChanged += (s, e) => UpdateSelection();
        list.VirtualItemsSelectionRangeChanged += (s, e) => UpdateSelection();
        list.RetrieveVirtualItem += (s, e) => {
            var item = ActiveEntries[e.ItemIndex];
            string proposed = item.New;
            if (selectedNames != null && !selectedNames.TryGetValue(item.Old, out proposed)) proposed = "";
            e.Item = new ListViewItem(new[] { item.Old, proposed, item.Created.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss") });
        };
        choose.Click += async (s, e) => {
            using (var dialog = new FolderBrowserDialog { Description = "Выберите папку с файлами", ShowNewFolderButton = false }) {
                if (folder != null) dialog.SelectedPath = folder;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                folder = dialog.SelectedPath;
                path.Text = folder;
            }
            await LoadPlan(null);
        };
        selectAll.Click += async (s, e) => {
            if (!All) {
                selectingAll = true;
                try { filter.SelectedIndex = 1; }
                finally { selectingAll = false; }
                await LoadPlan(null);
            }
            if (!valid || IsDisposed || entries.Length == 0) return;
            var state = new ListItemState { State = 2, StateMask = 2 };
            SendMessage(list.Handle, 0x102B, new IntPtr(-1), ref state);
            list.Focus();
            UpdateSelection();
        };
        refresh.Click += async (s, e) => await LoadPlan(null);
        scope.SelectedIndexChanged += (s, e) => EnableControls();
        filter.SelectedIndexChanged += async (s, e) => { if (folder != null && !selectingAll) await LoadPlan(null); };
        mode.SelectedIndexChanged += (s, e) => UpdateSelection();
        apply.Click += async (s, e) => {
            var selected = SelectedPlan();
            bool subset = selected.Length > 0;
            var plan = subset ? selected : ActiveEntries;
            bool all = All, random = RandomMode;
            if (MessageBox.Show(this, "Переименовать файлы по предпросмотру? Файлов: " + plan.Length, Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            movesDone = 0;
            movesTotal = 2 * plan.Count(entry => entry.Old != entry.New);
            await LoadPlan(() => Renamer.Apply(folder, all, plan, subset, random,
                () => System.Threading.Interlocked.Increment(ref movesDone)), "Переименование завершено.", true);
        };
        recover.Click += async (s, e) => await LoadPlan(() => Renamer.Recover(folder), "Восстановление завершено.");
        backup.Click += async (s, e) => {
            using (var dialog = new FolderBrowserDialog { Description = "Выберите место для резервной копии" }) {
                var parent = Directory.GetParent(folder);
                if (parent != null) dialog.SelectedPath = parent.FullName;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                string destination = dialog.SelectedPath;
                var range = SelectedPlan();
                var selected = scope.SelectedIndex == 0 ? null : (range.Length == 0 ? entries : range);
                await Work(async () => {
                    string result = await Task.Run(() => Renamer.Backup(folder, destination, selected));
                    status.Text = "Резервная копия создана.";
                    if (!closeRequested) MessageBox.Show(this, result, "Резервная копия создана");
                });
            }
        };
        FormClosing += (s, e) => {
            if ((!busy && !converter.Busy) || e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true;
            closeRequested = true;
            status.Text = "Окно закроется после завершения операции…";
        };
        EnableControls();
    }
    void EnableControls() {
        bool selected = folder != null;
        bool pending = selected && Directory.Exists(Renamer.Journal(folder));
        choose.Enabled = filter.Enabled = mode.Enabled = !busy;
        refresh.Enabled = !busy && selected;
        selectAll.Enabled = !busy && selected && (!All || valid && entries.Length > list.SelectedIndices.Count);
        scope.Enabled = !busy && selected && !pending;
        backup.Enabled = scope.Enabled && (scope.SelectedIndex == 0 || valid && entries.Length > 0);
        recover.Visible = pending;
        recover.Enabled = !busy && pending;
        apply.Enabled = !busy && !pending && valid &&
            (list.SelectedIndices.Count == 0 ? ActiveEntries : SelectedPlan()).Any(e => e.Old != e.New);
    }
    async Task Work(Func<Task> work) {
        busy = true;
        status.Text = "Выполняется…";
        EnableControls();
        try { await work(); }
        catch (Exception ex) {
            status.Text = "Операция остановлена.";
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally {
            busy = false;
            if (closeRequested) Close();
            else EnableControls();
        }
    }
    async Task LoadPlan(Action action, string message = null, bool renaming = false) {
        bool all = All, random = RandomMode;
        valid = false;
        if (renaming) {
            renameStatus.ForeColor = Color.DarkGreen;
            renameStatus.Text = "Выполняется… · Оценка времени";
            spinner.Visible = true;
            renameClock.Restart();
            progressTimer.Start();
        } else if (action == null) renameStatus.Text = "";
        await Work(async () => {
            Exception failure = null;
            try { if (action != null) await Task.Run(action); }
            catch (Exception ex) { failure = ex; }
            if (renaming) {
                progressTimer.Stop();
                renameClock.Stop();
                spinner.Visible = false;
                renameStatus.ForeColor = failure == null ? Color.DarkGreen : Color.Firebrick;
                renameStatus.Text = failure == null ? "Выполнено" : "Ошибка";
            }
            if (closeRequested) {
                if (failure != null) throw failure;
                return;
            }
            list.SelectedIndices.Clear();
            selectedNames = null;
            list.VirtualListSize = 0;
            entries = await Task.Run(() => {
                var plan = Renamer.Plan(folder, all);
                occupiedNames = Directory.GetFileSystemEntries(folder).Select(Path.GetFileName).ToArray();
                return plan;
            });
            randomEntries = null;
            list.VirtualListSize = entries.Length;
            valid = true;
            status.Text = Directory.Exists(Renamer.Journal(folder)) ? "Требуется восстановление предыдущей операции." :
                (message == null ? "" : message + " ") + "Файлов: " + entries.Length + (random ? " · Уникальные имена" : " · Сначала самые старые · Нумерация с 1");
            if (failure != null) throw failure;
        });
    }
    [STAThread]
    static void Main() {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using (var mutex = new System.Threading.Mutex(false, @"Local\FileNumberingApp-D82B5E16")) {
            try {
                if (!mutex.WaitOne(0)) {
                    MessageBox.Show("Приложение уже открыто.", "Нумерация файлов");
                    return;
                }
            } catch (System.Threading.AbandonedMutexException) { }
            try { Application.Run(new MainWindow()); }
            finally { mutex.ReleaseMutex(); }
        }
    }
}

















