using System.ComponentModel;
using System.Windows.Media;

namespace SideDock;

public sealed class ToolItem(ToolDefinition tool) : INotifyPropertyChanged
{
    private ImageSource? _icon;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ToolDefinition Tool { get; } = tool;

    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (ReferenceEquals(_icon, value))
            {
                return;
            }

            _icon = value;
            OnPropertyChanged(nameof(Icon));
            OnPropertyChanged(nameof(HasIcon));
        }
    }

    public bool HasIcon => Icon is not null;

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
