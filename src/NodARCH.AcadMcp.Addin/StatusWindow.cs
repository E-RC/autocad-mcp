using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NodARCH.AcadMcp.Contracts;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace NodARCH.AcadMcp.Addin;

/// <summary>
/// The window the ribbon's Status button opens (command NODARCH): whether the bridge is
/// listening, this session's details, how to connect an agent, and Start and Stop.
///
/// Modeless and owned by AutoCAD's main window, so drawing carries on around it. It follows
/// HttpBridge.StateChanged, so a start or stop shows the moment it happens, whether it came from
/// these buttons, the ribbon or a typed command.
///
/// Styled to the NodARCH WPF style guide: its palette, custom title bar, buttons, GroupBox, status
/// card and footer. The layout is XAML parsed at runtime rather than a compiled page, because
/// AutoCAD loads the add-in from its bundle folder and a parsed string needs no pack URI.
/// </summary>
internal sealed class StatusWindow : Window
{
    private static StatusWindow? _open;

    private readonly FrameworkElement _root;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly DispatcherTimer _feedbackClear = new() { Interval = TimeSpan.FromSeconds(4) };
    private string? _startError;

    internal static void ShowOrActivate()
    {
        if (_open is not null)
        {
            _open.Activate();
            return;
        }

        _open = new StatusWindow();
        AcApp.ShowModelessWindow(AcApp.MainWindow.Handle, _open, false);
    }

    private StatusWindow()
    {
        _root = (FrameworkElement)XamlReader.Parse(Layout);
        Content = _root;
        Title = "NodARCH MCP";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Find<Image>("TitleIcon").Source = RibbonSupport.Icons.Status(14);
        Find<FrameworkElement>("TitleBar").MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };
        Find<Button>("CloseBtn").Click += (_, _) => Close();
        Find<Button>("CloseFooterBtn").Click += (_, _) => Close();
        Find<Button>("StartBtn").Click += (_, _) => OnStart();
        Find<Button>("StopBtn").Click += (_, _) => OnStop();
        Find<Button>("CopyPathBtn").Click += (_, _) => Copy(AcadMcpProtocol.DefaultServerPath, "Path copied");
        Find<Button>("CopyCommandBtn").Click += (_, _) => Copy(ClaudeCodeCommand, "Command copied");
        Find<Button>("GitHubBtn").Click += (_, _) => OpenRepository();

