namespace FetchPkg.Win;

internal sealed class LinkPickerForm : Form
{
    private readonly DataGridView _grid;

    public LinkPickerForm(IReadOnlyList<BrowserLink> links)
    {
        Text = "Select update or additional content";
        Width = 1000;
        Height = 560;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            MultiSelect = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoGenerateColumns = false,
            RowHeadersVisible = false
        };
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Description",
            DataPropertyName = nameof(BrowserLink.Text),
            Width = 410
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Sony URL",
            DataPropertyName = nameof(BrowserLink.Url),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
        });
        _grid.DataSource = links.ToList();

        var ok = new Button { Text = "Add selected", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8)
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        Controls.Add(_grid);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public IReadOnlyList<BrowserLink> SelectedLinks => _grid.SelectedRows
        .Cast<DataGridViewRow>()
        .Select(row => row.DataBoundItem)
        .OfType<BrowserLink>()
        .DistinctBy(link => link.Url, StringComparer.OrdinalIgnoreCase)
        .ToArray();
}
