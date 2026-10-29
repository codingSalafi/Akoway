using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using VoiceKeyboard.Core;
using VoiceKeyboard.Models;
using VoiceKeyboard.Native;

namespace VoiceKeyboard.UI;

/// <summary>
/// Floating, draggable pill overlay that shows the current dictation state.
///
/// Behaviour:
///   • Appears (fade in) when the hotkey is pressed.
///   • Updates its appearance as dictation progresses:
///       idle → listening → processing → writing → idle  (error shows a message below)
///   • Can be dragged anywhere on screen.
///   • Clicking the pill toggles dictation.
///   • The close button cancels any recording without typing anything, then hides.
/// </summary>
public partial class OverlayWindow : Window
{
    private DictationController? _dictation;

    // Drag tracking
    private Point _mouseDownPos;
    private bool _isDragging;
    private const double DragThreshold = 5.0;

    public OverlayWindow()
    {
        InitializeComponent();
    }

    // ── Non-activating window (keeps focus in the target app) ─────────────────

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE,
                                    exStyle | NativeMethods.WS_EX_NOACTIVATE);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void SetController(DictationController dictation)
    {
        _dictation = dictation;
    }

    /// <summary>Show overlay and optionally reset to idle visual.</summary>
    public void ShowOverlay()
    {
        if (!IsVisible) Show();

        var fadeIn = (Storyboard)Resources["FadeIn"];
        fadeIn.Begin(this);
    }

    public void HideOverlay() => FadeOut();

    /// <param name="message">For <see cref="DictationStatus.Error"/>: what went wrong.</param>
    public void UpdateState(DictationStatus status, string? message = null)
    {
        Dispatcher.Invoke(() =>
        {
            StopAnimations();
            ErrorLabel.Visibility = Visibility.Collapsed;

            switch (status)
            {
                case DictationStatus.Idle:
                    ApplyMicVisual(BgIdle, CircleIdle, IconOnIdle);
                    break;

                case DictationStatus.Listening:
                    ApplyMicVisual(BgActive, CircleActive, IconOnActive);
                    RecordDot.Visibility = Visibility.Visible;
                    ((Storyboard)Resources["PulseAnim"]).Begin(this, true);
                    break;

                case DictationStatus.Processing:
                    ShowIcon(ProcessingIcon, BgActive, CircleActive);
                    ((Storyboard)Resources["SpinAnim"]).Begin(this, true);
                    break;

                case DictationStatus.Injecting:
                    ShowIcon(WritingIcon, BgActive, CircleWriting);
                    break;

                case DictationStatus.Error:
                    ApplyMicVisual(BgError, CircleIdle, IconOnIdle);
                    ErrorText.Text = string.IsNullOrWhiteSpace(message) ? "Something went wrong — try again" : message;
                    ErrorLabel.Visibility = Visibility.Visible;
                    break;
            }
        });
    }

    // ── Colour helpers ────────────────────────────────────────────────────────

    private static System.Windows.Media.SolidColorBrush Brush(byte r, byte g, byte b)
        => new(System.Windows.Media.Color.FromRgb(r, g, b));

    // Colours from the Figma design
    private static readonly System.Windows.Media.SolidColorBrush BgIdle        = Brush(0xC9, 0xFE, 0x68); // lime
    private static readonly System.Windows.Media.SolidColorBrush BgActive      = Brush(0x96, 0xE6, 0x00); // stronger lime
    private static readonly System.Windows.Media.SolidColorBrush BgError       = Brush(0xE5, 0x39, 0x35); // red
    private static readonly System.Windows.Media.SolidColorBrush CircleIdle    = BgIdle;
    private static readonly System.Windows.Media.SolidColorBrush CircleActive  = Brush(0x02, 0x87, 0x00); // green
    private static readonly System.Windows.Media.SolidColorBrush CircleWriting = Brush(0x00, 0x00, 0x00);
    private static readonly System.Windows.Media.SolidColorBrush IconOnIdle    = CircleActive;
    private static readonly System.Windows.Media.SolidColorBrush IconOnActive  = Brush(0xE0, 0xFF, 0xA6); // pale lime

    // ── Visual states ─────────────────────────────────────────────────────────

    private void ApplyMicVisual(System.Windows.Media.Brush outer, System.Windows.Media.Brush circle,
                                System.Windows.Media.Brush icon)
    {
        ShowIcon(MicIcon, outer, circle);
        MicBody.Fill = icon;
        MicStand.Fill = icon;
    }

    private void ShowIcon(UIElement iconToShow, System.Windows.Media.Brush outer, System.Windows.Media.Brush circle)
    {
        OuterPill.Background = outer;
        MicCircle.Fill = circle;
        foreach (var icon in new UIElement[] { MicIcon, ProcessingIcon, WritingIcon })
            icon.Visibility = icon == iconToShow ? Visibility.Visible : Visibility.Collapsed;
        RecordDot.Visibility = Visibility.Collapsed;
    }

    // ── Animations ────────────────────────────────────────────────────────────

    private void StopAnimations()
    {
        foreach (var key in new[] { "PulseAnim", "SpinAnim" })
        {
            try { ((Storyboard)Resources[key]).Stop(this); }
            catch { /* may not be running */ }
        }
        RecordDot.Opacity = 1;
        SpinnerRotate.Angle = 0;
    }

    // ── Fade helpers ──────────────────────────────────────────────────────────

    private void FadeOut()
    {
        var fadeOut = (Storyboard)Resources["FadeOut"];
        fadeOut.Completed += (_, _) =>
        {
            if (Opacity < 0.05) Hide();
        };
        fadeOut.Begin(this);
    }

    // ── Drag + Click ──────────────────────────────────────────────────────────

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        _mouseDownPos = e.GetPosition(this);
        _isDragging = false;
        // Capture on the pill so MouseMove keeps firing while over it
        ((UIElement)sender).CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var el = (UIElement)sender;
        if (e.LeftButton != MouseButtonState.Pressed || !el.IsMouseCaptured) return;

        var current = e.GetPosition(this);
        var dx = current.X - _mouseDownPos.X;
        var dy = current.Y - _mouseDownPos.Y;

        if (!_isDragging && (Math.Abs(dx) > DragThreshold || Math.Abs(dy) > DragThreshold))
        {
            _isDragging = true;
            el.ReleaseMouseCapture();
            DragMove(); // hand off to Windows for smooth drag
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        ((UIElement)sender).ReleaseMouseCapture();

        if (!_isDragging)
        {
            // Click → toggle dictation (only way to start / stop)
            _dictation?.ToggleDictation();
        }

        _isDragging = false;
    }

    private void OnCloseMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // stop bubbling so the pill never calls CaptureMouse
    }

    private void OnCloseClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // stop bubbling so the pill's OnMouseUp never fires
        _dictation?.CancelDictation(); // discard any recording — nothing gets typed
        HideOverlay();
    }
}
