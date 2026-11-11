using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Cvte.Windows.Controls;

namespace FloatingX;

internal sealed partial class FloatingSettingsWindow : SafeWindow
{
    private readonly Action _openWhiteboard;
    private readonly ToolbarIcons _icons;
    private Point _dragStart;
    private ButtonOption? _dragCandidate;
    private ButtonOption? _dragged;
    private Border? _dropRow;
    private bool _dropAfter;
    private long _lastDragScroll;
    public ObservableCollection<ButtonOption> Buttons { get; } = new();
    public ImageSource? AnchorIcon => _icons.Anchor;

    internal FloatingSettingsWindow(Window owner, Action openWhiteboard)
    {
        _openWhiteboard = openWhiteboard;
        InitializeComponent();
        Owner = owner;
        _icons = new ToolbarIcons(this);
        // 复用宿主语义颜色；独立预览时使用窗口自有的后备色。
        foreach (var pair in new[] {
            ("Accent", "Brush.Accent.Normal"), ("AccentHover", "Brush.Accent.Light"),
            ("Ink", "Brush.Text.Dark"), ("Muted", "Brush.Text.Normal"),
            ("Line", "Brush.BorderBrush.Lighter") })
        {
            if (owner.TryFindResource(pair.Item2) is Brush brush) Resources[pair.Item1] = brush;
        }
        if (TryFindResource("Style.ShadowDialog") is Style style) Style = style;
        Height = Math.Min(830, SystemParameters.WorkArea.Height - 48);
        PreviewMouseLeftButtonDown += (_, _) => _dragCandidate = null;
        PreviewMouseLeftButtonUp += (_, _) => _dragCandidate = null;
        DataContext = this;
        Populate(useSavedOrder: true);
    }

