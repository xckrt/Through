using System.Windows;

namespace Tropa.Tray;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow() : this(new MainViewModel()) { }

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = _vm;
        // проверка запускается сама: открыл окно и сразу видишь картину
        Loaded += (_, _) => _vm.CheckCommand.Execute(null);
        Closed += (_, _) => _vm.Cancel();
    }
}
