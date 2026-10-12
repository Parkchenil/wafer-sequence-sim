using System.ComponentModel;
using System.Globalization;

namespace App.WinForms;

public sealed class RecipeEditorPanel : Panel
{
    private static readonly string[] Groups = ["기본", "측정 위치", "전기 측정", "모션", "고정 대기", "핸들링", "저장", "시계"];
    private readonly RecipeEditModel _model;
    private readonly List<(PropertyDescriptor Property, Control Input)> _inputs = new();

    public RecipeEditorPanel(RecipeEditModel model)
    {
        _model = model;
        Dock = DockStyle.Fill;
        AutoScroll = true;
        BackColor = Color.FromArgb(25, 32, 43);
        var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 0, MinimumSize = new Size(280, 0), Padding = new Padding(2) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
        Controls.Add(table);
        var properties = TypeDescriptor.GetProperties(model).Cast<PropertyDescriptor>()
            .Where(p => !p.IsReadOnly && p.Name != nameof(RecipeEditModel.Recipe)).ToArray();
        foreach (var group in Groups)
        {
            var members = properties.Where(p => p.Category == group).ToArray();
            if (members.Length == 0) continue;
            AddRow(table);
            var header = new Label { Text = group, Dock = DockStyle.Fill, Height = 27, BackColor = Color.FromArgb(42, 59, 76), ForeColor = Color.WhiteSmoke, Padding = new Padding(5, 5, 0, 0), Margin = new Padding(0, 8, 0, 2) };
            table.Controls.Add(header, 0, table.RowCount - 1);
            table.SetColumnSpan(header, 2);
            foreach (var property in members)
            {
                AddRow(table);
                var label = new Label { Text = property.Description, Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Color.WhiteSmoke, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(3, 0, 3, 0) };
                table.Controls.Add(label, 0, table.RowCount - 1);
                var input = MakeInput(property);
                table.Controls.Add(input, 1, table.RowCount - 1);
                _inputs.Add((property, input));
            }
        }
    }

    private static void AddRow(TableLayoutPanel table)
    {
        table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
    }

    private Control MakeInput(PropertyDescriptor property)
    {
        if (property.PropertyType.IsEnum)
        {
            var combo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(1, 2, 1, 2) };
            combo.Items.AddRange(Enum.GetValues(property.PropertyType).Cast<object>().ToArray());
            combo.SelectedItem = property.GetValue(_model);
            combo.SelectedIndexChanged += (_, _) => { if (combo.SelectedItem is { } value) property.SetValue(_model, value); };
            return combo;
        }
        if (property.PropertyType == typeof(bool))
        {
            var check = new CheckBox { Dock = DockStyle.Fill, Checked = (bool)(property.GetValue(_model) ?? false), Margin = new Padding(4, 3, 0, 0) };
            check.CheckedChanged += (_, _) => property.SetValue(_model, check.Checked);
            return check;
        }
        return new TextBox { Dock = DockStyle.Fill, Text = Convert.ToString(property.GetValue(_model), CultureInfo.InvariantCulture) ?? "", Margin = new Padding(1, 3, 1, 2) };
    }

    public string? Commit()
    {
        foreach (var (property, input) in _inputs)
        {
            try
            {
                if (input is TextBox text)
                {
                    var converter = TypeDescriptor.GetConverter(property.PropertyType);
                    var value = converter.ConvertFromInvariantString(text.Text);
                    property.SetValue(_model, value);
                }
            }
            catch (Exception ex) { return $"{property.Name}: {ex.Message}"; }
        }
        return _model.Validate();
    }

    public void Reload()
    {
        foreach (var (property, input) in _inputs)
        {
            var value = property.GetValue(_model);
            switch (input)
            {
                case TextBox text: text.Text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""; break;
                case ComboBox combo: combo.SelectedItem = value; break;
                case CheckBox check: check.Checked = (bool)(value ?? false); break;
            }
        }
    }
}
