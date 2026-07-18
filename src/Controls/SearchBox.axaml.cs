using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace FH6RB.Controls;

public partial class SearchBox : UserControl
{
    public static readonly StyledProperty<string> SearchTextProperty =
        AvaloniaProperty.Register<SearchBox, string>(nameof(SearchText), defaultBindingMode: BindingMode.TwoWay);

    private const double ClosedWidth = 32;
    private const double ExpandedWidth = 300;

    private bool _isOpen;
    private bool _syncing;
    private bool _suppressOpen;
    private DispatcherTimer? _suppressTimer;

    public SearchBox()
    {
        InitializeComponent();
    }

    public string SearchText
    {
        get => (string)GetValue(SearchTextProperty);
        set => SetValue(SearchTextProperty, value);
    }

    private void OnButtonClick(object? sender, RoutedEventArgs e)
    {
        if (_isOpen)
        {
            CloseSearch();
        }
        else
        {
            if (_suppressOpen) return;
            Expand();
        }
    }

    private void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        if (string.IsNullOrEmpty(SearchText))
        {
            CloseSearch();
        }
        else
        {
            Shell.Focus();
        }
        e.Handled = true;
    }

    private void OnQueryLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_isOpen && string.IsNullOrEmpty(SearchText)) CloseSearch();
    }

    private void CloseSearch()
    {
        if (!_isOpen) return;
        SearchText = "";
        Collapse();
        _suppressOpen = true;
        _suppressTimer?.Stop();
        _suppressTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Normal, (_, _) =>
        {
            _suppressOpen = false;
            _suppressTimer?.Stop();
        });
        _suppressTimer.Start();
    }

    private void OnQueryTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_syncing || Query is null) return;

        var t = Query.Text ?? "";
        if (!string.Equals(SearchText, t, StringComparison.Ordinal))
        {
            _syncing = true;
            try { SetCurrentValue(SearchTextProperty, t); }
            finally { _syncing = false; }
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != SearchTextProperty) return;

        if (!_syncing && Query is not null
            && !string.Equals(Query.Text ?? "", SearchText ?? "", StringComparison.Ordinal))
        {
            _syncing = true;
            try { Query.Text = SearchText ?? ""; }
            finally { _syncing = false; }
        }
    }

    private void Expand()
    {
        _isOpen = true;
        Width = ExpandedWidth;
        Shell.Classes.Set("open", true);
        Query.IsVisible = true;
        IconIdle.IsVisible = false;
        IconActive.IsVisible = true;
        Dispatcher.UIThread.Post(async () =>
        {
            await Task.Delay(170);
            if (_isOpen) Query?.Focus();
        }, DispatcherPriority.Render);
    }

    private void Collapse()
    {
        _isOpen = false;
        Width = ClosedWidth;
        Shell.Classes.Set("open", false);
        Query.IsVisible = false;
        IconIdle.IsVisible = true;
        IconActive.IsVisible = false;
    }
}
