using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hydra.Tray;

namespace Tests.Tray;

// Scrolling the settings page used to change whatever the pointer happened to be over. A ComboBox
// and a NumericUpDown both take the mouse wheel as input, and on the desk a monitor's picker applies
// the moment it changes — so scrolling past one switched a real monitor to another computer. It also
// explained the other half of the report: a value that "changed by itself later" was a wheel event
// nobody meant, arriving while the page moved.
public class WheelSafetyTests
{
    [AvaloniaTest]
    public void ScrollingOverAChoiceDoesNotChangeIt()
    {
        var combo = SettingsWindow.NoWheel(new ComboBox { ItemsSource = new[] { "NINOG", "Mac" }, SelectedIndex = 0 });
        var window = Show(combo);

        window.MouseWheel(Centre(combo), new Vector(0, -1));

        Assert.That(combo.SelectedIndex, Is.Zero, "a monitor must not change computer because the page scrolled");
    }

    [AvaloniaTest]
    public void ScrollingOverANumberDoesNotChangeIt()
    {
        var number = SettingsWindow.NoWheel(new NumericUpDown { Minimum = 0, Maximum = 255, Value = 15 });
        var window = Show(number);
        number.Focus();
        Dispatcher.UIThread.RunJobs();

        window.MouseWheel(Centre(number), new Vector(0, -1));
        window.MouseWheel(Centre(number), new Vector(0, 1));

        Assert.That(number.Value, Is.EqualTo(15), "an input code must not drift while reading the page");
    }

    [AvaloniaTest]
    public void ScrollingOverAChoiceStillScrollsThePage()
    {
        // Swallowing the wheel must not make the page unscrollable — the pointer spends most of its
        // time over the controls.
        var combo = SettingsWindow.NoWheel(new ComboBox { ItemsSource = new[] { "NINOG", "Mac" }, SelectedIndex = 0 });
        var tall = new StackPanel { Height = 4000, Children = { combo } };
        var scroller = new ScrollViewer { Content = tall, Height = 200, Width = 300 };
        var window = new Window { Content = scroller, Width = 320, Height = 220 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.MouseWheel(Centre(combo), new Vector(0, -1));
        Dispatcher.UIThread.RunJobs();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scroller.Offset.Y, Is.GreaterThan(0), "the page is what the wheel was meant for");
            Assert.That(combo.SelectedIndex, Is.Zero);
        }
    }

    // Proves the guard is what stops it, and that the tests above are not passing because the
    // harness never delivered a wheel event in the first place.
    //
    // A NumericUpDown spins on the wheel once it has focus, and focus is a click away — so a click
    // on an input code followed by a scroll of the page silently retunes the monitor. A ComboBox in
    // this toolkit turns out not to take the wheel at all, which is worth knowing: it means the
    // pickers were never the ones drifting, and this test is what says so.
    [AvaloniaTest]
    public void WithoutTheGuardScrollingAFocusedNumberDoesChangeIt()
    {
        var unguarded = new NumericUpDown { Minimum = 0, Maximum = 255, Value = 15 };
        var window = Show(unguarded);
        unguarded.Focus();
        Dispatcher.UIThread.RunJobs();

        window.MouseWheel(Centre(unguarded), new Vector(0, 1));

        Assert.That(unguarded.Value, Is.Not.EqualTo(15),
            "if this ever stops being true the wheel tests above prove nothing");
    }

    [AvaloniaTest]
    public void AnUntouchedChoiceIsStillChangeableByHand()
    {
        // The guard must only stop the wheel, not the control.
        var combo = SettingsWindow.NoWheel(new ComboBox { ItemsSource = new[] { "NINOG", "Mac" }, SelectedIndex = 0 });
        Show(combo);

        combo.SelectedIndex = 1;

        Assert.That(combo.SelectedIndex, Is.EqualTo(1));
    }

    private static Window Show(Control content)
    {
        var window = new Window { Content = new ScrollViewer { Content = content }, Width = 320, Height = 220 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    // In window coordinates. Bounds are relative to the parent, so using them directly aims the
    // wheel at whatever happens to be at that offset in the window — which for these tests was the
    // background, making every one of them pass without touching the control.
    private static Point Centre(Visual control)
    {
        var window = control.FindAncestorOfType<Window>()
                     ?? throw new InvalidOperationException("the control is not in a window");
        var middle = new Point(control.Bounds.Width / 2, control.Bounds.Height / 2);
        return control.TranslatePoint(middle, window)
               ?? throw new InvalidOperationException("the control is not laid out");
    }
}