    private void Populate(bool useSavedOrder)
    {
        Buttons.Clear();
        foreach (var choice in DesktopToolbar.GetButtons(useSavedOrder))
        {
            var option = new ButtonOption(choice, _icons.Get(choice), !useSavedOrder || !FloatingSettings.Current.HiddenButtons.Contains(choice.Key));
            option.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(ButtonOption.IsShown)) UpdateSummary(); };
            Buttons.Add(option);
        }
        UpdatePositions();
    }

    private void MoveUp(object sender, RoutedEventArgs args) => Move(sender, -1);
    private void MoveDown(object sender, RoutedEventArgs args) => Move(sender, 1);
    private void Move(object sender, int offset)
    {
        if (sender is not FrameworkElement { DataContext: ButtonOption option }) return;
        int index = Buttons.IndexOf(option), next = index + offset;
        if (index < 0 || next < 0 || next >= Buttons.Count) return;
        Buttons.Move(index, next);
        UpdatePositions();
    }

    private void BeginReorder(object sender, MouseButtonEventArgs args)
    {
        _dragCandidate = (sender as FrameworkElement)?.DataContext as ButtonOption;
        _dragStart = args.GetPosition(this);
    }

    private void ContinueReorder(object sender, MouseEventArgs args)
    {
        if (args.LeftButton != MouseButtonState.Pressed) { _dragCandidate = null; return; }
        if (_dragCandidate == null || _dragged != null) return;
        Point point = args.GetPosition(this);
        if (Math.Abs(point.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragged = _dragCandidate;
        _dragCandidate = null;
        try
        {
            DragDrop.DoDragDrop(this, new DataObject(typeof(ButtonOption), _dragged), DragDropEffects.Move);
        }
        finally
        {
            ClearDropMark();
            _dragged = null;
        }
        args.Handled = true;
    }

    private bool IsLocalDrag(DragEventArgs args) => _dragged != null &&
        ReferenceEquals(args.Data.GetData(typeof(ButtonOption)), _dragged);

    private void DragOverRow(object sender, DragEventArgs args)
    {
        args.Handled = true;
        args.Effects = DragDropEffects.None;
        if (!IsLocalDrag(args) || sender is not Border { DataContext: ButtonOption target } row) return;
        ClearDropMark();
        if (ReferenceEquals(target, _dragged)) return;
        _dropRow = row;
        _dropAfter = args.GetPosition(row).Y >= row.ActualHeight / 2;
        row.BorderBrush = (Brush)FindResource("Accent");
        row.BorderThickness = _dropAfter ? new Thickness(0, 0, 0, 4) : new Thickness(0, 4, 0, 0);
        args.Effects = DragDropEffects.Move;
    }

    private void DragLeaveRow(object sender, DragEventArgs args)
    {
        if (ReferenceEquals(sender, _dropRow)) ClearDropMark();
    }

    private void DropOnRow(object sender, DragEventArgs args)
    {
        args.Handled = true;
        args.Effects = DragDropEffects.None;
        if (IsLocalDrag(args) && sender is Border { DataContext: ButtonOption target } row)
        {
            ReorderAt(_dragged!, target, args.GetPosition(row).Y >= row.ActualHeight / 2);
            args.Effects = DragDropEffects.Move;
        }
        ClearDropMark();
    }

    private void ReorderAt(ButtonOption source, ButtonOption target, bool after)
    {
        int from = Buttons.IndexOf(source), to = Buttons.IndexOf(target);
        if (from < 0 || to < 0 || from == to) return;
        int insertion = to + (after ? 1 : 0);
        if (from < insertion) insertion--;
        if (from == insertion) return;
        Buttons.Move(from, insertion);
        UpdatePositions();
    }

    private void ScrollWhileDragging(object sender, DragEventArgs args)
    {
        if (!IsLocalDrag(args)) return;
        // 限制滚动频率，保持拖到列表边缘时的移动速度稳定。
        long now = Environment.TickCount64;
        if (now - _lastDragScroll < 70) return;
        double y = args.GetPosition(ToolScroll).Y;
        if (y < 32) ToolScroll.ScrollToVerticalOffset(ToolScroll.VerticalOffset - 18);
        else if (y > ToolScroll.ActualHeight - 32) ToolScroll.ScrollToVerticalOffset(ToolScroll.VerticalOffset + 18);
        else return;
        _lastDragScroll = now;
    }

    private void ClearDropMark()
    {
        if (_dropRow == null) return;
        _dropRow.BorderBrush = Brushes.Transparent;
        _dropRow.BorderThickness = new Thickness(0, 2, 0, 2);
        _dropRow = null;
    }

    private void UpdatePositions()
    {
        for (int i = 0; i < Buttons.Count; i++) Buttons[i].SetPosition(i, Buttons.Count);
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        int shown = Buttons.Count(option => option.IsShown);
        VisibleCount.Text = $"显示 {shown} / {Buttons.Count}";
        EmptyHint.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Reset(object sender, RoutedEventArgs args) => Populate(useSavedOrder: false);
    private void Cancel(object sender, RoutedEventArgs args) => Close();
    private void OpenWhiteboard(object sender, RoutedEventArgs args) { Close(); _openWhiteboard(); }
    private void Save(object sender, RoutedEventArgs args)
    {
        try
        {
            FloatingSettings.Save(Buttons.Select(option => option.Key), Buttons.Where(option => !option.IsShown).Select(option => option.Key));
            DesktopToolbar.ApplyOrder();
            Close();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "设置保存失败，请检查本机用户目录是否可写。\n" + ex.Message,
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public sealed class ButtonOption : INotifyPropertyChanged
    {
        private bool _isShown;
        public string Key { get; }
        public string Label { get; }
        public string Description { get; }
        public ImageSource Icon { get; }
        public bool CanMoveUp { get; private set; }
        public bool CanMoveDown { get; private set; }
        public bool IsShown
        {
            get => _isShown;
            set { if (_isShown == value) return; _isShown = value; Changed(nameof(IsShown)); }
        }
        internal ButtonOption(DesktopToolbar.ButtonChoice choice, ImageSource icon, bool shown)
        {
            Key = choice.Key; Label = choice.Label; Description = choice.Description;
            Icon = icon; _isShown = shown;
        }
        internal void SetPosition(int index, int count)
        {
            CanMoveUp = index > 0; CanMoveDown = index < count - 1;
            Changed(nameof(CanMoveUp)); Changed(nameof(CanMoveDown));
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
