using Avalonia;
using Avalonia.Input;

namespace SqlStudio.Services;

public enum Os { Mac, Windows, Linux }

/// <summary>
/// Everything that differs between macOS, Windows and Linux lives here, so the rest of the app never asks "which system is this?"
/// or hard-codes a Mac-only value. A test (PlatformRulesTests) fails the build if a Mac-only shortcut, key name or wording
/// shows up anywhere else. Each rule takes the system as a parameter so it can be tested for all three without running on them.
/// </summary>
public static class Platform
{
    public static Os Current { get; } = OperatingSystem.IsMacOS() ? Os.Mac : OperatingSystem.IsWindows() ? Os.Windows : Os.Linux;

    /// <summary>Room the Windows minimize / maximize / close buttons take at the right edge: three buttons of 46 units, plus a little air.</summary>
    public const double WindowsCaptionButtonsWidth = 144;

    /// <summary>Room the Mac's red / yellow / green buttons take at the left edge (placed by MacTrafficLights).</summary>
    public const double MacTrafficLightsWidth = 84;

    /// <summary>
    /// Margin for what sits on the title bar (logo, tab chips, theme button). The system draws its own window buttons in a corner of the
    /// bar, which is the left on a Mac and the right on Windows, and anything of ours under them is hidden or unclickable.
    /// </summary>
    public static Thickness TitleBarMargin(Os os) => os switch
    {
        Os.Mac => new Thickness(MacTrafficLightsWidth, 0, 14, 0),
        Os.Windows => new Thickness(14, 0, WindowsCaptionButtonsWidth, 0),
        _ => new Thickness(14, 0, 14, 0),
    };

    public static string ThisComputer(Os os) => os == Os.Mac ? "this Mac" : "this computer";

    public static string Tagline(Os os) => os switch
    {
        Os.Mac => "Browse, query, back up and restore SQL Server, built for your Mac.",
        Os.Windows => "Browse, query, back up and restore SQL Server, built for Windows.",
        _ => "Browse, query, back up and restore SQL Server.",
    };

    /// <summary>How saved passwords are protected on each system, worded to match what the credential stores really do.</summary>
    public static string PasswordNote(Os os) => os switch
    {
        Os.Mac => "Passwords are kept in your macOS Keychain, never in a file.",
        Os.Windows => "Passwords are encrypted for your Windows account, never saved as plain text.",
        _ => "Passwords are kept in your desktop keyring when there is one, otherwise in an encrypted file, never as plain text.",
    };

    public static string AddServerHint(Os os) => $"Add a SQL Server or Azure SQL database. {PasswordNote(os)}";
    public static string DownloadZipLabel(Os os) => $"Also download the backup as a zip to {ThisComputer(os)}";

    public static string PasswordNoteShort(Os os) => os switch
    {
        Os.Mac => "Saved in your macOS Keychain, never in a file.",
        Os.Windows => "Saved encrypted for your Windows account.",
        _ => "Saved in your desktop keyring or an encrypted file.",
    };

    // The same rules for the system this is running on, for use from XAML ({x:Static svc:Platform.TitleBar}).
    public static Thickness TitleBar => TitleBarMargin(Current);
    public static string ThisComputerText => ThisComputer(Current);
    public static string TaglineText => Tagline(Current);
    public static string PasswordNoteText => PasswordNote(Current);
    public static string PasswordNoteShortText => PasswordNoteShort(Current);
    public static string AddServerHintText => AddServerHint(Current);
    public static string DownloadZipLabelText => DownloadZipLabel(Current);
}

/// <summary>
/// The command key and how shortcuts are written. A Mac uses ⌘ (the Meta key); Windows and Linux use Ctrl. On Windows the Meta key is the
/// Windows key, which the system keeps for itself, so a shortcut bound to it never reaches the app.
/// </summary>
public static class Shortcuts
{
    public static KeyModifiers CommandKey(Os os) => os == Os.Mac ? KeyModifiers.Meta : KeyModifiers.Control;

    public static string NewQuery(Os os) => os == Os.Mac ? "⌘T" : "Ctrl+T";
    public static string Run(Os os) => os == Os.Mac ? "⌘↵" : "Ctrl+Enter";
    public static string Stop(Os os) => os == Os.Mac ? "⌘." : "Ctrl+.";
    public static string CloseTab(Os os) => os == Os.Mac ? "⌘W" : "Ctrl+W";
    public static string SelectAll(Os os) => os == Os.Mac ? "⌘A" : "Ctrl+A";

    public static string RunTip(Os os) => $"Run ({Run(os)} or F5). Runs only the selected text if there is a selection.";
    public static string StopTip(Os os) => $"Stop the running query ({Stop(os)})";
    public static string NewQueryTip(Os os) => $"New query ({NewQuery(os)})";
    public static string OpenQueryTip(Os os) => $"Open a query on the selected server ({NewQuery(os)})";
    public static string OpenQueryOnSelectionTip(Os os) => $"Open a query on the selected server or database ({NewQuery(os)})";
    public static string CloseTabTip(Os os) => $"Close tab ({CloseTab(os)})";
    public static string EmptyEditorHint(Os os) => $"Write SQL here, then press {Run(os)} to run it.";
    public static string MessagesHint(Os os) => $"Press {Run(os)} to run. PRINT output and errors show up on this tab.";
    public static string SaveNoteHint(Os os) => $"{Run(os)} save · Esc cancel";

    // For the system this is running on.
    public static KeyModifiers Command => CommandKey(Platform.Current);
    public static string NewQueryText => NewQuery(Platform.Current);
    public static string RunText => Run(Platform.Current);
    public static string StopText => Stop(Platform.Current);
    public static string CloseTabText => CloseTab(Platform.Current);
    public static string SelectAllText => SelectAll(Platform.Current);
    public static string RunTipText => RunTip(Platform.Current);
    public static string StopTipText => StopTip(Platform.Current);
    public static string NewQueryTipText => NewQueryTip(Platform.Current);
    public static string OpenQueryTipText => OpenQueryTip(Platform.Current);
    public static string OpenQueryOnSelectionTipText => OpenQueryOnSelectionTip(Platform.Current);
    public static string CloseTabTipText => CloseTabTip(Platform.Current);
    public static string EmptyEditorHintText => EmptyEditorHint(Platform.Current);
    public static string MessagesHintText => MessagesHint(Platform.Current);
    public static string SaveNoteHintText => SaveNoteHint(Platform.Current);
}
