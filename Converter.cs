using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Media.Imaging;
using Media = System.Windows.Media;

sealed class ImageConverter : IDisposable {
    string decoderFolder;
    public static readonly HashSet<string> Extensions = new HashSet<string>(
        new[] { ".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".webp", ".web", ".bmp", ".dib",
            ".tif", ".tiff", ".ico", ".heic", ".heif", ".avif", ".jxr" }, StringComparer.OrdinalIgnoreCase);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, ref byte info, uint size);

    public string ConvertFile(string source, string extension) {
        bool jpeg = extension == ".jpg" || extension == ".jpeg";
        if (!jpeg && extension != ".png") throw new ArgumentException("Неподдерживаемый формат результата.");
        if (!Extensions.Contains(Path.GetExtension(source)))
            throw new NotSupportedException("Для конвертации нужны изображения поддерживаемых форматов.");

        string destination = Path.GetDirectoryName(source);
        // Hold read and delete access to this exact file until the verified result is in place.
        using (var handle = CreateFile(source, 0x80010000, 1, IntPtr.Zero, 3, 0x00200080, IntPtr.Zero)) {
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Файл является ссылкой.");
            using (var input = new FileStream(handle, FileAccess.Read)) {
            byte[] header = new byte[12];
            int length = input.Read(header, 0, header.Length);
            input.Position = 0;
            string signature = Encoding.ASCII.GetString(header, 0, 6);
            if (signature == "GIF87a" || signature == "GIF89a" ||
                (length == 12 && Encoding.ASCII.GetString(header, 4, 4) == "ftyp" &&
                 !new[] { ".heic", ".heif", ".avif" }.Contains(Path.GetExtension(source), StringComparer.OrdinalIgnoreCase)))
                throw new NotSupportedException("GIF и видео не поддерживаются.");
            bool webp = length == 12 && Encoding.ASCII.GetString(header, 0, 4) == "RIFF" &&
                Encoding.ASCII.GetString(header, 8, 4) == "WEBP";
            bool png = length == 12 && header[0] == 137 && Encoding.ASCII.GetString(header, 1, 3) == "PNG";
            if (png) RejectAnimatedPng(input);
            using (var decoded = webp ? DecodeWebP(input) : null) {
                var decoder = BitmapDecoder.Create(decoded ?? input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (decoder is GifBitmapDecoder) throw new NotSupportedException("GIF не поддерживается.");
                if (decoder.Frames.Count != 1)
                    throw new InvalidDataException("Изображение содержит несколько кадров или страниц.");
                bool sameEncoding = jpeg ? decoder is JpegBitmapDecoder : decoder is PngBitmapDecoder && !webp;
                if (sameEncoding && string.Equals(Path.GetExtension(source), extension, StringComparison.OrdinalIgnoreCase))
                    return null;
                BitmapFrame frame = decoder.Frames[0];
                BitmapSource pixels = frame;
                BitmapEncoder encoder = null;
                if (!sameEncoding) {
                    BitmapSource oriented = Orient(frame);
                    pixels = jpeg ? Flatten(oriented) : oriented;
                    encoder = jpeg ? (BitmapEncoder)new JpegBitmapEncoder { QualityLevel = 100 } : new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(pixels, null, null, frame.ColorContexts));
                }
                string temporary = Path.Combine(destination, "." + Guid.NewGuid().ToString("N") + ".tmp");
                bool ownsTemporary = false;
                try {
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                        ownsTemporary = true;
                        if (sameEncoding) {
                            input.Position = 0;
                            input.CopyTo(output);
                        } else encoder.Save(output);
                        output.Flush(true);
                    }
                    ValidateOutput(temporary, jpeg, pixels.PixelWidth, pixels.PixelHeight);
                    File.SetCreationTimeUtc(temporary, File.GetCreationTimeUtc(source));
                    File.SetLastWriteTimeUtc(temporary, File.GetLastWriteTimeUtc(source));
                    string stem = Path.GetFileNameWithoutExtension(source);
                    for (int index = 1; ; index++) {
                        string name = stem + (index == 1 ? "" : " (" + index.ToString(CultureInfo.InvariantCulture) + ")") + extension;
                        string target = Path.Combine(destination, name);
                        try { File.Move(temporary, target); }
                        catch (IOException) {
                            if (!File.Exists(target) && !Directory.Exists(target)) throw;
                            continue;
                        }
                        byte deleteFile = 1;
                        if (!SetFileInformationByHandle(handle, 4, ref deleteFile, 1)) {
                            var error = new Win32Exception(Marshal.GetLastWin32Error());
                            try { File.Delete(target); }
                            catch (IOException) { throw new IOException("Исходник сохранён. Проверьте результат: " + name, error); }
                            catch (UnauthorizedAccessException) { throw new IOException("Исходник сохранён. Проверьте результат: " + name, error); }
                            throw new IOException("Исходник сохранён: " + error.Message, error);
                        }
                        return name;
                    }
                } finally {
                    if (ownsTemporary && File.Exists(temporary)) File.Delete(temporary);
                }
            }
        }
    }
    }

    static void ValidateOutput(string path, bool jpeg, int width, int height) {
        using (var file = File.OpenRead(path)) {
            var decoder = BitmapDecoder.Create(file, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count != 1 || !(jpeg ? decoder is JpegBitmapDecoder : decoder is PngBitmapDecoder))
                throw new InvalidDataException("Проверка результата не пройдена.");
            var image = decoder.Frames[0];
            if (image.PixelWidth != width || image.PixelHeight != height)
                throw new InvalidDataException("Размер результата изменился.");
            int stride = checked((width * image.Format.BitsPerPixel + 7) / 8);
            const int rows = 32;
            var buffer = new byte[checked(stride * Math.Min(rows, height))];
            for (int y = 0; y < height; y += rows)
                image.CopyPixels(new System.Windows.Int32Rect(0, y, width, Math.Min(rows, height - y)), buffer, stride, 0);
        }
    }

    static void RejectAnimatedPng(Stream input) {
        input.Position = 8;
        var chunk = new byte[8];
        try {
            while (input.Read(chunk, 0, chunk.Length) == chunk.Length) {
                long length = ((long)chunk[0] << 24) | ((long)chunk[1] << 16) | ((long)chunk[2] << 8) | chunk[3];
                string type = Encoding.ASCII.GetString(chunk, 4, 4);
                if (type == "acTL") throw new InvalidDataException("Анимированный PNG пропущен.");
                if (type == "IDAT" || type == "IEND") return;
                input.Seek(length + 4, SeekOrigin.Current);
            }
        } finally { input.Position = 0; }
    }

    static BitmapSource Orient(BitmapFrame frame) {
        int orientation = 1;
        try {
            var metadata = frame.Metadata as BitmapMetadata;
            if (metadata != null) {
                var tag = metadata.GetQuery("/app1/ifd/{ushort=274}") ?? metadata.GetQuery("/ifd/{ushort=274}");
                if (tag != null) orientation = System.Convert.ToInt32(tag, CultureInfo.InvariantCulture);
            }
        } catch (NotSupportedException) { }
        Media.Transform transform;
        switch (orientation) {
            case 2: transform = new Media.ScaleTransform(-1, 1); break;
            case 3: transform = new Media.RotateTransform(180); break;
            case 4: transform = new Media.ScaleTransform(1, -1); break;
            case 5: transform = new Media.MatrixTransform(0, 1, 1, 0, 0, 0); break;
            case 6: transform = new Media.RotateTransform(90); break;
            case 7: transform = new Media.MatrixTransform(0, -1, -1, 0, 0, 0); break;
            case 8: transform = new Media.RotateTransform(270); break;
            default: return frame;
        }
        return new TransformedBitmap(frame, transform);
    }

    static BitmapSource Flatten(BitmapSource image) {
        if (image.Format == Media.PixelFormats.Bgr24 || image.Format == Media.PixelFormats.Bgr32 ||
            image.Format == Media.PixelFormats.Gray8) return image;
        var rgba = new FormatConvertedBitmap(image, Media.PixelFormats.Bgra32, null, 0);
        int inputStride = checked(image.PixelWidth * 4);
        int outputStride = checked(image.PixelWidth * 3);
        byte[] row = new byte[inputStride];
        byte[] output = new byte[checked(outputStride * image.PixelHeight)];
        for (int y = 0; y < image.PixelHeight; y++) {
            rgba.CopyPixels(new System.Windows.Int32Rect(0, y, image.PixelWidth, 1), row, inputStride, 0);
            for (int x = 0, dest = y * outputStride; x < row.Length; x += 4, dest += 3) {
                int a = row[x + 3];
                for (int c = 0; c < 3; c++)
                    output[dest + c] = (byte)((row[x + c] * a + 255 * (255 - a) + 127) / 255);
            }
        }
        return BitmapSource.Create(image.PixelWidth, image.PixelHeight, image.DpiX, image.DpiY,
            Media.PixelFormats.Bgr24, null, output, outputStride);
    }

    Stream DecodeWebP(Stream source) {
        if (decoderFolder == null) {
            decoderFolder = Path.Combine(Path.GetTempPath(), "rename-webp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(decoderFolder);
        }
        string executable = Path.Combine(decoderFolder, "dwebp.exe");
        string input = Path.Combine(decoderFolder, "input.webp");
        string output = Path.Combine(decoderFolder, "output.png");
        if (!File.Exists(executable)) {
            using (var data = typeof(ImageConverter).Assembly.GetManifestResourceStream("dwebp.exe"))
            using (var file = new FileStream(executable, FileMode.CreateNew)) data.CopyTo(file);
        }
        try {
            using (var file = new FileStream(input, FileMode.CreateNew)) source.CopyTo(file);
            var options = new ProcessStartInfo(executable, "-quiet input.webp -o output.png") {
                WorkingDirectory = decoderFolder, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true
            };
            using (var process = Process.Start(options)) {
                string errors = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new InvalidDataException(errors.IndexOf("animat", StringComparison.OrdinalIgnoreCase) >= 0
                        ? "Анимированный WebP пропущен." : "Не удалось прочитать WebP.");
            }
            return new MemoryStream(File.ReadAllBytes(output), false);
        } finally {
            File.Delete(input);
            File.Delete(output);
        }
    }

    public void Dispose() {
        if (decoderFolder == null) return;
        try {
            File.Delete(Path.Combine(decoderFolder, "dwebp.exe"));
            Directory.Delete(decoderFolder);
        } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

sealed class ConverterPanel : UserControl {
    readonly Button choose = new Button { Text = "Выбрать папку", AutoSize = true };
    readonly Button all = new Button { Text = "Выбрать все файлы", AutoSize = true };
    readonly Button convert = new Button { Text = "Конвертировать", AutoSize = true };
    readonly Button open = new Button { Text = "Открыть папку", AutoSize = true };
    readonly ComboBox format = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 105 };
    readonly Label path = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, Text = "Выберите папку с изображениями" };
    readonly Label status = new Label { Dock = DockStyle.Fill, AutoEllipsis = true };
    readonly ListView list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false };
    string folder;
    readonly Button backup = new Button { Text = "Сделать резервную копию", AutoSize = true };
    public bool Busy { get; private set; }
    public event Action<string> FilesChanged;
    public event Action Finished;

    public ConverterPanel() {
        Dock = DockStyle.Fill;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        Controls.Add(layout);
        format.Items.AddRange(new object[] { "PNG", "JPG", "JPEG" });
        format.SelectedIndex = 0;
        var top = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var supported = new Button { Text = "Форматы ⓘ", AutoSize = true };
        var hint = new ToolTip { ToolTipTitle = "Входные форматы", IsBalloon = true };
        string formats = "PNG · JPG/JPEG · JPE/JFIF · WebP (.webp/.web)\r\n" +
            "BMP/DIB · TIFF · ICO · JXR\r\n" +
            "HEIC/HEIF и AVIF — при наличии кодека Windows.";
        hint.SetToolTip(supported, formats);
        supported.Click += (s, e) => hint.Show(formats, supported, 0, supported.Height, 8500);
        Disposed += (s, e) => hint.Dispose();
        top.Controls.AddRange(new Control[] { choose, all, format, supported });
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var license = new LinkLabel { Text = "Лицензия WebP", AutoSize = true, Margin = new Padding(10, 7, 0, 0) };
        license.LinkClicked += (s, e) => {
            using (var notice = new Form { Text = "Лицензия WebP", Width = 640, Height = 530, StartPosition = FormStartPosition.CenterParent })
            using (var text = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical }) {
                text.Text = string.Join(Environment.NewLine + Environment.NewLine, new[] { "WebP-LICENSE", "WebP-PATENTS" }.Select(name => {
                    using (var reader = new StreamReader(typeof(ImageConverter).Assembly.GetManifestResourceStream(name)))
                        return reader.ReadToEnd().Replace("\n", Environment.NewLine);
                }));
                notice.Controls.Add(text);
                notice.ShowDialog(this);
            }
        };
        bottom.Controls.AddRange(new Control[] { convert, backup, open, license });
        layout.Controls.Add(top); layout.Controls.Add(path); layout.Controls.Add(list);
        layout.Controls.Add(bottom); layout.Controls.Add(status);
        list.Columns.Add("Файл", 370);
        list.Columns.Add("Результат", 440);
        choose.Click += async (s, e) => {
            using (var dialog = new FolderBrowserDialog { Description = "Выберите папку с изображениями", ShowNewFolderButton = false }) {
                if (folder != null) dialog.SelectedPath = folder;
                if (dialog.ShowDialog(this) == DialogResult.OK) await LoadFolder(dialog.SelectedPath);
            }
        };
        all.Click += (s, e) => {
            foreach (ListViewItem item in list.Items) item.Selected = true;
            list.Focus();
        };
        open.Click += (s, e) => {
            if (Directory.Exists(folder)) Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        };
        backup.Click += async (s, e) => await BackupSelection();
        convert.Click += async (s, e) => await ConvertSelection();
        list.SelectedIndexChanged += (s, e) => UpdateControls();
        UpdateControls();
    }

    public async Task LoadFolder(string source) {
        if (Busy || source == folder) return;
        await Work(async () => {
            status.Text = "Загрузка списка…";
            var files = await Task.Run(() => Directory.GetFiles(source)
                .Where(p => ImageConverter.Extensions.Contains(Path.GetExtension(p)) &&
                    (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0)
                .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase).ToArray());
            folder = source;
            path.Text = source;
            list.BeginUpdate();
            try {
                list.Items.Clear();
                list.Items.AddRange(files.Select(p => new ListViewItem(new[] { Path.GetFileName(p), "" }) { Tag = p }).ToArray());
            } finally { list.EndUpdate(); }
            status.Text = "Изображений: " + files.Length + " · Shift выделяет диапазон";
        });
    }

    async Task ConvertSelection() {
        var items = list.SelectedItems.Cast<ListViewItem>().ToArray();
        if (items.Length == 0) return;
        string extension = "." + ((string)format.SelectedItem).ToLowerInvariant();
        bool jpeg = extension != ".png";
        string warning = jpeg
            ? "При преобразовании в JPG или JPEG применяется сжатие с потерями, а прозрачные участки становятся белыми. Смена расширения уже существующего JPEG сохраняет данные изображения."
            : "PNG сохранит пиксели без потерь при сжатии. Часть метаданных может не перенестись.";
        if (MessageBox.Show(this, warning + Environment.NewLine + Environment.NewLine +
            "После проверки результата исходный файл будет заменён. При ошибке исходник сохранится. " +
            "Для сохранения оригиналов сначала сделайте резервную копию." + Environment.NewLine +
            "Продолжить? Выбрано файлов: " + items.Length, "Замена исходных файлов",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        await Work(async () => {
            int completed = 0, saved = 0, skipped = 0, errors = 0;
            status.Text = "Выполняется… 0 / " + items.Length;
            var progress = new Progress<Tuple<int, string>> (result => {
                if (!Busy) return;
                items[result.Item1].SubItems[1].Text = result.Item2;
                status.Text = "Выполняется… " + (result.Item1 + 1) + " / " + items.Length;
            });
            var report = (IProgress<Tuple<int, string>>)progress;
            var paths = items.Select(i => (string)i.Tag).ToArray();
            var replacements = new string[paths.Length];
            var results = await Task.Run(() => {
                var messages = new string[paths.Length];
                using (var converter = new ImageConverter()) {
                    for (int i = 0; i < paths.Length; i++) {
                        try {
                            string name = converter.ConvertFile(paths[i], extension);
                            if (name == null) { skipped++; messages[i] = "Уже в выбранном формате"; }
                            else { saved++; messages[i] = name; replacements[i] = name; }
                        } catch (Exception ex) {
                            errors++;
                            messages[i] = ex.Message;
                        }
                        completed++;
                        report.Report(Tuple.Create(i, messages[i]));
                    }
                }
                return messages;
            });
            for (int i = 0; i < items.Length; i++) {
                items[i].SubItems[1].Text = results[i];
                if (replacements[i] == null) continue;
                items[i].Text = replacements[i];
                items[i].Tag = Path.Combine(folder, replacements[i]);
            }
            if (saved > 0 && FilesChanged != null) FilesChanged(folder);
            status.Text = "Готово: " + saved + " · Уже в формате: " + skipped + " · Ошибок: " + errors +
                " · Обработано: " + completed;
        });
    }

    async Task BackupSelection() {
        var selected = list.SelectedItems.Cast<ListViewItem>().Select(item => new Entry { Old = item.Text }).ToArray();
        if (selected.Length == 0) return;
        using (var dialog = new FolderBrowserDialog { Description = "Выберите место для резервной копии выделенных файлов" }) {
            var parent = Directory.GetParent(folder);
            if (parent != null) dialog.SelectedPath = parent.FullName;
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            string destination = dialog.SelectedPath;
            await Work(async () => {
                status.Text = "Создаётся резервная копия…";
                string result = await Task.Run(() => Renamer.Backup(folder, destination, selected));
                status.Text = "Резервная копия создана";
                MessageBox.Show(this, result, "Резервная копия создана");
            });
        }
    }

    async Task Work(Func<Task> action) {
        Busy = true;
        UpdateControls();
        try { await action(); }
        catch (Exception ex) { status.Text = ex.Message; }
        finally {
            Busy = false;
            UpdateControls();
            if (Finished != null) Finished();
        }
    }
    void UpdateControls() {
        choose.Enabled = format.Enabled = list.Enabled = !Busy;
        all.Enabled = !Busy && list.Items.Count > 0;
        convert.Enabled = !Busy && list.SelectedItems.Count > 0;
        backup.Enabled = convert.Enabled;
        open.Enabled = !Busy && Directory.Exists(folder);
    }
}





