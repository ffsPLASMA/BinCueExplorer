namespace BinCueExplorer
{
    public partial class Form1 : Form
    {
        private int sortColumn = -1;
        private bool sortAscending = true;
        private string? loadedImagePath;
        private IReadOnlyList<IsoFileEntry> loadedFiles = Array.Empty<IsoFileEntry>();

        public Form1()
        {
            InitializeComponent();

            components ??= new System.ComponentModel.Container();
            var contextMenu = new ContextMenuStrip(components);
            var extractSelectedItem = new ToolStripMenuItem("Extract selected...");
            extractSelectedItem.Click += extractSelectedItem_Click;
            contextMenu.Items.Add(extractSelectedItem);
            contextMenu.Opening += (_, e) => e.Cancel = listView1.SelectedItems.Count == 0;
            listView1.ContextMenuStrip = contextMenu;
            listView1.MouseDown += listView1_MouseDown;
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            button2.Enabled = false;
            button3.Enabled = false;
            button5.Enabled = false;
            button6.Enabled = false;
            button7.Enabled = false;
        }

        private void button4_Click(object sender, EventArgs e)
        {
            MessageBox.Show("BinCueExplorer v1.0\n\nDeveloped by -ffs-PLASMA\n\nGithub: https://github.com/ffsPLASMA/BinCueExplorer\n\nThis application allows you to open .bin files and extract their contents.", "About BinCueExplorer", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private async void button1_Click(object sender, EventArgs e)
        {
            using var dlg = new OpenFileDialog();
            dlg.Filter = "BIN files (*.bin)|*.bin|All files (*.*)|*.*";
            dlg.InitialDirectory = AppContext.BaseDirectory;
            dlg.Multiselect = false;

            if (dlg.ShowDialog() != DialogResult.OK)
            {
                return;
            }

            button1.Enabled = false;
            try
            {
                var files = await Task.Run(() => Iso9660Reader.ReadFiles(dlg.FileName));
                loadedImagePath = dlg.FileName;
                loadedFiles = files;

                listView1.BeginUpdate();
                try
                {
                    listView1.Items.Clear();
                    foreach (var file in files.OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        var item = new ListViewItem(file.Name)
                        {
                            Tag = file,
                            ToolTipText = $"{file.Name} ({file.Size:N0} bytes)"
                        };
                        item.SubItems.Add(file.Size.ToString("N0"));
                        item.SubItems.Add(file.RecordedDate?.ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty);
                        item.SubItems.Add(file.Path);
                        listView1.Items.Add(item);
                    }

                    listView1.AutoResizeColumn(0, ColumnHeaderAutoResizeStyle.ColumnContent);
                }
                finally
                {
                    listView1.EndUpdate();
                }

                button2.Enabled = true;
                button3.Enabled = true;
                MessageBox.Show($"Found {files.Count:N0} files.", "BIN File Parsed", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or OverflowException)
            {
                MessageBox.Show($"Could not parse the BIN file:\n{ex.Message}", "Parse Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                button1.Enabled = true;
            }
        }

        private void listView1_SelectedIndexChanged(object sender, EventArgs e)
        {

        }

        private void listView1_MouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right)
            {
                return;
            }

            var clickedItem = listView1.GetItemAt(e.X, e.Y);
            if (clickedItem is not null && !clickedItem.Selected)
            {
                listView1.SelectedItems.Clear();
                clickedItem.Selected = true;
                clickedItem.Focused = true;
                listView1.Focus();
            }
        }

        private async void extractSelectedItem_Click(object? sender, EventArgs e)
        {
            var selectedFiles = listView1.SelectedItems
                .Cast<ListViewItem>()
                .Select(item => item.Tag)
                .OfType<IsoFileEntry>()
                .ToArray();

            await ExtractFilesAsync(selectedFiles);
        }

        private void listView1_ColumnClick(object sender, ColumnClickEventArgs e)
        {
            if (e.Column == sortColumn)
            {
                sortAscending = !sortAscending;
            }
            else
            {
                sortColumn = e.Column;
                sortAscending = true;
            }

            listView1.ListViewItemSorter = new ListViewItemComparer(sortColumn, sortAscending);
            listView1.Sort();
        }

        private sealed class ListViewItemComparer(int column, bool ascending) : System.Collections.IComparer
        {
            public int Compare(object? x, object? y)
            {
                if (x is not ListViewItem first || y is not ListViewItem second)
                {
                    return 0;
                }

                int result;
                if (column == 1 && first.Tag is IsoFileEntry firstFile && second.Tag is IsoFileEntry secondFile)
                {
                    result = firstFile.Size.CompareTo(secondFile.Size);
                }
                else if (column == 2 && first.Tag is IsoFileEntry firstDatedFile && second.Tag is IsoFileEntry secondDatedFile)
                {
                    result = Comparer<DateTimeOffset?>.Default.Compare(firstDatedFile.RecordedDate, secondDatedFile.RecordedDate);
                }
                else
                {
                    result = StringComparer.OrdinalIgnoreCase.Compare(
                        first.SubItems[column].Text,
                        second.SubItems[column].Text);
                }

                return ascending ? result : -result;
            }
        }

        private void button2_Click(object sender, EventArgs e)
        {
            listView1.Items.Clear();
            loadedImagePath = null;
            loadedFiles = Array.Empty<IsoFileEntry>();
            button1.Enabled = true;
            button2.Enabled = false;
            button3.Enabled = false;
            MessageBox.Show("File list cleared.", "Clear List", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private async void button3_Click(object sender, EventArgs e)
        {
            await ExtractFilesAsync(loadedFiles);
        }

        private async Task ExtractFilesAsync(IReadOnlyList<IsoFileEntry> files)
        {
            if (loadedImagePath is null || files.Count == 0)
            {
                MessageBox.Show("Open a BIN image containing files before extracting.", "Nothing to Extract", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var dlg = new FolderBrowserDialog
            {
                Description = "Select a folder to extract the file(s) into.",
                ShowNewFolderButton = true
            };

            if (dlg.ShowDialog() != DialogResult.OK)
            {
                return;
            }

            button2.Enabled = false;
            button3.Enabled = false;
            try
            {
                var imagePath = loadedImagePath;
                await Task.Run(() => Iso9660Reader.ExtractFiles(imagePath, files, dlg.SelectedPath));
                MessageBox.Show($"Extracted {files.Count:N0} file(s) to:\n{dlg.SelectedPath}", "Extraction Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or OverflowException)
            {
                MessageBox.Show($"Could not extract the file(s):\n{ex.Message}", "Extraction Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                button2.Enabled = true;
                button3.Enabled = true;
            }
        }
    }
}
