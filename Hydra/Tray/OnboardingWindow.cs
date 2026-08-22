using Avalonia;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Hydra.Pairing;

namespace Hydra.Tray;

internal sealed class OnboardingWindow : Window
{
    private readonly string _configPath;
    private readonly Action _restartAfterSave;
    private readonly Action _openAdvanced;
    private PairingDiscovery? _discovery;
    private readonly TextBlock _status = new() { Text = "Looking for another ScreenFuse computer…", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _peerName = new() { FontSize = 22, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _code = new() { FontSize = 42, FontWeight = FontWeight.Bold, LetterSpacing = 4 };
    private readonly TextBlock _role = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Border _candidatePanel;
    private readonly Button _connect;
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Height = 4 };
    private PairingCandidate? _candidate;
    private int _finishing;

    internal OnboardingWindow(string configPath, Action restartAfterSave, Action openAdvanced)
    {
        _configPath = Path.GetFullPath(configPath);
        _restartAfterSave = restartAfterSave;
        _openAdvanced = openAdvanced;
        Title = "Set up ScreenFuse";
        Width = 700;
        Height = 600;
        MinWidth = 600;
        MinHeight = 520;
        Icon = TrayIconImage.Create();

        _connect = new Button { Content = "Codes match — connect", IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Left };
        _connect.Classes.Add("accent");
        _connect.Click += async (_, _) => await ConnectAsync();
        var notMine = new Button { Content = "Not this computer" };
        notMine.Click += (_, _) =>
        {
            if (_candidate != null) _discovery?.IgnoreCandidate(_candidate.InstanceId);
            _candidate = null;
            if (_candidatePanel != null) _candidatePanel.IsVisible = false;
            _connect.IsEnabled = false;
            _progress.IsVisible = true;
            _status.Text = "Looking for a different ScreenFuse computer…";
        };
        var advanced = new Button { Content = "Advanced setup" };
        advanced.Click += (_, _) => { Close(); _openAdvanced(); };

        _candidatePanel = new Border
        {
            IsVisible = false,
            BorderBrush = Brushes.DimGray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18),
            Child = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "Computer found", Opacity = 0.72 },
                    _peerName,
                    new TextBlock { Text = "Check that this same code appears on both computers:" },
                    _code,
                    _role,
                    new TextBlock
                    {
                        Text = "Screen positions are imported from the display arrangement already configured in your operating systems.",
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.78,
                    },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _connect, notMine } },
                },
            },
        };

        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Margin = new Thickness(28),
            Children =
            {
                At(new StackPanel
                {
                    Spacing = 15,
                    Children =
                    {
                        new TextBlock { Text = "Install on both computers. That's it.", FontSize = 30, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                        new TextBlock
                        {
                            Text = "Open ScreenFuse on the second computer while both are connected to the same router. They will find each other automatically and start with your computer after sign-in.",
                            FontSize = 16,
                            Opacity = 0.82,
                            TextWrapping = TextWrapping.Wrap,
                        },
                        _progress,
                        _status,
                        _candidatePanel,
                    },
                }, 0),
                At(new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        advanced,
                        new TextBlock { Text = "Use only for unusual monitor or network setups.", Opacity = 0.62, VerticalAlignment = VerticalAlignment.Center },
                    },
                }, 1),
            },
        };

        Opened += async (_, _) =>
        {
            try
            {
                _discovery = new PairingDiscovery(layout: CaptureDesktopLayout());
                _discovery.CandidateFound += OnCandidate;
                _discovery.PairingCompleted += OnPairingCompleted;
                await _discovery.StartAsync();
            }
            catch (Exception ex)
            {
                _progress.IsVisible = false;
                _status.Text = $"Automatic pairing could not start: {ex.Message}";
            }
        };
        Closed += async (_, _) =>
        {
            if (_discovery != null) await _discovery.DisposeAsync();
        };
    }

    private void OnCandidate(PairingCandidate candidate) => Dispatcher.UIThread.Post(() =>
    {
        _candidate = candidate;
        _peerName.Text = candidate.Host;
        _code.Text = $"{candidate.VerificationCode[..3]} {candidate.VerificationCode[3..]}";
        _role.Text = candidate.LocalIsMaster
            ? "This computer will provide the keyboard and mouse for the desk."
            : $"{candidate.Host} will provide the keyboard and mouse for the desk.";
        _candidatePanel.IsVisible = true;
        _connect.IsEnabled = true;
        _progress.IsVisible = false;
        _status.Text = "ScreenFuse created a private encrypted desk. Approve the matching code on both computers.";
    });

    private async Task ConnectAsync()
    {
        if (_candidate == null) return;
        try
        {
            _connect.IsEnabled = false;
            _status.Text = $"Approved. Click the matching button on {_candidate.Host}…";
            await (_discovery?.ApproveAsync(_candidate) ?? Task.CompletedTask);
        }
        catch (Exception ex)
        {
            _connect.IsEnabled = true;
            _status.Text = $"Could not approve this computer: {ex.Message}";
        }
    }

    private void OnPairingCompleted(PairingCandidate candidate) =>
        Dispatcher.UIThread.Post(async () => await FinishPairingAsync(candidate));

    private async Task FinishPairingAsync(PairingCandidate candidate)
    {
        if (Interlocked.Exchange(ref _finishing, 1) != 0) return;
        try
        {
            _status.Text = "Both computers approved. Finishing setup…";
            var config = PairedDeskConfig.Create(
                candidate.LocalConfigName,
                candidate.RemoteConfigName,
                candidate.LocalIsMaster,
                candidate.DeskName,
                candidate.RelaySecret,
                candidate.LocalLayout,
                candidate.RemoteLayout);
            await NativeSettingsPersistence.SaveAsync(config, _configPath);
            _status.Text = "Connected. Enabling launch on startup…";
            var startupEnabled = await EnableStartupAsync();
            _status.Text = startupEnabled
                ? "Connected. ScreenFuse will launch automatically and is starting now…"
                : "Connected. ScreenFuse is starting now; launch on startup can be enabled from the tray.";
            if (_discovery != null) await _discovery.DisposeAsync();
            await Task.Delay(500);
            _restartAfterSave();
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _finishing, 0);
            _connect.IsEnabled = true;
            _status.Text = $"Could not finish setup: {ex.Message}";
        }
    }

    private static async Task<bool> EnableStartupAsync()
    {
        try
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine executable path.");
            var start = new ProcessStartInfo(executable, "--install") { UseShellExecute = true };
            if (OperatingSystem.IsWindows()) start.Verb = "runas";
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the startup installer.");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private PairingDesktopLayout CaptureDesktopLayout()
    {
        var ordered = Screens.All
            .Select(s => s.Bounds)
            .OrderBy(b => b.X)
            .ThenBy(b => b.Y)
            .ThenBy(b => b.Width)
            .ThenBy(b => b.Height)
            .Select(b => new PairingScreenBounds(b.X, b.Y, b.Width, b.Height))
            .ToList();
        return new PairingDesktopLayout(ordered);
    }

    private static T At<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }
}
