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
///   • Updates its appearance as dictation progresses.
///   • Fades out automatically after text is injected (returns to idle).
///   • Can be dragged anywhere on screen.
///   • Clicking the pill also toggles dictation.
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

    public void UpdateState(DictationStatus status)
    {
        Dispatcher.Invoke(() =>
        {
            switch (status)
            {
                case DictationStatus.Idle:
                    ApplyIdleVisual();
                    break;

                case DictationStatus.Listening:
                    ApplyListeningVisual();
                    break;

                case DictationStatus.Processing:
                    ApplyProcessingVisual();
                    break;

                case DictationStatus.Injecting:
                    OuterPill.Background = BgProcessing;
                    StatusText.Text = "Inserting…";
                    StatusText.Foreground = Brush(0x0D, 0x0D, 0x0D);
                    break;

                case DictationStatus.Error:
                    ApplyErrorVisual();
                    break;
            }
        });
    }

    // ── Colour helpers ────────────────────────────────────────────────────────

    private static System.Windows.Media.SolidColorBrush Brush(byte r, byte g, byte b)
        => new(System.Windows.Media.Color.FromRgb(r, g, b));

    // Outer-pill background colours that encode state (matching SVG spec)
    private static readonly System.Windows.Media.SolidColorBrush BgIdle       = Brush(0xC9, 0xFE, 0x68); // lime green
    private static readonly System.Windows.Media.SolidColorBrush BgListening  = Brush(0x06, 0x09, 0x00); // near-black
    private static readonly System.Windows.Media.SolidColorBrush BgProcessing = Brush(0x22, 0x22, 0x22); // dark charcoal
    private static readonly System.Windows.Media.SolidColorBrush BgError      = Brush(0xE5, 0x39, 0x35); // red

    // ── Visual states ─────────────────────────────────────────────────────────

    private void ApplyIdleVisual()
    {
        StopPulse();
        OuterPill.Background = BgIdle;
        StatusText.Text = "Press hotkey or click mic to start";
        StatusText.Foreground = Brush(0x0D, 0x0D, 0x0D);
        MicIdle.Visibility = Visibility.Visible;
        MicActive.Visibility = Visibility.Collapsed;
        MicProcessing.Visibility = Visibility.Collapsed;
    }

    private void ApplyListeningVisual()
    {
        OuterPill.Background = BgListening;
        StatusText.Text = "Listening… Press mic to stop.";
        StatusText.Foreground = Brush(0x0D, 0x0D, 0x0D);
        MicIdle.Visibility = Visibility.Collapsed;
        MicActive.Visibility = Visibility.Visible;
        MicProcessing.Visibility = Visibility.Collapsed;
        StartPulse();
    }

    private void ApplyProcessingVisual()
    {
        StopPulse();
        OuterPill.Background = BgProcessing;
        StatusText.Text = "Processing…";
        StatusText.Foreground = Brush(0x0D, 0x0D, 0x0D);
        MicIdle.Visibility = Visibility.Collapsed;
        MicActive.Visibility = Visibility.Collapsed;
        MicProcessing.Visibility = Visibility.Visible;
    }

    private void ApplyErrorVisual()
    {
        StopPulse();
        OuterPill.Background = BgError;
        StatusText.Text = "Error — try again";
        StatusText.Foreground = Brush(0x0D, 0x0D, 0x0D);
        MicIdle.Visibility = Visibility.Visible;
        MicActive.Visibility = Visibility.Collapsed;
        MicProcessing.Visibility = Visibility.Collapsed;
    }

    // ── Pulse animation ───────────────────────────────────────────────────────

    private void StartPulse()
    {
        var pulse = (Storyboard)Resources["PulseAnim"];
        pulse.Begin(this, true);
    }

    private void StopPulse()
    {
        try
        {
            var pulse = (Storyboard)Resources["PulseAnim"];
            pulse.Stop(this);
            RecordDot.Opacity = 1;
        }
        catch { /* may not be running */ }
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
        // Capture on the Border element so MouseMove keeps firing while over it
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
        e.Handled = true; // stop bubbling so OuterPill never calls CaptureMouse
    }

    private void OnCloseClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // stop bubbling so OuterPill's OnMouseUp never fires
        HideOverlay();
    }
}