        // The clock only moves the "running for" line along; state changes arrive as events.
        _clock.Tick += (_, _) => Refresh();
        _feedbackClear.Tick += (_, _) =>
        {
            _feedbackClear.Stop();
            Find<TextBlock>("Feedback").BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(400)));
        };

        HttpBridge.StateChanged += OnBridgeStateChanged;
        Closed += (_, _) =>
        {
            HttpBridge.StateChanged -= OnBridgeStateChanged;
            _clock.Stop();
            _feedbackClear.Stop();
            _open = null;
        };

        Refresh();
        _clock.Start();
    }

    private static string ClaudeCodeCommand =>
        $"claude mcp add --scope user nodarch-acad -- \"{AcadMcpProtocol.DefaultServerPath}\"";

    private void OnBridgeStateChanged()
    {
        if (Dispatcher.CheckAccess()) Refresh();
        else Dispatcher.BeginInvoke(Refresh);
    }

    private void OnStart()
    {
        var message = AcadMcpApp.StartBridge();
        if (HttpBridge.IsRunning)
        {
            ShowFeedback("Started");
            return;
        }

        _startError = message.Trim().Replace("NodARCH AcadMcp failed to start: ", string.Empty);
        Refresh();
    }

    private void OnStop()
    {
        AcadMcpApp.StopBridge();
        ShowFeedback("Token revoked", "TextSecondary");
    }

    private void Copy(string text, string done)
    {
        // The clipboard can be held by another program for a moment; say so rather than throw.
        try
        {
            Clipboard.SetText(text);
            ShowFeedback(done);
        }
        catch
        {
            ShowFeedback("Clipboard busy, try again", "WarningText");
        }
    }

    private void OpenRepository()
    {
        try
        {
            Process.Start(new ProcessStartInfo(AcadMcpProtocol.RepositoryUrl) { UseShellExecute = true });
        }
        catch
        {
            ShowFeedback("Could not open the browser", "WarningText");
        }
    }

    /// <summary>
    /// A short note in the status card's top right corner, after something done in this window.
    /// It fades after a few seconds; the card itself carries the lasting state.
    /// </summary>
    private void ShowFeedback(string text, string colourKey = "SuccessGreen")
    {
        var note = Find<TextBlock>("Feedback");
        note.BeginAnimation(OpacityProperty, null);   // cancel a fade still running
        note.Opacity = 1;
        note.Text = text;
        note.Foreground = Palette(colourKey);
        _feedbackClear.Stop();
        _feedbackClear.Start();
    }

    private void Refresh()
    {
        var running = HttpBridge.IsRunning;
        if (running) _startError = null;

        if (running)
        {
            var moved = HttpBridge.Port == AcadMcpProtocol.DefaultPort ? string.Empty :
                $" Port {AcadMcpProtocol.DefaultPort} was taken, so this session took {HttpBridge.Port}; the server finds it by itself.";
            ShowState("SuccessFill", $"Listening on port {HttpBridge.Port}",
                "MCP clients can drive this AutoCAD session, including run_csharp and run_lisp, which run arbitrary code." + moved);
        }
        else if (_startError is not null)
        {
            ShowState("RedAccent", "Could not start", _startError);
        }
        else
        {
            ShowState(null, "Stopped", "Nothing is listening. Start opens the bridge for this AutoCAD session only.");
        }

        var failures = AcadMcpApp.LoadFailures;
        Find<FrameworkElement>("FailCard").Visibility = failures.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        Find<TextBlock>("FailTitle").Text = failures.Count == 1
            ? "1 part of the add-in failed to load, so its tools are missing"
            : $"{failures.Count} parts of the add-in failed to load, so their tools are missing";
        Find<TextBlock>("FailDetail").Text = string.Join("\n", failures);

        ShowValue("Address", running ? AcadMcpProtocol.BaseUrl(HttpBridge.Port) : "None while stopped", muted: !running);
        ShowValue("Uptime", HttpBridge.StartedAt is { } since ? RunningFor(since) : "Not running", muted: !running);
        ShowValue("Tools", $"{Router.List().Count} registered");
        ShowValue("Architecture", AcaSupport.IsAvailable ? "Loaded" : "Not loaded. Its tools answer aca_unavailable.");
        ShowValue("Process", $"acad.exe, pid {Environment.ProcessId}");
        ShowValue("Version", AddinVersion());

        var server = AcadMcpProtocol.DefaultServerPath;
        var found = System.IO.File.Exists(server);
        ShowValue("ServerPath", found ? server : "Not found at the default location", muted: !found);
        Find<TextBlock>("ServerPath").ToolTip = server;
        Find<TextBlock>("ServerHint").Text = found
            ? "Register it as a stdio server in your MCP client. It takes no arguments and finds this session by itself."
            : $"Put the release zip's server folder at {System.IO.Path.GetDirectoryName(server)}, or register the server from wherever you keep it.";
        Find<FrameworkElement>("CopyLinks").Visibility = found ? Visibility.Visible : Visibility.Collapsed;

        Find<Button>("StartBtn").IsEnabled = !running;
        Find<Button>("StopBtn").IsEnabled = running;
    }

    /// <summary>The style guide's status card: a solid bar, the card tinted with it. Null is the neutral, idle look.</summary>
    private void ShowState(string? colourKey, string title, string detail)
    {
        var card = Find<Border>("StateCard");
        if (colourKey is null)
        {
            Find<Border>("StateBar").Background = Palette("CheckOff");
            card.Background = Palette("SurfaceAlt");
            card.BorderBrush = Palette("BorderLight");
        }
        else
        {
            var c = ((SolidColorBrush)Palette(colourKey)).Color;
            Find<Border>("StateBar").Background = Palette(colourKey);
            card.Background = new SolidColorBrush(Color.FromArgb(28, c.R, c.G, c.B));
            card.BorderBrush = new SolidColorBrush(Color.FromArgb(90, c.R, c.G, c.B));
        }

        Find<TextBlock>("StateTitle").Text = title;
        Find<TextBlock>("StateDetail").Text = detail;
    }

    private void ShowValue(string name, string text, bool muted = false)
    {
        var block = Find<TextBlock>(name);
        block.Text = text;
        block.Foreground = Palette(muted ? "TextMuted" : "TextPrimary");
    }

    private static string RunningFor(DateTime since)
    {
        var span = DateTime.Now - since;
        var length = span.TotalMinutes < 1 ? "Under a minute"
            : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min"
            : $"{(int)span.TotalHours} h {span.Minutes} min";
        return $"{length}, since {since:t}";
    }

    /// <summary>"0.3.1 (dee0b04)" from the informational version "0.3.1+dee0b04...".</summary>
    private static string AddinVersion()
    {
        var full = typeof(StatusWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var parts = full.Split('+');
        return parts.Length > 1 && parts[1].Length >= 7 ? $"{parts[0]} ({parts[1][..7]})" : parts[0];
    }

    private T Find<T>(string name) where T : class => (T)_root.FindName(name);

    private Brush Palette(string key) => (Brush)_root.FindResource(key);

    // ------------------------------------------------------------------------------- layout

    private const string Layout = """
<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      Margin="12" Width="484">
    <Grid.Resources>
        <!-- NodARCH palette (style guide section 1), the keys this window uses -->
        <SolidColorBrush x:Key="Surface"       Color="#FFFFFF"/>
        <SolidColorBrush x:Key="WindowBg"      Color="#F7F9FD"/>
        <SolidColorBrush x:Key="SurfaceAlt"    Color="#EEF1F8"/>
        <SolidColorBrush x:Key="Border"        Color="#DFE4EF"/>
        <SolidColorBrush x:Key="BorderLight"   Color="#E7EBF3"/>
        <SolidColorBrush x:Key="TextPrimary"   Color="#1E2B45"/>
        <SolidColorBrush x:Key="TextSecondary" Color="#6B7895"/>
        <SolidColorBrush x:Key="TextMuted"     Color="#97A2BC"/>
        <SolidColorBrush x:Key="CheckOff"      Color="#C3CBDD"/>
        <SolidColorBrush x:Key="Accent"        Color="#2C66D6"/>
        <SolidColorBrush x:Key="AccentHover"   Color="#2853AC"/>
        <SolidColorBrush x:Key="AccentPressed" Color="#24467F"/>
        <SolidColorBrush x:Key="SuccessGreen"  Color="#1E7A53"/>
        <SolidColorBrush x:Key="SuccessFill"   Color="#2FB67C"/>
        <SolidColorBrush x:Key="RedAccent"     Color="#E5484D"/>
        <SolidColorBrush x:Key="WarningText"   Color="#CA8A04"/>
        <!-- The status card tints of WarningText (alpha 28 and 90), and the shadow's TextPrimary -->
        <SolidColorBrush x:Key="WarningTint"   Color="#1CCA8A04"/>
        <SolidColorBrush x:Key="WarningEdge"   Color="#5ACA8A04"/>
        <Color x:Key="ShadowColor">#1E2B45</Color>

        <Style x:Key="DarkToolTip" TargetType="ToolTip">
            <Setter Property="Background" Value="{StaticResource TextPrimary}"/>
            <Setter Property="Foreground" Value="White"/>
            <Setter Property="FontSize" Value="11.5"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="ToolTip">
                        <Border Background="{TemplateBinding Background}" CornerRadius="5" Padding="8,4">
                            <ContentPresenter/>
                        </Border>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
        <Style TargetType="ToolTip" BasedOn="{StaticResource DarkToolTip}"/>

        <Style x:Key="TitleBarBtn" TargetType="Button">
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="Foreground" Value="{StaticResource TextSecondary}"/>
            <Setter Property="Width" Value="28"/>
            <Setter Property="Height" Value="24"/>
            <Setter Property="Margin" Value="1,0"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="Button">
                        <Border x:Name="Bd" Background="{TemplateBinding Background}" CornerRadius="4">
                            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="Bd" Property="Background" Value="{StaticResource BorderLight}"/>
                            </Trigger>
                            <Trigger Property="IsEnabled" Value="False">
                                <Setter Property="Opacity" Value="0.4"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <Style x:Key="CloseBtnStyle" TargetType="Button" BasedOn="{StaticResource TitleBarBtn}">
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="Button">
                        <Border x:Name="Bd" Background="{TemplateBinding Background}" CornerRadius="4">
                            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="Bd" Property="Background" Value="{StaticResource RedAccent}"/>
                                <Setter Property="Foreground" Value="White"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <Style x:Key="ModernButton" TargetType="Button">
            <Setter Property="Height" Value="30"/>
            <Setter Property="Padding" Value="14,0"/>
            <Setter Property="FontSize" Value="12"/>
            <Setter Property="Foreground" Value="{StaticResource TextPrimary}"/>
            <Setter Property="Background" Value="{StaticResource Surface}"/>
            <Setter Property="BorderBrush" Value="{StaticResource Border}"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="Button">
                        <Border x:Name="Bd" Background="{TemplateBinding Background}"
                                BorderBrush="{TemplateBinding BorderBrush}"
                                BorderThickness="{TemplateBinding BorderThickness}"
                                CornerRadius="4" Padding="{TemplateBinding Padding}">
                            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="Bd" Property="Background" Value="{StaticResource SurfaceAlt}"/>
                                <Setter TargetName="Bd" Property="BorderBrush" Value="{StaticResource CheckOff}"/>
                            </Trigger>
                            <Trigger Property="IsPressed" Value="True">
                                <Setter TargetName="Bd" Property="Background" Value="{StaticResource BorderLight}"/>
                            </Trigger>
                            <Trigger Property="IsEnabled" Value="False">
                                <Setter Property="Opacity" Value="0.5"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <Style x:Key="AccentButton" TargetType="Button">
            <Setter Property="Height" Value="30"/>
            <Setter Property="Padding" Value="18,0"/>
            <Setter Property="MinWidth" Value="100"/>
            <Setter Property="FontSize" Value="12"/>
            <Setter Property="FontWeight" Value="SemiBold"/>
            <Setter Property="Foreground" Value="White"/>
            <Setter Property="Background" Value="{StaticResource Accent}"/>
            <Setter Property="BorderThickness" Value="0"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="Button">
                        <Border x:Name="Bd" Background="{TemplateBinding Background}"
                                CornerRadius="4" Padding="{TemplateBinding Padding}">
                            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="Bd" Property="Background" Value="{StaticResource AccentHover}"/>
                            </Trigger>
                            <Trigger Property="IsPressed" Value="True">
                                <Setter TargetName="Bd" Property="Background" Value="{StaticResource AccentPressed}"/>
                            </Trigger>
                            <Trigger Property="IsEnabled" Value="False">
                                <Setter Property="Opacity" Value="0.5"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <Style x:Key="IconButton" TargetType="Button">
            <Setter Property="Width" Value="30"/>
            <Setter Property="Height" Value="30"/>
            <Setter Property="Padding" Value="0"/>
            <Setter Property="Background" Value="{StaticResource Surface}"/>
            <Setter Property="BorderBrush" Value="{StaticResource Border}"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="Button">
                        <Border x:Name="Bd" Background="{TemplateBinding Background}"
                                BorderBrush="{TemplateBinding BorderBrush}"
                                BorderThickness="{TemplateBinding BorderThickness}"
                                CornerRadius="4">
                            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="Bd" Property="Background" Value="{StaticResource SurfaceAlt}"/>
                                <Setter TargetName="Bd" Property="BorderBrush" Value="{StaticResource CheckOff}"/>
                            </Trigger>
                            <Trigger Property="IsPressed" Value="True">
                                <Setter TargetName="Bd" Property="Background" Value="{StaticResource BorderLight}"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <Style x:Key="LinkButton" TargetType="Button">
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="Foreground" Value="{StaticResource Accent}"/>
            <Setter Property="FontSize" Value="11"/>
            <Setter Property="Padding" Value="6,1"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="Button">
                        <Border Background="{TemplateBinding Background}" Padding="{TemplateBinding Padding}">
                            <ContentPresenter VerticalAlignment="Center"/>
                        </Border>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
            <Style.Triggers>
                <Trigger Property="IsMouseOver" Value="True">
                    <Setter Property="Foreground" Value="{StaticResource AccentHover}"/>
                </Trigger>
            </Style.Triggers>
        </Style>

        <Style TargetType="GroupBox">
            <Setter Property="Margin" Value="0,0,0,12"/>
            <Setter Property="Padding" Value="12"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="GroupBox">
                        <Grid>
                            <Grid.RowDefinitions>
                                <RowDefinition Height="Auto"/>
                                <RowDefinition Height="*"/>
                            </Grid.RowDefinitions>
                            <ContentPresenter Grid.Row="0" ContentSource="Header" Margin="1,0,0,4">
                                <ContentPresenter.Resources>
                                    <Style TargetType="TextBlock">
                                        <Setter Property="FontWeight" Value="Bold"/>
                                        <Setter Property="FontSize" Value="11.5"/>
                                        <Setter Property="Foreground" Value="{StaticResource TextSecondary}"/>
                                    </Style>
                                </ContentPresenter.Resources>
                            </ContentPresenter>
                            <Border Grid.Row="1" Background="{StaticResource Surface}"
                                    BorderBrush="{StaticResource Border}"
                                    BorderThickness="1" CornerRadius="5"
                                    Padding="{TemplateBinding Padding}">
                                <ContentPresenter/>
                            </Border>
                        </Grid>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <!-- A label and its value, one row of a details card -->
        <Style x:Key="Label" TargetType="TextBlock">
            <Setter Property="FontSize" Value="11.5"/>
            <Setter Property="Foreground" Value="{StaticResource TextSecondary}"/>
            <Setter Property="Margin" Value="0,0,12,8"/>
            <Setter Property="VerticalAlignment" Value="Center"/>
        </Style>
        <Style x:Key="Value" TargetType="TextBlock">
            <Setter Property="FontSize" Value="12.5"/>
            <Setter Property="Foreground" Value="{StaticResource TextPrimary}"/>
            <Setter Property="Margin" Value="0,0,0,8"/>
            <Setter Property="VerticalAlignment" Value="Center"/>
            <Setter Property="TextWrapping" Value="Wrap"/>
        </Style>
    </Grid.Resources>

    <!-- The shadow is drawn by a separate border behind the window so text is not rendered
         through the effect. -->
    <Border CornerRadius="8" Background="{StaticResource WindowBg}">
        <Border.Effect>
            <DropShadowEffect BlurRadius="16" ShadowDepth="2" Opacity="0.28" Color="{StaticResource ShadowColor}"/>
        </Border.Effect>
    </Border>

    <Border CornerRadius="8" Background="{StaticResource WindowBg}"
            BorderBrush="{StaticResource Border}" BorderThickness="1">
        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height="32"/>
                <RowDefinition Height="Auto"/>
            </Grid.RowDefinitions>

            <!-- Title bar, secondary configuration: a small fixed window, so Minimize and
                 Maximize stay visible but disabled. -->
            <Border x:Name="TitleBar" Background="{StaticResource SurfaceAlt}" CornerRadius="7,7,0,0">
                <Grid Background="Transparent">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="Auto"/>
                        <ColumnDefinition Width="*"/>
                        <ColumnDefinition Width="Auto"/>
                    </Grid.ColumnDefinitions>
                    <StackPanel Orientation="Horizontal" Margin="8,0,0,0" VerticalAlignment="Center">
                        <Image x:Name="TitleIcon" Width="14" Height="14" Margin="0,0,6,0"/>
                        <TextBlock Text="NodARCH MCP" FontSize="11" FontWeight="SemiBold"
                                   Foreground="{StaticResource TextSecondary}" VerticalAlignment="Center"/>
                    </StackPanel>
                    <StackPanel Grid.Column="2" Orientation="Horizontal" Margin="0,0,8,0" VerticalAlignment="Center">
                        <Button Style="{StaticResource TitleBarBtn}" IsEnabled="False">
                            <Path Width="12" Height="12" Stroke="{Binding Foreground, RelativeSource={RelativeSource AncestorType=Button}}"
                                  StrokeThickness="1.3" StrokeStartLineCap="Round" StrokeEndLineCap="Round"
                                  Data="M2.5,6.5 L9.5,6.5"/>
                        </Button>
                        <Button Style="{StaticResource TitleBarBtn}" IsEnabled="False">
                            <Path Width="12" Height="12" Stroke="{Binding Foreground, RelativeSource={RelativeSource AncestorType=Button}}"
                                  StrokeThickness="1.3" StrokeLineJoin="Round"
                                  Data="M2.5,2.5 H9.5 V9.5 H2.5 Z"/>
                        </Button>
                        <Button x:Name="CloseBtn" Style="{StaticResource CloseBtnStyle}" ToolTip="Close">
                            <Path Width="12" Height="12" Stroke="{Binding Foreground, RelativeSource={RelativeSource AncestorType=Button}}"
                                  StrokeThickness="1.3" StrokeStartLineCap="Round" StrokeEndLineCap="Round"
                                  Data="M3,3 L9,9 M9,3 L3,9"/>
                        </Button>
                    </StackPanel>
                </Grid>
            </Border>

            <StackPanel Grid.Row="1" Margin="16">

                <!-- Bridge state: the style guide's status card. Feedback on this window's own
                     actions sits in its top right corner and fades. -->
                <Border x:Name="StateCard" CornerRadius="5" BorderThickness="1" ClipToBounds="True" Margin="0,0,0,12">
                    <Grid>
                        <Border x:Name="StateBar" Width="4" HorizontalAlignment="Left" CornerRadius="5,0,0,5"/>
                        <StackPanel Margin="16,12">
                            <DockPanel>
                                <TextBlock x:Name="Feedback" DockPanel.Dock="Right" FontSize="11.5" FontWeight="SemiBold"
                                           VerticalAlignment="Center" Margin="12,0,0,0"/>
                                <TextBlock x:Name="StateTitle" FontSize="12.5" FontWeight="SemiBold"
                                           Foreground="{StaticResource TextPrimary}" TextWrapping="Wrap"/>
                            </DockPanel>
                            <TextBlock x:Name="StateDetail" FontSize="10.5" Margin="0,4,0,0"
                                       Foreground="{StaticResource TextSecondary}" TextWrapping="Wrap"/>
                        </StackPanel>
                    </Grid>
                </Border>

                <!-- Only when part of the add-in failed to load -->
                <Border x:Name="FailCard" CornerRadius="5" BorderThickness="1" ClipToBounds="True" Margin="0,0,0,12"
                        Background="{StaticResource WarningTint}" BorderBrush="{StaticResource WarningEdge}" Visibility="Collapsed">
                    <Grid>
                        <Border Width="4" HorizontalAlignment="Left" CornerRadius="5,0,0,5" Background="{StaticResource WarningText}"/>
                        <StackPanel Margin="16,12">
                            <TextBlock x:Name="FailTitle" FontSize="12.5" FontWeight="SemiBold"
                                       Foreground="{StaticResource TextPrimary}" TextWrapping="Wrap"/>
                            <TextBlock x:Name="FailDetail" FontSize="10.5" Margin="0,4,0,0"
                                       Foreground="{StaticResource TextSecondary}" TextWrapping="Wrap"/>
                        </StackPanel>
                    </Grid>
                </Border>

                <GroupBox Header="SESSION">
                    <Grid>
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="128"/>
                            <ColumnDefinition Width="*"/>
                        </Grid.ColumnDefinitions>
                        <Grid.RowDefinitions>
                            <RowDefinition/><RowDefinition/><RowDefinition/><RowDefinition/><RowDefinition/><RowDefinition/>
                        </Grid.RowDefinitions>
                        <TextBlock Grid.Row="0" Style="{StaticResource Label}" Text="Address"/>
                        <TextBlock Grid.Row="0" Grid.Column="1" Style="{StaticResource Value}" x:Name="Address"/>
                        <TextBlock Grid.Row="1" Style="{StaticResource Label}" Text="Running for"/>
                        <TextBlock Grid.Row="1" Grid.Column="1" Style="{StaticResource Value}" x:Name="Uptime"/>
                        <TextBlock Grid.Row="2" Style="{StaticResource Label}" Text="Tools"/>
                        <TextBlock Grid.Row="2" Grid.Column="1" Style="{StaticResource Value}" x:Name="Tools"/>
                        <TextBlock Grid.Row="3" Style="{StaticResource Label}" Text="AutoCAD Architecture"/>
                        <TextBlock Grid.Row="3" Grid.Column="1" Style="{StaticResource Value}" x:Name="Architecture"/>
                        <TextBlock Grid.Row="4" Style="{StaticResource Label}" Text="Process"/>
                        <TextBlock Grid.Row="4" Grid.Column="1" Style="{StaticResource Value}" x:Name="Process"/>
                        <TextBlock Grid.Row="5" Style="{StaticResource Label}" Text="Add-in version" Margin="0,0,12,0"/>
                        <TextBlock Grid.Row="5" Grid.Column="1" Style="{StaticResource Value}" x:Name="Version" Margin="0"/>
                    </Grid>
                </GroupBox>

                <GroupBox Header="CONNECT AN AGENT">
                    <StackPanel>
                        <Grid Margin="0,0,0,8">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="128"/>
                                <ColumnDefinition Width="*"/>
                            </Grid.ColumnDefinitions>
                            <TextBlock Style="{StaticResource Label}" Text="MCP server" Margin="0,0,12,0"/>
                            <TextBlock Grid.Column="1" x:Name="ServerPath" FontSize="12.5" VerticalAlignment="Center"
                                       TextTrimming="CharacterEllipsis"/>
                        </Grid>
                        <TextBlock x:Name="ServerHint" FontSize="11.5" Foreground="{StaticResource TextSecondary}"
                                   TextWrapping="Wrap" Margin="0,0,0,4"/>
                        <StackPanel x:Name="CopyLinks" Orientation="Horizontal" Margin="-6,0,0,0">
                            <Button x:Name="CopyPathBtn" Style="{StaticResource LinkButton}" Content="Copy path"/>
                            <TextBlock Text="|" FontSize="11" Foreground="{StaticResource TextMuted}" VerticalAlignment="Center"/>
                            <Button x:Name="CopyCommandBtn" Style="{StaticResource LinkButton}" Content="Copy Claude Code command"/>
                        </StackPanel>
                    </StackPanel>
                </GroupBox>

                <!-- Footer: the project link on the left; the actions on the right, all one
                     width, primary first and Close last. -->
                <Grid>
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="Auto"/>
                        <ColumnDefinition Width="*"/>
                        <ColumnDefinition Width="Auto"/>
                    </Grid.ColumnDefinitions>
                    <Button x:Name="GitHubBtn" Style="{StaticResource IconButton}" ToolTip="NodARCH AcadMcp on GitHub">
                        <!-- The GitHub mark (Octicons mark-github, MIT), used to link to the project -->
                        <Path Width="16" Height="16" Stretch="Uniform" Fill="{StaticResource TextPrimary}"
                              Data="M8,0 C3.58,0 0,3.58 0,8 c0,3.54 2.29,6.53 5.47,7.59 .4,.07 .55,-.17 .55,-.38 0,-.19 -.01,-.82 -.01,-1.49 -2.01,.37 -2.53,-.49 -2.69,-.94 -.09,-.23 -.48,-.94 -.82,-1.13 -.28,-.15 -.68,-.52 -.01,-.53 .63,-.01 1.08,.58 1.23,.82 .72,1.21 1.87,.87 2.33,.66 .07,-.52 .28,-.87 .51,-1.07 -1.78,-.2 -3.64,-.89 -3.64,-3.95 0,-.87 .31,-1.59 .82,-2.15 -.08,-.2 -.36,-1.02 .08,-2.12 0,0 .67,-.21 2.2,.82 .64,-.18 1.32,-.27 2,-.27 .68,0 1.36,.09 2,.27 1.53,-1.04 2.2,-.82 2.2,-.82 .44,1.1 .16,1.92 .08,2.12 .51,.56 .82,1.27 .82,2.15 0,3.07 -1.87,3.75 -3.65,3.95 .29,.25 .54,.73 .54,1.48 0,1.07 -.01,1.93 -.01,2.2 0,.21 .15,.46 .55,.38 A8.013,8.013 0 0 0 16,8 c0,-4.42 -3.58,-8 -8,-8 z"/>
                    </Button>
                    <StackPanel Grid.Column="2" Orientation="Horizontal">
                        <Button x:Name="StartBtn" Style="{StaticResource AccentButton}" Width="100" Margin="0,0,8,0">
                            <StackPanel Orientation="Horizontal">
                                <Path Width="11" Height="11" Stretch="Uniform" Margin="0,0,7,0" VerticalAlignment="Center"
                                      Stroke="{Binding Foreground, RelativeSource={RelativeSource AncestorType=Button}}"
                                      StrokeThickness="1.4" StrokeLineJoin="Round" Data="M3,2 L13,8 L3,14 Z"/>
                                <TextBlock Text="Start" VerticalAlignment="Center"/>
                            </StackPanel>
                        </Button>
                        <Button x:Name="StopBtn" Style="{StaticResource ModernButton}" Width="100" Margin="0,0,8,0">
                            <StackPanel Orientation="Horizontal">
                                <Path Width="10" Height="10" Stretch="Uniform" Margin="0,0,7,0" VerticalAlignment="Center"
                                      Stroke="{Binding Foreground, RelativeSource={RelativeSource AncestorType=Button}}"
                                      StrokeThickness="1.4" StrokeLineJoin="Round" Data="M3,3 H13 V13 H3 Z"/>
                                <TextBlock Text="Stop" VerticalAlignment="Center"/>
                            </StackPanel>
                        </Button>
                        <Button x:Name="CloseFooterBtn" Style="{StaticResource ModernButton}" Width="100" Content="Close"/>
                    </StackPanel>
                </Grid>
            </StackPanel>
        </Grid>
    </Border>
</Grid>
""";
}
